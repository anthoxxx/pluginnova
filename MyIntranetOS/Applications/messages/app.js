/* Messages : SMS du téléphone du jeu (lecture et envoi en direct) */
(function () {
  'use strict';
  var MI = window.MyIntranet, h = UI.h;
  var root = document.getElementById('app');
  var current = null;
  var timer = null;

  function list() {
    current = null;
    return MI.api('/sms').then(function (convs) {
      UI.clear(root);
      root.appendChild(UI.header('Messages', null, h('button', { type: 'button', class: 'mi-btn', text: 'Nouveau', onclick: compose })));
      var box = h('div', { class: 'mi-list' });
      if (!convs.length) box.appendChild(UI.empty('Aucune conversation.', '💬'));
      convs.forEach(function (c) {
        box.appendChild(h('div', { class: 'mi-row clickable', onclick: function () { open(c.number); } },
          UI.avatar(c.name || c.number),
          h('div', { class: 'mi-grow', style: 'min-width: 0' },
            h('div', { class: 'mi-title', text: c.name || c.number }),
            h('div', { class: 'mi-sub', style: 'white-space: nowrap', text: (c.lastFromMe ? 'Vous : ' : '') + c.last })),
          h('span', { class: 'mi-sub', text: UI.ago(c.timestamp) })));
      });
      root.appendChild(box);
    });
  }

  function compose() {
    var number = h('input', { class: 'mi-input block', placeholder: 'Numéro', inputmode: 'tel', id: 'new-number' });
    UI.sheet('Nouveau message', UI.field('Destinataire', number), [
      { label: 'Annuler' },
      { label: 'Ouvrir', primary: true, onclick: function (close) {
        var n = number.value.replace(/[^0-9]/g, '');
        if (n.length < 3) { UI.error({ message: 'Numéro invalide.' }); return; }
        close();
        open(n);
      } }
    ]);
  }

  function open(number) {
    current = number;
    return MI.api('/sms/' + encodeURIComponent(number)).then(function (conv) {
      if (current !== number) return;
      UI.clear(root);
      var back = h('button', { type: 'button', class: 'mi-btn ghost', text: '‹ Conversations', onclick: list });
      root.appendChild(h('div', { class: 'mi-header' }, h('div', null, back, h('h1', { text: conv.name || conv.number }), conv.name ? h('p', { text: conv.number }) : null)));
      var thread = h('div', { class: 'mi-stack', style: 'gap: 6px; margin-bottom: 1rem' });
      if (!conv.messages.length) thread.appendChild(UI.empty('Écrivez le premier message.'));
      conv.messages.forEach(function (m) {
        thread.appendChild(h('div', {
          style: 'max-width: 78%; padding: .55rem .8rem; border-radius: 18px; white-space: pre-wrap; word-break: break-word; ' +
            (m.fromMe ? 'justify-self: end; background: var(--mi-accent); color: #fff; border-bottom-right-radius: 6px'
                      : 'justify-self: start; background: var(--mi-surface-2); border-bottom-left-radius: 6px'),
          title: UI.date(m.timestamp) + ' ' + UI.time(m.timestamp), text: m.message
        }));
      });
      root.appendChild(thread);
      var input = h('input', { class: 'mi-input mi-grow', placeholder: 'Message', maxlength: 500, id: 'sms-input' });
      var send = function () {
        var text = input.value.trim();
        if (!text) return Promise.resolve();
        return MI.api('/sms', { body: { to: number, message: text } }).then(function () { input.value = ''; return open(number); });
      };
      input.addEventListener('keydown', function (e) { if (e.key === 'Enter') send().catch(UI.error); });
      root.appendChild(h('div', { class: 'mi-row', style: 'position: sticky; bottom: 0; background: var(--mi-bg); padding: .6rem 0' }, input, UI.action('Envoyer', send)));
      window.scrollTo(0, document.body.scrollHeight);
      input.focus();
    });
  }

  root.appendChild(UI.loading());
  var p = MI.params();
  (p.number ? open(p.number.replace(/[^0-9]/g, '')) : list()).catch(function (e) { UI.clear(root); root.appendChild(UI.empty(e.message, '⚠')); });
  timer = setInterval(function () {
    if (current && document.activeElement && document.activeElement.id === 'sms-input' && document.activeElement.value) return;
    (current ? open(current) : list()).catch(function () {});
  }, 8000);
})();
