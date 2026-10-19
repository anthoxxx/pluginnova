// Connexion (Steam ou code donné en jeu), session, verrouillage et réglages du compte
const express = require('express');
const steam = require('../steam');
const { now, HttpError, text, randomToken, hashPassword, checkPassword, route } = require('../util');

const COOKIE = 'el_session';
const SESSION_DAYS = 30;

function readCookie(req, name) {
  for (const part of (req.headers.cookie || '').split(';')) {
    const i = part.indexOf('=');
    if (i > 0 && part.slice(0, i).trim() === name) return decodeURIComponent(part.slice(i + 1).trim());
  }
  return null;
}

function browserName(ua = '') {
  if (/UnityPlayer|Vuplex|Nova-?Life|ZFBrowser|CEF/i.test(ua)) return 'le jeu';
  if (/Edg\//.test(ua)) return 'Edge';
  if (/OPR\//.test(ua)) return 'Opera';
  if (/Firefox\//.test(ua)) return 'Firefox';
  if (/Chrome\//.test(ua)) return 'Chrome';
  if (/Safari\//.test(ua)) return 'Safari';
  return 'un navigateur';
}

module.exports = (ctx) => {
  const { db, config } = ctx;
  const router = express.Router();
  const secureCookie = config.baseUrl.startsWith('https://');

  function upsertUser(steamId, fields = {}) {
    const existing = db.prepare('SELECT steam_id FROM users WHERE steam_id = ?').get(steamId);
    if (!existing) {
      db.prepare('INSERT INTO users (steam_id, pseudo, avatar, created_at) VALUES (?, ?, ?, ?)')
        .run(steamId, fields.pseudo || '', fields.avatar || '', now());
    } else if (fields.pseudo) {
      db.prepare('UPDATE users SET pseudo = ?, avatar = ? WHERE steam_id = ?').run(fields.pseudo, fields.avatar || '', steamId);
    }
  }
  ctx.upsertUser = upsertUser;

  function startSession(req, res, steamId, via) {
    const user = db.prepare('SELECT pin_hash FROM users WHERE steam_id = ?').get(steamId);
    const token = randomToken();
    const t = now();
    db.prepare('INSERT INTO sessions (token, steam_id, via, locked, created_at, last_seen) VALUES (?, ?, ?, ?, ?, ?)')
      .run(token, steamId, via, user?.pin_hash ? 1 : 0, t, t);
    res.cookie(COOKIE, token, {
      httpOnly: true,
      sameSite: 'lax',
      secure: secureCookie,
      maxAge: SESSION_DAYS * 24 * 3600e3,
      path: '/',
    });
    const how = via === 'steam' ? 'Steam' : 'code en jeu';
    ctx.notify(steamId, 'system', 'Nouvelle connexion', `Connexion à votre tablette (${how}) depuis ${browserName(req.headers['user-agent'])}.`);
  }

  function loadSession(req) {
    const token = readCookie(req, COOKIE);
    if (!token) return null;
    const session = db.prepare('SELECT * FROM sessions WHERE token = ?').get(token);
    if (!session) return null;
    const t = now();
    if (t - session.last_seen > 60e3) db.prepare('UPDATE sessions SET last_seen = ? WHERE token = ?').run(t, token);
    const user = db.prepare('SELECT * FROM users WHERE steam_id = ?').get(session.steam_id);
    return user ? { session, user } : null;
  }

  const requireUser = (req, res, next) => {
    const s = loadSession(req);
    if (!s) return res.status(401).json({ error: 'Non connecté.' });
    req.session = s.session;
    req.user = s.user;
    req.isAdmin = ctx.isAdmin(s.user);
    next();
  };

  const requireUnlocked = (req, res, next) => {
    if (req.session.locked && req.user.pin_hash) return res.status(423).json({ error: 'Tablette verrouillée.' });
    next();
  };

  // ---------- Steam ----------
  router.get('/auth/steam', (req, res) => res.redirect(steam.loginUrl(config.baseUrl)));

  router.get('/auth/steam/retour', async (req, res) => {
    try {
      const steamId = await steam.verify(req.query, config.baseUrl);
      if (!steamId) return res.redirect('/?erreur=steam');
      upsertUser(steamId, (await steam.fetchProfile(config.steamApiKey, steamId)) || {});
      startSession(req, res, steamId, 'steam');
      res.redirect('/');
    } catch (e) {
      console.error('Connexion Steam :', e);
      res.redirect('/?erreur=steam');
    }
  });

  // ---------- Code donné en jeu (/tablette) ----------
  const codeAttempts = new Map();
  router.post('/auth/code', route((req, res) => {
    const ip = req.ip;
    const attempts = (codeAttempts.get(ip) || []).filter((t) => now() - t < 10 * 60e3);
    if (attempts.length >= 10) throw new HttpError(429, 'Trop de tentatives, réessayez dans quelques minutes.');
    attempts.push(now());
    codeAttempts.set(ip, attempts);

    const code = text(req.body, 'code', { min: 4, max: 12 }).toUpperCase().replace(/[^A-Z0-9]/g, '');
    const row = db.prepare('SELECT * FROM login_codes WHERE code = ? AND expires_at > ?').get(code, now());
    if (!row) throw new HttpError(400, 'Code invalide ou expiré. Tapez /tablette en jeu pour en obtenir un nouveau.');
    db.prepare('DELETE FROM login_codes WHERE code = ?').run(code);
    upsertUser(row.steam_id);
    startSession(req, res, row.steam_id, 'jeu');
    return { ok: true };
  }));

  router.post('/auth/deconnexion', route((req, res) => {
    const token = readCookie(req, COOKIE);
    if (token) db.prepare('DELETE FROM sessions WHERE token = ?').run(token);
    res.clearCookie(COOKIE, { path: '/' });
    return { ok: true };
  }));

  // ---------- Compte ----------
  router.get('/api/moi', requireUser, route((req) => {
    const u = req.user;
    return {
      steamId: u.steam_id,
      pseudo: u.pseudo,
      avatar: u.avatar,
      rpName: u.rp_name,
      job: u.job,
      money: u.money,
      bank: u.bank,
      inGame: u.in_game === 1,
      lastSync: u.last_sync,
      isAdmin: req.isAdmin,
      isJournalist: u.is_journalist === 1,
      hasPin: !!u.pin_hash,
      locked: !!(req.session.locked && u.pin_hash),
      settings: JSON.parse(u.settings || '{}'),
      serverName: config.serverName || 'Easy Life',
      mailDomain: config.mailDomain,
    };
  }));

  router.post('/api/verrouiller', requireUser, route((req) => {
    if (!req.user.pin_hash) throw new HttpError(400, 'Définissez d\'abord un code dans Réglages › Code & sécurité.');
    db.prepare('UPDATE sessions SET locked = 1 WHERE token = ?').run(req.session.token);
    return { ok: true };
  }));

  const unlockAttempts = new Map();
  router.post('/api/deverrouiller', requireUser, route((req) => {
    const key = req.session.token;
    const attempts = (unlockAttempts.get(key) || []).filter((t) => now() - t < 5 * 60e3);
    if (attempts.length >= 5) throw new HttpError(429, 'Trop d\'essais. Patientez 5 minutes.');
    const pin = text(req.body, 'pin', { min: 4, max: 8 });
    if (!checkPassword(pin, req.user.pin_hash)) {
      attempts.push(now());
      unlockAttempts.set(key, attempts);
      throw new HttpError(403, 'Code incorrect.');
    }
    unlockAttempts.delete(key);
    db.prepare('UPDATE sessions SET locked = 0 WHERE token = ?').run(req.session.token);
    return { ok: true };
  }));

  router.put('/api/reglages', requireUser, requireUnlocked, route((req) => {
    const current = JSON.parse(req.user.settings || '{}');
    const allowed = {
      wallpaper: (v) => typeof v === 'string' && /^[a-z0-9-]{1,30}$/.test(v),
      accent: (v) => typeof v === 'string' && /^#[0-9a-f]{6}$/i.test(v),
      textSize: (v) => ['petit', 'normal', 'grand'].includes(v),
      showNews: (v) => typeof v === 'boolean',
      showMoney: (v) => typeof v === 'boolean',
      gameNotifications: (v) => typeof v === 'boolean',
      hiddenApps: (v) => Array.isArray(v) && v.length < 50 && v.every((x) => typeof x === 'string' && x.length < 30),
    };
    for (const [k, check] of Object.entries(allowed)) {
      if (k in (req.body || {})) {
        if (!check(req.body[k])) throw new HttpError(400, `Réglage « ${k} » invalide.`);
        current[k] = req.body[k];
      }
    }
    db.prepare('UPDATE users SET settings = ? WHERE steam_id = ?').run(JSON.stringify(current), req.user.steam_id);
    return { settings: current };
  }));

  router.put('/api/code-securite', requireUser, requireUnlocked, route((req) => {
    const pin = req.body?.pin;
    if (pin === null || pin === '') {
      if (req.user.pin_hash && !checkPassword(String(req.body?.current || ''), req.user.pin_hash)) {
        throw new HttpError(403, 'Code actuel incorrect.');
      }
      db.prepare('UPDATE users SET pin_hash = NULL WHERE steam_id = ?').run(req.user.steam_id);
      return { hasPin: false };
    }
    if (typeof pin !== 'string' || !/^\d{4,8}$/.test(pin)) throw new HttpError(400, 'Le code doit contenir 4 à 8 chiffres.');
    if (req.user.pin_hash && !checkPassword(String(req.body?.current || ''), req.user.pin_hash)) {
      throw new HttpError(403, 'Code actuel incorrect.');
    }
    db.prepare('UPDATE users SET pin_hash = ? WHERE steam_id = ?').run(hashPassword(pin), req.user.steam_id);
    return { hasPin: true };
  }));

  router.get('/api/appareils', requireUser, requireUnlocked, route((req) => ({
    sessions: db.prepare('SELECT token, via, created_at, last_seen FROM sessions WHERE steam_id = ? ORDER BY last_seen DESC')
      .all(req.user.steam_id)
      .map((s) => ({ current: s.token === req.session.token, via: s.via, createdAt: s.created_at, lastSeen: s.last_seen })),
  })));

  router.post('/api/appareils/deconnecter-autres', requireUser, requireUnlocked, route((req) => {
    db.prepare('DELETE FROM sessions WHERE steam_id = ? AND token != ?').run(req.user.steam_id, req.session.token);
    return { ok: true };
  }));

  return { router, requireUser, requireUnlocked };
};
