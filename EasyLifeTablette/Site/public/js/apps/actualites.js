// Application Actualités : journal de la ville
import { api, h, clear, icon, ago, dateTimeFr, attempt, modal, form, confirmBox, empty, loading } from '../core.js';

export default {
  id: 'actualites',
  name: 'Actualités',
  icon: 'news',
  color: 'linear-gradient(180deg,#ff5a5f,#d92d3a)',
  keywords: 'journal news info presse',
  open(root, ctx, param) {
    let data = null;
    let filter = '';
    const filters = h('div', { class: 'chips' });
    const list = h('div', { class: 'news-list' });
    const actions = h('div', { class: 'row gap end' });

    clear(root, h('div', { class: 'single' },
      h('header', { class: 'single-head' }, h('div', {}), h('h2', {}, 'Actualités'), actions),
      h('div', { class: 'single-body narrow' }, filters, list)));

    async function load() {
      clear(list, loading());
      data = await attempt(() => api('actualites'));
      if (!data) return;
      clear(actions,
        h('button', { class: 'icon-btn', title: 'Actualiser', onclick: load }, icon('refresh', 16)),
        data.canWrite ? h('button', { class: 'btn btn-primary', onclick: () => edit() }, icon('pencil', 14), 'Publier') : null);
      draw();
      if (param) {
        const a = data.articles.find((x) => String(x.id) === String(param));
        param = undefined;
        if (a) read(a);
      }
    }

    function draw() {
      clear(filters, ['', ...data.categories].map((c) => h('button', {
        class: `chip ${filter === c ? 'active' : ''}`,
        onclick: () => { filter = c; draw(); },
      }, c || 'Tout')));
      const articles = data.articles.filter((a) => !filter || a.category === filter);
      if (!articles.length) return clear(list, empty('news', 'Aucun article', data.canWrite ? 'Publiez la première actualité de la ville !' : 'Revenez plus tard.'));
      clear(list, articles.map((a) => h('button', { class: 'news-card', onclick: () => read(a) },
        h('div', { class: 'row gap' },
          a.pinned ? h('span', { class: 'pill red' }, icon('pin', 11), 'À la une') : null,
          h('span', { class: 'news-cat' }, a.category),
          h('small', { class: 'muted push' }, ago(a.createdAt))),
        h('h3', {}, a.title),
        h('p', { class: 'muted' }, a.body.length > 220 ? `${a.body.slice(0, 220)}…` : a.body),
        h('small', { class: 'muted' }, `Par ${a.authorName}`))));
    }

    function read(a) {
      modal(a.category, (close) => h('article', { class: 'stack' },
        h('h2', {}, a.title),
        h('small', { class: 'muted' }, `Par ${a.authorName} · ${dateTimeFr(a.createdAt)}`),
        h('div', { class: 'pre article-body' }, a.body),
        a.canEdit ? h('div', { class: 'row end gap' },
          h('button', { class: 'btn btn-danger', onclick: async () => {
            if (!await confirmBox('Supprimer cet article ?', 'Supprimer')) return;
            if (await attempt(() => api(`actualites/${a.id}`, { method: 'DELETE', body: {} }), 'Article supprimé.')) { close(); load(); }
          } }, icon('trash', 14), 'Supprimer'),
          h('button', { class: 'btn btn-primary', onclick: () => { close(); edit(a); } }, icon('pencil', 14), 'Modifier')) : null), { wide: true });
    }

    function edit(a) {
      const fields = [
        { name: 'title', label: 'Titre', value: a?.title, required: true, max: 120 },
        { name: 'category', label: 'Rubrique', type: 'select', value: a?.category || 'Info', options: data.categories },
        { name: 'body', label: 'Article', type: 'textarea', rows: 12, value: a?.body, required: true, max: 8000 },
      ];
      if (ctx.me.isAdmin) fields.push({ name: 'pinned', label: 'Épingler à la une', type: 'checkbox', value: a?.pinned });
      modal(a ? 'Modifier l\'article' : 'Nouvel article', (close) => form(fields, a ? 'Enregistrer' : 'Publier', async (v) => {
        const r = a
          ? await attempt(() => api(`actualites/${a.id}`, { method: 'PUT', body: v }), 'Article mis à jour.')
          : await attempt(() => api('actualites', { method: 'POST', body: v }), 'Article publié !');
        if (r) { close(); load(); }
      }), { wide: true });
    }

    load();
  },
};
