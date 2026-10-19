// Application Réglages : compte Steam, personnalisation, sécurité
import { api, h, clear, icon, ago, initials, attempt, form, sideLayout, toast } from '../core.js';

const ACCENTS = ['#0a84ff', '#30d158', '#ff9f0a', '#ff453a', '#bf5af2', '#ff375f', '#64d2ff', '#ffd60a'];

export default {
  id: 'reglages',
  name: 'Réglages',
  icon: 'settings',
  color: 'linear-gradient(180deg,#8e8e93,#545458)',
  keywords: 'parametre compte code fond ecran',
  open(root, ctx) {
    const me = () => ctx.me;
    const profile = () => h('div', { class: 'side-profile' }, avatar(32), h('div', { class: 'ellipsis' }, h('strong', { class: 'block ellipsis' }, me().rpName || me().pseudo || 'Joueur'), h('small', { class: 'muted' }, me().pseudo)));

    const layout = sideLayout(root, {
      appName: 'Réglages',
      iconName: 'settings',
      color: '#636366',
      headerAction: profile(),
      sections: () => [
        { items: [{ id: 'compte', label: 'Compte', icon: 'user' }] },
        { title: 'Personnalisation', items: [
          { id: 'fond', label: 'Fond d\'écran', icon: 'image' },
          { id: 'affichage', label: 'Affichage & couleurs', icon: 'palette' },
          { id: 'accueil', label: 'Écran d\'accueil', icon: 'grid' },
        ] },
        { title: 'Système', items: [
          { id: 'notifications', label: 'Notifications', icon: 'bell' },
          { id: 'securite', label: 'Code & sécurité', icon: 'lock' },
          { id: 'appareils', label: 'Appareils connectés', icon: 'tablet' },
          { id: 'infos', label: 'Informations', icon: 'info' },
        ] },
      ],
      onSelect: (id) => PAGES[id](),
    });

    function avatar(size) {
      return me().avatar
        ? h('img', { class: 'avatar-img', src: me().avatar, alt: '', width: size, height: size, referrerpolicy: 'no-referrer' })
        : h('div', { class: 'avatar', style: { width: `${size}px`, height: `${size}px` } }, initials(me().rpName || me().pseudo));
    }

    async function save(patch, message) {
      const r = await attempt(() => api('reglages', { method: 'PUT', body: patch }), message);
      if (r) {
        ctx.me.settings = r.settings;
        ctx.applySettings();
      }
      return r;
    }

    const group = (title, ...rows) => [title ? h('div', { class: 'group-title' }, title) : null, h('div', { class: 'group' }, rows)];
    const row = (ic, label, value, onclick, cls = '') => h(onclick ? 'button' : 'div', { class: `group-row ${cls}`, onclick }, icon(ic, 16), h('span', { class: 'grow' }, label), value !== undefined ? h('span', { class: 'muted' }, value) : null);
    const toggle = (label, value, onChange) => {
      const input = h('input', { type: 'checkbox', class: 'switch' });
      input.checked = value;
      input.addEventListener('change', () => onChange(input.checked));
      return h('label', { class: 'group-row' }, h('span', { class: 'grow' }, label), input);
    };

    const PAGES = {
      compte() {
        layout.setTitle('Compte');
        const m = me();
        clear(layout.content, h('div', { class: 'settings-page' },
          h('div', { class: 'profile-card' }, avatar(64),
            h('div', {}, h('h2', {}, m.rpName || m.pseudo || 'Joueur'),
              h('div', { class: 'muted' }, m.job ? `${m.job}` : 'Métier inconnu'),
              h('span', { class: `pill ${m.inGame ? 'green' : ''}` }, h('span', { class: `sb-dot ${m.inGame ? 'on' : ''}` }), m.inGame ? 'En ville' : 'Hors du jeu'))),
          group('Compte Steam',
            row('user', 'Pseudo', m.pseudo || '—'),
            row('key', 'SteamID', m.steamId),
            m.isAdmin ? row('shield', 'Rôle', 'Staff') : null,
            m.isJournalist ? row('news', 'Rôle', 'Journaliste') : null),
          group(null,
            row('lock', 'Verrouiller la tablette', undefined, async () => {
              if (!m.hasPin) { toast('Définissez d\'abord un code dans « Code & sécurité ».', 'error'); return layout.select('securite'); }
              await attempt(() => api('verrouiller', { method: 'POST', body: {} }));
              window.dispatchEvent(new CustomEvent('tablette:session', { detail: 423 }));
            }),
            row('logout', 'Se déconnecter', undefined, async () => {
              await attempt(() => api('/auth/deconnexion', { method: 'POST', body: {} }));
              location.hash = '';
              window.dispatchEvent(new CustomEvent('tablette:session', { detail: 401 }));
            }, 'danger'))));
      },

      fond() {
        layout.setTitle('Fond d\'écran');
        const current = me().settings.wallpaper || 'aurore';
        clear(layout.content, h('div', { class: 'wallpapers' }, Object.entries(ctx.WALLPAPERS).map(([id, name]) => h('button', {
          class: `wallpaper-choice ${current === id ? 'active' : ''}`,
          onclick: async () => { if (await save({ wallpaper: id }, 'Fond d\'écran modifié.')) PAGES.fond(); },
        }, h('div', { class: 'wp-preview', 'data-wallpaper': id }), h('span', {}, name)))));
      },

      affichage() {
        layout.setTitle('Affichage & couleurs');
        const s = me().settings;
        clear(layout.content, h('div', { class: 'settings-page' },
          h('div', { class: 'group-title' }, 'Couleur d\'accentuation'),
          h('div', { class: 'swatches' }, ACCENTS.map((c) => h('button', {
            class: `swatch ${(s.accent || '#0a84ff') === c ? 'active' : ''}`, style: { background: c }, title: c,
            onclick: async () => { if (await save({ accent: c })) PAGES.affichage(); },
          }))),
          h('div', { class: 'group-title' }, 'Taille du texte'),
          h('div', { class: 'chips' }, [['petit', 'Petit'], ['normal', 'Normal'], ['grand', 'Grand']].map(([v, label]) => h('button', {
            class: `chip ${(s.textSize || 'normal') === v ? 'active' : ''}`,
            onclick: async () => { if (await save({ textSize: v })) PAGES.affichage(); },
          }, label)))));
      },

      accueil() {
        layout.setTitle('Écran d\'accueil');
        const s = me().settings;
        const hidden = new Set(s.hiddenApps || []);
        const apps = [['annuaire', 'Annuaire'], ['actualites', 'Actualités'], ['bonplan', 'Le Bon Plan'], ['commander', 'Commander']];
        clear(layout.content, h('div', { class: 'settings-page' },
          group('Widgets',
            toggle('Afficher « Mon argent »', s.showMoney !== false, (v) => save({ showMoney: v })),
            toggle('Afficher les actualités', s.showNews !== false, (v) => save({ showNews: v }))),
          group('Applications affichées', ...apps.map(([id, name]) => toggle(name, !hidden.has(id), (v) => {
            if (v) hidden.delete(id); else hidden.add(id);
            save({ hiddenApps: [...hidden] });
          })))));
      },

      notifications() {
        layout.setTitle('Notifications');
        const s = me().settings;
        clear(layout.content, h('div', { class: 'settings-page' },
          group(null, toggle('Recevoir aussi les notifications en jeu', s.gameNotifications !== false, (v) => save({ gameNotifications: v }, 'Réglage enregistré.'))),
          h('p', { class: 'muted small' }, 'Quand vous êtes en ville, les nouveaux mails, messages du Bon Plan et suivis de commande s\'affichent aussi à l\'écran du jeu.')));
      },

      securite() {
        layout.setTitle('Code & sécurité');
        const m = me();
        const fields = [];
        if (m.hasPin) fields.push({ name: 'current', label: 'Code actuel', type: 'password', inputmode: 'numeric', required: true });
        fields.push({ name: 'pin', label: m.hasPin ? 'Nouveau code (4 à 8 chiffres)' : 'Code (4 à 8 chiffres)', type: 'password', inputmode: 'numeric', required: true });
        clear(layout.content, h('div', { class: 'settings-page' },
          h('p', { class: 'muted' }, 'Le code protège votre tablette : il est demandé à chaque nouvelle connexion et quand vous la verrouillez.'),
          h('div', { class: 'card' }, form(fields, m.hasPin ? 'Changer le code' : 'Activer le code', async (v) => {
            const r = await attempt(() => api('code-securite', { method: 'PUT', body: v }), 'Code enregistré.');
            if (r) { await ctx.refreshMe(); PAGES.securite(); }
          })),
          m.hasPin ? h('div', { class: 'card' }, form([{ name: 'current', label: 'Code actuel', type: 'password', inputmode: 'numeric', required: true }], 'Désactiver le code', async (v) => {
            const r = await attempt(() => api('code-securite', { method: 'PUT', body: { pin: null, current: v.current } }), 'Code désactivé.');
            if (r) { await ctx.refreshMe(); PAGES.securite(); }
          })) : null));
      },

      async appareils() {
        layout.setTitle('Appareils connectés');
        const data = await attempt(() => api('appareils'));
        if (!data) return;
        clear(layout.content, h('div', { class: 'settings-page' },
          group(null, ...data.sessions.map((s) => h('div', { class: 'group-row' }, icon(s.via === 'steam' ? 'tablet' : 'gamepad', 16),
            h('div', { class: 'grow' }, h('strong', { class: 'block' }, s.via === 'steam' ? 'Connexion Steam' : 'Connexion par code en jeu'), h('small', { class: 'muted' }, `Ouverte ${ago(s.createdAt)} · active ${ago(s.lastSeen)}`)),
            s.current ? h('span', { class: 'pill green' }, 'Cet appareil') : null))),
          data.sessions.length > 1 ? h('button', { class: 'btn btn-danger', onclick: async () => {
            if (await attempt(() => api('appareils/deconnecter-autres', { method: 'POST', body: {} }), 'Les autres appareils ont été déconnectés.')) PAGES.appareils();
          } }, 'Déconnecter les autres appareils') : null));
      },

      infos() {
        layout.setTitle('Informations');
        clear(layout.content, h('div', { class: 'settings-page' },
          group(null,
            row('tablet', 'Serveur', me().serverName),
            row('info', 'Version', '1.0.0'),
            row('mail', 'Domaine mail', `@${me().mailDomain}`)),
          h('div', { class: 'card' },
            h('h4', {}, 'Se connecter à la tablette'),
            h('ul', { class: 'help' },
              h('li', {}, 'Sur le web : « Se connecter avec Steam ».'),
              h('li', {}, 'En jeu : tapez /tablette dans le chat, puis saisissez le code affiché sur cette page depuis l\'ordinateur (F2).')))));
      },
    };

    layout.select('compte');
  },
};
