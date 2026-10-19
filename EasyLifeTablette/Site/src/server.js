// Tablette Easy Life : serveur web (site + API pour le plugin Nova-Life)
const path = require('node:path');
const fs = require('node:fs');
const express = require('express');
const { open } = require('./db');
const { now, HttpError } = require('./util');

const ROOT = path.join(__dirname, '..');
const configFile = process.env.EASYLIFE_CONFIG || path.join(ROOT, 'config.json');
if (!fs.existsSync(configFile)) {
  console.error(`Fichier de configuration introuvable : ${configFile}\nCopiez config.example.json en config.json et remplissez-le.`);
  process.exit(1);
}
const config = JSON.parse(fs.readFileSync(configFile, 'utf8'));
config.baseUrl = String(config.baseUrl || '').replace(/\/+$/, '');
config.adminSteamIds = (config.adminSteamIds || []).map(String);
config.mailDomain = config.mailDomain || 'easylife.fr';
if (!config.baseUrl || !config.gameApiKey || config.gameApiKey.length < 16) {
  console.error('config.json : « baseUrl » et « gameApiKey » (16 caractères minimum) sont obligatoires.');
  process.exit(1);
}

const db = open(path.resolve(ROOT, config.database || 'data/easylife.sqlite'));

const ctx = {
  db,
  config,
  isAdmin: (user) => !!user && (user.is_admin === 1 || config.adminSteamIds.includes(user.steam_id)),
  /** Crée une notification (tablette + envoyée en jeu par le plugin si le joueur est connecté). */
  notify(steamId, app, title, body) {
    db.prepare('INSERT INTO notifications (steam_id, app, title, body, created_at) VALUES (?, ?, ?, ?, ?)')
      .run(String(steamId), app, title.slice(0, 120), body.slice(0, 300), now());
  },
  /** Nom à afficher : nom RP synchronisé depuis le jeu, sinon pseudo Steam. */
  displayName(steamId) {
    const u = db.prepare('SELECT rp_name, pseudo FROM users WHERE steam_id = ?').get(String(steamId));
    return u ? (u.rp_name || u.pseudo || 'Inconnu') : 'Inconnu';
  },
};

const app = express();
app.set('trust proxy', config.trustProxy ?? 'loopback');
app.disable('x-powered-by');
app.use(express.json({ limit: '200kb' }));
app.use((req, res, next) => {
  res.set('X-Content-Type-Options', 'nosniff');
  res.set('Referrer-Policy', 'same-origin');
  next();
});

// Les requêtes d'API qui modifient quelque chose doivent être en JSON :
// un formulaire d'un autre site ne peut pas en envoyer (protection CSRF avec SameSite=Lax).
app.use('/api', (req, res, next) => {
  if (req.method !== 'GET' && !req.is('application/json')) {
    return res.status(415).json({ error: 'Content-Type application/json requis.' });
  }
  next();
});

const auth = require('./routes/auth')(ctx);
app.use(auth.router);
app.use('/api/game', require('./routes/game')(ctx));

// Toutes les routes suivantes exigent une session (et la tablette déverrouillée)
app.use('/api', auth.requireUser, auth.requireUnlocked);
for (const name of ['home', 'mail', 'directory', 'news', 'ads', 'food', 'notes', 'admin']) {
  app.use('/api', require(`./routes/${name}`)(ctx));
}
app.use('/api', (req, res) => res.status(404).json({ error: 'Route inconnue.' }));

app.use(express.static(path.join(ROOT, 'public'), { index: 'index.html' }));
app.get('*', (req, res) => res.sendFile(path.join(ROOT, 'public', 'index.html')));

app.use((err, req, res, next) => {
  if (err instanceof HttpError || err.type === 'entity.parse.failed') {
    return res.status(err.status || 400).json({ error: err.message });
  }
  console.error(err);
  res.status(500).json({ error: 'Erreur interne du serveur.' });
});

// Ménage régulier : codes expirés, vieilles sessions et notifications
setInterval(() => {
  const t = now();
  db.prepare('DELETE FROM login_codes WHERE expires_at < ?').run(t);
  db.prepare('DELETE FROM sessions WHERE last_seen < ?').run(t - 30 * 24 * 3600e3);
  db.prepare('DELETE FROM notifications WHERE created_at < ?').run(t - 30 * 24 * 3600e3);
  db.prepare("UPDATE ads SET status = 'expired' WHERE status = 'active' AND expires_at < ?").run(t);
  // Un joueur que le plugin n'a pas synchronisé depuis 3 min n'est plus en jeu
  db.prepare('UPDATE users SET in_game = 0 WHERE in_game = 1 AND last_sync < ?').run(t - 3 * 60e3);
}, 60e3).unref();

const port = Number(process.env.PORT || config.port || 3000);
const host = process.env.HOST || config.host || '0.0.0.0';
app.listen(port, host, () => {
  console.log(`Tablette Easy Life en ligne sur http://${host}:${port} (adresse publique : ${config.baseUrl})`);
});
