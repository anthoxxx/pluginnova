/*!
 * MyIntranet OS — SDK des applications
 * Inclure dans une application : <script src="/sdk/myintranet.js"></script>
 * Documentation : DOCUMENTATION.md (dossier du plugin)
 */
(function (global) {
  'use strict';

  var VERSION = '1.0.0';

  // Fonds d'écran prédéfinis (identifiants acceptés par l'API)
  var WALLPAPERS = {
    aurora: { name: 'Aurore', css: 'radial-gradient(at 20% 15%, #7f5af0 0, transparent 55%), radial-gradient(at 85% 30%, #2cb67d 0, transparent 50%), radial-gradient(at 50% 95%, #0a84ff 0, transparent 55%), #10121a' },
    ocean: { name: 'Océan', css: 'linear-gradient(160deg, #0f2027 0%, #203a43 45%, #2c5364 100%)' },
    sunset: { name: 'Crépuscule', css: 'linear-gradient(160deg, #ff7e5f 0%, #feb47b 40%, #6a3093 100%)' },
    forest: { name: 'Forêt', css: 'linear-gradient(160deg, #134e5e 0%, #2f7d4f 55%, #71b280 100%)' },
    night: { name: 'Nuit', css: 'radial-gradient(at 70% 20%, #3a3d8f 0, transparent 50%), linear-gradient(180deg, #05060f 0%, #141836 100%)' },
    nova: { name: 'Nova', css: 'radial-gradient(at 15% 85%, #ff2e63 0, transparent 50%), radial-gradient(at 85% 15%, #08d9d6 0, transparent 50%), #252a34' },
    graphite: { name: 'Graphite', css: 'linear-gradient(160deg, #3a3a3c 0%, #1c1c1e 100%)' },
    candy: { name: 'Bonbon', css: 'linear-gradient(135deg, #fbc2eb 0%, #a6c1ee 100%)' }
  };

  var ACCENTS = ['#0a84ff', '#5e5ce6', '#bf5af2', '#ff375f', '#ff9f0a', '#ffd60a', '#30d158', '#64d2ff'];

  var inFrame = global.parent && global.parent !== global;
  var listeners = {};
  var pending = {};
  var seq = 0;
  var context = null;
  var readyCallbacks = [];

  function emit(type, payload) {
    (listeners[type] || []).forEach(function (cb) {
      try { cb(payload); } catch (e) { console.error(e); }
    });
  }

  function on(type, cb) {
    (listeners[type] = listeners[type] || []).push(cb);
    return function () { listeners[type] = (listeners[type] || []).filter(function (x) { return x !== cb; }); };
  }

  /** Appel à l'API de la tablette. Renvoie une promesse du JSON ; rejette avec une Error {status, code}. */
  function api(path, options) {
    options = options || {};
    var init = {
      method: options.method || (options.body !== undefined ? 'POST' : 'GET'),
      credentials: 'same-origin',
      headers: { 'X-MyIntranet': '1' }
    };
    if (options.body !== undefined) {
      init.headers['Content-Type'] = 'application/json';
      init.body = JSON.stringify(options.body);
    }
    return fetch('/api' + path, init).then(function (res) {
      return res.text().then(function (text) {
        var data = null;
        try { data = text ? JSON.parse(text) : null; } catch (e) { data = null; }
        if (!res.ok) {
          var err = new Error((data && (data.message || data.error)) || ('HTTP ' + res.status));
          err.status = res.status;
          err.code = data && data.error;
          err.data = data;
          if (res.status === 401 || res.status === 423) send('auth-required', { status: res.status });
          throw err;
        }
        return data;
      });
    });
  }

  /** Envoie un message à la tablette (application -> OS). */
  function send(type, payload) {
    if (!inFrame) return;
    global.parent.postMessage({ myintranet: 1, type: type, payload: payload }, global.location.origin);
  }

  /** Message avec réponse (application -> OS -> application). */
  function request(type, payload) {
    if (!inFrame) return Promise.reject(new Error('Hors de la tablette'));
    var id = ++seq;
    return new Promise(function (resolve, reject) {
      pending[id] = { resolve: resolve, reject: reject };
      global.parent.postMessage({ myintranet: 1, type: type, id: id, payload: payload }, global.location.origin);
      setTimeout(function () {
        if (pending[id]) { delete pending[id]; reject(new Error('Pas de réponse de la tablette')); }
      }, 5000);
    });
  }

  global.addEventListener('message', function (event) {
    if (event.origin !== global.location.origin || event.source !== global.parent) return;
    var msg = event.data;
    if (!msg || msg.myintranet !== 1) return;
    if (msg.type === 'response' && pending[msg.id]) {
      var p = pending[msg.id];
      delete pending[msg.id];
      if (msg.error) p.reject(new Error(msg.error)); else p.resolve(msg.payload);
      return;
    }
    if (msg.type === 'context') {
      context = msg.payload;
      applyAppearance(context.settings);
      readyCallbacks.splice(0).forEach(function (cb) { cb(context); });
    }
    if (msg.type === 'settings') {
      if (context) context.settings = msg.payload;
      applyAppearance(msg.payload);
    }
    emit(msg.type, msg.payload);
  });

  function resolveTheme(theme) {
    if (theme === 'light' || theme === 'dark') return theme;
    return global.matchMedia && global.matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark';
  }

  function wallpaperCss(id) {
    if (typeof id === 'string' && id.indexOf('url:') === 0) {
      return 'center / cover no-repeat url("' + id.substring(4).replace(/["\\()\s]/g, '') + '"), #111';
    }
    return (WALLPAPERS[id] || WALLPAPERS.aurora).css;
  }

  /** Applique le thème, la couleur et la taille du texte du joueur au document courant. */
  function applyAppearance(settings) {
    if (!settings) return;
    var root = document.documentElement;
    root.setAttribute('data-theme', resolveTheme(settings.theme));
    if (settings.accent) root.style.setProperty('--mi-accent', settings.accent);
    if (settings.textScale) root.style.setProperty('--mi-text-scale', String(settings.textScale / 100));
  }

  function ready(cb) {
    if (context) cb(context); else readyCallbacks.push(cb);
  }

  var MyIntranet = {
    version: VERSION,
    inTablet: inFrame,
    wallpapers: WALLPAPERS,
    accents: ACCENTS,
    api: api,
    on: on,
    ready: ready,
    send: send,
    request: request,
    resolveTheme: resolveTheme,
    wallpaperCss: wallpaperCss,
    applyAppearance: applyAppearance,
    get context() { return context; },

    me: function () { return api('/me'); },
    apps: function () { return api('/apps'); },
    settings: {
      get: function () { return api('/settings'); },
      update: function (patch) {
        return api('/settings', { body: patch }).then(function (s) {
          applyAppearance(s);
          send('settings-updated', s);
          return s;
        });
      },
      reset: function () {
        return api('/settings/reset', { body: {} }).then(function (s) {
          applyAppearance(s);
          send('settings-updated', s);
          return s;
        });
      }
    },
    storage: {
      keys: function (appId) { return api('/storage/' + encodeURIComponent(appId || currentAppId())); },
      get: function (key, appId) {
        return api('/storage/' + encodeURIComponent(appId || currentAppId()) + '/' + encodeURIComponent(key))
          .then(function (r) { return r.value; })
          .catch(function (e) { if (e.status === 404) return null; throw e; });
      },
      set: function (key, value, appId) {
        return api('/storage/' + encodeURIComponent(appId || currentAppId()) + '/' + encodeURIComponent(key), { body: { value: value } });
      },
      remove: function (key, appId) {
        return api('/storage/' + encodeURIComponent(appId || currentAppId()) + '/' + encodeURIComponent(key), { method: 'DELETE' });
      }
    },

    /** Notification affichée par la tablette. type : info | success | warning | error */
    notify: function (title, message, type) { send('notify', { title: title, message: message, type: type || 'info' }); },
    /** Ferme l'application (retour à l'écran d'accueil). */
    close: function () { send('close'); },
    /** Ouvre une autre application, avec des paramètres facultatifs (lus par MyIntranet.params()). */
    openApp: function (appId, params) { send('open-app', { id: appId, params: params || null }); },
    /** Paramètres reçus à l'ouverture (ex. { number: "0612345678" }). */
    params: function () {
      var out = {};
      try { new URLSearchParams(global.location.search).forEach(function (v, k) { out[k] = v; }); } catch (e) { /* ancien navigateur */ }
      if (context && context.params) Object.keys(context.params).forEach(function (k) { if (!(k in out)) out[k] = context.params[k]; });
      return out;
    },
    /** Demande à la tablette de rafraîchir argent, notifications et actualités (après une action). */
    refresh: function () { send('refresh'); },
    /** Pastille sur l'icône de l'application (0 pour l'enlever). */
    setBadge: function (count) { send('badge', { count: count | 0 }); },
    /** Verrouille la tablette. */
    lock: function () { send('lock'); }
  };

  function currentAppId() {
    var m = /^\/apps\/([^/]+)\//.exec(global.location.pathname);
    return m ? decodeURIComponent(m[1]) : '';
  }

  global.MyIntranet = MyIntranet;

  if (inFrame) {
    // Signale l'activité du joueur à la tablette (verrouillage automatique)
    var lastActivity = 0;
    ['pointerdown', 'keydown', 'wheel'].forEach(function (ev) {
      global.addEventListener(ev, function () {
        var now = Date.now();
        if (now - lastActivity > 10000) { lastActivity = now; send('activity'); }
      }, { passive: true });
    });

    // Thème appliqué dès que possible, puis contexte complet
    request('context').then(function (ctx) {
      context = ctx;
      applyAppearance(ctx.settings);
      readyCallbacks.splice(0).forEach(function (cb) { cb(ctx); });
    }).catch(function () { /* hors tablette */ });
  }
})(window);
