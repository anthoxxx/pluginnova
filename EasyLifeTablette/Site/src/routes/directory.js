// Application Annuaire : entreprises du jeu (synchronisées) + fiches ajoutées par le staff
const express = require('express');
const { now, HttpError, text, route } = require('../util');

module.exports = (ctx) => {
  const { db } = ctx;
  const router = express.Router();

  function entry(id) {
    const e = db.prepare('SELECT * FROM directory WHERE id = ?').get(Number(id));
    if (!e) throw new HttpError(404, 'Fiche introuvable.');
    return e;
  }

  router.get('/annuaire', route((req) => ({
    canManage: req.isAdmin,
    entries: db.prepare('SELECT * FROM directory ORDER BY name COLLATE NOCASE').all().map((e) => ({
      id: e.id,
      name: e.name,
      category: e.category,
      phone: e.phone,
      description: e.description,
      recruiting: e.recruiting === 1,
      fromGame: e.game_biz_id !== null,
      ownerName: e.owner_steam_id ? ctx.displayName(e.owner_steam_id) : null,
      canEdit: req.isAdmin || e.owner_steam_id === req.user.steam_id,
    })),
  })));

  function readFields(body) {
    return {
      name: text(body, 'name', { max: 60 }),
      category: text(body, 'category', { max: 40, optional: true }) || 'Services',
      phone: text(body, 'phone', { max: 20, optional: true }),
      description: text(body, 'description', { max: 500, optional: true }),
      recruiting: body?.recruiting ? 1 : 0,
    };
  }

  router.post('/annuaire', route((req) => {
    if (!req.isAdmin) throw new HttpError(403, 'Réservé au staff.');
    const f = readFields(req.body);
    const id = db.prepare('INSERT INTO directory (name, category, phone, description, recruiting, created_at) VALUES (?, ?, ?, ?, ?, ?)')
      .run(f.name, f.category, f.phone, f.description, f.recruiting, now()).lastInsertRowid;
    return { id: Number(id) };
  }));

  router.put('/annuaire/:id', route((req) => {
    const e = entry(req.params.id);
    if (!req.isAdmin && e.owner_steam_id !== req.user.steam_id) throw new HttpError(403, 'Vous ne gérez pas cette fiche.');
    const f = readFields({ name: e.name, ...req.body });
    // Le nom d'une entreprise du jeu vient du jeu : il serait écrasé à la synchronisation suivante
    const name = e.game_biz_id !== null ? e.name : f.name;
    db.prepare('UPDATE directory SET name = ?, category = ?, phone = ?, description = ?, recruiting = ? WHERE id = ?')
      .run(name, f.category, f.phone, f.description, f.recruiting, e.id);
    return { ok: true };
  }));

  router.put('/annuaire/:id/gerant', route((req) => {
    if (!req.isAdmin) throw new HttpError(403, 'Réservé au staff.');
    const e = entry(req.params.id);
    const steamId = text(req.body, 'steamId', { max: 17, optional: true });
    if (steamId && !/^\d{17}$/.test(steamId)) throw new HttpError(400, 'SteamID64 invalide (17 chiffres).');
    db.prepare('UPDATE directory SET owner_steam_id = ? WHERE id = ?').run(steamId || null, e.id);
    return { ok: true };
  }));

  router.delete('/annuaire/:id', route((req) => {
    if (!req.isAdmin) throw new HttpError(403, 'Réservé au staff.');
    const e = entry(req.params.id);
    if (e.game_biz_id !== null) throw new HttpError(400, 'Cette entreprise vient du jeu : supprimez-la en jeu.');
    db.prepare('DELETE FROM directory WHERE id = ?').run(e.id);
    return { ok: true };
  }));

  return router;
};
