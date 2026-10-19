// Application Notes : bloc-notes personnel
const express = require('express');
const { now, HttpError, text, route } = require('../util');

module.exports = (ctx) => {
  const { db } = ctx;
  const router = express.Router();

  function note(req, id) {
    const n = db.prepare('SELECT * FROM notes WHERE id = ? AND steam_id = ?').get(Number(id), req.user.steam_id);
    if (!n) throw new HttpError(404, 'Note introuvable.');
    return n;
  }

  router.get('/notes', route((req) => ({
    notes: db.prepare('SELECT id, title, body, updated_at FROM notes WHERE steam_id = ? ORDER BY updated_at DESC').all(req.user.steam_id),
  })));

  router.post('/notes', route((req) => {
    const count = db.prepare('SELECT COUNT(*) n FROM notes WHERE steam_id = ?').get(req.user.steam_id).n;
    if (count >= 200) throw new HttpError(400, 'Limite de 200 notes atteinte.');
    const id = db.prepare('INSERT INTO notes (steam_id, title, body, updated_at) VALUES (?, ?, ?, ?)')
      .run(req.user.steam_id, text(req.body, 'title', { max: 100, optional: true }) || 'Nouvelle note',
        text(req.body, 'body', { max: 10000, optional: true }), now()).lastInsertRowid;
    return { id: Number(id) };
  }));

  router.put('/notes/:id', route((req) => {
    const n = note(req, req.params.id);
    db.prepare('UPDATE notes SET title = ?, body = ?, updated_at = ? WHERE id = ?')
      .run(text(req.body, 'title', { max: 100, optional: true }) || 'Sans titre', text(req.body, 'body', { max: 10000, optional: true }), now(), n.id);
    return { ok: true };
  }));

  router.delete('/notes/:id', route((req) => {
    db.prepare('DELETE FROM notes WHERE id = ?').run(note(req, req.params.id).id);
    return { ok: true };
  }));

  return router;
};
