/* Radio : stations configurées par le serveur (configuration.json, RadioStations) */
(function () {
  'use strict';
  var MI = window.MyIntranet, h = UI.h;
  var root = document.getElementById('app');
  var audio = new Audio();
  var playing = null;
  audio.volume = 0.7;

  function render(stations) {
    UI.clear(root);
    root.appendChild(UI.header('Radio', playing ? 'En écoute : ' + playing.name : 'Choisissez une station'));
    var vol = h('input', { type: 'range', min: '0', max: '100', id: 'volume' });
    vol.value = String(Math.round(audio.volume * 100));
    vol.addEventListener('input', function () { audio.volume = vol.value / 100; });
    root.appendChild(h('div', { class: 'mi-card mi-row', style: 'min-height: 0' }, h('span', { text: '🔈' }), vol, h('span', { text: '🔊' })));
    root.appendChild(h('h2', { text: 'Stations' }));
    var list = h('div', { class: 'mi-list' });
    if (!stations.length) list.appendChild(UI.empty('Aucune station configurée par le serveur.', '📻'));
    stations.forEach(function (s) {
      var on = playing && playing.url === s.url;
      list.appendChild(h('div', { class: 'mi-row clickable', onclick: function () {
        if (on) { audio.pause(); playing = null; }
        else {
          playing = s;
          audio.src = s.url;
          audio.play().catch(function () { UI.error({ message: 'Lecture impossible pour cette station.' }); playing = null; render(stations); });
        }
        render(stations);
      } },
        h('div', { class: 'mi-avatar', style: 'background: ' + (on ? 'var(--mi-accent)' : '#3d4a1c'), text: on ? '❚❚' : '▶' }),
        h('div', { class: 'mi-grow' }, h('div', { class: 'mi-title', text: s.name }), h('div', { class: 'mi-sub', text: on ? 'En direct' : 'Appuyer pour écouter' }))));
    });
    root.appendChild(list);
  }

  root.appendChild(UI.loading());
  MI.api('/radio').then(render).catch(function (e) { UI.clear(root); root.appendChild(UI.empty(e.message, '⚠')); });
})();
