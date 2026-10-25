/* Portefeuille : argent, carte d'identité et permis (données du personnage en jeu) */
(function () {
  'use strict';
  var MI = window.MyIntranet, h = UI.h;
  var root = document.getElementById('app');

  function card(title, gradient, rows) {
    return h('div', { class: 'mi-card', style: 'background: ' + gradient + '; color: #fff; min-height: 150px; display: grid; gap: 6px; align-content: start' },
      h('div', { style: 'font-size: .78rem; font-weight: 800; letter-spacing: .06em; text-transform: uppercase; opacity: .85', text: title }),
      rows);
  }

  function line(label, value) {
    return h('div', { style: 'display: flex; justify-content: space-between; gap: 12px' },
      h('span', { style: 'opacity: .8', text: label }), h('b', { text: value === null || value === undefined || value === '' ? '—' : String(value) }));
  }

  root.appendChild(UI.loading());
  MI.api('/wallet').then(function (w) {
    UI.clear(root);
    root.appendChild(UI.header('Portefeuille', w.fullName));
    var grid = h('div', { class: 'mi-grid2' });
    if (w.bank !== null) {
      grid.appendChild(card('Argent', 'linear-gradient(135deg, #30d158, #0b7a35)', [
        h('div', { style: 'font-size: 2rem; font-weight: 800', text: UI.money(w.bank) }),
        h('div', { style: 'opacity: .85', text: 'Solde bancaire' }),
        line('Espèces', UI.money(w.money)),
        line('Carte bancaire', w.hasBankCard ? 'Oui' : 'Non')
      ]));
    }
    grid.appendChild(card('Carte d\'identité', 'linear-gradient(135deg, #0a84ff, #1e3a8a)', [
      h('div', { style: 'font-size: 1.3rem; font-weight: 800', text: (w.lastName || '').toUpperCase() + ' ' + (w.firstName || '') }),
      line('Né(e) le', w.birthday),
      line('Commune', w.commune),
      line('Taille', w.height ? w.height + ' cm' : null),
      line('N°', w.idCard)
    ]));
    grid.appendChild(card('Permis de conduire', 'linear-gradient(135deg, #ff375f, #8e1a3a)', [
      h('div', { style: 'font-size: 1.3rem; font-weight: 800', text: w.permisB ? 'Catégorie B' : 'Aucun permis' }),
      line('Points', w.permisB ? w.permisPoints + ' / 12' : null)
    ]));
    grid.appendChild(card('Informations', 'linear-gradient(135deg, #5e5ce6, #2c2a8a)', [
      line('Téléphone', w.phoneNumber),
      line('Entreprise', w.biz && w.biz.name),
      line('Niveau', w.level)
    ]));
    root.appendChild(grid);
  }).catch(function (e) { UI.clear(root); root.appendChild(UI.empty(e.message, '⚠')); });
})();
