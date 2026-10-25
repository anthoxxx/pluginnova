/* Actualités : articles publiés par la presse, les services publics autorisés et le staff */
(function () {
  'use strict';
  var MI = window.MyIntranet, h = UI.h;
  var root = document.getElementById('app');

  function publish() {
    var title = h('input', { class: 'mi-input block', maxlength: 120, id: 'n-title' });
    var body = h('textarea', { class: 'mi-input', maxlength: 5000, id: 'n-body', style: 'min-height: 180px' });
    UI.sheet('Publier un article', h('div', { class: 'mi-stack' }, UI.field('Titre', title), UI.field('Texte', body)), [
      { label: 'Annuler' },
      { label: 'Publier', primary: true, onclick: function (close) {
        return MI.api('/news', { body: { title: title.value, body: body.value } }).then(function () { close(); UI.success('Article publié.'); MI.refresh(); return load(); });
      } }
    ]);
  }

  function read(a) {
    UI.sheet(a.title, h('div', { class: 'mi-stack' },
      h('div', { class: 'mi-sub', text: a.outlet + ' · ' + a.author + ' · ' + UI.date(a.createdAt) }),
      h('div', { style: 'white-space: pre-wrap; line-height: 1.6', text: a.body })),
      (a.canDelete ? [{ label: 'Supprimer', danger: true, onclick: function (close) {
        return MI.api('/news/' + a.id, { method: 'DELETE' }).then(function () { close(); return load(); });
      } }] : []).concat([{ label: 'Fermer', primary: true }]));
  }

  function load() {
    return MI.api('/news').then(function (d) {
      UI.clear(root);
      root.appendChild(UI.header('Actualités', 'Amboise et ses environs', d.canPublish ? h('button', { type: 'button', class: 'mi-btn', text: 'Publier', onclick: publish }) : null));
      if (!d.items.length) { root.appendChild(h('div', { class: 'mi-card' }, UI.empty('Aucun article pour le moment.', '📰'))); return; }
      var list = h('div', { class: 'mi-stack' });
      d.items.forEach(function (a, i) {
        list.appendChild(h('div', { class: 'mi-card mi-row clickable', style: 'display: block; cursor: pointer', onclick: function () { read(a); } },
          h('div', { style: 'font-size: .72rem; font-weight: 800; letter-spacing: .06em; text-transform: uppercase; color: var(--mi-accent)', text: a.outlet }),
          h('div', { style: 'font-size: ' + (i === 0 ? '1.35rem' : '1.05rem') + '; font-weight: 800; margin: 2px 0 4px', text: a.title }),
          h('div', { class: 'mi-sub mi-clamp', text: a.body }),
          h('div', { class: 'mi-sub', style: 'margin-top: 6px; font-size: .78rem', text: a.author + ' · ' + UI.ago(a.createdAt) })));
      });
      root.appendChild(list);
    });
  }

  root.appendChild(UI.loading());
  load().catch(function (e) { UI.clear(root); root.appendChild(UI.empty(e.message, '⚠')); });
})();
