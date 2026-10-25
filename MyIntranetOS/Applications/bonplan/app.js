/* Le Bon Plan : petites annonces entre habitants (expirent automatiquement) */
(function () {
  'use strict';
  var MI = window.MyIntranet, h = UI.h;
  var root = document.getElementById('app');
  var data = null, category = 'Toutes', query = '';

  function norm(s) { return (s || '').toLowerCase().normalize('NFD').replace(/[̀-ͯ]/g, ''); }

  function post() {
    var title = h('input', { class: 'mi-input block', maxlength: 80, id: 'a-title' });
    var cat = h('select', { class: 'mi-input block', id: 'a-cat' });
    data.categories.forEach(function (c) { cat.appendChild(h('option', { value: c, text: c })); });
    var price = h('input', { class: 'mi-input block', type: 'number', min: '0', step: '1', id: 'a-price' });
    var desc = h('textarea', { class: 'mi-input', maxlength: 1000, id: 'a-desc' });
    UI.sheet('Déposer une annonce', h('div', { class: 'mi-stack' }, UI.field('Titre', title), UI.field('Catégorie', cat), UI.field('Prix (€)', price), UI.field('Description', desc),
      h('div', { class: 'mi-sub', text: 'Votre numéro de téléphone sera affiché pour être contacté.' })), [
      { label: 'Annuler' },
      { label: 'Publier', primary: true, onclick: function (close) {
        return MI.api('/ads', { body: { title: title.value, category: cat.value, price: parseFloat(price.value) || 0, description: desc.value } }).then(function () { close(); UI.success('Annonce publiée.'); return load(); });
      } }
    ]);
  }

  function render() {
    UI.clear(root);
    root.appendChild(UI.header('Le Bon Plan', 'Petites annonces', h('button', { type: 'button', class: 'mi-btn', text: 'Déposer', onclick: post })));
    var cats = ['Toutes'].concat(data.categories);
    root.appendChild(UI.tabs(cats.map(function (c) { return { id: c, label: c }; }), function (c) { category = c; renderList(); }, category));
    var search = h('input', { class: 'mi-input block', type: 'search', placeholder: 'Rechercher une annonce…', id: 'search', value: query });
    search.addEventListener('input', function () { query = search.value; renderList(); });
    root.appendChild(search);
    root.appendChild(h('div', { id: 'list', class: 'mi-grid2', style: 'margin-top: .8rem' }));
    renderList();
  }

  function renderList() {
    var box = UI.clear(document.getElementById('list'));
    var q = norm(query);
    var items = data.items.filter(function (a) {
      return (category === 'Toutes' || a.category === category) && (!q || norm(a.title + ' ' + a.description).indexOf(q) >= 0);
    });
    if (!items.length) { box.appendChild(UI.empty('Aucune annonce.', '🏷')); return; }
    items.forEach(function (a) {
      box.appendChild(h('div', { class: 'mi-card mi-stack', style: 'gap: .4rem' },
        h('div', { class: 'mi-row', style: 'padding: 0; min-height: 0; align-items: flex-start' },
          h('div', { class: 'mi-grow', style: 'min-width: 0' }, h('span', { class: 'mi-chip accent', text: a.category }), h('div', { class: 'mi-title', style: 'margin-top: 4px', text: a.title })),
          h('div', { class: 'mi-amount', style: 'font-size: 1.15rem; color: #ff6a00', text: a.price > 0 ? UI.money(a.price, 0) : 'Gratuit' })),
        a.description ? h('div', { class: 'mi-sub', style: 'white-space: pre-wrap', text: a.description }) : null,
        h('div', { class: 'mi-sub', style: 'font-size: .78rem', text: a.author + ' · ' + UI.ago(a.createdAt) }),
        h('div', { class: 'mi-actions' },
          a.canDelete ? h('button', { type: 'button', class: 'mi-btn small ghost', text: 'Retirer', onclick: function () {
            UI.confirm('Retirer cette annonce ?', 'Retirer', true).then(function (ok) { if (ok) MI.api('/ads/' + a.id, { method: 'DELETE' }).then(load).catch(UI.error); });
          } }) : null,
          !a.mine && a.phone ? h('button', { type: 'button', class: 'mi-btn small', text: 'Contacter', onclick: function () { MI.openApp('messages', { number: a.phone }); } }) : null)));
    });
  }

  function load() { return MI.api('/ads').then(function (d) { data = d; render(); }); }

  root.appendChild(UI.loading());
  load().catch(function (e) { UI.clear(root); root.appendChild(UI.empty(e.message, '⚠')); });
})();
