// API appelée par le plugin du serveur Nova-Life (protégée par la clé gameApiKey)
const express = require('express');
const { now, HttpError, safeEqual, randomCode, route } = require('../util');
const { tx } = require('../db');

const STEAM_ID = /^\d{17}$/;

module.exports = (ctx) => {
  const { db, config } = ctx;
  const router = express.Router();

  router.use((req, res, next) => {
    if (!safeEqual(req.get('X-Api-Key') || '', config.gameApiKey)) {
      return res.status(401).json({ error: 'Clé API invalide.' });
    }
    next();
  });

  /**
   * Appelée chaque minute par le plugin avec les joueurs connectés.
   * Met à jour leur fiche et renvoie les notifications à afficher en jeu.
   */
  router.post('/sync', route((req) => {
    const players = Array.isArray(req.body?.players) ? req.body.players.slice(0, 500) : [];
    const businesses = Array.isArray(req.body?.businesses) ? req.body.businesses.slice(0, 1000) : null;
    const t = now();
    const online = [];

    tx(db, () => {
      for (const p of players) {
        const steamId = String(p?.steamId ?? '');
        if (!STEAM_ID.test(steamId)) continue;
        online.push(steamId);
        const money = Number.isFinite(Number(p.money)) ? Number(p.money) : null;
        const bank = Number.isFinite(Number(p.bank)) ? Number(p.bank) : null;
        const name = String(p.name ?? '').slice(0, 80);
        const job = String(p.job ?? '').slice(0, 80);

        ctx.upsertUser(steamId, {});
        db.prepare(`UPDATE users SET rp_name = ?, job = ?, money = ?, bank = ?, is_admin = ?, in_game = 1, last_sync = ?
                    WHERE steam_id = ?`).run(name, job, money, bank, p.isAdmin ? 1 : 0, t, steamId);

        if (money !== null && bank !== null) {
          const last = db.prepare('SELECT money, bank FROM bank_history WHERE steam_id = ? ORDER BY id DESC LIMIT 1').get(steamId);
          if (!last || last.money !== money || last.bank !== bank) {
            db.prepare('INSERT INTO bank_history (steam_id, money, bank, at) VALUES (?, ?, ?, ?)').run(steamId, money, bank, t);
          }
        }
      }

      if (online.length) {
        db.prepare(`UPDATE users SET in_game = 0 WHERE in_game = 1 AND steam_id NOT IN (${online.map(() => '?').join(',')})`).run(...online);
      } else {
        db.prepare('UPDATE users SET in_game = 0 WHERE in_game = 1').run();
      }

      // Entreprises du jeu -> Annuaire (les fiches ajoutées à la main ne sont jamais touchées)
      if (businesses) {
        const seen = [];
        for (const b of businesses) {
          const id = Number(b?.id);
          const name = String(b?.name ?? '').trim().slice(0, 80);
          if (!Number.isInteger(id) || !name) continue;
          seen.push(id);
          const phone = String(b.phone ?? '').slice(0, 20);
          const existing = db.prepare('SELECT id FROM directory WHERE game_biz_id = ?').get(id);
          if (existing) {
            db.prepare('UPDATE directory SET name = ?, phone = CASE WHEN ? != \'\' THEN ? ELSE phone END WHERE id = ?')
              .run(name, phone, phone, existing.id);
          } else {
            db.prepare('INSERT INTO directory (name, category, phone, game_biz_id, created_at) VALUES (?, ?, ?, ?, ?)')
              .run(name, String(b.category || 'Entreprise').slice(0, 40), phone, id, t);
          }
        }
        if (seen.length) {
          db.prepare(`DELETE FROM directory WHERE game_biz_id IS NOT NULL AND game_biz_id NOT IN (${seen.map(() => '?').join(',')})`).run(...seen);
        }
      }
    });

    // Notifications non encore envoyées aux joueurs connectés (10 dernières minutes)
    const notifications = [];
    if (online.length) {
      const rows = db.prepare(`SELECT n.id, n.steam_id, n.title, n.body, u.settings FROM notifications n
                               JOIN users u ON u.steam_id = n.steam_id
                               WHERE n.pushed = 0 AND n.app != 'system' AND n.created_at > ?
                               AND n.steam_id IN (${online.map(() => '?').join(',')})
                               ORDER BY n.id LIMIT 200`).all(t - 10 * 60e3, ...online);
      const mark = db.prepare('UPDATE notifications SET pushed = 1 WHERE id = ?');
      for (const r of rows) {
        mark.run(r.id);
        if (JSON.parse(r.settings || '{}').gameNotifications === false) continue;
        notifications.push({ steamId: r.steam_id, title: r.title, body: r.body });
      }
    }
    return { notifications };
  }));

  /** Code de connexion à usage unique (commande /tablette en jeu), valable 10 minutes. */
  router.post('/code', route((req) => {
    const steamId = String(req.body?.steamId ?? '');
    if (!STEAM_ID.test(steamId)) throw new HttpError(400, 'steamId invalide.');
    ctx.upsertUser(steamId, {});
    const name = String(req.body?.name ?? '').slice(0, 80);
    if (name) db.prepare('UPDATE users SET rp_name = ? WHERE steam_id = ?').run(name, steamId);

    db.prepare('DELETE FROM login_codes WHERE steam_id = ?').run(steamId);
    let code;
    do code = randomCode(6);
    while (db.prepare('SELECT 1 FROM login_codes WHERE code = ?').get(code));
    db.prepare('INSERT INTO login_codes (code, steam_id, expires_at) VALUES (?, ?, ?)').run(code, steamId, now() + 10 * 60e3);
    return { code, url: config.baseUrl };
  }));

  return router;
};
