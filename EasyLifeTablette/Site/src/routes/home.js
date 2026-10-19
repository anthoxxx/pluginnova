// Écran d'accueil (widgets), notifications et banque
const express = require('express');
const { route } = require('../util');

module.exports = (ctx) => {
  const { db } = ctx;
  const router = express.Router();

  router.get('/accueil', route((req) => {
    const me = req.user.steam_id;
    const unreadMail = db.prepare(`SELECT COUNT(*) n FROM mails m JOIN mail_access a ON a.account_id = m.account_id
                                   WHERE a.steam_id = ? AND m.folder = 'inbox' AND m.read = 0`).get(me).n;
    return {
      notifications: db.prepare('SELECT id, app, title, body, read, created_at FROM notifications WHERE steam_id = ? ORDER BY id DESC LIMIT 6').all(me),
      unreadNotifications: db.prepare('SELECT COUNT(*) n FROM notifications WHERE steam_id = ? AND read = 0').get(me).n,
      news: db.prepare('SELECT id, title, body, category, created_at FROM news ORDER BY pinned DESC, id DESC LIMIT 3').all(),
      badges: { mail: unreadMail },
    };
  }));

  router.get('/notifications', route((req) => ({
    notifications: db.prepare('SELECT id, app, title, body, read, created_at FROM notifications WHERE steam_id = ? ORDER BY id DESC LIMIT 100')
      .all(req.user.steam_id),
  })));

  router.post('/notifications/lues', route((req) => {
    db.prepare('UPDATE notifications SET read = 1 WHERE steam_id = ?').run(req.user.steam_id);
    return { ok: true };
  }));

  router.delete('/notifications', route((req) => {
    db.prepare('DELETE FROM notifications WHERE steam_id = ?').run(req.user.steam_id);
    return { ok: true };
  }));

  router.get('/banque', route((req) => ({
    money: req.user.money,
    bank: req.user.bank,
    lastSync: req.user.last_sync,
    inGame: req.user.in_game === 1,
    history: db.prepare('SELECT money, bank, at FROM bank_history WHERE steam_id = ? ORDER BY id DESC LIMIT 60').all(req.user.steam_id),
  })));

  return router;
};
