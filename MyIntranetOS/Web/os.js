/* MyIntranet OS — logique de la tablette */
(function () {
  'use strict';

  var MI = window.MyIntranet;
  var $ = function (id) { return document.getElementById(id); };
  var device = $('device');

  var state = {
    info: null,
    settings: null,
    me: null,
    apps: [],
    badges: {},
    notifications: { unread: 0, items: [] },
    seenNotif: null,
    news: [],
    online: true,
    currentApp: null,
    editing: false,
    lock: null,
    pin: '',
    lastActivity: Date.now(),
    authTimer: null,
    meTimer: null,
    notifTimer: null
  };

  var SETTINGS_APP = 'reglages';
  var DOCK_MAX = 5;

  // ------------------------------------------------------------------
  //  Utilitaires
  // ------------------------------------------------------------------

  function el(tag, cls, text) {
    var e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text !== undefined && text !== null) e.textContent = text;
    return e;
  }

  function pad(n) { return n < 10 ? '0' + n : '' + n; }

  function formatTime(d) {
    if (state.settings && state.settings.use24h === false) {
      var h = d.getHours() % 12 || 12;
      return h + ':' + pad(d.getMinutes()) + (d.getHours() < 12 ? ' AM' : ' PM');
    }
    return pad(d.getHours()) + ':' + pad(d.getMinutes());
  }

  function formatDate(d) {
    return d.toLocaleDateString('fr-FR', { weekday: 'long', day: 'numeric', month: 'long' });
  }

  function shortDate(d) {
    return d.toLocaleDateString('fr-FR', { weekday: 'short', day: 'numeric', month: 'short' });
  }

  function formatMoney(v) {
    if (v === null || v === undefined) return '—';
    return Number(v).toLocaleString('fr-FR', { minimumFractionDigits: 0, maximumFractionDigits: 2 }) + ' €';
  }

  function timeAgo(ts) {
    var s = Math.max(0, Math.floor(Date.now() / 1000) - ts);
    if (s < 60) return "à l'instant";
    if (s < 3600) return 'il y a ' + Math.floor(s / 60) + ' min';
    if (s < 86400) return 'il y a ' + Math.floor(s / 3600) + ' h';
    var d = new Date(ts * 1000);
    return d.toLocaleDateString('fr-FR', { weekday: 'long' }) + ' à ' + pad(d.getHours()) + ':' + pad(d.getMinutes());
  }

  function initials(name) {
    return (name || '?').split(/\s+/).filter(Boolean).slice(0, 2).map(function (w) { return w[0].toUpperCase(); }).join('') || '?';
  }

  function svg(path, size) {
    var ns = 'http://www.w3.org/2000/svg';
    var s = document.createElementNS(ns, 'svg');
    s.setAttribute('viewBox', '0 0 24 24');
    s.setAttribute('width', size || 15);
    s.setAttribute('height', size || 15);
    s.setAttribute('aria-hidden', 'true');
    var p = document.createElementNS(ns, 'path');
    p.setAttribute('fill', 'currentColor');
    p.setAttribute('d', path);
    s.appendChild(p);
    return s;
  }

  var ICONS = {
    bell: 'M12 2a6 6 0 0 1 6 6v4l2 3v1H4v-1l2-3V8a6 6 0 0 1 6-6Zm-2 16h4a2 2 0 0 1-4 0Z',
    news: 'M4 4h13v14a2 2 0 0 0 2 2H6a2 2 0 0 1-2-2V4Zm3 3v3h7V7H7Zm0 5v2h7v-2H7Zm0 4v2h7v-2H7Zm12-8h2v10a2 2 0 0 1-2 2V8Z',
    money: 'M12 2a10 10 0 1 1 0 20 10 10 0 0 1 0-20Zm1 4h-2v1.1A3 3 0 0 0 11.6 13h.8a1 1 0 0 1 0 2H9v2h2v1h2v-1.1a3 3 0 0 0-.6-5.9h-.8a1 1 0 0 1 0-2H15V7h-2V6Z',
    pin: 'M12 2a7 7 0 0 1 7 7c0 5-7 13-7 13S5 14 5 9a7 7 0 0 1 7-7Zm0 4.5a2.5 2.5 0 1 0 0 5 2.5 2.5 0 0 0 0-5Z'
  };

  function showScreen(id) {
    device.scrollTop = 0;
    device.scrollLeft = 0;
    ['screen-boot', 'screen-lock', 'screen-home'].forEach(function (s) {
      $(s).classList.toggle('active', s === id);
    });
    device.classList.toggle('blurred', id === 'screen-boot');
  }

  function toast(title, message, type, onClick) {
    var t = el('div', 'toast ' + (type || 'info'));
    t.appendChild(el('span', 'dot'));
    var body = el('div');
    body.appendChild(el('b', null, title || ''));
    if (message) body.appendChild(el('p', null, message));
    t.appendChild(body);
    $('toasts').appendChild(t);
    var remove = function () { t.classList.add('out'); setTimeout(function () { t.remove(); }, 300); };
    t.addEventListener('click', function () { remove(); if (onClick) onClick(); });
    setTimeout(remove, 5000);
    while ($('toasts').children.length > 4) $('toasts').firstChild.remove();
  }

  /** Erreurs d'API communes : session perdue -> réidentification, verrouillée -> écran de verrouillage. */
  function handleApiError(err) {
    if (err && err.status === 401) { closeApp(true); boot(); return; }
    if (err && err.status === 423) { lockNow(false); return; }
    toast('Erreur', (err && err.message) || 'Action impossible', 'error');
  }

  function setOnline(on) {
    state.online = on;
    $('sb-status').classList.toggle('offline', !on);
    $('sb-status').lastChild.textContent = on ? 'En ligne' : 'Hors ligne';
    var w = document.querySelector('.w-money .state');
    if (w) {
      w.classList.toggle('offline', !on);
      w.lastChild.textContent = on ? 'En ligne' : 'Hors ligne';
    }
  }

  // ------------------------------------------------------------------
  //  Apparence
  // ------------------------------------------------------------------

  function applyAppearance(s) {
    if (!s) return;
    MI.applyAppearance(s);
    $('wallpaper').style.background = MI.wallpaperCss(s.wallpaper);
    $('dim').style.opacity = String(((100 - (s.brightness || 100)) / 100) * 0.85);
  }

  var saveTimer = null;
  var pendingPatch = {};

  /** Applique immédiatement puis enregistre (regroupé) un changement de réglages. */
  function patchSettings(patch, immediate) {
    state.settings = Object.assign({}, state.settings, patch);
    applyAppearance(state.settings);
    syncControlCenter();
    broadcastSettings();
    Object.assign(pendingPatch, patch);
    clearTimeout(saveTimer);
    saveTimer = setTimeout(flushSettings, immediate ? 0 : 400);
  }

  function flushSettings() {
    var patch = pendingPatch;
    pendingPatch = {};
    if (!Object.keys(patch).length) return Promise.resolve();
    return MI.api('/settings', { body: patch }).then(function (s) {
      state.settings = s;
    }).catch(handleApiError);
  }

  if (window.matchMedia) {
    var mq = window.matchMedia('(prefers-color-scheme: light)');
    var onScheme = function () { if (state.settings) { applyAppearance(state.settings); broadcastSettings(); } };
    if (mq.addEventListener) mq.addEventListener('change', onScheme); else if (mq.addListener) mq.addListener(onScheme);
  }

  // ------------------------------------------------------------------
  //  Horloge
  // ------------------------------------------------------------------

  function tick() {
    var now = new Date();
    var t = formatTime(now);
    $('sb-time').textContent = t;
    $('sb-date').textContent = shortDate(now);
    $('lock-time').textContent = t;
    $('lock-date').textContent = formatDate(now);
    document.querySelectorAll('[data-clock]').forEach(function (n) { n.textContent = t; });

    // Verrouillage automatique
    var minutes = state.settings && state.settings.autoLockMinutes;
    if (minutes > 0 && $('screen-home').classList.contains('active') && Date.now() - state.lastActivity > minutes * 60000) {
      lockNow(true);
    }
  }
  setInterval(tick, 1000);

  function activity() { state.lastActivity = Date.now(); }
  ['pointerdown', 'keydown', 'wheel', 'touchstart'].forEach(function (ev) {
    document.addEventListener(ev, activity, { passive: true });
  });

  // ------------------------------------------------------------------
  //  Démarrage et identification
  // ------------------------------------------------------------------

  function setBootMessage(text, spinner) {
    $('boot-state').hidden = false;
    $('pairing').hidden = true;
    $('boot-message').textContent = text;
    document.querySelector('#boot-state .spinner').style.display = spinner === false ? 'none' : '';
  }

  function stopTimers() {
    clearTimeout(state.authTimer);
    clearInterval(state.meTimer);
    clearInterval(state.notifTimer);
  }

  function boot() {
    stopTimers();
    showScreen('screen-boot');
    setBootMessage('Connexion au serveur…');

    var infoPromise = state.info ? Promise.resolve(state.info) : MI.api('/info').then(function (info) {
      state.info = info;
      document.title = info.name;
      $('boot-title').textContent = info.name;
      $('sb-server').textContent = info.server || '';
      return info;
    });

    infoPromise.then(checkAuth).catch(function () {
      setBootMessage('Serveur de la tablette injoignable. Nouvelle tentative…');
      state.authTimer = setTimeout(boot, 5000);
    });
  }

  function checkAuth() {
    return MI.api('/auth/session').then(function (res) {
      if (res.status === 'ok') {
        state.lock = res.lock;
        state.player = res.player;
        state.settings = Object.assign({}, state.settings, res.appearance);
        applyAppearance(state.settings);
        showLock();
        return;
      }
      if (res.status === 'pairing') {
        showPairing(res);
        state.authTimer = setTimeout(checkAuth, 2500);
        return;
      }
      setBootMessage(res.message || 'Connectez-vous au serveur de jeu puis rechargez la tablette.', false);
      state.authTimer = setTimeout(checkAuth, 5000);
    }).catch(function () {
      setBootMessage('Serveur de la tablette injoignable. Nouvelle tentative…');
      state.authTimer = setTimeout(checkAuth, 5000);
    });
  }

  function showPairing(res) {
    $('boot-state').hidden = true;
    $('pairing').hidden = false;
    $('pair-command').textContent = res.command;
    var code = $('pair-code');
    code.textContent = '';
    res.code.split('').forEach(function (c) { code.appendChild(el('span', null, c)); });
    var minutes = Math.max(1, Math.round(res.expiresIn / 60));
    $('pair-expire').textContent = 'Code valable ' + minutes + ' min. La tablette s\'ouvrira automatiquement.';
  }

  // ------------------------------------------------------------------
  //  Verrouillage
  // ------------------------------------------------------------------

  var lockCountdown = null;

  function showLock() {
    closeOverlays();
    showScreen('screen-lock');
    state.pin = '';
    renderPinDots();
    $('lock-hello').textContent = state.player ? 'Bonjour, ' + state.player.firstName : '';
    $('pin-error').textContent = '';
    var hasPin = state.lock && state.lock.hasPin;
    $('lock-swipe').hidden = !!hasPin;
    $('lock-pin').hidden = !hasPin;
    if (hasPin && state.lock.lockedFor > 0) startLockCountdown(state.lock.lockedFor);
    tick();
  }

  function lockNow(callServer) {
    closeApp(true);
    closeOverlays();
    setEditing(false);
    clearInterval(state.meTimer);
    clearInterval(state.notifTimer);
    var p = callServer === false ? Promise.resolve(state.lock) : MI.api('/lock/lock', { body: {} }).catch(function () { return state.lock; });
    p.then(function (lock) {
      if (lock) state.lock = lock;
      showLock();
    });
  }

  function renderPinDots() {
    var dots = $('pin-dots').children;
    for (var i = 0; i < dots.length; i++) dots[i].classList.toggle('on', i < state.pin.length);
  }

  function startLockCountdown(seconds) {
    clearInterval(lockCountdown);
    var end = Date.now() + seconds * 1000;
    $('keypad').classList.add('disabled');
    var update = function () {
      var left = Math.ceil((end - Date.now()) / 1000);
      if (left <= 0) {
        clearInterval(lockCountdown);
        $('keypad').classList.remove('disabled');
        $('pin-error').textContent = '';
        return;
      }
      var m = Math.floor(left / 60);
      $('pin-error').textContent = 'Trop de tentatives. Réessayez dans ' + (m > 0 ? m + ' min ' : '') + pad(left % 60) + ' s';
    };
    update();
    lockCountdown = setInterval(update, 1000);
  }

  function submitPin() {
    var pin = state.pin;
    $('keypad').classList.add('disabled');
    MI.api('/lock/unlock', { body: { pin: pin } }).then(function (lock) {
      state.lock = lock;
      $('keypad').classList.remove('disabled');
      enterHome();
    }).catch(function (err) {
      $('keypad').classList.remove('disabled');
      state.pin = '';
      renderPinDots();
      if (err.status === 401) { boot(); return; }
      $('pin-dots').classList.remove('shake');
      void $('pin-dots').offsetWidth;
      $('pin-dots').classList.add('shake');
      var data = err.data || {};
      if (data.lockedFor > 0) startLockCountdown(data.lockedFor);
      else if (data.attemptsLeft !== undefined) $('pin-error').textContent = 'Code incorrect · ' + data.attemptsLeft + ' essai(s) restant(s)';
      else $('pin-error').textContent = 'Code incorrect';
    });
  }

  function pressKey(k) {
    if ($('keypad').classList.contains('disabled')) return;
    if (k === 'del') state.pin = state.pin.slice(0, -1);
    else if (state.pin.length < 4) state.pin += k;
    renderPinDots();
    if (state.pin.length === 4) setTimeout(submitPin, 120);
  }

  $('keypad').addEventListener('click', function (e) {
    var b = e.target.closest('button[data-k]');
    if (b) pressKey(b.getAttribute('data-k'));
  });

  document.addEventListener('keydown', function (e) {
    if (!$('screen-lock').classList.contains('active')) {
      if (e.key === 'Escape') {
        if (!$('search').hidden || !$('notif-center').hidden || device.classList.contains('cc-open')) closeOverlays();
        else if (state.currentApp) closeApp();
        else setEditing(false);
      }
      return;
    }
    if ($('lock-pin').hidden) {
      if (e.key === 'Enter' || e.key === ' ') unlockNoPin();
      return;
    }
    if (/^[0-9]$/.test(e.key)) pressKey(e.key);
    else if (e.key === 'Backspace') pressKey('del');
  });

  function unlockNoPin() {
    MI.api('/lock/unlock', { body: {} }).then(function (lock) {
      state.lock = lock;
      if (lock.unlocked) enterHome(); else showLock();
    }).catch(function (err) { if (err.status === 401) boot(); else handleApiError(err); });
  }

  $('lock-open').addEventListener('click', unlockNoPin);

  // ------------------------------------------------------------------
  //  Accueil
  // ------------------------------------------------------------------

  function enterHome() {
    return Promise.all([MI.api('/settings'), MI.api('/me'), MI.api('/apps')]).then(function (r) {
      state.settings = r[0];
      state.me = r[1];
      state.apps = r[2];
      applyAppearance(state.settings);
      syncControlCenter();
      renderHome();
      activity();
      showScreen('screen-home');
      setOnline(true);
      clearInterval(state.meTimer);
      clearInterval(state.notifTimer);
      state.meTimer = setInterval(refreshMe, 30000);
      state.notifTimer = setInterval(refreshNotifications, 15000);
      refreshNotifications();
      refreshNews();
    }).catch(handleApiError);
  }

  function refreshMe() {
    MI.api('/me').then(function (me) {
      state.me = me;
      setOnline(true);
      renderWidgets();
    }).catch(function (err) {
      if (err.status === 401 || err.status === 423) handleApiError(err);
      else setOnline(false);
    });
  }

  function refreshApps() {
    return MI.api('/apps').then(function (apps) { state.apps = apps; renderHome(); }).catch(handleApiError);
  }

  /** Notifications du serveur : pastille, widget et bannière pour les nouvelles. */
  function refreshNotifications() {
    return MI.api('/notifications').then(function (n) {
      var known = state.seenNotif;
      state.notifications = n;
      var maxId = n.items.reduce(function (m, x) { return Math.max(m, x.id); }, 0);
      if (known !== null) {
        n.items.filter(function (x) { return x.id > known && !x.read; }).reverse().forEach(function (x) {
          toast(x.title, x.message, x.type, function () { openNotification(x); });
        });
      }
      state.seenNotif = Math.max(known || 0, maxId);
      setOnline(true);
      renderBadge();
      renderWidgets();
      if (!$('notif-center').hidden) renderNotifCenter();
    }).catch(function (err) {
      if (err.status === 401 || err.status === 423) handleApiError(err);
      else setOnline(false);
    });
  }

  function refreshNews() {
    if (!appById('actualites')) return;
    MI.api('/news').then(function (n) { state.news = n.items || []; renderWidgets(); }).catch(function () { /* facultatif */ });
  }

  function renderBadge() {
    var b = $('sb-badge');
    var count = state.notifications.unread || 0;
    b.hidden = count === 0;
    b.textContent = count > 99 ? '99+' : String(count);
  }

  function renderHome() {
    renderWidgets();
    renderApps();
  }

  function appIcon(appId) {
    var app = appById(appId);
    var box = el('span', 'n-icon');
    if (!app) {
      box.style.background = '#8e8e93';
      box.appendChild(svg(ICONS.bell, 14));
      return box;
    }
    box.style.background = app.color;
    if (app.icon && app.icon.type === 'image') {
      var img = el('img');
      img.src = app.icon.value;
      img.alt = '';
      box.appendChild(img);
    } else {
      box.textContent = (app.icon && app.icon.value) || app.name[0];
    }
    return box;
  }

  function notifRow(n) {
    var row = el('div', 'n-row' + (n.read ? '' : ' unread'));
    row.appendChild(appIcon(n.app));
    var t = el('div', 'n-text');
    t.appendChild(el('b', null, n.title));
    t.appendChild(el('span', null, n.message));
    row.appendChild(t);
    row.appendChild(el('span', 'n-time', timeAgo(n.createdAt)));
    row.addEventListener('click', function (e) { e.stopPropagation(); openNotification(n); });
    return row;
  }

  function openNotification(n) {
    closeOverlays();
    MI.api('/notifications/read', { body: { ids: [n.id] } }).then(refreshNotifications).catch(function () {});
    var app = appById(n.app);
    if (app) openApp(app);
  }

  function widgetHead(icon, text) {
    var h = el('div', 'widget-head');
    h.appendChild(svg(icon, 15));
    h.appendChild(el('span', null, text));
    return h;
  }

  function renderWidgets() {
    var box = $('widgets');
    box.textContent = '';
    var widgets = (state.settings && state.settings.widgets) || [];
    var me = state.me || {};
    var now = new Date();

    widgets.forEach(function (id) {
      var w;
      if (id === 'clock') {
        w = el('div', 'widget w-date');
        w.appendChild(el('div', 'day', now.toLocaleDateString('fr-FR', { weekday: 'long' })));
        w.appendChild(el('div', 'num', String(now.getDate())));
        w.appendChild(el('div', 'month', now.toLocaleDateString('fr-FR', { month: 'long' })));
        var time = el('div', 'time', formatTime(now));
        time.setAttribute('data-clock', '');
        w.appendChild(time);
        var place = el('div', 'place');
        place.appendChild(svg(ICONS.pin, 12));
        place.appendChild(el('span', null, (state.info && state.info.server) || 'Amboise'));
        w.appendChild(place);
      } else if (id === 'wallet') {
        if (me.bank === null || me.bank === undefined) return;
        w = el('div', 'widget w-money' + (appById('banque') ? ' clickable' : ''));
        w.appendChild(widgetHead(ICONS.money, 'Mon argent'));
        w.appendChild(el('div', 'amount', formatMoney(me.bank)));
        w.appendChild(el('div', 'sub', 'Solde bancaire'));
        if (me.money !== null && me.money !== undefined) w.appendChild(el('div', 'sub', formatMoney(me.money) + ' en espèces'));
        var st = el('div', 'state' + (state.online ? '' : ' offline'));
        st.appendChild(el('i'));
        st.appendChild(el('span', null, state.online ? 'En ligne' : 'Hors ligne'));
        w.appendChild(st);
        if (appById('banque')) w.addEventListener('click', function () { openApp(appById('banque')); });
      } else if (id === 'notifications') {
        w = el('div', 'widget wide w-notifs clickable');
        w.appendChild(widgetHead(ICONS.bell, 'Notifications'));
        var items = state.notifications.items.slice(0, 3);
        if (!items.length) w.appendChild(el('div', 'w-empty', 'Aucune notification.'));
        items.forEach(function (n) { w.appendChild(notifRow(n)); });
        w.addEventListener('click', openNotifCenter);
      } else if (id === 'news') {
        if (!appById('actualites')) return;
        w = el('div', 'widget wide w-news clickable');
        w.appendChild(widgetHead(ICONS.news, 'Actualités'));
        if (!state.news.length) w.appendChild(el('div', 'w-empty', 'Aucun article pour le moment.'));
        state.news.slice(0, 2).forEach(function (a) {
          var item = el('div', 'news-item');
          item.appendChild(el('div', 'outlet', a.outlet));
          item.appendChild(el('b', null, a.title));
          item.appendChild(el('p', null, a.body));
          w.appendChild(item);
        });
        w.addEventListener('click', function () { openApp(appById('actualites')); });
      } else if (id === 'profile') {
        w = el('div', 'widget w-profile');
        w.appendChild(el('div', 'widget-title', 'Identité'));
        var who = el('div', 'who');
        who.appendChild(el('div', 'avatar', initials(me.fullName)));
        var txt = el('div');
        txt.appendChild(el('div', 'name', me.fullName || '—'));
        var meta = [];
        if (me.phoneNumber) meta.push('☎ ' + me.phoneNumber);
        if (me.biz && me.biz.name) meta.push(me.biz.name);
        txt.appendChild(el('div', 'meta', meta.join(' · ') || 'Citoyen'));
        who.appendChild(txt);
        w.appendChild(who);
        var lvl = el('div', 'w-row');
        lvl.appendChild(el('span', 'mi-small', 'Niveau ' + (me.level || 0)));
        lvl.appendChild(el('span', 'mi-small', (me.xp || 0) + ' XP'));
        lvl.style.marginTop = 'auto';
        w.appendChild(lvl);
      } else if (id === 'calendar') {
        w = el('div', 'widget w-calendar');
        w.appendChild(el('div', 'day', now.toLocaleDateString('fr-FR', { weekday: 'long' })));
        w.appendChild(el('div', 'num', String(now.getDate())));
        w.appendChild(el('div', 'month', now.toLocaleDateString('fr-FR', { month: 'long', year: 'numeric' })));
      }
      if (w) box.appendChild(w);
    });
    box.hidden = !box.children.length;
  }

  function appById(id) {
    for (var i = 0; i < state.apps.length; i++) if (state.apps[i].id === id) return state.apps[i];
    return null;
  }

  function dockIds() {
    return ((state.settings && state.settings.dock) || []).filter(function (id) {
      var a = appById(id);
      return a && !a.hidden;
    }).slice(0, DOCK_MAX);
  }

  function makeIcon(app, where) {
    var b = el('button', 'app-icon' + (app.builtin ? ' builtin' : ''));
    b.type = 'button';
    b.setAttribute('data-id', app.id);
    b.title = app.description || app.name;

    var tile = el('span', 'tile');
    tile.style.background = 'linear-gradient(145deg, ' + app.color + ', ' + shade(app.color, -28) + ')';
    if (app.icon && app.icon.type === 'image') {
      var img = el('img');
      img.src = app.icon.value;
      img.alt = '';
      img.draggable = false;
      tile.appendChild(img);
    } else {
      tile.textContent = (app.icon && app.icon.value) || app.name[0];
    }
    b.appendChild(tile);
    b.appendChild(el('span', 'label', app.name));

    var count = state.badges[app.id];
    if (count > 0) b.appendChild(el('span', 'badge', count > 99 ? '99+' : String(count)));

    var remove = el('span', 'remove', where === 'hidden' ? '+' : '–');
    remove.setAttribute('role', 'button');
    remove.title = where === 'hidden' ? 'Afficher' : 'Masquer';
    remove.addEventListener('click', function (e) {
      e.stopPropagation();
      toggleHidden(app.id, where !== 'hidden');
    });
    b.appendChild(remove);

    b.addEventListener('click', function () {
      if (state.editing) return;
      openApp(app, null, b);
    });
    attachLongPress(b);
    if (where !== 'hidden') attachDrag(b, where);
    return b;
  }

  function shade(hex, percent) {
    var n = parseInt(hex.slice(1), 16);
    var f = function (c) { return Math.max(0, Math.min(255, Math.round(c + (percent / 100) * 255))); };
    var r = f(n >> 16), g = f((n >> 8) & 255), b = f(n & 255);
    return '#' + ((1 << 24) + (r << 16) + (g << 8) + b).toString(16).slice(1);
  }

  function renderApps() {
    var grid = $('app-grid');
    var dock = $('dock');
    var hiddenGrid = $('hidden-grid');
    grid.textContent = '';
    dock.textContent = '';
    hiddenGrid.textContent = '';

    var inDock = dockIds();
    var visible = state.apps.filter(function (a) { return !a.hidden && inDock.indexOf(a.id) < 0; });
    visible.forEach(function (a) { grid.appendChild(makeIcon(a, 'grid')); });
    inDock.forEach(function (id) { dock.appendChild(makeIcon(appById(id), 'dock')); });

    if (!visible.length && !inDock.length) {
      grid.appendChild(el('div', 'empty-hint', 'Aucune application. Déposez-en dans le dossier Applications/ du plugin.'));
    }

    var hidden = state.apps.filter(function (a) { return a.hidden; });
    hidden.forEach(function (a) { hiddenGrid.appendChild(makeIcon(a, 'hidden')); });
    $('hidden-apps').hidden = !(state.editing && hidden.length);
    dock.hidden = !inDock.length && !state.editing;
  }

  // ------------------------------------------------------------------
  //  Mode édition : réorganiser, masquer, dock
  // ------------------------------------------------------------------

  function setEditing(on) {
    state.editing = !!on;
    $('screen-home').classList.toggle('editing', state.editing);
    $('edit-bar').hidden = !state.editing;
    if ($('screen-home').classList.contains('active')) renderApps();
  }

  $('edit-done').addEventListener('click', function () { setEditing(false); });

  function attachLongPress(node) {
    var timer = null;
    node.addEventListener('pointerdown', function () {
      if (state.editing) return;
      timer = setTimeout(function () { setEditing(true); }, 650);
    });
    ['pointerup', 'pointerleave', 'pointercancel'].forEach(function (ev) {
      node.addEventListener(ev, function () { clearTimeout(timer); });
    });
    node.addEventListener('contextmenu', function (e) { e.preventDefault(); setEditing(true); });
  }

  var dragId = null;

  function attachDrag(node, where) {
    node.draggable = true;
    node.addEventListener('dragstart', function (e) {
      if (!state.editing) setEditing(true);
      dragId = node.getAttribute('data-id');
      e.dataTransfer.effectAllowed = 'move';
      try { e.dataTransfer.setData('text/plain', dragId); } catch (x) { /* ignore */ }
      setTimeout(function () { node.classList.add('dragging'); }, 0);
    });
    node.addEventListener('dragend', function () {
      node.classList.remove('dragging');
      clearDropMarks();
      dragId = null;
    });
    node.addEventListener('dragover', function (e) {
      if (!dragId) return;
      e.preventDefault();
      clearDropMarks();
      if (node.getAttribute('data-id') !== dragId) node.classList.add('drop-before');
    });
    node.addEventListener('drop', function (e) {
      if (!dragId) return;
      e.preventDefault();
      e.stopPropagation();
      dropApp(dragId, where, node.getAttribute('data-id'));
    });
  }

  ['app-grid', 'dock'].forEach(function (id) {
    var zone = $(id);
    zone.addEventListener('dragover', function (e) { if (dragId) e.preventDefault(); });
    zone.addEventListener('drop', function (e) {
      if (!dragId) return;
      e.preventDefault();
      dropApp(dragId, id === 'dock' ? 'dock' : 'grid', null);
    });
  });

  function clearDropMarks() {
    document.querySelectorAll('.drop-before').forEach(function (n) { n.classList.remove('drop-before'); });
  }

  /** Déplace une app vers la grille ou le dock, avant l'app "beforeId" (ou à la fin). */
  function dropApp(id, target, beforeId) {
    clearDropMarks();
    var dock = dockIds().filter(function (x) { return x !== id; });
    var order = state.apps.map(function (a) { return a.id; }).filter(function (x) { return x !== id; });

    if (target === 'dock') {
      if (dock.length >= DOCK_MAX) { toast('Dock plein', 'Le dock contient au maximum ' + DOCK_MAX + ' applications.', 'warning'); return; }
      var di = beforeId ? dock.indexOf(beforeId) : -1;
      dock.splice(di < 0 ? dock.length : di, 0, id);
      order.push(id);
    } else {
      var oi = beforeId ? order.indexOf(beforeId) : -1;
      order.splice(oi < 0 ? order.length : oi, 0, id);
    }

    state.apps.sort(function (a, b) { return order.indexOf(a.id) - order.indexOf(b.id); });
    patchSettings({ appOrder: order, dock: dock });
    renderApps();
  }

  function toggleHidden(id, hide) {
    var hidden = state.apps.filter(function (a) { return a.hidden; }).map(function (a) { return a.id; });
    hidden = hidden.filter(function (x) { return x !== id; });
    if (hide) hidden.push(id);
    state.apps.forEach(function (a) { if (a.id === id) a.hidden = hide; });
    var patch = { hiddenApps: hidden };
    if (hide) patch.dock = dockIds().filter(function (x) { return x !== id; });
    patchSettings(patch);
    renderApps();
  }

  // ------------------------------------------------------------------
  //  Applications
  // ------------------------------------------------------------------

  function appUrl(app, params) {
    if (!params || !Object.keys(params).length) return app.url;
    var q = Object.keys(params).map(function (k) { return encodeURIComponent(k) + '=' + encodeURIComponent(params[k]); }).join('&');
    return app.url + (app.url.indexOf('?') < 0 ? '?' : '&') + q;
  }

  function loadFrame(app, params) {
    var frame = $('app-frame');
    frame.setAttribute('data-app', app.id);
    frame.setAttribute('data-params', JSON.stringify(params || {}));
    frame.src = appUrl(app, params);
  }

  function openApp(app, params, fromNode) {
    if (!app) return;
    closeOverlays();
    var win = $('app-window');
    if (fromNode) {
      var r = fromNode.getBoundingClientRect();
      win.style.setProperty('--origin', (r.left + r.width / 2) + 'px ' + (r.top + r.height / 2 - 30) + 'px');
    }
    state.currentApp = app;
    state.badges[app.id] = 0;
    $('app-title').textContent = app.name;
    var frame = $('app-frame');
    var sameParams = frame.getAttribute('data-params') === JSON.stringify(params || {});
    if (frame.getAttribute('data-app') !== app.id || (params && !sameParams)) loadFrame(app, params);
    win.classList.add('open');
    win.setAttribute('aria-hidden', 'false');
    device.classList.add('app-open');
  }

  function closeApp(unload) {
    var win = $('app-window');
    win.classList.remove('open');
    win.setAttribute('aria-hidden', 'true');
    device.classList.remove('app-open');
    device.scrollTop = 0;
    device.scrollLeft = 0;
    state.currentApp = null;
    var frame = $('app-frame');
    setTimeout(function () {
      if (!state.currentApp && unload !== false) {
        frame.removeAttribute('data-app');
        frame.removeAttribute('data-params');
        frame.src = 'about:blank';
      }
    }, unload ? 0 : 320);
    if ($('screen-home').classList.contains('active')) {
      renderApps();
      refreshNotifications();
      refreshNews();
    }
  }

  $('app-home').addEventListener('click', function () { closeApp(); });
  $('app-reload').addEventListener('click', function () {
    if (state.currentApp) loadFrame(state.currentApp, JSON.parse($('app-frame').getAttribute('data-params') || '{}'));
  });

  function broadcastSettings() {
    var frame = $('app-frame');
    if (frame.contentWindow && frame.getAttribute('data-app')) {
      frame.contentWindow.postMessage({ myintranet: 1, type: 'settings', payload: state.settings }, location.origin);
    }
  }

  function frameApp(source) {
    var frame = $('app-frame');
    if (source !== frame.contentWindow) return null;
    return appById(frame.getAttribute('data-app'));
  }

  // Messages des applications (voir DOCUMENTATION.md)
  window.addEventListener('message', function (e) {
    if (e.origin !== location.origin) return;
    var msg = e.data;
    if (!msg || msg.myintranet !== 1) return;
    var app = frameApp(e.source);
    if (!app) return;
    activity();

    var reply = function (payload, error) {
      e.source.postMessage({ myintranet: 1, type: 'response', id: msg.id, payload: payload, error: error }, location.origin);
    };
    var p = msg.payload || {};

    switch (msg.type) {
      case 'context':
        reply({
          app: app, settings: state.settings, me: state.me,
          params: JSON.parse($('app-frame').getAttribute('data-params') || '{}'),
          os: { name: state.info && state.info.name, version: state.info && state.info.version, server: state.info && state.info.server }
        });
        break;
      case 'notify':
        toast(String(p.title || app.name).slice(0, 80), p.message ? String(p.message).slice(0, 300) : '', p.type);
        break;
      case 'close':
        closeApp();
        break;
      case 'open-app':
        var target = appById(p.id);
        if (target) openApp(target, p.params || null); else toast(app.name, 'Application introuvable : ' + p.id, 'warning');
        break;
      case 'badge':
        state.badges[app.id] = Math.max(0, p.count | 0);
        break;
      case 'lock':
        lockNow(true);
        break;
      case 'refresh':
        refreshMe();
        refreshNotifications();
        refreshNews();
        break;
      case 'settings-updated':
        state.settings = p;
        applyAppearance(state.settings);
        syncControlCenter();
        refreshApps();
        break;
      case 'auth-required':
        if (p.status === 423) lockNow(false); else { closeApp(true); boot(); }
        break;
      case 'activity':
        break;
      default:
        if (msg.id) reply(null, 'Message inconnu : ' + msg.type);
    }
  });

  // ------------------------------------------------------------------
  //  Recherche et centre de notifications
  // ------------------------------------------------------------------

  function closeOverlays() {
    $('search').hidden = true;
    $('notif-center').hidden = true;
    closeControlCenter();
  }

  function normalize(s) {
    return (s || '').toLowerCase().normalize('NFD').replace(/[̀-ͯ]/g, '');
  }

  function renderSearch() {
    var q = normalize($('search-input').value.trim());
    var list = $('search-results');
    list.textContent = '';
    var results = state.apps.filter(function (a) {
      return !q || normalize(a.name).indexOf(q) >= 0 || normalize(a.description).indexOf(q) >= 0;
    });
    if (!results.length) list.appendChild(el('div', 'w-empty', 'Aucun résultat.'));
    results.forEach(function (a) {
      var row = el('button', 's-row');
      row.type = 'button';
      row.appendChild(appIcon(a.id));
      var t = el('div');
      t.appendChild(el('b', null, a.name));
      if (a.description) t.appendChild(el('small', null, a.description));
      row.appendChild(t);
      row.addEventListener('click', function () { openApp(a); });
      list.appendChild(row);
    });
  }

  $('search-open').addEventListener('click', function () {
    closeOverlays();
    $('search').hidden = false;
    $('search-input').value = '';
    renderSearch();
    setTimeout(function () { $('search-input').focus(); }, 30);
  });
  $('search-input').addEventListener('input', renderSearch);
  $('search-input').addEventListener('keydown', function (e) {
    if (e.key === 'Enter') {
      var first = $('search-results').querySelector('.s-row');
      if (first) first.click();
    }
  });
  $('search-close').addEventListener('click', closeOverlays);
  $('search').addEventListener('click', function (e) { if (e.target === $('search')) closeOverlays(); });

  function renderNotifCenter() {
    var list = $('nc-list');
    list.textContent = '';
    if (!state.notifications.items.length) list.appendChild(el('div', 'w-empty', 'Aucune notification.'));
    state.notifications.items.forEach(function (n) { list.appendChild(notifRow(n)); });
  }

  function openNotifCenter() {
    closeOverlays();
    renderNotifCenter();
    $('notif-center').hidden = false;
    if (state.notifications.unread > 0) {
      MI.api('/notifications/read', { body: {} }).then(function () {
        state.notifications.unread = 0;
        renderBadge();
      }).catch(function () {});
    }
  }

  $('sb-badge').addEventListener('click', function (e) { e.stopPropagation(); openNotifCenter(); });
  $('nc-close').addEventListener('click', closeOverlays);
  $('notif-center').addEventListener('click', function (e) { if (e.target === $('notif-center')) closeOverlays(); });
  $('nc-clear').addEventListener('click', function () {
    MI.api('/notifications/clear', { body: {} }).then(function () {
      state.notifications = { unread: 0, items: [] };
      renderBadge();
      renderNotifCenter();
      renderWidgets();
    }).catch(handleApiError);
  });

  // ------------------------------------------------------------------
  //  Centre de contrôle
  // ------------------------------------------------------------------

  function openControlCenter() {
    if (!$('screen-home').classList.contains('active')) return;
    $('search').hidden = true;
    $('notif-center').hidden = true;
    syncControlCenter();
    device.classList.add('cc-open');
    $('control-center').setAttribute('aria-hidden', 'false');
  }

  function closeControlCenter() {
    device.classList.remove('cc-open');
    $('control-center').setAttribute('aria-hidden', 'true');
  }

  $('sb-cc').addEventListener('click', function () {
    if (device.classList.contains('cc-open')) closeControlCenter(); else openControlCenter();
  });
  $('cc-backdrop').addEventListener('click', closeControlCenter);

  function syncControlCenter() {
    var s = state.settings;
    if (!s) return;
    document.querySelectorAll('#cc-theme button').forEach(function (b) {
      b.classList.toggle('on', b.getAttribute('data-v') === s.theme);
    });
    $('cc-brightness').value = s.brightness;
    $('cc-brightness-v').textContent = s.brightness + ' %';
    $('cc-text').value = s.textScale;
    $('cc-text-v').textContent = s.textScale + ' %';
    var sw = $('cc-accents');
    if (!sw.children.length) {
      MI.accents.forEach(function (c) {
        var b = el('button', 'swatch');
        b.type = 'button';
        b.style.background = c;
        b.setAttribute('data-v', c);
        b.setAttribute('aria-label', 'Couleur ' + c);
        b.addEventListener('click', function () { patchSettings({ accent: c }); });
        sw.appendChild(b);
      });
    }
    sw.querySelectorAll('.swatch').forEach(function (b) {
      b.classList.toggle('on', b.getAttribute('data-v') === (s.accent || '').toLowerCase());
    });
  }

  $('cc-theme').addEventListener('click', function (e) {
    var b = e.target.closest('button[data-v]');
    if (b) patchSettings({ theme: b.getAttribute('data-v') });
  });
  $('cc-brightness').addEventListener('input', function (e) { patchSettings({ brightness: +e.target.value }); });
  $('cc-text').addEventListener('input', function (e) { patchSettings({ textScale: +e.target.value }); });
  $('cc-lock').addEventListener('click', function () { lockNow(true); });
  $('cc-edit').addEventListener('click', function () { closeControlCenter(); closeApp(); setEditing(true); });
  $('cc-settings').addEventListener('click', function () { openApp(appById(SETTINGS_APP)); });

  // ------------------------------------------------------------------

  boot();
})();
