/* Banque : solde réel, historique des opérations et virements entre joueurs */
(function () {
  'use strict';
  var MI = window.MyIntranet, h = UI.h;
  var root = document.getElementById('app');
  var data = null;

  function transfer(prefill) {
    var to = h('input', { class: 'mi-input block', placeholder: 'Numéro de téléphone ou Prénom Nom', id: 'tr-to' });
    var amount = h('input', { class: 'mi-input block', type: 'number', min: '1', step: '0.01', placeholder: '0,00', id: 'tr-amount' });
    var label = h('input', { class: 'mi-input block', placeholder: 'Loyer, remboursement…', maxlength: 60, id: 'tr-label' });
    if (prefill) to.value = prefill;
    UI.sheet('Nouveau virement', h('div', { class: 'mi-stack' },
      UI.field('Bénéficiaire', to), UI.field('Montant (€) · max ' + UI.money(data.maxTransfer, 0), amount), UI.field('Libellé', label),
      h('div', { class: 'mi-sub', text: 'Le montant est débité immédiatement. Si le bénéficiaire est hors ligne, il le recevra à sa prochaine connexion.' })), [
      { label: 'Annuler' },
      { label: 'Envoyer', primary: true, onclick: function (close) {
        var value = parseFloat(String(amount.value).replace(',', '.'));
        if (!to.value.trim() || !(value > 0)) { UI.error({ message: 'Bénéficiaire et montant obligatoires.' }); return; }
        return UI.confirm('Envoyer ' + UI.money(value) + ' ?', 'Envoyer').then(function (ok) {
          if (!ok) return;
          return MI.api('/bank/transfer', { body: { to: to.value.trim(), amount: value, label: label.value.trim() || 'Virement' } }).then(function (r) {
            close();
            UI.success(UI.money(r.amount) + ' envoyés à ' + r.to + (r.delivered ? '.' : ' (crédité à sa prochaine connexion).'));
            MI.refresh();
            return load();
          });
        });
      } }
    ]);
  }

  function render() {
    UI.clear(root);
    root.appendChild(UI.header('Banque', 'Compte courant'));
    root.appendChild(h('div', { class: 'mi-card', style: 'background: linear-gradient(135deg, #7b6cff, #3a2fb0); color: #fff; display: grid; gap: 4px' },
      h('div', { style: 'opacity: .85', text: 'Solde disponible' }),
      h('div', { style: 'font-size: 2.3rem; font-weight: 800; letter-spacing: -.02em', text: UI.money(data.bank) }),
      h('div', { style: 'opacity: .8; font-size: .85rem', text: data.iban + (data.money !== null ? ' · ' + UI.money(data.money) + ' en espèces' : '') }),
      h('div', { class: 'mi-actions', style: 'justify-content: flex-start; margin-top: 8px' },
        h('button', { type: 'button', class: 'mi-btn', style: 'background: #fff; color: #3a2fb0', text: 'Faire un virement', onclick: function () { transfer(); } }))));

    root.appendChild(h('h2', { text: 'Opérations récentes' }));
    var list = h('div', { class: 'mi-list' });
    if (!data.history.length) list.appendChild(UI.empty('Aucune opération enregistrée depuis l\'installation de la tablette.'));
    data.history.forEach(function (x) {
      list.appendChild(h('div', { class: 'mi-row' },
        h('div', { class: 'mi-grow', style: 'min-width: 0' }, h('div', { class: 'mi-title', text: x.reason }), h('div', { class: 'mi-sub', text: UI.date(x.createdAt) + ' · ' + UI.time(x.createdAt) })),
        h('span', { class: 'mi-amount' + (x.amount > 0 ? ' pos' : ''), text: (x.amount > 0 ? '+' : '') + UI.money(x.amount) })));
    });
    root.appendChild(list);
  }

  function load() {
    return MI.api('/bank').then(function (d) { data = d; render(); });
  }

  root.appendChild(UI.loading());
  load().then(function () {
    var p = MI.params();
    if (p.to) transfer(p.to);
  }).catch(function (e) { UI.clear(root); root.appendChild(UI.empty(e.message, '⚠')); });
})();
