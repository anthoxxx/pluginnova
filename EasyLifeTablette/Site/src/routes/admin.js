// Recherche de joueurs + application Administration (staff du serveur)
const express = require('express');
const { HttpError, route } = require('../util');

module.exports = (ctx) => {
  const { db } = ctx;
  const router = express.Router();

  const requireAdmin = (req) => {
    if (!req.isAdmin) throw new HttpError(403, 'Réservé au staff.');
  };

  // Utilisé pour choisir un joueur (équipe d'un restaurant, gérant...)
  router.get('/joueurs', route((req) => {
    const q = typeof req.query.q === 'string' ? req.query.q.trim().slice(0, 40) : '';
    if (q.length < 2) return { players: [] };
    const like = `%${q}%`;
    return {
      players: db.prepare(`SELECT steam_id, rp_name, pseudo, in_game FROM users
                           WHERE rp_name LIKE ? OR pseudo LIKE ? OR steam_id = ? ORDER BY in_game DESC, rp_name LIMIT 20`)
        .all(like, like, q)
        .map((u) => ({ steamId: u.steam_id, name: u.rp_name || u.pseudo || u.steam_id, pseudo: u.pseudo, inGame: u.in_game === 1 })),
    };
  }));

  router.get('/admin', route((req) => {
    requireAdmin(req);
    const count = (sql) => db.prepare(sql).get().n;
    return {
      stats: {
        users: count('SELECT COUNT(*) n FROM users'),
        inGame: count('SELECT COUNT(*) n FROM users WHERE in_game = 1'),
        ads: count("SELECT COUNT(*) n FROM ads WHERE status = 'active'"),
        mails: count('SELECT COUNT(*) n FROM mail_accounts'),
        orders: count('SELECT COUNT(*) n FROM orders'),
      },
      journalists: db.prepare('SELECT steam_id, rp_name, pseudo FROM users WHERE is_journalist = 1').all()
        .map((u) => ({ steamId: u.steam_id, name: u.rp_name || u.pseudo || u.steam_id })),
      reports: db.prepare(`SELECT r.id, r.reason, r.created_at, r.steam_id, a.id AS ad_id, a.title FROM ad_reports r
                           JOIN ads a ON a.id = r.ad_id ORDER BY r.id DESC LIMIT 100`).all()
        .map((r) => ({ id: r.id, reason: r.reason, createdAt: r.created_at, by: ctx.displayName(r.steam_id), adId: r.ad_id, adTitle: r.title })),
    };
  }));

  router.put('/admin/journalistes/:steamId', route((req) => {
    requireAdmin(req);
    const res = db.prepare('UPDATE users SET is_journalist = ? WHERE steam_id = ?').run(req.body?.value ? 1 : 0, req.params.steamId);
    if (!res.changes) throw new HttpError(404, 'Joueur introuvable.');
    if (req.body?.value) ctx.notify(req.params.steamId, 'actualites', 'Actualités', 'Vous pouvez maintenant publier des articles.');
    return { ok: true };
  }));

  router.delete('/admin/signalements/:id', route((req) => {
    requireAdmin(req);
    db.prepare('DELETE FROM ad_reports WHERE id = ?').run(Number(req.params.id));
    return { ok: true };
  }));

  return router;
};
