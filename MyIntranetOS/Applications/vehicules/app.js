/* Véhicules : véhicules du personnage (propriétaire ou co-propriétaire) avec GPS en jeu */
(function () {
  'use strict';
  var MI = window.MyIntranet, h = UI.h;
  var root = document.getElementById('app');

  function load() {
    return MI.api('/vehicles').then(function (list) {
      UI.clear(root);
      root.appendChild(UI.header('Véhicules', list.length + ' véhicule(s)'));
      if (!list.length) { root.appendChild(h('div', { class: 'mi-card' }, UI.empty('Aucun véhicule à votre nom.', '🚗'))); return; }
      var grid = h('div', { class: 'mi-grid2' });
      list.forEach(function (v) {
        var fuel = Math.max(0, Math.min(100, v.fuel));
        grid.appendChild(h('div', { class: 'mi-card mi-stack', style: 'gap: .6rem' },
          h('div', { class: 'mi-row', style: 'padding: 0; min-height: 0' },
            h('div', { class: 'mi-grow' }, h('div', { class: 'mi-title', text: v.model }), h('div', { class: 'mi-sub', text: v.owner ? 'Propriétaire' : 'Co-propriétaire' })),
            h('span', { style: 'font-family: ui-monospace, monospace; font-weight: 800; border: 2px solid var(--mi-text); border-radius: 6px; padding: 2px 8px; background: #fff; color: #111', text: v.plate || '—' })),
          h('div', { class: 'mi-sub', text: 'Carburant ' + fuel + ' %' }),
          h('div', { style: 'height: 6px; border-radius: 6px; background: var(--mi-surface-2); overflow: hidden' },
            h('div', { style: 'height: 100%; width: ' + fuel + '%; background: ' + (fuel < 20 ? 'var(--mi-danger)' : 'var(--mi-success)') })),
          h('div', { class: 'mi-actions', style: 'justify-content: space-between; align-items: center' },
            h('span', { class: 'mi-chip ' + (v.stowed ? '' : 'ok'), text: v.stowed ? 'Au garage' : 'Sorti' }),
            v.stowed ? null : UI.action('Localiser (GPS)', function () {
              return MI.api('/vehicles/' + v.id + '/locate', { body: {} }).then(function () { UI.success('Point GPS placé sur votre véhicule.'); });
            }, 'small'))));
      });
      root.appendChild(grid);
    });
  }

  root.appendChild(UI.loading());
  load().catch(function (e) { UI.clear(root); root.appendChild(UI.empty(e.message, '⚠')); });
})();
