// Démarrage de la tablette : connexion, verrouillage, écran d'accueil et ouverture des applications
import { api, h, clear, icon, euro, ago, timeFr, toast, attempt, modal } from './core.js';
import mail from './apps/mail.js';
import annuaire from './apps/annuaire.js';
import actualites from './apps/actualites.js';
import bonplan from './apps/bonplan.js';
import commander from './apps/commander.js';
import banque from './apps/banque.js';
import notes from './apps/notes.js';
import reglages from './apps/reglages.js';
import admin from './apps/admin.js';

const APPS = [mail, annuaire, actualites, bonplan, commander, admin, banque, notes, reglages];
const DOCK = ['banque', 'notes', 'mail', 'reglages'];
export const WALLPAPERS = {
  aurore: 'Aurore',
  ocean: 'Océan',
  nuit: 'Nuit étoilée',
  coucher: 'Coucher de soleil',
  foret: 'Forêt',
  graphite: 'Graphite',
};

const screen = document.getElementById('screen');
const state = { me: null, home: null, lastNotifId: null, cleanup: null, timers: [] };

const ctx = {
  get me() { return state.me; },
  refreshMe,
  openApp: (id, param) => { location.hash = param !== undefined ? `${id}/${encodeURIComponent(param)}` : id; },
  goHome: () => { location.hash = ''; },
  applySettings,
  WALLPAPERS,
};

// ------------------------------------------------------------ barre d'état
function statusBar() {
  const clock = h('span', { class: 'sb-time' });
  const badge = h('span', { class: 'sb-badge' });
  const tick = () => {
    const d = new Date();
    clock.textContent = `${timeFr(d)}  ${d.toLocaleDateString('fr-FR', { weekday: 'short', day: 'numeric', month: 'short' })}`;
    const n = state.home?.unreadNotifications || 0;
    badge.textContent = n > 9 ? '9+' : String(n);
    badge.style.display = n ? '' : 'none';
  };
  tick();
  state.timers.push(setInterval(tick, 10e3));
  const inGame = state.me?.inGame;
  return h('div', { class: 'status-bar' },
    h('div', { class: 'sb-left' }, clock, badge),
    h('div', { class: 'sb-right' },
      h('span', { class: `sb-dot ${inGame ? 'on' : ''}` }), h('span', {}, inGame ? 'En ville' : 'Hors ligne'),
      icon('wifi', 14)));
}

function homeIndicator() {
  return h('button', { class: 'home-indicator', title: 'Accueil', onclick: () => ctx.goHome() });
}

// ------------------------------------------------------------ session
async function refreshMe() {
  state.me = await api('moi');
  applySettings();
  return state.me;
}

function applySettings() {
  const s = state.me?.settings || {};
  const device = document.getElementById('device');
  device.dataset.wallpaper = WALLPAPERS[s.wallpaper] ? s.wallpaper : 'aurore';
  device.dataset.textSize = s.textSize || 'normal';
  document.documentElement.style.setProperty('--accent', s.accent || '#0a84ff');
}

function stopTimers() {
  state.timers.forEach(clearInterval);
  state.timers = [];
}

window.addEventListener('tablette:session', (e) => {
  if (e.detail === 401) showLogin();
  else if (e.detail === 423) showLock();
});

// ------------------------------------------------------------ connexion
function showLogin() {
  stopTimers();
  state.me = null;
  document.getElementById('device').dataset.wallpaper = 'aurore';
  const params = new URLSearchParams(location.search);
  const codeInput = h('input', { class: 'code-input', maxlength: 8, placeholder: 'CODE', autocomplete: 'off', spellcheck: 'false' });
  const submit = async (e) => {
    e.preventDefault();
    const code = codeInput.value.trim();
    if (!code) return;
    const ok = await attempt(() => api('/auth/code', { method: 'POST', body: { code } }));
    if (ok) start();
  };
  clear(screen,
    h('div', { class: 'login' },
      h('div', { class: 'login-card' },
        h('div', { class: 'login-logo' }, icon('tablet', 34)),
        h('h1', {}, 'Tablette Easy Life'),
        h('p', { class: 'muted' }, 'Mail, annuaire, petites annonces, commandes, actualités… Toute la ville dans votre poche, en jeu comme sur le web.'),
        params.get('erreur') === 'steam' ? h('div', { class: 'alert alert-error' }, 'La connexion Steam a échoué, réessayez.') : null,
        h('a', { class: 'btn btn-steam', href: '/auth/steam' }, steamLogo(), 'Se connecter avec Steam'),
        h('div', { class: 'login-sep' }, h('span', {}, 'ou, depuis le jeu')),
        h('form', { class: 'code-form', onsubmit: submit },
          codeInput,
          h('button', { class: 'btn btn-primary', type: 'submit' }, 'Valider')),
        h('p', { class: 'muted small' }, 'En jeu, tapez ', h('code', {}, '/tablette'), ' dans le chat pour recevoir votre code de connexion (valable 10 minutes).'))),
  );
  if (params.has('erreur')) history.replaceState(null, '', '/');
}

function steamLogo() {
  const span = document.createElement('span');
  span.className = 'ico';
  span.innerHTML = '<svg width="20" height="20" viewBox="0 0 24 24" fill="currentColor"><path d="M11.98 0C5.67 0 .5 4.86 0 11.04l6.43 2.66a3.4 3.4 0 0 1 1.92-.59l.19.01 2.86-4.15v-.06a4.53 4.53 0 1 1 4.53 4.53h-.1l-4.08 2.91.01.16a3.4 3.4 0 0 1-6.75.59L.4 15.27A12 12 0 1 0 11.98 0zM7.54 18.21l-1.47-.61a2.55 2.55 0 1 0 1.4-3.47l1.52.63a1.88 1.88 0 1 1-1.45 3.45zm11.4-9.27a3.02 3.02 0 1 0-6.04 0 3.02 3.02 0 0 0 6.04 0zm-5.28 0a2.27 2.27 0 1 1 4.54 0 2.27 2.27 0 0 1-4.54 0z"/></svg>';
  return span;
}

// ------------------------------------------------------------ verrouillage
function showLock() {
  stopTimers();
  let pin = '';
  const dots = h('div', { class: 'pin-dots' });
  const msg = h('div', { class: 'pin-msg' });
  const time = h('div', { class: 'lock-time' });
  const date = h('div', { class: 'lock-date' });
  const tick = () => {
    const d = new Date();
    time.textContent = timeFr(d);
    date.textContent = d.toLocaleDateString('fr-FR', { weekday: 'long', day: 'numeric', month: 'long' });
  };
  tick();
  state.timers.push(setInterval(tick, 10e3));

  const draw = () => clear(dots, ...Array.from({ length: Math.max(4, pin.length) }, (_, i) => h('span', { class: i < pin.length ? 'full' : '' })));
  const press = async (k) => {
    if (k === 'del') pin = pin.slice(0, -1);
    else if (k === 'ok') {
      if (pin.length < 4) return;
      try {
        await api('deverrouiller', { method: 'POST', body: { pin } });
        start();
        return;
      } catch (e) {
        msg.textContent = e.message;
        pin = '';
      }
    } else if (pin.length < 8) pin += k;
    draw();
  };
  draw();
  const keys = ['1', '2', '3', '4', '5', '6', '7', '8', '9', 'del', '0', 'ok'];
  clear(screen, h('div', { class: 'lock' },
    statusBarLite(),
    h('div', { class: 'lock-head' }, icon('lock', 22), time, date),
    h('div', { class: 'pin' }, h('p', {}, 'Saisissez votre code'), dots, msg,
      h('div', { class: 'pin-pad' }, keys.map((k) => h('button', { class: `pin-key ${k.length > 1 ? 'pin-fn' : ''}`, onclick: () => press(k) },
        k === 'del' ? icon('back', 22) : k === 'ok' ? icon('check', 22) : k)))),
    h('button', { class: 'link', onclick: async () => { await api('/auth/deconnexion', { method: 'POST', body: {} }); showLogin(); } }, 'Changer de compte')));

  const onKey = (e) => {
    if (!document.querySelector('.lock')) return document.removeEventListener('keydown', onKey);
    if (/^\d$/.test(e.key)) press(e.key);
    else if (e.key === 'Backspace') press('del');
    else if (e.key === 'Enter') press('ok');
  };
  document.addEventListener('keydown', onKey);
}

function statusBarLite() {
  return h('div', { class: 'status-bar' }, h('div', { class: 'sb-left' }), h('div', { class: 'sb-right' }, icon('lock', 14)));
}

// ------------------------------------------------------------ accueil
function visibleApps() {
  const hidden = state.me.settings.hiddenApps || [];
  return APPS.filter((a) => (!a.adminOnly || state.me.isAdmin) && !hidden.includes(a.id));
}

function appIcon(app, { label = true } = {}) {
  const badge = state.home?.badges?.[app.id];
  return h('button', { class: 'app', onclick: () => ctx.openApp(app.id) },
    h('div', { class: 'app-icon', style: { background: app.color, color: app.iconColor || '#fff' } }, icon(app.icon, 30), badge ? h('span', { class: 'app-badge' }, badge > 99 ? '99+' : badge) : null),
    label ? h('span', { class: 'app-label' }, app.name) : null);
}

function renderHome() {
  stopTimers();
  const me = state.me;
  const home = state.home || { notifications: [], news: [], badges: {} };
  const settings = me.settings;
  const now = new Date();
  const dock = DOCK.map((id) => APPS.find((a) => a.id === id)).filter(Boolean);
  const gridApps = visibleApps().filter((a) => !DOCK.includes(a.id));

  const widgets = h('div', { class: 'widgets' },
    h('div', { class: 'widget-row' },
      h('div', { class: 'widget w-date' },
        h('div', { class: 'w-day' }, now.toLocaleDateString('fr-FR', { weekday: 'long' })),
        h('div', { class: 'w-num' }, now.getDate()),
        h('div', { class: 'w-month' }, now.toLocaleDateString('fr-FR', { month: 'long' })),
        h('div', { class: 'w-clock' }, timeFr(now)),
        h('div', { class: 'w-place' }, icon('pin', 11), 'Amboise')),
      settings.showMoney !== false ? h('button', { class: 'widget w-money', onclick: () => ctx.openApp('banque') },
        h('div', { class: 'w-label green' }, icon('wallet', 12), 'Mon argent'),
        h('div', { class: 'w-amount' }, euro(me.bank)),
        h('div', { class: 'w-sub' }, me.lastSync ? `Solde bancaire (synchronisé ${ago(me.lastSync)})` : 'Connectez-vous en jeu pour synchroniser'),
        h('div', { class: 'w-status' }, h('span', { class: `sb-dot ${me.inGame ? 'on' : ''}` }), me.inGame ? 'En jeu' : 'Hors ligne')) : null),
    h('button', { class: 'widget w-list', onclick: showNotifications },
      h('div', { class: 'w-label orange' }, icon('bell', 12), 'Notifications'),
      home.notifications.length
        ? home.notifications.slice(0, 3).map((n) => h('div', { class: 'w-item' },
          h('div', { class: 'w-item-ico' }, icon(appIconName(n.app), 12)),
          h('div', { class: 'w-item-text' }, h('strong', {}, n.title), h('span', {}, n.body)),
          h('small', {}, ago(n.created_at))))
        : h('div', { class: 'w-empty' }, 'Aucune notification')),
    settings.showNews !== false ? h('button', { class: 'widget w-list', onclick: () => ctx.openApp('actualites') },
      h('div', { class: 'w-label red' }, icon('news', 12), 'Actualités'),
      home.news.length
        ? home.news.slice(0, 2).map((n) => h('div', { class: 'w-news' },
          h('small', { class: 'w-cat' }, n.category), h('strong', {}, n.title), h('span', {}, n.body.slice(0, 110))))
        : h('div', { class: 'w-empty' }, 'Aucune actualité pour le moment')) : null);

  const searchBtn = h('button', { class: 'search-pill', onclick: openSearch }, icon('search', 13), 'Rechercher');

  clear(screen,
    statusBar(),
    h('div', { class: 'home' },
      widgets,
      h('div', { class: 'app-grid' }, gridApps.map((a) => appIcon(a)))),
    h('div', { class: 'home-bottom' }, searchBtn,
      h('div', { class: 'dock' }, dock.map((a) => appIcon(a, { label: false })))));
}

function appIconName(appId) {
  return APPS.find((a) => a.id === appId)?.icon || (appId === 'system' ? 'shield' : 'bell');
}

function openSearch() {
  const list = h('div', { class: 'search-results' });
  const input = h('input', { class: 'search-input', placeholder: 'Rechercher une application…' });
  const draw = () => {
    const q = input.value.trim().toLowerCase();
    clear(list, visibleApps().filter((a) => !q || a.name.toLowerCase().includes(q) || (a.keywords || '').includes(q))
      .map((a) => h('button', { class: 'search-row', onclick: () => { m.close(); ctx.openApp(a.id); } },
        h('div', { class: 'app-icon tiny', style: { background: a.color, color: a.iconColor || '#fff' } }, icon(a.icon, 14)), a.name)));
  };
  input.addEventListener('input', draw);
  input.addEventListener('keydown', (e) => { if (e.key === 'Enter') list.querySelector('button')?.click(); });
  draw();
  const m = modal('Rechercher', h('div', {}, input, list));
}

async function showNotifications() {
  const data = await attempt(() => api('notifications'));
  if (!data) return;
  await attempt(() => api('notifications/lues', { method: 'POST', body: {} }));
  if (state.home) state.home.unreadNotifications = 0;
  const m = modal('Notifications', (close) => h('div', { class: 'notif-list' },
    data.notifications.length
      ? data.notifications.map((n) => h('div', { class: `notif ${n.read ? '' : 'unread'}` },
        h('div', { class: 'w-item-ico' }, icon(appIconName(n.app), 14)),
        h('div', { class: 'grow' }, h('strong', {}, n.title), h('p', {}, n.body)),
        h('small', { class: 'muted' }, ago(n.created_at))))
      : h('p', { class: 'muted' }, 'Aucune notification.'),
    data.notifications.length ? h('div', { class: 'row end' }, h('button', { class: 'btn', onclick: async () => {
      await attempt(() => api('notifications', { method: 'DELETE', body: {} }));
      close();
      poll();
    } }, icon('trash', 14), 'Tout effacer')) : null), { onClose: () => { if (!location.hash) renderHome(); } });
  void m;
}

// ------------------------------------------------------------ applications
function route() {
  if (!state.me || state.me.locked) return;
  state.cleanup?.();
  state.cleanup = null;
  document.getElementById('modals').replaceChildren();
  const [id, param] = location.hash.replace(/^#/, '').split('/');
  const app = APPS.find((a) => a.id === id && (!a.adminOnly || state.me.isAdmin));
  if (!app) {
    stopTimers();
    renderHome();
    return;
  }
  stopTimers();
  const body = h('div', { class: 'app-window' });
  clear(screen, statusBar(), body, homeIndicator());
  try {
    state.cleanup = app.open(body, ctx, param !== undefined ? decodeURIComponent(param) : undefined) || null;
  } catch (e) {
    console.error(e);
    toast('Impossible d\'ouvrir l\'application.', 'error');
  }
}

window.addEventListener('hashchange', route);
document.addEventListener('keydown', (e) => {
  if (e.key === 'Escape' && !document.querySelector('.modal-overlay') && location.hash) ctx.goHome();
});

// ------------------------------------------------------------ actualisation
async function poll() {
  try {
    const home = await api('accueil');
    const newest = home.notifications[0];
    if (state.lastNotifId !== null && newest && newest.id > state.lastNotifId && !newest.read) {
      banner(newest);
    }
    state.lastNotifId = newest ? newest.id : 0;
    const changed = JSON.stringify(home) !== JSON.stringify(state.home);
    state.home = home;
    if (changed && !location.hash && !document.querySelector('.modal-overlay')) renderHome();
  } catch { /* hors ligne : on réessaie au prochain tour */ }
}

function banner(n) {
  const el = h('button', { class: 'banner', onclick: () => { el.remove(); showNotifications(); } },
    h('div', { class: 'w-item-ico' }, icon(appIconName(n.app), 14)),
    h('div', {}, h('strong', {}, n.title), h('span', {}, n.body)));
  document.getElementById('toasts').append(el);
  setTimeout(() => el.remove(), 6000);
}

let pollTimer = null;
async function start() {
  try {
    await refreshMe();
  } catch (e) {
    if (e.status !== 401) {
      clear(screen, h('div', { class: 'login' }, h('div', { class: 'login-card' }, h('h1', {}, 'Tablette indisponible'), h('p', { class: 'muted' }, e.message),
        h('button', { class: 'btn btn-primary', onclick: start }, 'Réessayer'))));
    }
    return;
  }
  if (state.me.locked) return showLock();
  await poll();
  route();
  clearInterval(pollTimer);
  pollTimer = setInterval(async () => {
    if (!state.me || state.me.locked) return;
    const before = JSON.stringify(state.me);
    await poll();
    try { await refreshMe(); } catch { return; /* géré par l'évènement de session */ }
    if (JSON.stringify(state.me) !== before && !location.hash && !document.querySelector('.modal-overlay')) renderHome();
  }, 20e3);
}

start();
