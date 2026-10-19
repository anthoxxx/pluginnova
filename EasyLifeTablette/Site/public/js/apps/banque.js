// Application Banque : solde synchronisé depuis le jeu et historique
import { api, h, clear, icon, euro, ago, dateTimeFr, attempt, empty, loading } from '../core.js';

export default {
  id: 'banque',
  name: 'Banque',
  icon: 'bank',
  color: 'linear-gradient(180deg,#4f6bff,#2a3fd1)',
  keywords: 'argent compte solde money',
  open(root) {
    const body = h('div', { class: 'single-body narrow' }, loading());
    clear(root, h('div', { class: 'single' },
      h('header', { class: 'single-head' }, h('div', {}), h('h2', {}, 'Banque'),
        h('div', { class: 'row end' }, h('button', { class: 'icon-btn', title: 'Actualiser', onclick: load }, icon('refresh', 16)))),
      body));

    async function load() {
      const data = await attempt(() => api('banque'));
      if (!data) return;
      const hist = data.history;
      const rows = hist.map((row, i) => {
        const prev = hist[i + 1];
        const diff = prev ? (row.bank - prev.bank) : null;
        return h('div', { class: 'dir-row' },
          h('div', { class: `bank-dot ${diff > 0 ? 'up' : diff < 0 ? 'down' : ''}` }, icon(diff === null ? 'clock' : 'trend', 14)),
          h('div', { class: 'grow' }, h('strong', {}, euro(row.bank)), h('small', { class: 'muted block' }, `Liquide : ${euro(row.money)} · ${dateTimeFr(row.at)}`)),
          diff !== null && diff !== 0 ? h('strong', { class: diff > 0 ? 'text-green' : 'text-red' }, `${diff > 0 ? '+' : ''}${euro(diff)}`) : h('small', { class: 'muted' }, diff === null ? 'Premier relevé' : 'Liquide modifié'));
      });
      clear(body,
        h('div', { class: 'bank-card' },
          h('div', { class: 'row between' }, h('span', {}, 'Compte courant'), icon('bank', 20)),
          h('div', { class: 'bank-amount' }, euro(data.bank)),
          h('div', { class: 'row between' },
            h('span', {}, `Liquide : ${euro(data.money)}`),
            h('small', {}, data.lastSync ? `Synchronisé ${ago(data.lastSync)}` : 'Jamais synchronisé'))),
        h('p', { class: 'muted small' }, data.inGame
          ? 'Vous êtes en ville : votre solde est mis à jour automatiquement chaque minute.'
          : 'Vous êtes hors ligne : le solde affiché est celui de votre dernière connexion en jeu. Les opérations se font en jeu, au distributeur ou à la banque.'),
        h('h3', { class: 'menu-cat' }, 'Évolution du solde'),
        rows.length ? h('div', { class: 'card-list' }, rows) : empty('bank', 'Aucun relevé', 'Connectez-vous en jeu pour synchroniser votre compte.'));
    }

    load();
  },
};
