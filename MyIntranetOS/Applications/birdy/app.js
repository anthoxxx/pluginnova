/* Birdy : réseau social de la ville (messages de 280 caractères, j'aime) */
(function () {
  'use strict';
  var MI = window.MyIntranet, h = UI.h;
  var root = document.getElementById('app');

  function load() {
    return MI.api('/birdy').then(function (d) {
      UI.clear(root);
      root.appendChild(UI.header('Birdy', 'Fil de la ville'));
      var text = h('textarea', { class: 'mi-input', maxlength: 280, placeholder: 'Quoi de neuf à Amboise ?', id: 'b-text', style: 'min-height: 70px' });
      var count = h('span', { class: 'mi-sub', text: '280' });
      text.addEventListener('input', function () { count.textContent = String(280 - text.value.length); });
      root.appendChild(h('div', { class: 'mi-card mi-stack' },
        h('div', { class: 'mi-row', style: 'padding: 0; min-height: 0; align-items: flex-start' }, UI.avatar(d.me.name),
          h('div', { class: 'mi-grow' }, h('div', { class: 'mi-title', text: d.me.name }), h('div', { class: 'mi-sub', text: d.me.handle }))),
        text,
        h('div', { class: 'mi-actions', style: 'align-items: center' }, count, UI.action('Publier', function () {
          if (!text.value.trim()) return;
          return MI.api('/birdy', { body: { text: text.value } }).then(load);
        }))));

      root.appendChild(h('h2', { text: 'Fil' }));
      var list = h('div', { class: 'mi-list' });
      if (!d.items.length) list.appendChild(UI.empty('Soyez le premier à publier.', '🐦'));
      d.items.forEach(function (p) {
        var like = h('button', { type: 'button', class: 'mi-btn small ' + (p.liked ? '' : 'secondary'), text: (p.liked ? '♥ ' : '♡ ') + p.likes });
        like.addEventListener('click', function () {
          MI.api('/birdy/' + p.id + '/like', { body: {} }).then(function (r) {
            like.textContent = (r.liked ? '♥ ' : '♡ ') + r.likes;
            like.className = 'mi-btn small ' + (r.liked ? '' : 'secondary');
          }).catch(UI.error);
        });
        list.appendChild(h('div', { class: 'mi-row', style: 'align-items: flex-start' }, UI.avatar(p.author),
          h('div', { class: 'mi-grow', style: 'min-width: 0' },
            h('div', null, h('b', { text: p.author }), ' ', h('span', { class: 'mi-sub', text: p.handle + ' · ' + UI.ago(p.createdAt) })),
            h('div', { style: 'white-space: pre-wrap; word-break: break-word; margin: 4px 0 8px', text: p.text }),
            h('div', { class: 'mi-actions', style: 'justify-content: flex-start' }, like,
              p.canDelete ? h('button', { type: 'button', class: 'mi-btn small ghost', text: 'Supprimer', onclick: function () {
                UI.confirm('Supprimer ce message ?', 'Supprimer', true).then(function (ok) {
                  if (ok) MI.api('/birdy/' + p.id, { method: 'DELETE' }).then(load).catch(UI.error);
                });
              } }) : null))));
      });
      root.appendChild(list);
    });
  }

  root.appendChild(UI.loading());
  load().catch(function (e) { UI.clear(root); root.appendChild(UI.empty(e.message, '⚠')); });
})();
