/* Réglages — application intégrée de MyIntranet OS (sert aussi d'exemple d'application) */
(function () {
  'use strict';

  var MI = window.MyIntranet;
  var $ = function (id) { return document.getElementById(id); };
  var settings = null;
  var apps = [];
  var me = null;

  var SECTIONS = [
    { id: 'profil', name: 'Mon personnage', icon: '👤', color: '#0a84ff' },
    { id: 'apparence', name: 'Apparence', icon: '🎨', color: '#bf5af2' },
    { id: 'affichage', name: 'Affichage', icon: '☀', color: '#ff9f0a' },
    { id: 'accueil', name: "Écran d'accueil", icon: '▦', color: '#5e5ce6' },
    { id: 'securite', name: 'Code et sécurité', icon: '🔒', color: '#ff375f' },
    { id: 'apropos', name: 'À propos', icon: 'ℹ', color: '#8e8e93' }
  ];
  var WIDGETS = [
    { id: 'clock', name: 'Date et heure' },
    { id: 'wallet', name: 'Mon argent' },
    { id: 'notifications', name: 'Notifications' },
    { id: 'news', name: 'Actualités' },
    { id: 'profile', name: 'Identité' },
    { id: 'calendar', name: 'Calendrier' }
  ];

  function el(tag, cls, text) {
    var e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text !== undefined && text !== null) e.textContent = text;
    return e;
  }

  function row(label, value) {
    var r = el('div', 'mi-row');
    r.appendChild(el('span', 'mi-grow', label));
    r.appendChild(el('span', 'mi-muted', value === null || value === undefined || value === '' ? '—' : String(value)));
    return r;
  }

  function money(v) {
    return v === null || v === undefined ? '—' : Number(v).toLocaleString('fr-FR', { minimumFractionDigits: 2, maximumFractionDigits: 2 }) + ' €';
  }

  function fail(err) { MI.notify('Réglages', (err && err.message) || 'Erreur', 'error'); }

  /** Enregistre un changement : l'OS l'applique immédiatement (thème, fond, etc.). */
  function save(patch) {
    Object.assign(settings, patch);
    renderAll();
    return MI.settings.update(patch).then(function (s) { settings = s; renderAll(); }).catch(fail);
  }

  var timers = {};
  function saveLater(key, patch) {
    Object.assign(settings, patch);
    MI.applyAppearance(settings);
    clearTimeout(timers[key]);
    timers[key] = setTimeout(function () { save(patch); }, 300);
  }

  // ---------------- Navigation ----------------

  function showSection(id) {
    document.querySelectorAll('section[data-section]').forEach(function (s) {
      s.hidden = s.getAttribute('data-section') !== id;
    });
    document.querySelectorAll('.nav-item').forEach(function (b) {
      b.classList.toggle('on', b.getAttribute('data-id') === id);
    });
    document.body.classList.add('detail');
    document.querySelector('main').scrollTop = 0;
  }

  SECTIONS.forEach(function (s) {
    var b = el('button', 'nav-item');
    b.type = 'button';
    b.setAttribute('data-id', s.id);
    var ico = el('span', 'nav-ico', s.icon);
    ico.style.background = s.color;
    b.appendChild(ico);
    b.appendChild(el('span', null, s.name));
    b.addEventListener('click', function () { showSection(s.id); });
    $('nav-list').appendChild(b);
  });
  $('back').addEventListener('click', function () { document.body.classList.remove('detail'); });

  // ---------------- Rendu ----------------

  function renderProfile() {
    if (!me) return;
    var name = me.fullName || '—';
    $('avatar').textContent = name.split(/\s+/).filter(Boolean).slice(0, 2).map(function (w) { return w[0].toUpperCase(); }).join('') || '?';
    $('profile-name').textContent = name;
    $('profile-sub').textContent = 'Niveau ' + me.level + (me.biz && me.biz.name ? ' · ' + me.biz.name : '');

    var rows = $('profile-rows');
    rows.textContent = '';
    rows.appendChild(row('Prénom', me.firstName));
    rows.appendChild(row('Nom', me.lastName));
    rows.appendChild(row('Date de naissance', me.birthday));
    rows.appendChild(row('Téléphone', me.phoneNumber));
    rows.appendChild(row('Niveau', me.level));
    rows.appendChild(row('Expérience', me.xp + ' XP'));
    rows.appendChild(row('Entreprise', me.biz && me.biz.name));
    if (me.money !== null) rows.appendChild(row('Espèces', money(me.money)));
    if (me.bank !== null) rows.appendChild(row('Banque', money(me.bank)));
    rows.appendChild(row('Compte Steam', me.steamName));
  }

  function renderAppearance() {
    document.querySelectorAll('#theme-seg button').forEach(function (b) {
      b.classList.toggle('on', b.getAttribute('data-v') === settings.theme);
    });

    var acc = $('accents');
    if (!acc.children.length) {
      MI.accents.forEach(function (c) {
        var b = el('button', 'swatch');
        b.type = 'button';
        b.style.background = c;
        b.setAttribute('data-v', c);
        b.addEventListener('click', function () { save({ accent: c }); });
        acc.appendChild(b);
      });
      var picker = el('input');
      picker.type = 'color';
      picker.id = 'accent-picker';
      picker.title = 'Couleur personnalisée';
      picker.addEventListener('change', function () { save({ accent: picker.value }); });
      acc.appendChild(picker);
    }
    acc.querySelectorAll('.swatch').forEach(function (b) {
      b.classList.toggle('on', b.getAttribute('data-v') === settings.accent);
    });
    $('accent-picker').value = settings.accent;

    var wps = $('wallpapers');
    if (!wps.children.length) {
      Object.keys(MI.wallpapers).forEach(function (id) {
        var b = el('button', 'wp');
        b.type = 'button';
        b.setAttribute('data-v', id);
        b.style.background = MI.wallpapers[id].css;
        b.appendChild(el('span', null, MI.wallpapers[id].name));
        b.addEventListener('click', function () { save({ wallpaper: id }); });
        wps.appendChild(b);
      });
    }
    wps.querySelectorAll('.wp').forEach(function (b) {
      b.classList.toggle('on', b.getAttribute('data-v') === settings.wallpaper);
    });
    if (settings.wallpaper.indexOf('url:') === 0 && document.activeElement !== $('wp-url')) $('wp-url').value = settings.wallpaper.substring(4);
  }

  function renderDisplay() {
    $('brightness').value = settings.brightness;
    $('brightness-v').textContent = settings.brightness + ' %';
    $('textscale').value = settings.textScale;
    $('textscale-v').textContent = settings.textScale + ' %';
    $('use24h').checked = settings.use24h !== false;
  }

  function renderHome() {
    var w = $('widgets');
    w.textContent = '';
    WIDGETS.forEach(function (def) {
      var r = el('div', 'mi-row');
      r.appendChild(el('span', 'mi-grow', def.name));
      var sw = el('label', 'mi-switch');
      var input = el('input');
      input.type = 'checkbox';
      input.checked = settings.widgets.indexOf(def.id) >= 0;
      input.addEventListener('change', function () {
        var list = settings.widgets.filter(function (x) { return x !== def.id; });
        if (input.checked) {
          // conserve l'ordre de référence
          list.push(def.id);
          list.sort(function (a, b) { return indexOfWidget(a) - indexOfWidget(b); });
        }
        save({ widgets: list });
      });
      sw.appendChild(input);
      sw.appendChild(el('span'));
      r.appendChild(sw);
      w.appendChild(r);
    });

    var list = $('apps');
    list.textContent = '';
    apps.forEach(function (app, i) {
      var r = el('div', 'mi-row');
      var ico = el('div', 'app-ico');
      ico.style.background = app.color;
      if (app.icon.type === 'image') {
        var img = el('img');
        img.src = app.icon.value;
        img.alt = '';
        ico.appendChild(img);
      } else {
        ico.textContent = app.icon.value;
      }
      r.appendChild(ico);

      var txt = el('div', 'mi-grow');
      txt.appendChild(el('div', null, app.name));
      if (app.description) txt.appendChild(el('div', 'mi-muted mi-small', app.description));
      r.appendChild(txt);

      var inDock = settings.dock.indexOf(app.id) >= 0;
      var star = el('button', 'icon-btn' + (inDock ? ' on' : ''), '★');
      star.type = 'button';
      star.title = inDock ? 'Retirer du dock' : 'Épingler dans le dock';
      star.disabled = app.hidden;
      star.addEventListener('click', function () {
        var dock = settings.dock.filter(function (x) { return x !== app.id; });
        if (!inDock) {
          if (dock.length >= 5) { MI.notify('Dock plein', '5 applications maximum dans le dock.', 'warning'); return; }
          dock.push(app.id);
        }
        save({ dock: dock });
      });

      var up = el('button', 'icon-btn', '↑');
      up.type = 'button';
      up.title = 'Monter';
      up.disabled = i === 0;
      up.addEventListener('click', function () { move(i, -1); });
      var down = el('button', 'icon-btn', '↓');
      down.type = 'button';
      down.title = 'Descendre';
      down.disabled = i === apps.length - 1;
      down.addEventListener('click', function () { move(i, 1); });

      r.appendChild(star);
      r.appendChild(up);
      r.appendChild(down);

      var sw = el('label', 'mi-switch');
      sw.title = 'Afficher sur l\'écran d\'accueil';
      var input = el('input');
      input.type = 'checkbox';
      input.checked = !app.hidden;
      input.disabled = app.builtin;
      input.addEventListener('change', function () {
        app.hidden = !input.checked;
        var hidden = apps.filter(function (a) { return a.hidden; }).map(function (a) { return a.id; });
        var patch = { hiddenApps: hidden };
        if (app.hidden) patch.dock = settings.dock.filter(function (x) { return x !== app.id; });
        save(patch);
      });
      sw.appendChild(input);
      sw.appendChild(el('span'));
      r.appendChild(sw);
      list.appendChild(r);
    });
  }

  function indexOfWidget(id) {
    for (var i = 0; i < WIDGETS.length; i++) if (WIDGETS[i].id === id) return i;
    return 99;
  }

  function move(i, delta) {
    var j = i + delta;
    if (j < 0 || j >= apps.length) return;
    var tmp = apps[i];
    apps[i] = apps[j];
    apps[j] = tmp;
    save({ appOrder: apps.map(function (a) { return a.id; }) });
  }

  function renderSecurity() {
    $('pin-state').textContent = settings.hasPin ? 'Activé' : 'Désactivé';
    $('current-wrap').hidden = !settings.hasPin;
    $('pin-remove').hidden = !settings.hasPin;
    $('pin-save').textContent = settings.hasPin ? 'Modifier le code' : 'Activer le code';
    $('autolock').value = String(settings.autoLockMinutes);
  }

  function renderAbout() {
    var ctx = MI.context || {};
    var os = ctx.os || {};
    var rows = $('about-rows');
    rows.textContent = '';
    rows.appendChild(row('Système', (os.name || 'MyIntranet OS')));
    rows.appendChild(row('Version', os.version || MI.version));
    rows.appendChild(row('Serveur', os.server));
    rows.appendChild(row('Applications installées', apps.length));
    rows.appendChild(row('Stockage', 'SQLite (serveur)'));
  }

  function renderAll() {
    if (!settings) return;
    renderAppearance();
    renderDisplay();
    renderHome();
    renderSecurity();
    renderAbout();
  }

  // ---------------- Évènements ----------------

  $('theme-seg').addEventListener('click', function (e) {
    var b = e.target.closest('button[data-v]');
    if (b) save({ theme: b.getAttribute('data-v') });
  });
  $('wp-apply').addEventListener('click', function () {
    var url = $('wp-url').value.trim();
    if (!/^https?:\/\//.test(url)) { MI.notify('Fond d\'écran', 'Indiquez une adresse commençant par http:// ou https://', 'warning'); return; }
    save({ wallpaper: 'url:' + url });
  });
  $('brightness').addEventListener('input', function (e) {
    $('brightness-v').textContent = e.target.value + ' %';
    saveLater('brightness', { brightness: +e.target.value });
  });
  $('textscale').addEventListener('input', function (e) {
    $('textscale-v').textContent = e.target.value + ' %';
    saveLater('textScale', { textScale: +e.target.value });
  });
  $('use24h').addEventListener('change', function (e) { save({ use24h: e.target.checked }); });
  $('autolock').addEventListener('change', function (e) { save({ autoLockMinutes: +e.target.value }); });
  $('lock-now').addEventListener('click', function () { MI.lock(); });

  function pinMessage(text, ok) {
    var m = $('pin-msg');
    m.textContent = text;
    m.className = 'msg ' + (ok ? 'ok' : 'err');
  }

  function changePin(newPin) {
    return MI.api('/lock/pin', { body: { current: $('pin-current').value, pin: newPin } }).then(function (r) {
      settings.hasPin = r.hasPin;
      ['pin-current', 'pin-new', 'pin-confirm'].forEach(function (id) { $(id).value = ''; });
      pinMessage(r.hasPin ? 'Code enregistré.' : 'Code supprimé.', true);
      renderSecurity();
      MI.notify('Sécurité', r.hasPin ? 'Code de verrouillage enregistré' : 'Code de verrouillage supprimé', 'success');
    }).catch(function (err) {
      if (err.code === 'too_many_attempts') pinMessage('Trop de tentatives, réessayez plus tard.');
      else pinMessage(err.message);
    });
  }

  $('pin-form').addEventListener('submit', function (e) {
    e.preventDefault();
    var p = $('pin-new').value;
    if (!/^[0-9]{4}$/.test(p)) { pinMessage('Le code doit contenir exactement 4 chiffres.'); return; }
    if (p !== $('pin-confirm').value) { pinMessage('Les deux codes ne correspondent pas.'); return; }
    if (settings.hasPin && !/^[0-9]{4}$/.test($('pin-current').value)) { pinMessage('Saisissez votre code actuel.'); return; }
    changePin(p);
  });
  $('pin-remove').addEventListener('click', function () {
    if (!/^[0-9]{4}$/.test($('pin-current').value)) { pinMessage('Saisissez votre code actuel pour le supprimer.'); return; }
    changePin('');
  });

  $('reset').addEventListener('click', function () {
    if (!confirm('Réinitialiser l\'apparence et l\'écran d\'accueil ?')) return;
    MI.settings.reset().then(function (s) { settings = s; return loadApps(); }).catch(fail);
  });

  // Réglages modifiés ailleurs (centre de contrôle)
  MI.on('settings', function (s) { settings = s; renderAll(); });

  function loadApps() {
    return MI.apps().then(function (a) { apps = a; renderAll(); });
  }

  // ---------------- Chargement ----------------

  MI.ready(function (ctx) {
    if (ctx.os && ctx.os.name) document.title = 'Réglages';
  });

  Promise.all([MI.settings.get(), MI.me(), MI.apps(), MI.api('/info')]).then(function (r) {
    settings = r[0];
    me = r[1];
    apps = r[2];
    if (r[3] && r[3].command) $('reset-cmd').textContent = r[3].command + ' resetcode';
    MI.applyAppearance(settings);
    renderProfile();
    renderAll();
    showSection('profil');
    if (window.matchMedia('(max-width: 720px)').matches) document.body.classList.remove('detail');
  }).catch(fail);
})();
