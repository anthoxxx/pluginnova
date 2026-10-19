// Application Commander : restaurants de la ville (McBlue…), commandes et espace équipe
import { api, h, clear, icon, euro, ago, timeFr, attempt, modal, form, confirmBox, empty, loading, sideLayout, toast } from '../core.js';
import { pickPlayer } from './admin.js';

const STATUS_FLOW = { nouvelle: 'en_preparation', en_preparation: 'prete', prete: 'livree', en_livraison: 'livree' };
const STATUS_ACTION = { nouvelle: 'Préparer', en_preparation: 'Prête', prete: 'Remise au client', en_livraison: 'Livrée' };
const STATUS_CLASS = { nouvelle: 'blue', en_preparation: 'orange', prete: 'green', en_livraison: 'orange', livree: '', annulee: 'red' };

export default {
  id: 'commander',
  name: 'Commander',
  icon: 'food',
  color: 'linear-gradient(180deg,#ffd43b,#f5a300)',
  iconColor: '#3a2a00',
  keywords: 'restaurant mcblue burger manger livraison',
  open(root, ctx, param) {
    let restaurants = [];
    let canCreate = false;
    const cart = new Map(); // itemId -> { item, qty }
    let cartRestaurant = null;
    let layout = null;
    let teamTimer = null;

    async function init(selectId) {
      clear(root, loading());
      const data = await attempt(() => api('commander'));
      if (!data) return;
      restaurants = data.restaurants;
      canCreate = data.canCreate;
      layout = sideLayout(root, {
        appName: 'Commander',
        subtitle: 'Restaurants de la ville',
        iconName: 'food',
        color: '#f5a300',
        headerAction: canCreate ? h('button', { class: 'btn block', onclick: createRestaurant }, icon('plus', 14), 'Nouveau restaurant') : null,
        sections: () => [
          { title: 'Restaurants', items: restaurants.map((r) => ({ id: `r:${r.id}`, label: r.name, icon: 'store', badge: r.open ? 'Ouvert' : null })) },
          { title: 'Mon espace', items: [{ id: 'orders', label: 'Mes commandes', icon: 'receipt' }] },
          ...(restaurants.some((r) => r.isStaff) ? [{ title: 'Espace équipe', items: restaurants.filter((r) => r.isStaff).map((r) => ({ id: `t:${r.id}`, label: r.name, icon: 'users' })) }] : []),
        ],
        onSelect: (id) => {
          clearInterval(teamTimer);
          if (id === 'orders') return myOrders();
          if (id === 'home') return overview();
          const [kind, rid] = id.split(':');
          return kind === 'r' ? restaurant(Number(rid)) : team(Number(rid));
        },
      });
      if (selectId) layout.select(selectId);
      else if (restaurants.length === 1) layout.select(`r:${restaurants[0].id}`);
      else layout.select('home');
    }

    function overview() {
      layout.setTitle('Commander');
      if (!restaurants.length) {
        return clear(layout.content, empty('food', 'Aucun restaurant', canCreate ? 'Créez le premier restaurant avec « Nouveau restaurant ».' : 'Aucun restaurant n\'est encore inscrit.'));
      }
      clear(layout.content, h('div', { class: 'resto-grid' }, restaurants.map((r) => h('button', {
        class: 'resto-card', style: { background: `linear-gradient(135deg, ${r.color}, #0b0b12)` }, onclick: () => layout.select(`r:${r.id}`),
      }, h('div', { class: 'resto-emoji' }, r.emoji), h('strong', {}, r.name), h('small', {}, r.slogan),
        h('span', { class: `pill ${r.open ? 'green' : 'red'}` }, h('span', { class: `sb-dot ${r.open ? 'on' : 'off'}` }), r.open ? 'Ouvert' : 'Fermé')))));
    }

    async function restaurant(id) {
      clear(layout.content, loading());
      const data = await attempt(() => api(`commander/restaurants/${id}`));
      if (!data) return;
      const r = data.restaurant;
      if (cartRestaurant !== r.id) { cart.clear(); cartRestaurant = r.id; }
      layout.setTitle('Commander');
      const items = data.items.filter((i) => i.available);
      const cats = [...new Set(items.map((i) => i.category))];
      let filter = '';
      const chips = h('div', { class: 'chips' });
      const menu = h('div', {});
      const cartBar = h('div', { class: 'cart-bar' });

      const drawMenu = () => {
        clear(chips, ['', ...cats].map((c) => h('button', { class: `chip ${filter === c ? 'active' : ''}`, onclick: () => { filter = c; drawMenu(); } }, c || 'Tout')));
        if (!items.length) return clear(menu, empty('food', 'Menu vide', r.isOwner ? 'Ajoutez vos produits depuis l\'espace équipe.' : 'Le restaurant n\'a pas encore de carte.'));
        clear(menu, (filter ? [filter] : cats).map((c) => [
          h('h3', { class: 'menu-cat' }, c),
          h('div', { class: 'menu-grid' }, items.filter((i) => i.category === c).map((i) => h('div', { class: 'menu-card' },
            h('div', { class: 'menu-visual' }, h('span', {}, i.emoji), i.featured ? h('span', { class: 'menu-flag' }, 'À LA UNE') : null),
            h('strong', {}, i.name),
            h('small', { class: 'muted ellipsis2' }, i.description),
            h('div', { class: 'row between' }, h('strong', {}, euro(i.price)),
              h('button', { class: 'round-btn', style: { background: r.color }, title: 'Ajouter au panier', disabled: !r.open, onclick: () => add(i) }, icon('plus', 14)))))),
        ]));
      };

      const drawCart = () => {
        const count = [...cart.values()].reduce((s, l) => s + l.qty, 0);
        const total = [...cart.values()].reduce((s, l) => s + l.qty * l.item.price, 0);
        clear(cartBar, count ? h('button', { class: 'cart-btn', style: { background: r.color }, onclick: () => checkout(r, drawCart) },
          icon('bag', 16), `Panier · ${count} article${count > 1 ? 's' : ''}`, h('strong', { class: 'push' }, euro(total)), icon('next', 16)) : null);
      };

      const add = (i) => {
        const line = cart.get(i.id) || { item: i, qty: 0 };
        line.qty = Math.min(20, line.qty + 1);
        cart.set(i.id, line);
        drawCart();
        toast(`${i.name} ajouté au panier`, 'success');
      };

      const delivery = r.deliveryFee > 0 ? `Livraison ${euro(r.deliveryFee)}` : 'Livraison offerte';
      clear(layout.content,
        h('div', { class: 'hero', style: { background: `linear-gradient(120deg, ${r.color}, #10121a 85%)` } },
          h('div', {},
            h('h1', {}, r.name),
            h('p', {}, r.slogan),
            h('div', { class: 'row gap wrap' },
              h('span', { class: 'pill dark' }, h('span', { class: `sb-dot ${r.open ? 'on' : 'off'}` }), r.open ? 'Ouvert' : 'Fermé'),
              h('span', { class: 'pill dark' }, icon('clock', 11), `Prêt en ${r.prepMinutes} min environ`),
              h('span', { class: 'pill dark' }, icon('truck', 11), delivery)),
            !r.open ? h('small', { class: 'hero-note' }, 'Aucun équipier n\'est en service : revenez un peu plus tard.') : null),
          h('div', { class: 'hero-emoji' }, r.emoji)),
        chips, menu, cartBar);
      drawMenu();
      drawCart();
    }

    function checkout(r, onChange) {
      modal(`Panier — ${r.name}`, (close) => {
        const lines = h('div', { class: 'stack' });
        const totalEl = h('strong', {});
        const mode = h('select', {}, h('option', { value: 'comptoir' }, 'À emporter (comptoir)'), h('option', { value: 'livraison' }, `Livraison${r.deliveryFee ? ` (+${euro(r.deliveryFee)})` : ' (offerte)'}`));
        const address = h('input', { placeholder: 'Adresse de livraison (rue, bâtiment, appartement…)', maxlength: 150 });
        const addressField = h('label', { class: 'field', style: { display: 'none' } }, h('span', {}, 'Adresse'), address);
        const note = h('input', { placeholder: 'Sans oignons, sauce à part…', maxlength: 300 });
        const draw = () => {
          clear(lines, [...cart.values()].map((l) => h('div', { class: 'cart-line' },
            h('span', { class: 'cart-emoji' }, l.item.emoji), h('div', { class: 'grow' }, h('strong', {}, l.item.name), h('small', { class: 'muted block' }, euro(l.item.price))),
            h('button', { class: 'icon-btn', onclick: () => { l.qty -= 1; if (l.qty <= 0) cart.delete(l.item.id); draw(); onChange(); if (!cart.size) close(); } }, icon('minus', 14)),
            h('strong', { class: 'qty' }, l.qty),
            h('button', { class: 'icon-btn', onclick: () => { l.qty = Math.min(20, l.qty + 1); draw(); onChange(); } }, icon('plus', 14)))));
          const fee = mode.value === 'livraison' ? r.deliveryFee : 0;
          totalEl.textContent = euro([...cart.values()].reduce((s, l) => s + l.qty * l.item.price, 0) + fee);
          addressField.style.display = mode.value === 'livraison' ? '' : 'none';
        };
        mode.addEventListener('change', draw);
        draw();
        return h('div', { class: 'stack' }, lines,
          h('label', { class: 'field' }, h('span', {}, 'Mode'), mode), addressField,
          h('label', { class: 'field' }, h('span', {}, 'Remarque pour l\'équipe'), note),
          h('div', { class: 'row between' }, h('span', { class: 'muted' }, 'Total à régler sur place, en jeu'), totalEl),
          h('button', { class: 'btn btn-primary block', onclick: async (e) => {
            e.target.disabled = true;
            const body = { items: [...cart.values()].map((l) => ({ id: l.item.id, qty: l.qty })), mode: mode.value, address: address.value, note: note.value };
            const res = await attempt(() => api(`commander/restaurants/${r.id}/commandes`, { method: 'POST', body }), 'Commande envoyée au restaurant !');
            e.target.disabled = false;
            if (res) { cart.clear(); onChange(); close(); layout.select('orders'); }
          } }, 'Valider la commande'));
      });
    }

    async function myOrders() {
      layout.setTitle('Mes commandes');
      layout.setActions(h('button', { class: 'icon-btn', title: 'Actualiser', onclick: myOrders }, icon('refresh', 16)));
      clear(layout.content, loading());
      const data = await attempt(() => api('commander/mes-commandes'));
      if (!data) return;
      if (!data.orders.length) return clear(layout.content, empty('receipt', 'Aucune commande', 'Vos commandes apparaîtront ici avec leur suivi.'));
      clear(layout.content, h('div', { class: 'stack' }, data.orders.map((o) => orderCard(o, false, myOrders))));
    }

    function orderCard(o, staff, reload) {
      const next = STATUS_FLOW[o.status];
      return h('div', { class: 'card order' },
        h('div', { class: 'row between' },
          h('strong', {}, `${staff ? o.customer : o.restaurantName} · #${o.id}`),
          h('span', { class: `pill ${STATUS_CLASS[o.status]}` }, o.statusLabel)),
        h('small', { class: 'muted' }, `${timeFr(o.createdAt)} · ${ago(o.createdAt)} · ${o.mode === 'livraison' ? `Livraison : ${o.address}` : 'Comptoir'}`),
        h('ul', { class: 'order-items' }, o.items.map((i) => h('li', {}, `${i.emoji} ${i.qty} × ${i.name}`, h('span', { class: 'muted' }, euro(i.price * i.qty))))),
        o.note ? h('p', { class: 'muted small' }, `« ${o.note} »`) : null,
        h('div', { class: 'row between' }, h('strong', {}, euro(o.total)),
          h('div', { class: 'row gap' },
            !staff && o.status === 'nouvelle' ? h('button', { class: 'btn btn-danger', onclick: async () => {
              if (!await confirmBox('Annuler cette commande ?', 'Annuler la commande')) return;
              if (await attempt(() => api(`commander/commandes/${o.id}/annuler`, { method: 'POST', body: {} }), 'Commande annulée.')) reload();
            } }, 'Annuler') : null,
            staff && o.status === 'prete' && o.mode === 'livraison' ? h('button', { class: 'btn', onclick: () => setStatus(o, 'en_livraison', reload) }, icon('truck', 14), 'En livraison') : null,
            staff && next ? h('button', { class: 'btn btn-primary', onclick: () => setStatus(o, next, reload) }, STATUS_ACTION[o.status]) : null,
            staff && !['livree', 'annulee'].includes(o.status) ? h('button', { class: 'icon-btn danger', title: 'Annuler', onclick: async () => {
              if (await confirmBox(`Annuler la commande #${o.id} ? Le client sera prévenu.`, 'Annuler la commande')) setStatus(o, 'annulee', reload);
            } }, icon('x', 14)) : null)));
    }

    async function setStatus(o, status, reload) {
      if (await attempt(() => api(`commander/commandes/${o.id}/statut`, { method: 'PUT', body: { status } }))) reload();
    }

    // ---------------- espace équipe
    async function team(id, tab = 'orders') {
      clearInterval(teamTimer);
      const data = await attempt(() => api(`commander/restaurants/${id}/gestion`));
      if (!data) return;
      const r = data.restaurant;
      layout.setTitle(`${r.name} — Équipe`);
      layout.setActions(
        h('button', { class: `btn ${data.onDuty ? 'btn-danger' : 'btn-green'}`, onclick: async () => {
          if (await attempt(() => api(`commander/restaurants/${id}/service`, { method: 'POST', body: { onDuty: !data.onDuty } }), data.onDuty ? 'Fin de service.' : 'Vous êtes en service : le restaurant est ouvert.')) {
            restaurants = (await api('commander')).restaurants;
            layout.refreshNav();
            team(id, tab);
          }
        } }, data.onDuty ? 'Quitter le service' : 'Prendre le service'));

      const tabs = [['orders', 'Commandes'], ['menu', 'Carte'], ...(r.isOwner ? [['staff', 'Équipe'], ['settings', 'Réglages']] : [])];
      const body = h('div', {});
      clear(layout.content,
        h('div', { class: 'chips' }, tabs.map(([k, label]) => h('button', { class: `chip ${tab === k ? 'active' : ''}`, onclick: () => team(id, k) }, label))),
        h('p', { class: 'muted small' }, `${data.staff.filter((s) => s.onDuty).length} équipier(s) en service · gérant : ${r.ownerName}`),
        body);

      if (tab === 'orders') {
        const active = data.orders.filter((o) => !['livree', 'annulee'].includes(o.status));
        const done = data.orders.filter((o) => ['livree', 'annulee'].includes(o.status));
        clear(body,
          active.length ? h('div', { class: 'stack' }, active.map((o) => orderCard(o, true, () => team(id, tab)))) : empty('receipt', 'Aucune commande en cours', data.onDuty ? 'Les nouvelles commandes apparaissent ici automatiquement.' : 'Prenez le service pour ouvrir le restaurant.'),
          done.length ? [h('h3', { class: 'menu-cat' }, 'Terminées (24 h)'), h('div', { class: 'stack faded' }, done.map((o) => orderCard(o, true, () => team(id, tab))))] : null);
        teamTimer = setInterval(() => { if (document.body.contains(body)) team(id, 'orders'); else clearInterval(teamTimer); }, 15e3);
      } else if (tab === 'menu') {
        const menu = await attempt(() => api(`commander/restaurants/${id}`));
        if (!menu) return;
        clear(body,
          r.isOwner ? h('div', { class: 'row end' }, h('button', { class: 'btn btn-primary', onclick: () => editItem(id, null, () => team(id, tab)) }, icon('plus', 14), 'Ajouter un produit')) : null,
          menu.items.length ? h('div', { class: 'card-list' }, menu.items.map((i) => h('div', { class: 'dir-row' },
            h('span', { class: 'cart-emoji' }, i.emoji),
            h('div', { class: 'grow' }, h('strong', {}, i.name, i.featured ? h('span', { class: 'pill tiny orange' }, 'À la une') : null), h('small', { class: 'muted block' }, `${i.category} · ${euro(i.price)}`)),
            h('button', { class: `btn ${i.available ? '' : 'btn-danger'}`, onclick: async () => {
              if (await attempt(() => api(`commander/menu/${i.id}`, { method: 'PUT', body: { available: !i.available } }))) team(id, tab);
            } }, i.available ? 'Disponible' : 'En rupture'),
            r.isOwner ? h('button', { class: 'icon-btn', title: 'Modifier', onclick: () => editItem(id, i, () => team(id, tab)) }, icon('pencil', 14)) : null,
            r.isOwner ? h('button', { class: 'icon-btn danger', title: 'Supprimer', onclick: async () => {
              if (!await confirmBox(`Supprimer « ${i.name} » de la carte ?`, 'Supprimer')) return;
              if (await attempt(() => api(`commander/menu/${i.id}`, { method: 'DELETE', body: {} }))) team(id, tab);
            } }, icon('trash', 14)) : null))) : empty('food', 'Carte vide', 'Ajoutez vos menus, burgers, boissons…'));
      } else if (tab === 'staff') {
        clear(body,
          h('div', { class: 'row end' }, h('button', { class: 'btn btn-primary', onclick: () => pickPlayer('Ajouter un équipier', async (p) => {
            if (await attempt(() => api(`commander/restaurants/${id}/equipe`, { method: 'POST', body: { steamId: p.steamId } }), `${p.name} ajouté à l'équipe.`)) team(id, tab);
          }) }, icon('plus', 14), 'Ajouter un équipier')),
          h('div', { class: 'card-list' }, data.staff.map((s) => h('div', { class: 'dir-row' },
            h('div', { class: 'avatar' }, s.name[0] || '?'),
            h('div', { class: 'grow' }, h('strong', {}, s.name), h('small', { class: 'muted block' }, s.steamId)),
            h('span', { class: `pill ${s.onDuty ? 'green' : ''}` }, s.onDuty ? 'En service' : 'Hors service'),
            h('button', { class: 'icon-btn danger', title: 'Retirer', onclick: async () => {
              if (!await confirmBox(`Retirer ${s.name} de l'équipe ?`, 'Retirer')) return;
              if (await attempt(() => api(`commander/restaurants/${id}/equipe/${s.steamId}`, { method: 'DELETE', body: {} }))) team(id, tab);
            } }, icon('trash', 14))))));
      } else if (tab === 'settings') {
        clear(body, h('div', { class: 'card' }, form([
          { name: 'name', label: 'Nom', value: r.name, required: true, max: 40 },
          { name: 'slogan', label: 'Slogan', value: r.slogan, max: 150 },
          { name: 'emoji', label: 'Emoji du restaurant', value: r.emoji, max: 8 },
          { name: 'color', label: 'Couleur', type: 'color', value: r.color },
          { name: 'prepMinutes', label: 'Temps de préparation (min)', type: 'number', value: r.prepMinutes, min: 1 },
          { name: 'deliveryFee', label: 'Frais de livraison (€, 0 = offerte)', type: 'number', step: '0.01', value: r.deliveryFee, min: 0 },
        ], 'Enregistrer', async (v) => {
          if (await attempt(() => api(`commander/restaurants/${id}`, { method: 'PUT', body: v }), 'Restaurant mis à jour.')) init(`t:${id}`);
        })),
        ctx.me.isAdmin ? h('div', { class: 'row end gap' },
          h('button', { class: 'btn', onclick: () => pickPlayer('Changer de gérant', async (p) => {
            if (await attempt(() => api(`commander/restaurants/${id}/gerant`, { method: 'PUT', body: { ownerSteamId: p.steamId } }), `${p.name} est le nouveau gérant.`)) team(id, tab);
          }) }, 'Changer de gérant'),
          h('button', { class: 'btn btn-danger', onclick: async () => {
            if (!await confirmBox(`Supprimer définitivement ${r.name}, sa carte et ses commandes ?`, 'Supprimer')) return;
            if (await attempt(() => api(`commander/restaurants/${id}`, { method: 'DELETE', body: {} }), 'Restaurant supprimé.')) init();
          } }, 'Supprimer le restaurant')) : null);
      }
    }

    function editItem(restaurantId, item, done) {
      modal(item ? `Modifier « ${item.name} »` : 'Nouveau produit', (close) => form([
        { name: 'name', label: 'Nom', value: item?.name, required: true, max: 60 },
        { name: 'description', label: 'Description', value: item?.description, max: 200 },
        { name: 'price', label: 'Prix (€)', type: 'number', step: '0.01', min: 0, value: item?.price ?? '', required: true },
        { name: 'category', label: 'Catégorie', value: item?.category || 'Menus', placeholder: 'Menus, Burgers, Accompagnements, Boissons…', max: 30 },
        { name: 'emoji', label: 'Emoji', value: item?.emoji || '🍔', max: 8 },
        { name: 'featured', label: 'Mettre à la une', type: 'checkbox', value: item?.featured },
        { name: 'available', label: 'Disponible', type: 'checkbox', value: item ? item.available : true },
      ], 'Enregistrer', async (v) => {
        const body = { ...v, price: Number(v.price) };
        const r = item
          ? await attempt(() => api(`commander/menu/${item.id}`, { method: 'PUT', body }), 'Produit mis à jour.')
          : await attempt(() => api(`commander/restaurants/${restaurantId}/menu`, { method: 'POST', body }), 'Produit ajouté à la carte.');
        if (r) { close(); done(); }
      }));
    }

    function createRestaurant() {
      modal('Nouveau restaurant', (close) => {
        let owner = null;
        const ownerLabel = h('span', { class: 'muted' }, 'Aucun gérant choisi');
        const f = form([
          { name: 'name', label: 'Nom', placeholder: 'McBlue', required: true, max: 40 },
          { name: 'slogan', label: 'Slogan', placeholder: 'Burgers, menus et boissons. Au comptoir ou livrés partout en ville.', max: 150 },
        ], 'Créer', async (v) => {
          if (!owner) return toast('Choisissez le gérant du restaurant.', 'error');
          const r = await attempt(() => api('commander/restaurants', { method: 'POST', body: { ...v, ownerSteamId: owner.steamId } }), 'Restaurant créé !');
          if (r) { close(); init(`t:${r.id}`); }
        });
        return h('div', { class: 'stack' }, f,
          h('div', { class: 'row between' }, ownerLabel, h('button', { class: 'btn', type: 'button', onclick: () => pickPlayer('Gérant du restaurant', (p) => { owner = p; ownerLabel.textContent = `Gérant : ${p.name}`; }) }, 'Choisir le gérant')));
      });
    }

    init(param ? `r:${param}` : undefined);
    return () => clearInterval(teamTimer);
  },
};
