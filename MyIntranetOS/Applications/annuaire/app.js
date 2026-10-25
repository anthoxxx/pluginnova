/* Annuaire : contacts du téléphone du jeu et entreprises de la ville */
(function () {
  'use strict';
  var MI = window.MyIntranet, h = UI.h;
  var root = document.getElementById('app');
  var tab = 'contacts', query = '';
  var contacts = [], bizs = [];

  function norm(s) { return (s || '').toLowerCase().normalize('NFD').replace(/[̀-ͯ]/g, ''); }
  function digits(s) { return (s || '').replace(/[^0-9]/g, ''); }

  function addContact() {
    var name = h('input', { class: 'mi-input block', placeholder: 'Prénom Nom', maxlength: 40, id: 'c-name' });
    var number = h('input', { class: 'mi-input block', placeholder: '06…', inputmode: 'tel', id: 'c-number' });
    UI.sheet('Nouveau contact', h('div', { class: 'mi-stack' }, UI.field('Nom', name), UI.field('Numéro', number)), [
      { label: 'Annuler' },
      { label: 'Enregistrer', primary: true, onclick: function (close) {
        return MI.api('/contacts', { body: { name: name.value, number: number.value } }).then(function () { close(); return load(); });
      } }
    ]);
  }

  function render() {
    UI.clear(root);
    root.appendChild(UI.header('Annuaire', null, tab === 'contacts' ? h('button', { type: 'button', class: 'mi-btn', text: 'Ajouter', onclick: addContact }) : null));
    root.appendChild(UI.tabs([{ id: 'contacts', label: 'Mes contacts' }, { id: 'bizs', label: 'Entreprises' }], function (t) { tab = t; render(); }, tab));
    var search = h('input', { class: 'mi-input block', type: 'search', placeholder: 'Rechercher…', id: 'search', value: query });
    search.addEventListener('input', function () { query = search.value; renderList(); });
    root.appendChild(search);
    root.appendChild(h('div', { id: 'list', style: 'margin-top: .8rem' }));
    renderList();
  }

  function renderList() {
    var box = UI.clear(document.getElementById('list'));
    var q = norm(query);
    var list = h('div', { class: 'mi-list' });
    if (tab === 'contacts') {
      var items = contacts.filter(function (c) { return !q || norm(c.name).indexOf(q) >= 0 || c.number.indexOf(q) >= 0; });
      if (!items.length) list.appendChild(UI.empty(contacts.length ? 'Aucun résultat.' : 'Aucun contact. Ils sont partagés avec le téléphone du jeu.', '👤'));
      items.forEach(function (c) {
        list.appendChild(h('div', { class: 'mi-row' }, UI.avatar(c.name),
          h('div', { class: 'mi-grow' }, h('div', { class: 'mi-title', text: c.name }), h('div', { class: 'mi-sub', text: c.number })),
          h('button', { type: 'button', class: 'mi-btn small secondary', text: 'SMS', onclick: function () { MI.openApp('messages', { number: c.number }); } }),
          h('button', { type: 'button', class: 'mi-btn small ghost', text: 'Supprimer', onclick: function () {
            UI.confirm('Supprimer ' + c.name + ' ?', 'Supprimer', true).then(function (ok) {
              if (ok) MI.api('/contacts/' + c.id, { method: 'DELETE' }).then(load).catch(UI.error);
            });
          } })));
      });
    } else {
      var bs = bizs.filter(function (b) { return !q || norm(b.name).indexOf(q) >= 0 || norm(b.description).indexOf(q) >= 0; });
      if (!bs.length) list.appendChild(UI.empty('Aucune entreprise.'));
      bs.forEach(function (b) {
        var phone = digits(b.contact);
        list.appendChild(h('div', { class: 'mi-row' }, UI.avatar(b.name),
          h('div', { class: 'mi-grow', style: 'min-width: 0' },
            h('div', { class: 'mi-title' }, b.name, b.recruiting ? h('span', { class: 'mi-chip ok', style: 'margin-left: 6px', text: 'Recrute' }) : null),
            h('div', { class: 'mi-sub mi-clamp', text: [b.description, b.contact].filter(Boolean).join(' · ') || 'Pas de description' })),
          phone.length >= 3 ? h('button', { type: 'button', class: 'mi-btn small secondary', text: 'SMS', onclick: function () { MI.openApp('messages', { number: phone }); } }) : null));
      });
    }
    box.appendChild(list);
  }

  function load() {
    return Promise.all([MI.api('/contacts'), MI.api('/directory')]).then(function (r) { contacts = r[0]; bizs = r[1]; render(); });
  }

  root.appendChild(UI.loading());
  load().catch(function (e) { UI.clear(root); root.appendChild(UI.empty(e.message, '⚠')); });
})();
