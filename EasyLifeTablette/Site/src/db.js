// Base SQLite de la tablette (module intégré à Node.js 22, aucune dépendance native)
const path = require('node:path');
const fs = require('node:fs');
const { DatabaseSync } = require('node:sqlite');

function open(file) {
  fs.mkdirSync(path.dirname(file), { recursive: true });
  const db = new DatabaseSync(file);
  db.exec('PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON;');
  db.exec(`
    CREATE TABLE IF NOT EXISTS users (
      steam_id TEXT PRIMARY KEY,
      pseudo TEXT NOT NULL DEFAULT '',
      avatar TEXT NOT NULL DEFAULT '',
      rp_name TEXT NOT NULL DEFAULT '',
      job TEXT NOT NULL DEFAULT '',
      money REAL,
      bank REAL,
      is_admin INTEGER NOT NULL DEFAULT 0,
      is_journalist INTEGER NOT NULL DEFAULT 0,
      in_game INTEGER NOT NULL DEFAULT 0,
      last_sync INTEGER,
      pin_hash TEXT,
      settings TEXT NOT NULL DEFAULT '{}',
      created_at INTEGER NOT NULL
    );
    CREATE TABLE IF NOT EXISTS sessions (
      token TEXT PRIMARY KEY,
      steam_id TEXT NOT NULL REFERENCES users(steam_id) ON DELETE CASCADE,
      via TEXT NOT NULL,
      locked INTEGER NOT NULL DEFAULT 0,
      created_at INTEGER NOT NULL,
      last_seen INTEGER NOT NULL
    );
    CREATE TABLE IF NOT EXISTS login_codes (
      code TEXT PRIMARY KEY,
      steam_id TEXT NOT NULL,
      expires_at INTEGER NOT NULL
    );
    CREATE TABLE IF NOT EXISTS notifications (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      steam_id TEXT NOT NULL,
      app TEXT NOT NULL,
      title TEXT NOT NULL,
      body TEXT NOT NULL,
      read INTEGER NOT NULL DEFAULT 0,
      pushed INTEGER NOT NULL DEFAULT 0,
      created_at INTEGER NOT NULL
    );
    CREATE INDEX IF NOT EXISTS idx_notif_user ON notifications(steam_id, created_at);
    CREATE TABLE IF NOT EXISTS bank_history (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      steam_id TEXT NOT NULL,
      money REAL NOT NULL,
      bank REAL NOT NULL,
      at INTEGER NOT NULL
    );
    CREATE TABLE IF NOT EXISTS mail_accounts (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      address TEXT NOT NULL UNIQUE,
      display_name TEXT NOT NULL,
      password_hash TEXT NOT NULL,
      owner_steam_id TEXT NOT NULL,
      created_at INTEGER NOT NULL
    );
    CREATE TABLE IF NOT EXISTS mail_access (
      account_id INTEGER NOT NULL REFERENCES mail_accounts(id) ON DELETE CASCADE,
      steam_id TEXT NOT NULL,
      PRIMARY KEY (account_id, steam_id)
    );
    CREATE TABLE IF NOT EXISTS mails (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      account_id INTEGER NOT NULL REFERENCES mail_accounts(id) ON DELETE CASCADE,
      folder TEXT NOT NULL,
      from_addr TEXT NOT NULL,
      from_name TEXT NOT NULL,
      to_addrs TEXT NOT NULL,
      subject TEXT NOT NULL,
      body TEXT NOT NULL,
      read INTEGER NOT NULL DEFAULT 0,
      created_at INTEGER NOT NULL
    );
    CREATE TABLE IF NOT EXISTS directory (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      name TEXT NOT NULL,
      category TEXT NOT NULL DEFAULT 'Services',
      phone TEXT NOT NULL DEFAULT '',
      description TEXT NOT NULL DEFAULT '',
      recruiting INTEGER NOT NULL DEFAULT 0,
      game_biz_id INTEGER UNIQUE,
      owner_steam_id TEXT,
      created_at INTEGER NOT NULL
    );
    CREATE TABLE IF NOT EXISTS news (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      title TEXT NOT NULL,
      body TEXT NOT NULL,
      category TEXT NOT NULL,
      author_steam_id TEXT NOT NULL,
      author_name TEXT NOT NULL,
      pinned INTEGER NOT NULL DEFAULT 0,
      created_at INTEGER NOT NULL
    );
    CREATE TABLE IF NOT EXISTS ads (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      steam_id TEXT NOT NULL,
      title TEXT NOT NULL,
      description TEXT NOT NULL,
      price REAL,
      category TEXT NOT NULL,
      image_url TEXT NOT NULL DEFAULT '',
      views INTEGER NOT NULL DEFAULT 0,
      status TEXT NOT NULL DEFAULT 'active',
      created_at INTEGER NOT NULL,
      expires_at INTEGER NOT NULL
    );
    CREATE TABLE IF NOT EXISTS ad_favorites (
      steam_id TEXT NOT NULL,
      ad_id INTEGER NOT NULL REFERENCES ads(id) ON DELETE CASCADE,
      PRIMARY KEY (steam_id, ad_id)
    );
    CREATE TABLE IF NOT EXISTS ad_threads (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      ad_id INTEGER NOT NULL REFERENCES ads(id) ON DELETE CASCADE,
      buyer_steam_id TEXT NOT NULL,
      updated_at INTEGER NOT NULL,
      UNIQUE (ad_id, buyer_steam_id)
    );
    CREATE TABLE IF NOT EXISTS ad_messages (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      thread_id INTEGER NOT NULL REFERENCES ad_threads(id) ON DELETE CASCADE,
      sender_steam_id TEXT NOT NULL,
      body TEXT NOT NULL,
      created_at INTEGER NOT NULL
    );
    CREATE TABLE IF NOT EXISTS ad_reports (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      ad_id INTEGER NOT NULL REFERENCES ads(id) ON DELETE CASCADE,
      steam_id TEXT NOT NULL,
      reason TEXT NOT NULL,
      created_at INTEGER NOT NULL
    );
    CREATE TABLE IF NOT EXISTS restaurants (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      name TEXT NOT NULL,
      slogan TEXT NOT NULL DEFAULT '',
      color TEXT NOT NULL DEFAULT '#1d4ed8',
      emoji TEXT NOT NULL DEFAULT '🍔',
      prep_minutes INTEGER NOT NULL DEFAULT 10,
      delivery_fee REAL NOT NULL DEFAULT 0,
      owner_steam_id TEXT NOT NULL,
      created_at INTEGER NOT NULL
    );
    CREATE TABLE IF NOT EXISTS restaurant_staff (
      restaurant_id INTEGER NOT NULL REFERENCES restaurants(id) ON DELETE CASCADE,
      steam_id TEXT NOT NULL,
      on_duty INTEGER NOT NULL DEFAULT 0,
      PRIMARY KEY (restaurant_id, steam_id)
    );
    CREATE TABLE IF NOT EXISTS menu_items (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      restaurant_id INTEGER NOT NULL REFERENCES restaurants(id) ON DELETE CASCADE,
      name TEXT NOT NULL,
      description TEXT NOT NULL DEFAULT '',
      price REAL NOT NULL,
      category TEXT NOT NULL,
      emoji TEXT NOT NULL DEFAULT '🍔',
      featured INTEGER NOT NULL DEFAULT 0,
      available INTEGER NOT NULL DEFAULT 1
    );
    CREATE TABLE IF NOT EXISTS orders (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      restaurant_id INTEGER NOT NULL REFERENCES restaurants(id) ON DELETE CASCADE,
      steam_id TEXT NOT NULL,
      items TEXT NOT NULL,
      total REAL NOT NULL,
      mode TEXT NOT NULL,
      address TEXT NOT NULL DEFAULT '',
      note TEXT NOT NULL DEFAULT '',
      status TEXT NOT NULL DEFAULT 'nouvelle',
      created_at INTEGER NOT NULL,
      updated_at INTEGER NOT NULL
    );
    CREATE TABLE IF NOT EXISTS notes (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      steam_id TEXT NOT NULL,
      title TEXT NOT NULL,
      body TEXT NOT NULL,
      updated_at INTEGER NOT NULL
    );
  `);
  return db;
}

/** Exécute fn dans une transaction (rollback si exception). */
function tx(db, fn) {
  db.exec('BEGIN');
  try {
    const r = fn();
    db.exec('COMMIT');
    return r;
  } catch (e) {
    db.exec('ROLLBACK');
    throw e;
  }
}

module.exports = { open, tx };
