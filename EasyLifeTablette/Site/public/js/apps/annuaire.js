// Application Annuaire : entreprises et services de la ville
import { api, h, clear, icon, attempt, modal, form, confirmBox, empty, loading } from '../core.js';

const COLORS = ['#e5484d', '#f76b15', '#ffb224', '#46a758', '#12a594', '#0090ff', '#3e63dd', '#8e4ec6', '#d6409f', '#a18072'];
const color = (name) => COLORS[[...name].reduce((s, c) => s + c.charCodeAt(0), 0) % COLORS.length];

export default {
  id: 'annuaire',
  name: 'Annuaire',
  icon: 'contact',
  color: 'linear-gradient(180deg,#f2f2f7,#c7c7cc)',
  iconColor: '#3a3a3c',
  keywords: 'entreprise telephone numero',
  open(root) {
    let data = null;
    const search = h('input', { class: 'search-input', placeholder: 'Rechercher une entreprise, un service, un numéro…' });
    const list = h('div', { class: 'directory' });

    clear(root, h('div', { class: 'single' },
      h('header', { class: 'single-head' },
        h('div', {}),
        h('h2', {}, 'Annuaire'),
        h('div', { class: 'row gap end' },
          h('button', { class: 'icon-btn', title: 'Actualiser', onclick: load }, icon('refresh', 16)),
          h('span', { class: 'add-slot' }))),
      h('div', { class: 'single-body narrow' }, search, list)));

    search.addEventListener('input', draw);

    async function load() {
      clear(list, loading());
      data = await attempt(() => api('annuaire'));
      if (!data) return;
      const slot = root.querySelector('.add-slot');
      clear(slot, data.canManage ? h('button', { class: 'btn btn-primary', onclick: () => edit() }, icon('plus', 14), 'Ajouter') : null);
      draw();
    }

    function draw() {
      const q = search.value.trim().toLowerCase();
      const entries = data.entries.filter((e) => !q || `${e.name} ${e.category} ${e.phone} ${e.description}`.toLowerCase().includes(q));
      if (!entries.length) return clear(list, empty('contact', 'Aucune fiche', q ? 'Aucun résultat pour cette recherche.' : 'Les entreprises du serveur apparaîtront ici automatiquement.'));
      const groups = new Map();
      for (const e of entries) {
        const letter = e.name[0].toUpperCase().normalize('NFD').replace(/[̀-ͯ]/g, '');
        if (!groups.has(letter)) groups.set(letter, []);
        groups.get(letter).push(e);
      }
      clear(list, [...groups].map(([letter, items]) => [
        h('div', { class: 'dir-letter' }, letter),
        h('div', { class: 'card-list' }, items.map((e) => h('button', { class: 'dir-row', onclick: () => detail(e) },
          h('div', { class: 'dir-badge', style: { background: color(e.name) } }, letter),
          h('div', { class: 'grow' }, h('strong', {}, e.name), h('small', { class: 'muted' }, e.category)),
          e.phone ? h('span', { class: 'muted small nowrap' }, icon('phone', 12), ' ', e.phone) : null,
          e.recruiting ? h('span', { class: 'pill green' }, h('span', { class: 'sb-dot on' }), 'Recrute') : null,
          icon('next', 14)))),
      ]));
    }

    function detail(e) {
      modal(e.name, (close) => h('div', { class: 'stack' },
        h('div', { class: 'row gap' }, h('span', { class: 'pill' }, e.category), e.recruiting ? h('span', { class: 'pill green' }, 'Recrute actuellement') : null,
          e.fromGame ? h('span', { class: 'pill' }, 'Entreprise du serveur') : null),
        e.phone ? h('div', { class: 'big-phone' }, icon('phone', 18), e.phone) : h('p', { class: 'muted' }, 'Pas de numéro renseigné.'),
        e.description ? h('p', { class: 'pre' }, e.description) : null,
        e.ownerName ? h('p', { class: 'muted small' }, `Gérée par ${e.ownerName}`) : null,
        e.canEdit ? h('div', { class: 'row end gap' },
          data.canManage && !e.fromGame ? h('button', { class: 'btn btn-danger', onclick: async () => {
            if (!await confirmBox(`Supprimer la fiche « ${e.name} » ?`, 'Supprimer')) return;
            if (await attempt(() => api(`annuaire/${e.id}`, { method: 'DELETE', body: {} }), 'Fiche supprimée.')) { close(); load(); }
          } }, 'Supprimer') : null,
          data.canManage ? h('button', { class: 'btn', onclick: () => { close(); manager(e); } }, 'Gérant') : null,
          h('button', { class: 'btn btn-primary', onclick: () => { close(); edit(e); } }, icon('pencil', 14), 'Modifier')) : null));
    }

    function edit(e) {
      const fields = [
        e?.fromGame ? null : { name: 'name', label: 'Nom', value: e?.name, required: true, max: 60 },
        { name: 'category', label: 'Catégorie', value: e?.category || 'Services', placeholder: 'Commerce, Services, Restauration…', max: 40 },
        { name: 'phone', label: 'Téléphone', value: e?.phone, max: 20 },
        { name: 'description', label: 'Description (horaires, adresse…)', type: 'textarea', value: e?.description, max: 500 },
        { name: 'recruiting', label: 'Nous recrutons', type: 'checkbox', value: e?.recruiting },
      ].filter(Boolean);
      modal(e ? `Modifier « ${e.name} »` : 'Nouvelle fiche', (close) => form(fields, 'Enregistrer', async (v) => {
        const r = e
          ? await attempt(() => api(`annuaire/${e.id}`, { method: 'PUT', body: v }), 'Fiche mise à jour.')
          : await attempt(() => api('annuaire', { method: 'POST', body: v }), 'Fiche ajoutée.');
        if (r) { close(); load(); }
      }));
    }

    function manager(e) {
      modal(`Gérant de « ${e.name} »`, (close) => form([
        { name: 'steamId', label: 'SteamID64 du gérant (vide pour retirer)', placeholder: '7656119…', max: 17, hint: 'Le gérant pourra modifier la fiche (numéro, description, recrutement).' },
      ], 'Enregistrer', async (v) => {
        if (await attempt(() => api(`annuaire/${e.id}/gerant`, { method: 'PUT', body: v }), 'Gérant mis à jour.')) { close(); load(); }
      }));
    }

    load();
  },
};
