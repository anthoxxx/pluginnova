/* Immobilier : terrains du personnage et biens à vendre ou à louer, avec GPS en jeu */
(function () {
  'use strict';
  var MI = window.MyIntranet, h = UI.h;
  var root = document.getElementById('app');
  var tab = 'sale', data = null;

  function gps(a) {
    return UI.action('GPS', function () {
      return MI.api('/realestate/' + a.id + '/locate', { body: {} }).then(function () { UI.success('Point GPS placé sur ' + a.address + '.'); });
    }, 'small secondary');
  }

  function render() {
    UI.clear(root);
    root.appendChild(UI.header('Immobilier', data.forSale.length + ' bien(s) disponible(s)'));
    root.appendChild(UI.tabs([{ id: 'sale', label: 'À vendre / à louer' }, { id: 'mine', label: 'Mes biens (' + data.mine.length + ')' }], function (t) { tab = t; render(); }, tab));
    var items = tab === 'sale' ? data.forSale : data.mine;
    var list = h('div', { class: 'mi-list' });
    if (!items.length) list.appendChild(UI.empty(tab === 'sale' ? 'Aucun bien disponible.' : 'Vous ne possédez aucun bien.', '🏠'));
    items.forEach(function (a) {
      var price = [];
      if (a.price > 0) price.push(UI.money(a.price, 0));
      if (a.rentable && a.rentPrice > 0) price.push(UI.money(a.rentPrice, 0) + ' / location');
      list.appendChild(h('div', { class: 'mi-row' },
        h('div', { class: 'mi-avatar', style: 'background: #1b8f4c; border-radius: 10px', text: '⌂' }),
        h('div', { class: 'mi-grow', style: 'min-width: 0' },
          h('div', { class: 'mi-title', text: a.address }),
          h('div', { class: 'mi-sub', text: tab === 'mine' ? (a.owner ? 'Propriétaire' : 'Accès partagé') : (price.join(' · ') || 'Prix sur demande') })),
        gps(a)));
    });
    root.appendChild(list);
    if (tab === 'sale') root.appendChild(h('p', { class: 'mi-sub', style: 'margin: .8rem', text: 'Rendez-vous sur place pour acheter ou louer : le point GPS vous y guide.' }));
  }

  root.appendChild(UI.loading());
  MI.api('/realestate').then(function (d) { data = d; render(); }).catch(function (e) { UI.clear(root); root.appendChild(UI.empty(e.message, '⚠')); });
})();
