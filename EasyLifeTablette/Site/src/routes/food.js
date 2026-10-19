// Application Commander : restaurants (type McBlue), menus, commandes et équipe en service
const express = require('express');
const { now, HttpError, text, number, int, route } = require('../util');
const { tx } = require('../db');

const STATUSES = ['nouvelle', 'en_preparation', 'prete', 'en_livraison', 'livree', 'annulee'];
const STATUS_LABELS = {
  nouvelle: 'Reçue',
  en_preparation: 'En préparation',
  prete: 'Prête',
  en_livraison: 'En livraison',
  livree: 'Terminée',
  annulee: 'Annulée',
};

module.exports = (ctx) => {
  const { db } = ctx;
  const router = express.Router();

  function restaurant(id) {
    const r = db.prepare('SELECT * FROM restaurants WHERE id = ?').get(Number(id));
    if (!r) throw new HttpError(404, 'Restaurant introuvable.');
    return r;
  }

  const isOwner = (req, r) => req.isAdmin || r.owner_steam_id === req.user.steam_id;
  const isStaff = (req, r) => isOwner(req, r)
    || !!db.prepare('SELECT 1 FROM restaurant_staff WHERE restaurant_id = ? AND steam_id = ?').get(r.id, req.user.steam_id);
  const onDutyCount = (r) => db.prepare('SELECT COUNT(*) n FROM restaurant_staff WHERE restaurant_id = ? AND on_duty = 1').get(r.id).n;

  function publicRestaurant(req, r) {
    return {
      id: r.id,
      name: r.name,
      slogan: r.slogan,
      color: r.color,
      emoji: r.emoji,
      prepMinutes: r.prep_minutes,
      deliveryFee: r.delivery_fee,
      open: onDutyCount(r) > 0,
      isStaff: isStaff(req, r),
      isOwner: isOwner(req, r),
    };
  }

  function requireStaff(req, r) {
    if (!isStaff(req, r)) throw new HttpError(403, 'Réservé à l\'équipe du restaurant.');
  }

  function requireOwner(req, r) {
    if (!isOwner(req, r)) throw new HttpError(403, 'Réservé au gérant.');
  }

  function orderView(o) {
    return {
      id: o.id,
      restaurantId: o.restaurant_id,
      restaurantName: o.restaurant_name,
      customer: ctx.displayName(o.steam_id),
      items: JSON.parse(o.items),
      total: o.total,
      mode: o.mode,
      address: o.address,
      note: o.note,
      status: o.status,
      statusLabel: STATUS_LABELS[o.status],
      createdAt: o.created_at,
      updatedAt: o.updated_at,
    };
  }

  // ---------- Clients ----------
  router.get('/commander', route((req) => ({
    canCreate: req.isAdmin,
    restaurants: db.prepare('SELECT * FROM restaurants ORDER BY name COLLATE NOCASE').all().map((r) => publicRestaurant(req, r)),
  })));

  router.get('/commander/restaurants/:id', route((req) => {
    const r = restaurant(req.params.id);
    const staff = isStaff(req, r);
    const items = db.prepare(`SELECT * FROM menu_items WHERE restaurant_id = ? ${staff ? '' : 'AND available = 1'}
                              ORDER BY featured DESC, category, name`).all(r.id);
    return {
      restaurant: publicRestaurant(req, r),
      items: items.map((i) => ({
        id: i.id,
        name: i.name,
        description: i.description,
        price: i.price,
        category: i.category,
        emoji: i.emoji,
        featured: i.featured === 1,
        available: i.available === 1,
      })),
    };
  }));

  router.post('/commander/restaurants/:id/commandes', route((req) => {
    const r = restaurant(req.params.id);
    if (onDutyCount(r) === 0) throw new HttpError(400, 'Le restaurant est fermé : aucun équipier en service.');
    const lines = Array.isArray(req.body?.items) ? req.body.items.slice(0, 50) : [];
    const items = [];
    let total = 0;
    for (const line of lines) {
      const qty = int(line, 'qty', { min: 1, max: 20 });
      const item = db.prepare('SELECT * FROM menu_items WHERE id = ? AND restaurant_id = ? AND available = 1').get(Number(line?.id), r.id);
      if (!item) throw new HttpError(400, 'Un article de votre panier n\'est plus disponible.');
      items.push({ name: item.name, emoji: item.emoji, qty, price: item.price });
      total += item.price * qty;
    }
    if (!items.length) throw new HttpError(400, 'Votre panier est vide.');
    const mode = req.body?.mode === 'livraison' ? 'livraison' : 'comptoir';
    const address = mode === 'livraison' ? text(req.body, 'address', { min: 3, max: 150 }) : '';
    if (mode === 'livraison') total += r.delivery_fee;
    const note = text(req.body, 'note', { max: 300, optional: true });
    const t = now();
    const id = Number(db.prepare(`INSERT INTO orders (restaurant_id, steam_id, items, total, mode, address, note, created_at, updated_at)
                                  VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)`)
      .run(r.id, req.user.steam_id, JSON.stringify(items), Math.round(total * 100) / 100, mode, address, note, t, t).lastInsertRowid);

    const staff = db.prepare('SELECT steam_id FROM restaurant_staff WHERE restaurant_id = ? AND on_duty = 1').all(r.id).map((s) => s.steam_id);
    for (const s of new Set(staff)) {
      ctx.notify(s, 'commander', `${r.name} : nouvelle commande #${id}`, `${ctx.displayName(req.user.steam_id)} — ${total.toFixed(2)} € (${mode === 'livraison' ? 'livraison' : 'comptoir'})`);
    }
    return { id };
  }));

  router.get('/commander/mes-commandes', route((req) => ({
    orders: db.prepare(`SELECT o.*, r.name AS restaurant_name FROM orders o JOIN restaurants r ON r.id = o.restaurant_id
                        WHERE o.steam_id = ? ORDER BY o.id DESC LIMIT 50`).all(req.user.steam_id).map(orderView),
  })));

  router.post('/commander/commandes/:id/annuler', route((req) => {
    const o = db.prepare('SELECT * FROM orders WHERE id = ? AND steam_id = ?').get(Number(req.params.id), req.user.steam_id);
    if (!o) throw new HttpError(404, 'Commande introuvable.');
    if (o.status !== 'nouvelle') throw new HttpError(400, 'La commande est déjà en préparation.');
    db.prepare("UPDATE orders SET status = 'annulee', updated_at = ? WHERE id = ?").run(now(), o.id);
    return { ok: true };
  }));

  // ---------- Équipe ----------
  router.get('/commander/restaurants/:id/gestion', route((req) => {
    const r = restaurant(req.params.id);
    requireStaff(req, r);
    const me = db.prepare('SELECT on_duty FROM restaurant_staff WHERE restaurant_id = ? AND steam_id = ?').get(r.id, req.user.steam_id);
    return {
      restaurant: { ...publicRestaurant(req, r), ownerName: ctx.displayName(r.owner_steam_id) },
      onDuty: me?.on_duty === 1,
      inTeam: !!me,
      staff: db.prepare('SELECT steam_id, on_duty FROM restaurant_staff WHERE restaurant_id = ?').all(r.id)
        .map((s) => ({ steamId: s.steam_id, name: ctx.displayName(s.steam_id), onDuty: s.on_duty === 1 })),
      orders: db.prepare(`SELECT o.*, r.name AS restaurant_name FROM orders o JOIN restaurants r ON r.id = o.restaurant_id
                          WHERE o.restaurant_id = ? AND (o.status NOT IN ('livree', 'annulee') OR o.updated_at > ?)
                          ORDER BY o.id DESC LIMIT 100`).all(r.id, now() - 24 * 3600e3).map(orderView),
    };
  }));

  router.post('/commander/restaurants/:id/service', route((req) => {
    const r = restaurant(req.params.id);
    requireStaff(req, r);
    const onDuty = req.body?.onDuty ? 1 : 0;
    // Le gérant peut prendre son service même s'il n'est pas listé dans l'équipe
    db.prepare('INSERT INTO restaurant_staff (restaurant_id, steam_id, on_duty) VALUES (?, ?, ?) ON CONFLICT DO UPDATE SET on_duty = excluded.on_duty')
      .run(r.id, req.user.steam_id, onDuty);
    return { onDuty: onDuty === 1 };
  }));

  router.put('/commander/commandes/:id/statut', route((req) => {
    const o = db.prepare('SELECT * FROM orders WHERE id = ?').get(Number(req.params.id));
    if (!o) throw new HttpError(404, 'Commande introuvable.');
    const r = restaurant(o.restaurant_id);
    requireStaff(req, r);
    const status = req.body?.status;
    if (!STATUSES.includes(status)) throw new HttpError(400, 'Statut invalide.');
    db.prepare('UPDATE orders SET status = ?, updated_at = ? WHERE id = ?').run(status, now(), o.id);
    ctx.notify(o.steam_id, 'commander', `${r.name} : commande #${o.id}`, `Votre commande est maintenant : ${STATUS_LABELS[status]}.`);
    return { ok: true };
  }));

  function readItem(body) {
    return {
      name: text(body, 'name', { max: 60 }),
      description: text(body, 'description', { max: 200, optional: true }),
      price: number(body, 'price', { min: 0, max: 100000 }),
      category: text(body, 'category', { max: 30, optional: true }) || 'Menus',
      emoji: text(body, 'emoji', { max: 8, optional: true }) || '🍔',
      featured: body?.featured ? 1 : 0,
      available: body?.available === false ? 0 : 1,
    };
  }

  router.post('/commander/restaurants/:id/menu', route((req) => {
    const r = restaurant(req.params.id);
    requireOwner(req, r);
    const f = readItem(req.body);
    const id = db.prepare(`INSERT INTO menu_items (restaurant_id, name, description, price, category, emoji, featured, available)
                           VALUES (?, ?, ?, ?, ?, ?, ?, ?)`)
      .run(r.id, f.name, f.description, f.price, f.category, f.emoji, f.featured, f.available).lastInsertRowid;
    return { id: Number(id) };
  }));

  router.put('/commander/menu/:id', route((req) => {
    const item = db.prepare('SELECT * FROM menu_items WHERE id = ?').get(Number(req.params.id));
    if (!item) throw new HttpError(404, 'Article introuvable.');
    const r = restaurant(item.restaurant_id);
    // L'équipe peut marquer un article en rupture ; le reste est réservé au gérant
    if (Object.keys(req.body || {}).length === 1 && 'available' in req.body) {
      requireStaff(req, r);
      db.prepare('UPDATE menu_items SET available = ? WHERE id = ?').run(req.body.available ? 1 : 0, item.id);
      return { ok: true };
    }
    requireOwner(req, r);
    const f = readItem(req.body);
    db.prepare('UPDATE menu_items SET name = ?, description = ?, price = ?, category = ?, emoji = ?, featured = ?, available = ? WHERE id = ?')
      .run(f.name, f.description, f.price, f.category, f.emoji, f.featured, f.available, item.id);
    return { ok: true };
  }));

  router.delete('/commander/menu/:id', route((req) => {
    const item = db.prepare('SELECT * FROM menu_items WHERE id = ?').get(Number(req.params.id));
    if (!item) throw new HttpError(404, 'Article introuvable.');
    requireOwner(req, restaurant(item.restaurant_id));
    db.prepare('DELETE FROM menu_items WHERE id = ?').run(item.id);
    return { ok: true };
  }));

  router.put('/commander/restaurants/:id', route((req) => {
    const r = restaurant(req.params.id);
    requireOwner(req, r);
    const color = typeof req.body?.color === 'string' && /^#[0-9a-f]{6}$/i.test(req.body.color) ? req.body.color : r.color;
    db.prepare('UPDATE restaurants SET name = ?, slogan = ?, color = ?, emoji = ?, prep_minutes = ?, delivery_fee = ? WHERE id = ?').run(
      text(req.body, 'name', { max: 40 }),
      text(req.body, 'slogan', { max: 150, optional: true }),
      color,
      text(req.body, 'emoji', { max: 8, optional: true }) || r.emoji,
      int(req.body, 'prepMinutes', { min: 1, max: 240 }),
      number(req.body, 'deliveryFee', { min: 0, max: 10000 }),
      r.id,
    );
    return { ok: true };
  }));

  router.post('/commander/restaurants/:id/equipe', route((req) => {
    const r = restaurant(req.params.id);
    requireOwner(req, r);
    const steamId = text(req.body, 'steamId', { min: 17, max: 17 });
    if (!db.prepare('SELECT 1 FROM users WHERE steam_id = ?').get(steamId)) throw new HttpError(404, 'Joueur inconnu de la tablette.');
    db.prepare('INSERT OR IGNORE INTO restaurant_staff (restaurant_id, steam_id) VALUES (?, ?)').run(r.id, steamId);
    ctx.notify(steamId, 'commander', r.name, `Vous avez été ajouté à l'équipe de ${r.name}.`);
    return { ok: true };
  }));

  router.delete('/commander/restaurants/:id/equipe/:steamId', route((req) => {
    const r = restaurant(req.params.id);
    requireOwner(req, r);
    db.prepare('DELETE FROM restaurant_staff WHERE restaurant_id = ? AND steam_id = ?').run(r.id, req.params.steamId);
    return { ok: true };
  }));

  // ---------- Staff du serveur ----------
  router.post('/commander/restaurants', route((req) => {
    if (!req.isAdmin) throw new HttpError(403, 'Réservé au staff.');
    const owner = text(req.body, 'ownerSteamId', { min: 17, max: 17 });
    if (!db.prepare('SELECT 1 FROM users WHERE steam_id = ?').get(owner)) throw new HttpError(404, 'Gérant inconnu de la tablette.');
    const name = text(req.body, 'name', { max: 40 });
    return tx(db, () => {
      const id = Number(db.prepare('INSERT INTO restaurants (name, slogan, owner_steam_id, created_at) VALUES (?, ?, ?, ?)')
        .run(name, text(req.body, 'slogan', { max: 150, optional: true }), owner, now()).lastInsertRowid);
      db.prepare('INSERT INTO restaurant_staff (restaurant_id, steam_id) VALUES (?, ?)').run(id, owner);
      return { id };
    });
  }));

  router.put('/commander/restaurants/:id/gerant', route((req) => {
    if (!req.isAdmin) throw new HttpError(403, 'Réservé au staff.');
    const r = restaurant(req.params.id);
    const owner = text(req.body, 'ownerSteamId', { min: 17, max: 17 });
    if (!db.prepare('SELECT 1 FROM users WHERE steam_id = ?').get(owner)) throw new HttpError(404, 'Gérant inconnu de la tablette.');
    db.prepare('UPDATE restaurants SET owner_steam_id = ? WHERE id = ?').run(owner, r.id);
    db.prepare('INSERT OR IGNORE INTO restaurant_staff (restaurant_id, steam_id) VALUES (?, ?)').run(r.id, owner);
    return { ok: true };
  }));

  router.delete('/commander/restaurants/:id', route((req) => {
    if (!req.isAdmin) throw new HttpError(403, 'Réservé au staff.');
    db.prepare('DELETE FROM restaurants WHERE id = ?').run(restaurant(req.params.id).id);
    return { ok: true };
  }));

  return router;
};
