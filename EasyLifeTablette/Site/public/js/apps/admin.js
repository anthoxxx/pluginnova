// Application Administration (staff) + sélecteur de joueur partagé
import { api, h, clear, icon, ago, attempt, modal, confirmBox, empty, loading } from '../core.js';

/** Fenêtre de recherche d'un joueur connu de la tablette ; appelle onPick({ steamId, name }). */
export function pickPlayer(title, onPick) {
  const input = h('input', { class: 'search-input', placeholder: 'Nom RP, pseudo Steam ou SteamID64…' });
  const results = h('div', { class: 'search-results' }, h('p', { class: 'muted small' }, 'Tapez au moins 2 caractères. Le joueur doit s\'être déjà connecté au serveur ou à la tablette.'));
  let timer;
  const { close } = modal(title, h('div', {}, input, results));
  input.addEventListener('input', () => {
    clearTimeout(timer);
    timer = setTimeout(async () => {
      const data = await attempt(() => api(`joueurs?q=${encodeURIComponent(input.value.trim())}`));
      if (!data) return;
      clear(results, data.players.length ? data.players.map((p) => h('button', { class: 'search-row', onclick: () => { close(); onPick(p); } },
        h('div', { class: 'avatar small' }, p.name[0] || '?'),
        h('div', { class: 'grow' }, h('strong', {}, p.name), h('small', { class: 'muted block' }, `${p.pseudo || ''} · ${p.steamId}`)),
        p.inGame ? h('span', { class: 'pill green' }, 'En ville') : null)) : h('p', { class: 'muted small' }, 'Aucun joueur trouvé.'));
    }, 250);
  });
}

export default {
  id: 'admin',
  name: 'Administration',
  icon: 'shield',
  color: 'linear-gradient(180deg,#34d399,#059669)',
  adminOnly: true,
  keywords: 'staff moderation admin',
  open(root) {
    const body = h('div', { class: 'single-body narrow' }, loading());
    clear(root, h('div', { class: 'single' },
      h('header', { class: 'single-head' }, h('div', {}), h('h2', {}, 'Administration'),
        h('div', { class: 'row end' }, h('button', { class: 'icon-btn', title: 'Actualiser', onclick: load }, icon('refresh', 16)))),
      body));

    async function load() {
      const data = await attempt(() => api('admin'));
      if (!data) return;
      const s = data.stats;
      const stat = (label, value, ic) => h('div', { class: 'stat' }, icon(ic, 16), h('strong', {}, value), h('small', { class: 'muted' }, label));
      clear(body,
        h('div', { class: 'stats' },
          stat('Joueurs inscrits', s.users, 'users'), stat('En ville', s.inGame, 'gamepad'), stat('Annonces en ligne', s.ads, 'tag'),
          stat('Adresses mail', s.mails, 'mail'), stat('Commandes', s.orders, 'receipt')),

        h('div', { class: 'row between' }, h('h3', { class: 'menu-cat' }, 'Journalistes (Actualités)'),
          h('button', { class: 'btn', onclick: () => pickPlayer('Ajouter un journaliste', async (p) => {
            if (await attempt(() => api(`admin/journalistes/${p.steamId}`, { method: 'PUT', body: { value: true } }), `${p.name} peut publier des articles.`)) load();
          }) }, icon('plus', 14), 'Ajouter')),
        data.journalists.length ? h('div', { class: 'card-list' }, data.journalists.map((j) => h('div', { class: 'dir-row' },
          h('div', { class: 'avatar' }, j.name[0] || '?'), h('div', { class: 'grow' }, h('strong', {}, j.name), h('small', { class: 'muted block' }, j.steamId)),
          h('button', { class: 'icon-btn danger', title: 'Retirer', onclick: async () => {
            if (await attempt(() => api(`admin/journalistes/${j.steamId}`, { method: 'PUT', body: { value: false } }))) load();
          } }, icon('trash', 14))))) : h('p', { class: 'muted small' }, 'Aucun journaliste : seul le staff peut publier.'),

        h('h3', { class: 'menu-cat' }, 'Signalements du Bon Plan'),
        data.reports.length ? h('div', { class: 'card-list' }, data.reports.map((r) => h('div', { class: 'dir-row' },
          icon('flag', 16),
          h('div', { class: 'grow' }, h('strong', {}, r.adTitle), h('small', { class: 'muted block' }, `${r.by} · ${ago(r.createdAt)} : ${r.reason}`)),
          h('a', { class: 'btn', href: `#bonplan/${r.adId}` }, 'Voir'),
          h('button', { class: 'btn btn-danger', onclick: async () => {
            if (!await confirmBox(`Supprimer l'annonce « ${r.adTitle} » ?`, 'Supprimer')) return;
            if (await attempt(() => api(`bonplan/annonces/${r.adId}`, { method: 'DELETE', body: {} }), 'Annonce supprimée.')) load();
          } }, 'Supprimer l\'annonce'),
          h('button', { class: 'icon-btn', title: 'Ignorer le signalement', onclick: async () => {
            if (await attempt(() => api(`admin/signalements/${r.id}`, { method: 'DELETE', body: {} }))) load();
          } }, icon('x', 14))))) : empty('flag', 'Aucun signalement', null),

        h('h3', { class: 'menu-cat' }, 'Aide'),
        h('div', { class: 'card' }, h('ul', { class: 'help' },
          h('li', {}, 'Annuaire : les entreprises du serveur sont ajoutées automatiquement ; le bouton « Ajouter » de l\'Annuaire crée des fiches supplémentaires (mairie, police…).'),
          h('li', {}, 'Commander : « Nouveau restaurant » crée un restaurant et son gérant. Le gérant compose la carte et son équipe ; le restaurant est ouvert dès qu\'un équipier prend son service.'),
          h('li', {}, 'Actualités : le staff et les journalistes publient ; le staff peut épingler un article à la une.'))));
    }

    load();
  },
};
