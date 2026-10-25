/*!
 * MyIntranet OS — application de service générique (taxi, bus, santé, avocats...).
 * Le service est indiqué par data-service sur #app et configuré dans configuration.json (Services).
 * Les demandes sont envoyées aux joueurs en jeu dont l'entreprise a l'activité ou l'identifiant configurés.
 */
(function () {
  'use strict';
  var MI = window.MyIntranet, h = UI.h;
  var root = document.getElementById('app');
  var serviceId = root.getAttribute('data-service');
  var tab = 'request';
  var data = null;
  var form = { message: '', share: true };

  var STATUS = {
    open: ['En attente', 'warn'], accepted: ['Acceptée', 'ok'], done: ['Terminée', ''],
    cancelled: ['Annulée', 'bad'], expired: ['Expirée', '']
  };

  function chip(status) {
    var s = STATUS[status] || [status, ''];
    return h('span', { class: 'mi-chip ' + s[1], text: s[0] });
  }

  function smsButton(number, label) {
    var digits = (number || '').replace(/[^0-9]/g, '');
    if (digits.length < 3) return null;
    return h('button', { type: 'button', class: 'mi-btn small secondary', text: label || 'SMS', onclick: function () { MI.openApp('messages', { number: digits }); } });
  }

  function act(rid, action, okText) {
    return MI.api('/services/requests/' + rid + '/' + action, { body: {} }).then(function (r) {
      if (okText) UI.success(okText + (r && r.gps ? ' Point GPS placé.' : ''));
      MI.refresh();
      return load();
    });
  }

  function requestRow(r, asProvider) {
    var buttons = h('div', { class: 'mi-actions' });
    if (asProvider) {
      if (r.status === 'open') buttons.appendChild(UI.action('Accepter', function () { return act(r.id, 'accept', 'Demande acceptée.'); }, 'small'));
      if (r.acceptedByMe && r.hasPosition) buttons.appendChild(UI.action('GPS', function () { return act(r.id, 'locate', 'Point GPS placé.'); }, 'small secondary'));
      if (r.acceptedByMe) {
        buttons.appendChild(UI.action('Terminer', function () { return act(r.id, 'done', 'Demande terminée.'); }, 'small secondary'));
        buttons.appendChild(UI.action('Se désister', function () { return act(r.id, 'cancel'); }, 'small ghost'));
      }
      buttons.appendChild(smsButton(r.requesterPhone, 'SMS'));
    } else if (r.status === 'open' || r.status === 'accepted') {
      buttons.appendChild(UI.action('Annuler', function () { return act(r.id, 'cancel', 'Demande annulée.'); }, 'small secondary'));
      if (r.status === 'accepted') buttons.appendChild(smsButton(r.acceptedByPhone, 'Contacter'));
    }

    var who = asProvider ? r.requester : (r.acceptedBy ? 'Pris en charge par ' + r.acceptedBy : 'En attente d\'un prestataire');
    return h('div', { class: 'mi-row', style: 'flex-wrap: wrap' },
      h('div', { class: 'mi-grow', style: 'min-width: 0' },
        h('div', { class: 'mi-title' }, who, ' ', chip(r.status)),
        h('div', { class: 'mi-sub', text: (r.message || 'Sans message') + ' · ' + UI.ago(r.createdAt) + (r.hasPosition ? ' · position partagée' : '') })),
      buttons);
  }

  function render() {
    UI.clear(root);
    if (!data) { root.appendChild(UI.loading()); return; }
    var s = data.service;
    var avail = h('span', { class: 'mi-chip ' + (data.available > 0 ? 'ok' : 'warn'), text: data.available > 0 ? data.available + ' disponible(s)' : 'Personne en service' });
    root.appendChild(UI.header(s.name, null, avail));

    if (data.provider) {
      root.appendChild(UI.tabs([{ id: 'request', label: 'Demander' }, { id: 'queue', label: 'Demandes reçues (' + data.queue.length + ')' }], function (t) { tab = t; render(); }, tab));
    }

    if (tab === 'queue' && data.provider) {
      if (!data.onDuty) root.appendChild(h('div', { class: 'mi-card mi-sub', text: 'Vous n\'êtes pas en service : prenez votre service en jeu pour recevoir les demandes.' }));
      root.appendChild(h('h2', { text: 'File d\'attente' }));
      var q = h('div', { class: 'mi-list' });
      if (!data.queue.length) q.appendChild(UI.empty('Aucune demande en attente.', '✓'));
      data.queue.forEach(function (r) { q.appendChild(requestRow(r, true)); });
      root.appendChild(q);
      return;
    }

    if (!s.configured) {
      root.appendChild(h('div', { class: 'mi-card' }, UI.empty('Ce service n\'est pas encore configuré par le serveur.', 'ⓘ')));
    } else {
      var text = h('textarea', { class: 'mi-input', placeholder: s.placeholder, maxlength: 300, id: 'svc-message' });
      text.value = form.message;
      text.addEventListener('input', function () { form.message = text.value; });
      var share = h('input', { type: 'checkbox', id: 'svc-share' });
      share.checked = form.share;
      share.addEventListener('change', function () { form.share = share.checked; });
      root.appendChild(h('div', { class: 'mi-card mi-stack' },
        UI.field('Votre demande', text),
        h('label', { class: 'mi-row', style: 'padding: 0; min-height: 0' }, h('span', { class: 'mi-grow', text: 'Partager ma position (point GPS pour le prestataire)' }),
          h('span', { class: 'mi-switch' }, share, h('span'))),
        h('div', { class: 'mi-actions' }, UI.action(s.requestLabel, function () {
          return MI.api('/services/' + serviceId + '/requests', { body: { message: form.message, sharePosition: form.share } }).then(function (r) {
            form.message = '';
            UI.success('Demande envoyée à ' + r.notified + ' personne(s).');
            return load();
          });
        }))));
    }

    root.appendChild(h('h2', { text: 'Mes demandes' }));
    var mine = h('div', { class: 'mi-list' });
    if (!data.mine.length) mine.appendChild(UI.empty('Aucune demande pour le moment.'));
    data.mine.forEach(function (r) { mine.appendChild(requestRow(r, false)); });
    root.appendChild(mine);

    if (data.businesses.length) {
      root.appendChild(h('h2', { text: 'Entreprises' }));
      var list = h('div', { class: 'mi-list' });
      data.businesses.forEach(function (b) {
        list.appendChild(h('div', { class: 'mi-row' },
          UI.avatar(b.name),
          h('div', { class: 'mi-grow', style: 'min-width: 0' }, h('div', { class: 'mi-title', text: b.name }), h('div', { class: 'mi-sub mi-clamp', text: b.description || b.contact || '' })),
          smsButton(b.contact, 'Contacter')));
      });
      root.appendChild(list);
    }
  }

  function load() {
    return MI.api('/services/' + serviceId).then(function (d) { data = d; render(); }).catch(function (e) {
      UI.clear(root);
      root.appendChild(UI.empty(e.message, '⚠'));
    });
  }

  render();
  load();
  setInterval(function () { if (!document.querySelector('.mi-sheet-bg') && document.activeElement && document.activeElement.id !== 'svc-message') load(); }, 10000);
})();
