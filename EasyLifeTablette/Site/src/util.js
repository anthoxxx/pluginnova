const crypto = require('node:crypto');

const now = () => Date.now();

class HttpError extends Error {
  constructor(status, message) {
    super(message);
    this.status = status;
  }
}

/** Lit un texte obligatoire (coupé, longueur bornée) dans un objet de requête. */
function text(obj, key, { min = 1, max = 200, optional = false } = {}) {
  const raw = obj?.[key];
  const value = typeof raw === 'string' ? raw.trim() : '';
  if (!value && optional) return '';
  if (value.length < min) throw new HttpError(400, `Champ « ${key} » manquant ou trop court.`);
  if (value.length > max) throw new HttpError(400, `Champ « ${key} » trop long (${max} caractères max).`);
  return value;
}

function number(obj, key, { min = 0, max = 1e9, optional = false } = {}) {
  const raw = obj?.[key];
  if ((raw === undefined || raw === null || raw === '') && optional) return null;
  const n = Number(raw);
  if (!Number.isFinite(n) || n < min || n > max) throw new HttpError(400, `Champ « ${key} » invalide.`);
  return n;
}

function int(obj, key, opts) {
  const n = number(obj, key, opts);
  return n === null ? null : Math.trunc(n);
}

function randomToken(bytes = 32) {
  return crypto.randomBytes(bytes).toString('base64url');
}

/** Code lisible pour la connexion en jeu (sans 0/O/1/I). */
function randomCode(length = 6) {
  const alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789';
  let code = '';
  for (const b of crypto.randomBytes(length)) code += alphabet[b % alphabet.length];
  return code;
}

function hashPassword(password) {
  const salt = crypto.randomBytes(16);
  const hash = crypto.scryptSync(password, salt, 32);
  return `${salt.toString('hex')}:${hash.toString('hex')}`;
}

function checkPassword(password, stored) {
  if (!stored) return false;
  const [saltHex, hashHex] = stored.split(':');
  const hash = crypto.scryptSync(password, Buffer.from(saltHex, 'hex'), 32);
  return crypto.timingSafeEqual(hash, Buffer.from(hashHex, 'hex'));
}

function safeEqual(a, b) {
  const ba = Buffer.from(String(a));
  const bb = Buffer.from(String(b));
  return ba.length === bb.length && crypto.timingSafeEqual(ba, bb);
}

/** Enveloppe un handler async : les HttpError deviennent des réponses JSON. */
const route = (fn) => async (req, res) => {
  try {
    const result = await fn(req, res);
    if (!res.headersSent) res.json(result ?? { ok: true });
  } catch (e) {
    const status = e.status || 500;
    if (status === 500) console.error(e);
    if (!res.headersSent) res.status(status).json({ error: status === 500 ? 'Erreur interne du serveur.' : e.message });
  }
};

module.exports = { now, HttpError, text, number, int, randomToken, randomCode, hashPassword, checkPassword, safeEqual, route };
