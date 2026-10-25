/*!
 * MyIntranet OS — kit d'interface pour les applications (facultatif)
 * <script src="/sdk/myintranet.js"></script><script src="/sdk/ui.js"></script>
 */
(function (global) {
  'use strict';

  /** Crée un élément : UI.h('div', { class: 'x', text: 'y', onclick: fn }, enfant1, enfant2...) */
  function h(tag, attrs) {
    var e = document.createElement(tag);
    attrs = attrs || {};
    Object.keys(attrs).forEach(function (k) {
      var v = attrs[k];
      if (v === null || v === undefined || v === false) return;
      if (k === 'class') e.className = v;
      else if (k === 'text') e.textContent = v;
      else if (k === 'style') e.setAttribute('style', v);
      else if (k.indexOf('on') === 0 && typeof v === 'function') e.addEventListener(k.substring(2), v);
      else if (k in e && typeof v !== 'string') e[k] = v;
      else e.setAttribute(k, v === true ? '' : v);
    });
    for (var i = 2; i < arguments.length; i++) add(e, arguments[i]);
    return e;
  }

  function add(parent, child) {
    if (child === null || child === undefined || child === false) return;
    if (Array.isArray(child)) { child.forEach(function (c) { add(parent, c); }); return; }
    parent.appendChild(child instanceof Node ? child : document.createTextNode(String(child)));
  }

  function clear(node) { while (node.firstChild) node.removeChild(node.firstChild); return node; }

  function pad(n) { return n < 10 ? '0' + n : '' + n; }

  function money(v, decimals) {
    if (v === null || v === undefined) return '—';
    var d = decimals === undefined ? 2 : decimals;
    return Number(v).toLocaleString('fr-FR', { minimumFractionDigits: d, maximumFractionDigits: d }) + ' €';
  }

  function ago(ts) {
    var s = Math.max(0, Math.floor(Date.now() / 1000) - ts);
    if (s < 60) return "à l'instant";
    if (s < 3600) return 'il y a ' + Math.floor(s / 60) + ' min';
    if (s < 86400) return 'il y a ' + Math.floor(s / 3600) + ' h';
    if (s < 7 * 86400) return 'il y a ' + Math.floor(s / 86400) + ' j';
    return date(ts);
  }

  function date(ts) { return new Date(ts * 1000).toLocaleDateString('fr-FR', { day: 'numeric', month: 'short', year: 'numeric' }); }
  function time(ts) { var d = new Date(ts * 1000); return pad(d.getHours()) + ':' + pad(d.getMinutes()); }

  var COLORS = ['#0a84ff', '#5e5ce6', '#bf5af2', '#ff375f', '#ff9f0a', '#30d158', '#64d2ff', '#ac8e68'];
  function avatar(name, size) {
    var n = name || '?';
    var sum = 0;
    for (var i = 0; i < n.length; i++) sum += n.charCodeAt(i);
    var a = h('div', { class: 'mi-avatar', text: n.split(/\s+/).filter(Boolean).slice(0, 2).map(function (w) { return w[0].toUpperCase(); }).join('') });
    a.style.background = COLORS[sum % COLORS.length];
    if (size) { a.style.width = a.style.height = size + 'px'; a.style.fontSize = (size / 2.6) + 'px'; }
    return a;
  }

  function empty(text, icon) {
    return h('div', { class: 'mi-empty' }, icon ? h('span', { class: 'ico', text: icon }) : null, text);
  }

  function loading() { return h('div', { class: 'mi-loading', 'aria-label': 'Chargement' }); }

  function header(title, subtitle, action) {
    return h('div', { class: 'mi-header' }, h('div', null, h('h1', { text: title }), subtitle ? h('p', { text: subtitle }) : null), action || null);
  }

  /** Onglets : UI.tabs([{id, label}], onChange, initial) -> élément */
  function tabs(list, onChange, initial) {
    var box = h('div', { class: 'mi-tabs', role: 'tablist' });
    var current = initial || list[0].id;
    list.forEach(function (t) {
      box.appendChild(h('button', {
        type: 'button', class: 'mi-tab' + (t.id === current ? ' on' : ''), text: t.label, 'data-id': t.id,
        onclick: function () {
          current = t.id;
          box.querySelectorAll('.mi-tab').forEach(function (b) { b.classList.toggle('on', b.getAttribute('data-id') === current); });
          onChange(current);
        }
      }));
    });
    return box;
  }

  function field(label, input) { return h('label', { class: 'mi-field' }, h('span', { text: label }), input); }

  /** Fenêtre modale : UI.sheet(titre, contenu, [{label, primary, danger, onclick}]) -> fonction de fermeture */
  function sheet(title, content, actions) {
    var bg = h('div', { class: 'mi-sheet-bg' });
    var close = function () { bg.remove(); };
    var box = h('div', { class: 'mi-sheet', role: 'dialog', 'aria-modal': 'true' }, h('h3', { text: title }), content);
    var row = h('div', { class: 'mi-actions' });
    (actions || [{ label: 'Fermer' }]).forEach(function (a) {
      row.appendChild(h('button', {
        type: 'button', class: 'mi-btn' + (a.primary ? '' : a.danger ? ' danger' : ' secondary'), text: a.label,
        onclick: function () {
          if (!a.onclick) { close(); return; }
          var r = a.onclick(close);
          if (r && r.then) r.catch(error);
        }
      }));
    });
    box.appendChild(row);
    bg.appendChild(box);
    bg.addEventListener('click', function (e) { if (e.target === bg) close(); });
    document.body.appendChild(bg);
    var first = box.querySelector('input, textarea, select');
    if (first) setTimeout(function () { first.focus(); }, 30);
    return close;
  }

  /** Confirmation dans la page (les boîtes du navigateur peuvent être bloquées). */
  function confirm(text, okLabel, danger) {
    return new Promise(function (resolve) {
      sheet(text, h('div'), [
        { label: 'Annuler', onclick: function (close) { close(); resolve(false); } },
        { label: okLabel || 'Confirmer', primary: !danger, danger: !!danger, onclick: function (close) { close(); resolve(true); } }
      ]);
    });
  }

  function error(err) {
    global.MyIntranet.notify('Erreur', (err && err.message) || 'Action impossible', 'error');
  }

  function success(text) { global.MyIntranet.notify('Fait', text, 'success'); }

  /** Bouton qui se désactive pendant l'action. */
  function action(label, fn, cls) {
    var b = h('button', { type: 'button', class: 'mi-btn' + (cls ? ' ' + cls : ''), text: label });
    b.addEventListener('click', function () {
      if (b.disabled) return;
      b.disabled = true;
      Promise.resolve().then(fn).catch(error).then(function () { b.disabled = false; });
    });
    return b;
  }

  global.UI = {
    h: h, add: add, clear: clear, money: money, ago: ago, date: date, time: time, avatar: avatar, empty: empty,
    loading: loading, header: header, tabs: tabs, field: field, sheet: sheet, confirm: confirm, error: error,
    success: success, action: action
  };
})(window);
