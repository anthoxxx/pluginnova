// Application Actualités : articles publiés par le staff et les journalistes
const express = require('express');
const { now, HttpError, text, route } = require('../util');

const CATEGORIES = ['Mairie', 'Info', 'Événement', 'Faits divers', 'Économie', 'Sport', 'Annonce'];

module.exports = (ctx) => {
  const { db } = ctx;
  const router = express.Router();
  const canWrite = (req) => req.isAdmin || req.user.is_journalist === 1;

  router.get('/actualites', route((req) => ({
    categories: CATEGORIES,
    canWrite: canWrite(req),
    articles: db.prepare('SELECT * FROM news ORDER BY pinned DESC, id DESC LIMIT 100').all().map((a) => ({
      id: a.id,
      title: a.title,
      body: a.body,
      category: a.category,
      authorName: a.author_name,
      pinned: a.pinned === 1,
      createdAt: a.created_at,
      canEdit: req.isAdmin || a.author_steam_id === req.user.steam_id,
    })),
  })));

  router.post('/actualites', route((req) => {
    if (!canWrite(req)) throw new HttpError(403, 'Seuls les journalistes et le staff peuvent publier.');
    const title = text(req.body, 'title', { min: 3, max: 120 });
    const body = text(req.body, 'body', { min: 3, max: 8000 });
    const category = CATEGORIES.includes(req.body?.category) ? req.body.category : 'Info';
    const pinned = req.isAdmin && req.body?.pinned ? 1 : 0;
    const id = db.prepare('INSERT INTO news (title, body, category, author_steam_id, author_name, pinned, created_at) VALUES (?, ?, ?, ?, ?, ?, ?)')
      .run(title, body, category, req.user.steam_id, ctx.displayName(req.user.steam_id), pinned, now()).lastInsertRowid;
    return { id: Number(id) };
  }));

  router.put('/actualites/:id', route((req) => {
    const a = db.prepare('SELECT * FROM news WHERE id = ?').get(Number(req.params.id));
    if (!a) throw new HttpError(404, 'Article introuvable.');
    if (!req.isAdmin && a.author_steam_id !== req.user.steam_id) throw new HttpError(403, 'Ce n\'est pas votre article.');
    const title = text(req.body, 'title', { min: 3, max: 120 });
    const body = text(req.body, 'body', { min: 3, max: 8000 });
    const category = CATEGORIES.includes(req.body?.category) ? req.body.category : a.category;
    const pinned = req.isAdmin ? (req.body?.pinned ? 1 : 0) : a.pinned;
    db.prepare('UPDATE news SET title = ?, body = ?, category = ?, pinned = ? WHERE id = ?').run(title, body, category, pinned, a.id);
    return { ok: true };
  }));

  router.delete('/actualites/:id', route((req) => {
    const a = db.prepare('SELECT * FROM news WHERE id = ?').get(Number(req.params.id));
    if (!a) throw new HttpError(404, 'Article introuvable.');
    if (!req.isAdmin && a.author_steam_id !== req.user.steam_id) throw new HttpError(403, 'Ce n\'est pas votre article.');
    db.prepare('DELETE FROM news WHERE id = ?').run(a.id);
    return { ok: true };
  }));

  return router;
};
