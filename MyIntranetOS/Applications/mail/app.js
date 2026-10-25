/* Mail : boîtes mail du jeu (entreprises, services) auxquelles le personnage a accès */
(function () {
  'use strict';
  var MI = window.MyIntranet, h = UI.h;
  var root = document.getElementById('app');
  var accounts = null, account = null, mails = [];

  function compose(prefill) {
    var to = h('select', { class: 'mi-input block', id: 'm-to' });
    accounts.directory.forEach(function (a) {
      var o = h('option', { value: a.domain, text: a.name + ' — ' + a.domain });
      if (prefill && prefill.to === a.domain) o.selected = true;
      to.appendChild(o);
    });
    var subject = h('input', { class: 'mi-input block', maxlength: 120, id: 'm-subject', value: prefill && prefill.subject || '' });
    var content = h('textarea', { class: 'mi-input', maxlength: 4000, id: 'm-content', style: 'min-height: 160px' });
    UI.sheet('Nouveau mail · ' + account.name, h('div', { class: 'mi-stack' }, UI.field('À', to), UI.field('Objet', subject), UI.field('Message', content)), [
      { label: 'Annuler' },
      { label: 'Envoyer', primary: true, onclick: function (close) {
        return MI.api('/mail/send', { body: { from: String(account.id), to: to.value, subject: subject.value, content: content.value } }).then(function () {
          close();
          UI.success('Mail envoyé.');
          return openAccount(account);
        });
      } }
    ]);
  }

  function read(m) {
    UI.sheet(m.subject || '(sans objet)', h('div', { class: 'mi-stack' },
      h('div', { class: 'mi-sub', text: 'De : ' + (m.senderName || m.sender) + ' <' + m.sender + '>' }),
      h('div', { class: 'mi-sub', text: 'À : ' + (m.recipientName || m.recipient) }),
      h('div', { style: 'white-space: pre-wrap; line-height: 1.55', text: m.content })), [
      { label: 'Supprimer', danger: true, onclick: function (close) {
        return MI.api('/mail/' + account.id + '/' + encodeURIComponent(m.guid), { method: 'DELETE' }).then(function () { close(); return openAccount(account); });
      } },
      { label: 'Répondre', onclick: function (close) { close(); compose({ to: m.sender === account.domain ? m.recipient : m.sender, subject: 'RE: ' + (m.subject || '') }); } },
      { label: 'Fermer', primary: true }
    ]);
  }

  function openAccount(a) {
    account = a;
    return MI.api('/mail/' + a.id).then(function (list) { mails = list; render(); });
  }

  function render() {
    UI.clear(root);
    if (!accounts.mine.length) {
      root.appendChild(UI.header('Mail'));
      root.appendChild(h('div', { class: 'mi-card' }, UI.empty('Vous n\'avez accès à aucune boîte mail. Les boîtes sont créées en jeu pour les entreprises et services.', '✉')));
      return;
    }
    root.appendChild(UI.header('Mail', account.name + ' · ' + account.domain, h('button', { type: 'button', class: 'mi-btn', text: 'Écrire', onclick: function () { compose(); } })));
    if (accounts.mine.length > 1) {
      root.appendChild(UI.tabs(accounts.mine.map(function (a) { return { id: String(a.id), label: a.name }; }), function (id) {
        openAccount(accounts.mine.filter(function (a) { return String(a.id) === id; })[0]).catch(UI.error);
      }, String(account.id)));
    }
    var list = h('div', { class: 'mi-list' });
    if (!mails.length) list.appendChild(UI.empty('Boîte vide.', '✉'));
    mails.forEach(function (m) {
      var outgoing = m.sender === account.domain;
      list.appendChild(h('div', { class: 'mi-row clickable', onclick: function () { read(m); } },
        UI.avatar(outgoing ? (m.recipientName || m.recipient) : (m.senderName || m.sender)),
        h('div', { class: 'mi-grow', style: 'min-width: 0' },
          h('div', { class: 'mi-title', style: m.read || outgoing ? '' : 'color: var(--mi-accent)', text: (outgoing ? 'À : ' + (m.recipientName || m.recipient) : (m.senderName || m.sender)) }),
          h('div', { class: 'mi-sub', style: 'white-space: nowrap', text: (m.subject || '(sans objet)') + ' — ' + (m.content || '').slice(0, 80) }))));
    });
    root.appendChild(list);
  }

  root.appendChild(UI.loading());
  MI.api('/mail/accounts').then(function (a) {
    accounts = a;
    if (!a.mine.length) { render(); return; }
    return openAccount(a.mine[0]);
  }).catch(function (e) { UI.clear(root); root.appendChild(UI.empty(e.message, '⚠')); });
})();
