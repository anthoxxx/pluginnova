// Application Mail : adresses @domaine partageables, boîte de réception, envoi
const express = require('express');
const { now, HttpError, text, hashPassword, checkPassword, route } = require('../util');
const { tx } = require('../db');

module.exports = (ctx) => {
  const { db, config } = ctx;
  const router = express.Router();
  const domain = config.mailDomain;

  function account(req, id) {
    const acc = db.prepare(`SELECT a.* FROM mail_accounts a JOIN mail_access x ON x.account_id = a.id
                            WHERE a.id = ? AND x.steam_id = ?`).get(Number(id), req.user.steam_id);
    if (!acc) throw new HttpError(404, 'Adresse introuvable sur cette tablette.');
    return acc;
  }

  function message(req, id) {
    const m = db.prepare('SELECT * FROM mails WHERE id = ?').get(Number(id));
    if (!m) throw new HttpError(404, 'Message introuvable.');
    account(req, m.account_id);
    return m;
  }

  router.get('/mail/comptes', route((req) => ({
    domain,
    accounts: db.prepare(`SELECT a.id, a.address, a.display_name, a.owner_steam_id = ? AS owner,
                            (SELECT COUNT(*) FROM mails m WHERE m.account_id = a.id AND m.folder = 'inbox' AND m.read = 0) AS unread
                          FROM mail_accounts a JOIN mail_access x ON x.account_id = a.id
                          WHERE x.steam_id = ? ORDER BY a.id`).all(req.user.steam_id, req.user.steam_id),
  })));

  router.post('/mail/comptes', route((req) => {
    const local = text(req.body, 'local', { min: 3, max: 30 }).toLowerCase();
    if (!/^[a-z0-9][a-z0-9._-]*$/.test(local)) throw new HttpError(400, 'Adresse invalide : lettres, chiffres, point, tiret uniquement.');
    const displayName = text(req.body, 'displayName', { max: 60 });
    const password = text(req.body, 'password', { min: 6, max: 100 });
    const address = `${local}@${domain}`;
    if (db.prepare('SELECT 1 FROM mail_accounts WHERE address = ?').get(address)) throw new HttpError(409, 'Cette adresse est déjà prise.');
    const owned = db.prepare('SELECT COUNT(*) n FROM mail_accounts WHERE owner_steam_id = ?').get(req.user.steam_id).n;
    if (owned >= 10) throw new HttpError(400, 'Limite de 10 adresses créées atteinte.');

    return tx(db, () => {
      const id = Number(db.prepare('INSERT INTO mail_accounts (address, display_name, password_hash, owner_steam_id, created_at) VALUES (?, ?, ?, ?, ?)')
        .run(address, displayName, hashPassword(password), req.user.steam_id, now()).lastInsertRowid);
      db.prepare('INSERT INTO mail_access (account_id, steam_id) VALUES (?, ?)').run(id, req.user.steam_id);
      db.prepare(`INSERT INTO mails (account_id, folder, from_addr, from_name, to_addrs, subject, body, created_at)
                  VALUES (?, 'inbox', ?, ?, ?, ?, ?, ?)`)
        .run(id, `bienvenue@${domain}`, config.serverName || 'Easy Life', address, 'Bienvenue sur votre messagerie',
          `Bonjour ${displayName},\n\nVotre adresse ${address} est prête.\nVous pouvez la partager : toute personne qui connaît le mot de passe peut l'ajouter à sa tablette (idéal pour une adresse d'entreprise).\n\nBonne journée !`, now());
      return { id, address };
    });
  }));

  router.post('/mail/connexion', route((req) => {
    let address = text(req.body, 'address', { max: 80 }).toLowerCase();
    if (!address.includes('@')) address = `${address}@${domain}`;
    const password = text(req.body, 'password', { max: 100 });
    const acc = db.prepare('SELECT * FROM mail_accounts WHERE address = ?').get(address);
    if (!acc || !checkPassword(password, acc.password_hash)) throw new HttpError(403, 'Adresse ou mot de passe incorrect.');
    db.prepare('INSERT OR IGNORE INTO mail_access (account_id, steam_id) VALUES (?, ?)').run(acc.id, req.user.steam_id);
    return { id: acc.id, address };
  }));

  // Retirer l'adresse de sa tablette (le propriétaire peut la supprimer définitivement)
  router.delete('/mail/comptes/:id', route((req) => {
    const acc = account(req, req.params.id);
    if (req.query.definitif === '1') {
      if (acc.owner_steam_id !== req.user.steam_id) throw new HttpError(403, 'Seul le créateur peut supprimer l\'adresse.');
      db.prepare('DELETE FROM mail_accounts WHERE id = ?').run(acc.id);
    } else {
      db.prepare('DELETE FROM mail_access WHERE account_id = ? AND steam_id = ?').run(acc.id, req.user.steam_id);
    }
    return { ok: true };
  }));

  router.put('/mail/comptes/:id/mot-de-passe', route((req) => {
    const acc = account(req, req.params.id);
    if (acc.owner_steam_id !== req.user.steam_id) throw new HttpError(403, 'Seul le créateur peut changer le mot de passe.');
    const password = text(req.body, 'password', { min: 6, max: 100 });
    db.prepare('UPDATE mail_accounts SET password_hash = ? WHERE id = ?').run(hashPassword(password), acc.id);
    // Les autres personnes perdent l'accès jusqu'à ce qu'elles aient le nouveau mot de passe
    db.prepare('DELETE FROM mail_access WHERE account_id = ? AND steam_id != ?').run(acc.id, req.user.steam_id);
    return { ok: true };
  }));

  router.get('/mail/comptes/:id/:dossier', route((req) => {
    const acc = account(req, req.params.id);
    const folder = req.params.dossier === 'envoyes' ? 'sent' : 'inbox';
    return {
      messages: db.prepare(`SELECT id, from_addr, from_name, to_addrs, subject, substr(body, 1, 140) AS preview, read, created_at
                            FROM mails WHERE account_id = ? AND folder = ? ORDER BY id DESC LIMIT 200`).all(acc.id, folder),
    };
  }));

  router.get('/mail/messages/:id', route((req) => {
    const m = message(req, req.params.id);
    if (!m.read) db.prepare('UPDATE mails SET read = 1 WHERE id = ?').run(m.id);
    return { message: { ...m, read: 1 } };
  }));

  router.delete('/mail/messages/:id', route((req) => {
    const m = message(req, req.params.id);
    db.prepare('DELETE FROM mails WHERE id = ?').run(m.id);
    return { ok: true };
  }));

  router.post('/mail/comptes/:id/envoyer', route((req) => {
    const acc = account(req, req.params.id);
    const subject = text(req.body, 'subject', { max: 150, optional: true }) || '(Sans objet)';
    const body = text(req.body, 'body', { max: 5000 });
    const recipients = [...new Set(text(req.body, 'to', { max: 500 }).toLowerCase().split(/[\s,;]+/).filter(Boolean)
      .map((a) => (a.includes('@') ? a : `${a}@${domain}`)))];
    if (recipients.length > 20) throw new HttpError(400, '20 destinataires maximum.');

    const targets = recipients.map((address) => ({ address, acc: db.prepare('SELECT id FROM mail_accounts WHERE address = ?').get(address) }));
    const unknown = targets.filter((t) => !t.acc).map((t) => t.address);
    if (unknown.length) throw new HttpError(400, `Adresse inconnue : ${unknown.join(', ')}`);

    const t = now();
    tx(db, () => {
      const insert = db.prepare(`INSERT INTO mails (account_id, folder, from_addr, from_name, to_addrs, subject, body, read, created_at)
                                 VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)`);
      insert.run(acc.id, 'sent', acc.address, acc.display_name, recipients.join(', '), subject, body, 1, t);
      for (const target of targets) {
        insert.run(target.acc.id, 'inbox', acc.address, acc.display_name, recipients.join(', '), subject, body, 0, t);
        const readers = db.prepare('SELECT steam_id FROM mail_access WHERE account_id = ?').all(target.acc.id);
        for (const r of readers) {
          if (r.steam_id !== req.user.steam_id) ctx.notify(r.steam_id, 'mail', `Mail de ${acc.display_name}`, `${target.address} : ${subject}`);
        }
      }
    });
    return { ok: true };
  }));

  return router;
};
