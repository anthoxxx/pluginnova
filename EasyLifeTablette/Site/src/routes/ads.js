// Application Le Bon Plan : petites annonces, favoris, messagerie acheteur/vendeur
const express = require('express');
const { now, HttpError, text, number, route } = require('../util');
const { tx } = require('../db');

const CATEGORIES = ['Véhicules', 'Immobilier', 'Emploi', 'Services', 'Maison & jardin', 'Mode', 'Multimédia', 'Loisirs', 'Matériel professionnel', 'Autres'];
const DURATION = 30 * 24 * 3600e3;

module.exports = (ctx) => {
  const { db } = ctx;
  const router = express.Router();

  function ad(id) {
    const a = db.prepare('SELECT * FROM ads WHERE id = ?').get(Number(id));
    if (!a) throw new HttpError(404, 'Annonce introuvable.');
    return a;
  }

  function summary(a, me) {
    return {
      id: a.id,
      title: a.title,
      price: a.price,
      category: a.category,
      imageUrl: a.image_url,
      status: a.status,
      createdAt: a.created_at,
      mine: a.steam_id === me,
    };
  }

  function readFields(body) {
    const category = body?.category;
    if (!CATEGORIES.includes(category)) throw new HttpError(400, 'Catégorie invalide.');
    const imageUrl = text(body, 'imageUrl', { max: 400, optional: true });
    if (imageUrl && !/^https:\/\/[^\s"'<>]+$/i.test(imageUrl)) throw new HttpError(400, 'Le lien de la photo doit commencer par https://');
    return {
      title: text(body, 'title', { min: 3, max: 80 }),
      description: text(body, 'description', { min: 5, max: 3000 }),
      price: number(body, 'price', { min: 0, max: 1e9, optional: true }),
      category,
      imageUrl,
    };
  }

  router.get('/bonplan', route((req) => {
    const me = req.user.steam_id;
    const where = [];
    const params = [];
    if (req.query.vue === 'miennes') {
      where.push('a.steam_id = ?');
      params.push(me);
    } else if (req.query.vue === 'favoris') {
      where.push("a.id IN (SELECT ad_id FROM ad_favorites WHERE steam_id = ?) AND a.status = 'active'");
      params.push(me);
    } else {
      where.push("a.status = 'active'");
    }
    if (CATEGORIES.includes(req.query.categorie)) {
      where.push('a.category = ?');
      params.push(req.query.categorie);
    }
    if (typeof req.query.q === 'string' && req.query.q.trim()) {
      where.push('(a.title LIKE ? OR a.description LIKE ?)');
      const q = `%${req.query.q.trim().slice(0, 50)}%`;
      params.push(q, q);
    }
    const counts = Object.fromEntries(db.prepare("SELECT category, COUNT(*) n FROM ads WHERE status = 'active' GROUP BY category").all()
      .map((r) => [r.category, r.n]));
    return {
      categories: CATEGORIES.map((name) => ({ name, count: counts[name] || 0 })),
      ads: db.prepare(`SELECT a.* FROM ads a WHERE ${where.join(' AND ')} ORDER BY a.id DESC LIMIT 200`).all(...params).map((a) => summary(a, me)),
    };
  }));

  router.get('/bonplan/annonces/:id', route((req) => {
    const me = req.user.steam_id;
    let a = ad(req.params.id);
    if (a.status !== 'active' && a.steam_id !== me && !req.isAdmin) throw new HttpError(404, 'Cette annonce n\'est plus en ligne.');
    if (a.steam_id !== me) {
      db.prepare('UPDATE ads SET views = views + 1 WHERE id = ?').run(a.id);
      a = { ...a, views: a.views + 1 };
    }
    const seller = db.prepare('SELECT in_game FROM users WHERE steam_id = ?').get(a.steam_id);
    const thread = db.prepare('SELECT id FROM ad_threads WHERE ad_id = ? AND buyer_steam_id = ?').get(a.id, me);
    return {
      ad: {
        ...summary(a, me),
        description: a.description,
        views: a.views,
        expiresAt: a.expires_at,
        seller: {
          name: ctx.displayName(a.steam_id),
          inGame: seller?.in_game === 1,
          activeAds: db.prepare("SELECT COUNT(*) n FROM ads WHERE steam_id = ? AND status = 'active'").get(a.steam_id).n,
        },
        favorite: !!db.prepare('SELECT 1 FROM ad_favorites WHERE steam_id = ? AND ad_id = ?').get(me, a.id),
        threadId: thread?.id ?? null,
        canModerate: req.isAdmin,
      },
    };
  }));

  router.post('/bonplan/annonces', route((req) => {
    const f = readFields(req.body);
    const active = db.prepare("SELECT COUNT(*) n FROM ads WHERE steam_id = ? AND status = 'active'").get(req.user.steam_id).n;
    if (active >= 20) throw new HttpError(400, 'Vous avez déjà 20 annonces en ligne.');
    const t = now();
    const id = db.prepare(`INSERT INTO ads (steam_id, title, description, price, category, image_url, created_at, expires_at)
                           VALUES (?, ?, ?, ?, ?, ?, ?, ?)`)
      .run(req.user.steam_id, f.title, f.description, f.price, f.category, f.imageUrl, t, t + DURATION).lastInsertRowid;
    return { id: Number(id) };
  }));

  router.put('/bonplan/annonces/:id', route((req) => {
    const a = ad(req.params.id);
    if (a.steam_id !== req.user.steam_id) throw new HttpError(403, 'Ce n\'est pas votre annonce.');
    const f = readFields(req.body);
    db.prepare('UPDATE ads SET title = ?, description = ?, price = ?, category = ?, image_url = ? WHERE id = ?')
      .run(f.title, f.description, f.price, f.category, f.imageUrl, a.id);
    return { ok: true };
  }));

  // Clôturer (vendu) ou remettre en ligne pour 30 jours
  router.post('/bonplan/annonces/:id/statut', route((req) => {
    const a = ad(req.params.id);
    if (a.steam_id !== req.user.steam_id) throw new HttpError(403, 'Ce n\'est pas votre annonce.');
    if (req.body?.status === 'active') {
      db.prepare("UPDATE ads SET status = 'active', expires_at = ? WHERE id = ?").run(now() + DURATION, a.id);
    } else {
      db.prepare("UPDATE ads SET status = 'closed' WHERE id = ?").run(a.id);
    }
    return { ok: true };
  }));

  router.delete('/bonplan/annonces/:id', route((req) => {
    const a = ad(req.params.id);
    if (a.steam_id !== req.user.steam_id && !req.isAdmin) throw new HttpError(403, 'Ce n\'est pas votre annonce.');
    db.prepare('DELETE FROM ads WHERE id = ?').run(a.id);
    if (a.steam_id !== req.user.steam_id) {
      ctx.notify(a.steam_id, 'bonplan', 'Annonce retirée', `Votre annonce « ${a.title} » a été retirée par la modération.`);
    }
    return { ok: true };
  }));

  router.post('/bonplan/annonces/:id/favori', route((req) => {
    const a = ad(req.params.id);
    const me = req.user.steam_id;
    const exists = db.prepare('SELECT 1 FROM ad_favorites WHERE steam_id = ? AND ad_id = ?').get(me, a.id);
    if (exists) db.prepare('DELETE FROM ad_favorites WHERE steam_id = ? AND ad_id = ?').run(me, a.id);
    else db.prepare('INSERT INTO ad_favorites (steam_id, ad_id) VALUES (?, ?)').run(me, a.id);
    return { favorite: !exists };
  }));

  router.post('/bonplan/annonces/:id/signaler', route((req) => {
    const a = ad(req.params.id);
    const reason = text(req.body, 'reason', { min: 3, max: 300 });
    db.prepare('INSERT INTO ad_reports (ad_id, steam_id, reason, created_at) VALUES (?, ?, ?, ?)').run(a.id, req.user.steam_id, reason, now());
    return { ok: true };
  }));

  // ---------- Messagerie ----------
  function thread(req, id) {
    const t = db.prepare('SELECT t.*, a.steam_id AS seller, a.title FROM ad_threads t JOIN ads a ON a.id = t.ad_id WHERE t.id = ?').get(Number(id));
    if (!t || (t.buyer_steam_id !== req.user.steam_id && t.seller !== req.user.steam_id)) throw new HttpError(404, 'Conversation introuvable.');
    return t;
  }

  function post(req, t, body) {
    const time = now();
    db.prepare('INSERT INTO ad_messages (thread_id, sender_steam_id, body, created_at) VALUES (?, ?, ?, ?)').run(t.id, req.user.steam_id, body, time);
    db.prepare('UPDATE ad_threads SET updated_at = ? WHERE id = ?').run(time, t.id);
    const other = req.user.steam_id === t.seller ? t.buyer_steam_id : t.seller;
    ctx.notify(other, 'bonplan', `Le Bon Plan : ${ctx.displayName(req.user.steam_id)}`, `${t.title} — ${body.slice(0, 100)}`);
  }

  router.post('/bonplan/annonces/:id/messages', route((req) => {
    const a = ad(req.params.id);
    if (a.steam_id === req.user.steam_id) throw new HttpError(400, 'C\'est votre propre annonce.');
    if (a.status !== 'active') throw new HttpError(400, 'Cette annonce n\'est plus en ligne.');
    const body = text(req.body, 'body', { max: 1000 });
    return tx(db, () => {
      db.prepare('INSERT OR IGNORE INTO ad_threads (ad_id, buyer_steam_id, updated_at) VALUES (?, ?, ?)').run(a.id, req.user.steam_id, now());
      const id = db.prepare('SELECT id FROM ad_threads WHERE ad_id = ? AND buyer_steam_id = ?').get(a.id, req.user.steam_id).id;
      post(req, thread(req, id), body);
      return { threadId: id };
    });
  }));

  router.get('/bonplan/conversations', route((req) => {
    const me = req.user.steam_id;
    return {
      threads: db.prepare(`SELECT t.id, t.updated_at, t.buyer_steam_id, a.steam_id AS seller, a.title, a.id AS ad_id,
                             (SELECT body FROM ad_messages m WHERE m.thread_id = t.id ORDER BY m.id DESC LIMIT 1) AS last
                           FROM ad_threads t JOIN ads a ON a.id = t.ad_id
                           WHERE t.buyer_steam_id = ? OR a.steam_id = ? ORDER BY t.updated_at DESC LIMIT 100`).all(me, me)
        .map((t) => ({
          id: t.id,
          adId: t.ad_id,
          adTitle: t.title,
          with: ctx.displayName(t.seller === me ? t.buyer_steam_id : t.seller),
          role: t.seller === me ? 'vendeur' : 'acheteur',
          last: t.last,
          updatedAt: t.updated_at,
        })),
    };
  }));

  router.get('/bonplan/conversations/:id', route((req) => {
    const t = thread(req, req.params.id);
    const me = req.user.steam_id;
    return {
      thread: { id: t.id, adId: t.ad_id, adTitle: t.title, with: ctx.displayName(t.seller === me ? t.buyer_steam_id : t.seller) },
      messages: db.prepare('SELECT sender_steam_id, body, created_at FROM ad_messages WHERE thread_id = ? ORDER BY id').all(t.id)
        .map((m) => ({ mine: m.sender_steam_id === me, body: m.body, createdAt: m.created_at })),
    };
  }));

  router.post('/bonplan/conversations/:id', route((req) => {
    const t = thread(req, req.params.id);
    post(req, t, text(req.body, 'body', { max: 1000 }));
    return { ok: true };
  }));

  return router;
};
