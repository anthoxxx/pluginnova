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
    currentApp: null,
    editing: false,
    lock: null,
    pin: '',
    lastActivity: Date.now(),
    authTimer: null,
    meTimer: null
  };

  var SETTINGS_APP = 'reglages';
  var DOCK_MAX = 4;

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

  function formatMoney(v) {
    if (v === null || v === undefined) return '—';
    return Number(v).toLocaleString('fr-FR', { minimumFractionDigits: 2, maximumFractionDigits: 2 }) + ' €';
  }

  function initials(name) {
    return (name || '?').split(/\s+/).filter(Boolean).slice(0, 2).map(function (w) { return w[0].toUpperCase(); }).join('') || '?';
  }

  function showScreen(id) {
    device.scrollTop = 0;
    device.scrollLeft = 0;
    ['screen-boot', 'screen-lock', 'screen-home'].forEach(function (s) {
      $(s).classList.toggle('active', s === id);
    });
    device.classList.toggle('blurred', id === 'screen-boot');
  }

  function toast(title, message, type) {
    var t = el('div', 'toast ' + (type || 'info'));
    t.appendChild(el('span', 'dot'));
    var body = el('div');
    body.appendChild(el('b', null, title || ''));
    if (message) body.appendChild(el('p', null, message));
    t.appendChild(body);
    $('toasts').appendChild(t);
    var remove = function () { t.classList.add('out'); setTimeout(function () { t.remove(); }, 300); };
    t.addEventListener('click', remove);
    setTimeout(remove, 4500);
    while ($('toasts').children.length > 4) $('toasts').firstChild.remove();
  }

  /** Erreurs d'API communes : session perdue -> réidentification, verrouillée -> écran de verrouillage. */
  function handleApiError(err) {
    if (err && err.status === 401) { closeApp(true); boot(); return; }
    if (err && err.status === 423) { lockNow(false); return; }
    toast('Erreur', (err && err.message) || 'Action impossible', 'error');
  }

  // ------------------------------------------------------------------
  //  Apparence
  // ------------------------------------------------------------------

  function applyAppearance(s) {
    if (!s) return;
    MI.applyAppearance(s);
    $('wallpaper').style.background = MI.wallpaperCss(s.wallpaper);
    $('dim').style.opacity = String(((100 - (s.brightness || 100)) / 100) * 0.85);
    var meta = document.querySelector('meta[name="theme-color"]');
    if (meta) meta.content = s.accent;
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
    $('lock-time').textContent = t;
    $('lock-date').textContent = formatDate(now);
    var wc = document.querySelector('.w-clock .big');
    if (wc) wc.textContent = t;

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

  function boot() {
    clearTimeout(state.authTimer);
    clearInterval(state.meTimer);
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
    closeControlCenter();
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
    closeControlCenter();
    setEditing(false);
    clearInterval(state.meTimer);
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
      if (e.key === 'Escape') { if (device.classList.contains('cc-open')) closeControlCenter(); else if (state.currentApp) closeApp(); else setEditing(false); }
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
      clearInterval(state.meTimer);
      state.meTimer = setInterval(refreshMe, 30000);
    }).catch(handleApiError);
  }

  function refreshMe() {
    MI.api('/me').then(function (me) { state.me = me; renderWidgets(); }).catch(function (err) {
      if (err.status === 401 || err.status === 423) handleApiError(err);
    });
  }

  function refreshApps() {
    return MI.api('/apps').then(function (apps) { state.apps = apps; renderHome(); }).catch(handleApiError);
  }

  function renderHome() {
    renderWidgets();
    renderApps();
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
        w = el('div', 'widget w-clock');
        w.appendChild(el('div', 'widget-title', 'Horloge'));
        w.appendChild(el('div', 'big', formatTime(now)));
        w.appendChild(el('div', 'sub', formatDate(now)));
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
        var bar = el('div', 'xp-bar');
        var fill = el('span');
        fill.style.width = Math.min(100, ((me.xp || 0) % 100)) + '%';
        bar.appendChild(fill);
        w.appendChild(bar);
      } else if (id === 'wallet') {
        if (me.money === null && me.bank === null) return;
        w = el('div', 'widget w-wallet');
        w.appendChild(el('div', 'widget-title', 'Portefeuille'));
        [['Espèces', me.money], ['Banque', me.bank]].forEach(function (row) {
          var r = el('div', 'w-row');
          r.appendChild(el('span', 'mi-small', row[0]));
          r.appendChild(el('span', 'amount', formatMoney(row[1])));
          w.appendChild(r);
        });
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
      openApp(app, b);
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

  function openApp(app, fromNode) {
    if (!app) return;
    closeControlCenter();
    var win = $('app-window');
    if (fromNode) {
      var r = fromNode.getBoundingClientRect();
      win.style.setProperty('--origin', (r.left + r.width / 2) + 'px ' + (r.top + r.height / 2 - 30) + 'px');
    }
    state.currentApp = app;
    $('app-title').textContent = app.name;
    if ($('app-frame').getAttribute('data-app') !== app.id) {
      $('app-frame').setAttribute('data-app', app.id);
      $('app-frame').src = app.url;
    }
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
        frame.src = 'about:blank';
      }
    }, unload ? 0 : 320);
  }

  $('app-home').addEventListener('click', function () { closeApp(); });
  $('app-reload').addEventListener('click', function () {
    if (state.currentApp) $('app-frame').src = state.currentApp.url;
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
        reply({ app: app, settings: state.settings, me: state.me, os: { name: state.info && state.info.name, version: state.info && state.info.version, server: state.info && state.info.server } });
        break;
      case 'notify':
        toast(String(p.title || app.name).slice(0, 80), p.message ? String(p.message).slice(0, 300) : '', p.type);
        break;
      case 'close':
        closeApp();
        break;
      case 'open-app':
        var target = appById(p.id);
        if (target) openApp(target); else toast(app.name, 'Application introuvable : ' + p.id, 'warning');
        break;
      case 'badge':
        state.badges[app.id] = Math.max(0, p.count | 0);
        renderApps();
        break;
      case 'lock':
        lockNow(true);
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
  //  Centre de contrôle
  // ------------------------------------------------------------------

  function openControlCenter() {
    if (!$('screen-home').classList.contains('active')) return;
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
