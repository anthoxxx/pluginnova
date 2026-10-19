// Application Le Bon Plan : petites annonces de la ville
import { api, h, clear, icon, euro, ago, dateTimeFr, initials, attempt, modal, form, confirmBox, empty, loading, sideLayout } from '../core.js';

const CAT_ICONS = {
  'Véhicules': 'truck', 'Immobilier': 'store', 'Emploi': 'users', 'Services': 'settings', 'Maison & jardin': 'store',
  'Mode': 'bag', 'Multimédia': 'tablet', 'Loisirs': 'gamepad', 'Matériel professionnel': 'key', 'Autres': 'tag',
};
const ORANGE = '#ff6b1a';

const price = (p) => (p === null || p === undefined ? 'Prix sur demande' : euro(p));

export default {
  id: 'bonplan',
  name: 'Le Bon Plan',
  icon: 'tag',
  color: 'linear-gradient(180deg,#ff8a3d,#f25c05)',
  keywords: 'annonce vente achat leboncoin',
  open(root, ctx, param) {
    let categories = [];
    let view = { kind: 'all' };
    let layout = null;

    layout = sideLayout(root, {
      appName: 'Le Bon Plan',
      subtitle: 'Petites annonces de la ville',
      iconName: 'tag',
      color: ORANGE,
      headerAction: h('button', { class: 'btn btn-orange block', onclick: () => editAd() }, icon('plus', 14), 'Déposer une annonce'),
      sections: () => [
        { items: [{ id: 'all', label: 'Toutes les annonces', icon: 'tag' }] },
        { title: 'Mon espace', items: [
          { id: 'messages', label: 'Messages', icon: 'message' },
          { id: 'mine', label: 'Mes annonces', icon: 'user' },
          { id: 'favs', label: 'Favoris', icon: 'heart' },
        ] },
        { title: 'Catégories', items: categories.map((c) => ({ id: `cat:${c.name}`, label: c.name, icon: CAT_ICONS[c.name] || 'tag', badge: c.count || null })) },
      ],
      onSelect: (id, extra) => {
        if (id === 'messages') return extra ? conversation(extra) : threads();
        if (id === 'ad') return adDetail(extra);
        if (id.startsWith('cat:')) view = { kind: 'cat', category: id.slice(4) };
        else view = { kind: id };
        return listAds();
      },
    });

    const search = h('input', { class: 'search-input', placeholder: 'Rechercher dans les annonces…' });
    let searchTimer;
    search.addEventListener('input', () => { clearTimeout(searchTimer); searchTimer = setTimeout(listAds, 300); });

    async function listAds() {
      const titles = { all: 'Toutes les annonces', mine: 'Mes annonces', favs: 'Favoris', cat: view.category };
      layout.setTitle(titles[view.kind]);
      const params = new URLSearchParams();
      if (view.kind === 'mine') params.set('vue', 'miennes');
      if (view.kind === 'favs') params.set('vue', 'favoris');
      if (view.kind === 'cat') params.set('categorie', view.category);
      if (search.value.trim()) params.set('q', search.value.trim());
      const grid = h('div', { class: 'ad-grid' }, loading());
      clear(layout.content, search, grid);
      const data = await attempt(() => api(`bonplan?${params}`));
      if (!data) return;
      categories = data.categories;
      layout.refreshNav();
      if (!data.ads.length) {
        const msg = { mine: 'Vous n\'avez déposé aucune annonce.', favs: 'Ajoutez des annonces à vos favoris avec le ♥.' }[view.kind];
        return clear(grid, empty('tag', 'Aucune annonce', msg || 'Soyez le premier à déposer une annonce !'));
      }
      clear(grid, data.ads.map((a) => h('button', { class: 'ad-card', onclick: () => layout.select('ad', a.id) },
        h('div', { class: 'ad-photo' }, a.imageUrl ? h('img', { src: a.imageUrl, alt: '', loading: 'lazy', referrerpolicy: 'no-referrer' }) : icon(CAT_ICONS[a.category] || 'tag', 30),
          a.status !== 'active' ? h('span', { class: 'ad-status' }, a.status === 'closed' ? 'Clôturée' : 'Expirée') : null),
        h('div', { class: 'ad-info' },
          h('strong', { class: 'ellipsis2' }, a.title),
          h('span', { class: 'ad-price' }, price(a.price)),
          h('small', { class: 'muted' }, `${a.category} · ${ago(a.createdAt)}`)))));
    }

    async function adDetail(id) {
      clear(layout.content, loading());
      const data = await attempt(() => api(`bonplan/annonces/${id}`));
      if (!data) return;
      const a = data.ad;
      layout.setTitle(a.title);
      layout.setActions(h('button', { class: 'btn', onclick: () => layout.select(view.kind === 'cat' ? `cat:${view.category}` : view.kind) }, icon('back', 14), 'Retour'));

      const favBtn = h('button', { class: 'btn block', onclick: async () => {
        const r = await attempt(() => api(`bonplan/annonces/${a.id}/favori`, { method: 'POST', body: {} }));
        if (r) { a.favorite = r.favorite; drawFav(); }
      } });
      const drawFav = () => clear(favBtn, icon('heart', 14), a.favorite ? 'Retirer des favoris' : 'Ajouter aux favoris');
      drawFav();

      const ownerActions = a.mine ? h('div', { class: 'stack' },
        h('button', { class: 'btn btn-orange block', onclick: () => editAd(a) }, icon('pencil', 14), 'Modifier'),
        h('button', { class: 'btn block', onclick: async () => {
          const status = a.status === 'active' ? 'closed' : 'active';
          if (await attempt(() => api(`bonplan/annonces/${a.id}/statut`, { method: 'POST', body: { status } }), status === 'active' ? 'Annonce remise en ligne pour 30 jours.' : 'Annonce clôturée.')) adDetail(a.id);
        } }, a.status === 'active' ? 'Clôturer (vendu)' : 'Remettre en ligne'),
        h('button', { class: 'btn btn-danger block', onclick: () => removeAd(a) }, icon('trash', 14), 'Supprimer')) : h('div', { class: 'stack' },
        h('button', { class: 'btn btn-orange block', onclick: () => (a.threadId ? layout.select('messages', a.threadId) : firstMessage(a)) }, icon('message', 14), 'Envoyer un message'),
        favBtn);

      clear(layout.content, h('div', { class: 'ad-detail' },
        h('div', { class: 'stack' },
          h('div', { class: 'ad-photo big' }, a.imageUrl ? h('img', { src: a.imageUrl, alt: '', referrerpolicy: 'no-referrer' }) : h('div', { class: 'stack center' }, icon('image', 34), h('small', { class: 'muted' }, 'Aucune photo'))),
          h('div', { class: 'card' }, h('h4', {}, 'Description'), h('p', { class: 'pre' }, a.description)),
          h('div', { class: 'card' }, h('h4', {}, 'Informations'),
            h('div', { class: 'info-grid' },
              info('Catégorie', a.category), info('Publiée le', dateTimeFr(a.createdAt)),
              info('Expire le', new Date(a.expiresAt).toLocaleDateString('fr-FR', { weekday: 'long', day: 'numeric', month: 'long', year: 'numeric' })),
              info('Vues', a.views)))),
        h('div', { class: 'stack' },
          h('div', { class: 'card' },
            h('small', { class: 'ad-cat' }, icon(CAT_ICONS[a.category] || 'tag', 12), a.category.toUpperCase()),
            h('h3', {}, a.title),
            h('div', { class: 'ad-price big' }, price(a.price)),
            h('small', { class: 'muted' }, icon('clock', 11), ' ', ago(a.createdAt)),
            a.status !== 'active' ? h('div', { class: 'alert' }, 'Cette annonce n\'est plus en ligne.') : null,
            ownerActions),
          h('div', { class: 'card' }, h('h4', {}, 'Vendeur'),
            h('div', { class: 'row gap' }, h('div', { class: 'avatar', style: { background: ORANGE } }, initials(a.seller.name)),
              h('div', {}, h('strong', {}, a.seller.name), h('small', { class: 'muted block' }, `${a.seller.activeAds} annonce${a.seller.activeAds > 1 ? 's' : ''} en ligne`)),
              h('span', { class: `pill push ${a.seller.inGame ? 'green' : ''}` }, h('span', { class: `sb-dot ${a.seller.inGame ? 'on' : ''}` }), a.seller.inGame ? 'En ville' : 'Hors ligne')),
            h('p', { class: 'muted small' }, 'Le vendeur préfère être contacté par la messagerie du Bon Plan.')),
          h('div', { class: 'card safe' }, icon('shield', 16), h('small', {}, 'Ne payez jamais d\'avance sans avoir vu le bien. Préférez la remise en main propre et un virement bancaire traçable.')),
          !a.mine ? h('button', { class: 'link center', onclick: () => report(a) }, icon('flag', 12), ' Signaler l\'annonce') : null,
          a.canModerate && !a.mine ? h('button', { class: 'btn btn-danger block', onclick: () => removeAd(a) }, 'Retirer (modération)') : null)));
    }

    const info = (label, value) => h('div', {}, h('small', { class: 'muted upper' }, label), h('div', {}, String(value)));

    async function removeAd(a) {
      if (!await confirmBox(`Supprimer l'annonce « ${a.title} » ?`, 'Supprimer')) return;
      if (await attempt(() => api(`bonplan/annonces/${a.id}`, { method: 'DELETE', body: {} }), 'Annonce supprimée.')) layout.select('mine');
    }

    function editAd(a) {
      const cats = categories.length ? categories.map((c) => c.name) : Object.keys(CAT_ICONS);
      modal(a ? 'Modifier l\'annonce' : 'Déposer une annonce', (close) => form([
        { name: 'title', label: 'Titre', value: a?.title, required: true, max: 80 },
        { name: 'category', label: 'Catégorie', type: 'select', value: a?.category || cats[0], options: cats },
        { name: 'price', label: 'Prix en € (laisser vide = « Prix sur demande »)', type: 'number', step: '0.01', min: 0, value: a?.price ?? '' },
        { name: 'description', label: 'Description', type: 'textarea', rows: 7, value: a?.description, required: true, max: 3000 },
        { name: 'imageUrl', label: 'Lien d\'une photo (facultatif, https://…)', value: a?.imageUrl, max: 400 },
      ], a ? 'Enregistrer' : 'Publier l\'annonce', async (v) => {
        const body = { ...v, price: v.price === '' ? null : Number(v.price) };
        const r = a
          ? await attempt(() => api(`bonplan/annonces/${a.id}`, { method: 'PUT', body }), 'Annonce mise à jour.')
          : await attempt(() => api('bonplan/annonces', { method: 'POST', body }), 'Annonce publiée pour 30 jours !');
        if (r) { close(); layout.select('ad', a ? a.id : r.id); }
      }), { wide: true });
    }

    function firstMessage(a) {
      modal(`Contacter ${a.seller.name}`, (close) => form([
        { name: 'body', label: `À propos de « ${a.title} »`, type: 'textarea', rows: 5, value: 'Bonjour, votre annonce est-elle toujours disponible ?', required: true, max: 1000 },
      ], 'Envoyer', async (v) => {
        const r = await attempt(() => api(`bonplan/annonces/${a.id}/messages`, { method: 'POST', body: v }), 'Message envoyé.');
        if (r) { close(); layout.select('messages', r.threadId); }
      }));
    }

    function report(a) {
      modal('Signaler l\'annonce', (close) => form([
        { name: 'reason', label: 'Pourquoi signalez-vous cette annonce ?', type: 'textarea', rows: 4, required: true, max: 300 },
      ], 'Signaler', async (v) => {
        if (await attempt(() => api(`bonplan/annonces/${a.id}/signaler`, { method: 'POST', body: v }), 'Merci, l\'annonce a été signalée au staff.')) close();
      }));
    }

    async function threads() {
      layout.setTitle('Messages');
      clear(layout.content, loading());
      const data = await attempt(() => api('bonplan/conversations'));
      if (!data) return;
      if (!data.threads.length) return clear(layout.content, empty('message', 'Aucune conversation', 'Contactez un vendeur depuis une annonce.'));
      clear(layout.content, h('div', { class: 'list' }, data.threads.map((t) => h('button', { class: 'list-row', onclick: () => layout.select('messages', t.id) },
        h('div', { class: 'avatar', style: { background: ORANGE } }, initials(t.with)),
        h('div', { class: 'grow ellipsis' },
          h('div', { class: 'row between' }, h('strong', {}, t.with), h('small', { class: 'muted' }, ago(t.updatedAt))),
          h('div', { class: 'ellipsis' }, t.adTitle, h('span', { class: 'pill tiny' }, t.role)),
          h('small', { class: 'muted ellipsis' }, t.last || ''))))));
    }

    let convTimer = null;
    async function conversation(threadId) {
      clearInterval(convTimer);
      const input = h('textarea', { rows: 2, placeholder: 'Votre message…', maxlength: 1000 });
      const bubbles = h('div', { class: 'bubbles' });
      const refresh = async (scroll) => {
        const data = await attempt(() => api(`bonplan/conversations/${threadId}`));
        if (!data) return clearInterval(convTimer);
        layout.setTitle(`${data.thread.with} — ${data.thread.adTitle}`);
        clear(bubbles, data.messages.map((m) => h('div', { class: `bubble ${m.mine ? 'mine' : ''}` }, h('div', { class: 'pre' }, m.body), h('small', {}, ago(m.createdAt)))));
        if (scroll) bubbles.scrollTop = bubbles.scrollHeight;
        layout.setActions(
          h('button', { class: 'btn', onclick: () => layout.select('ad', data.thread.adId) }, 'Voir l\'annonce'),
          h('button', { class: 'btn', onclick: () => layout.select('messages') }, icon('back', 14), 'Conversations'));
      };
      const send = async (e) => {
        e.preventDefault();
        const body = input.value.trim();
        if (!body) return;
        if (await attempt(() => api(`bonplan/conversations/${threadId}`, { method: 'POST', body: { body } }))) {
          input.value = '';
          refresh(true);
        }
      };
      input.addEventListener('keydown', (e) => { if (e.key === 'Enter' && !e.shiftKey) send(e); });
      clear(layout.content, h('div', { class: 'chat' }, bubbles,
        h('form', { class: 'chat-form', onsubmit: send }, input, h('button', { class: 'btn btn-orange', type: 'submit' }, icon('send', 14)))));
      await refresh(true);
      convTimer = setInterval(() => {
        if (!document.body.contains(bubbles)) return clearInterval(convTimer);
        refresh(false);
      }, 8000);
    }

    if (param) {
      // Lien direct vers une annonce : on charge quand même les catégories de la barre latérale
      api('bonplan').then((d) => { categories = d.categories; layout.refreshNav(); }).catch(() => {});
      layout.select('ad', Number(param));
    } else {
      layout.select('all');
    }
    return () => clearInterval(convTimer);
  },
};
