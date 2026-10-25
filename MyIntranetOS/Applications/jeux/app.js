/* Jeux : 2048 et Serpent (meilleurs scores enregistrés par personnage) */
(function () {
  'use strict';
  var MI = window.MyIntranet, h = UI.h;
  var root = document.getElementById('app');
  var best = { g2048: 0, snake: 0 };
  var stop = null;

  function saveBest(key, score) {
    if (score <= (best[key] || 0)) return;
    best[key] = score;
    MI.storage.set('best', best).catch(function () {});
  }

  function menu() {
    if (stop) { stop(); stop = null; }
    UI.clear(root);
    root.appendChild(UI.header('Jeux'));
    var grid = h('div', { class: 'mi-grid2' });
    [['2048', 'Fusionnez les tuiles jusqu\'à 2048. Flèches du clavier.', 'g2048', play2048, '#edc22e'],
     ['Serpent', 'Mangez sans vous mordre. Flèches du clavier.', 'snake', playSnake, '#30d158']].forEach(function (g) {
      grid.appendChild(h('div', { class: 'mi-card mi-stack', style: 'cursor: pointer', onclick: g[3] },
        h('div', { style: 'height: 90px; border-radius: 12px; display: flex; align-items: center; justify-content: center; font-size: 2rem; font-weight: 900; color: #fff; background: ' + g[4], text: g[0] }),
        h('div', { class: 'mi-sub', text: g[1] }),
        h('div', { class: 'mi-title', text: 'Record : ' + (best[g[2]] || 0) })));
    });
    root.appendChild(grid);
  }

  function back() { return h('button', { type: 'button', class: 'mi-btn ghost', text: '‹ Jeux', onclick: menu }); }

  function pad(onDir) {
    var b = function (label, d) { return h('button', { type: 'button', class: 'mi-btn secondary', text: label, onclick: function () { onDir(d); } }); };
    return h('div', { style: 'display: grid; grid-template-columns: repeat(3, 56px); gap: 6px; justify-content: center; margin-top: 1rem' },
      h('span'), b('↑', 'up'), h('span'), b('←', 'left'), b('↓', 'down'), b('→', 'right'));
  }

  function keys(onDir) {
    var map = { ArrowUp: 'up', ArrowDown: 'down', ArrowLeft: 'left', ArrowRight: 'right' };
    var fn = function (e) { if (map[e.key]) { e.preventDefault(); onDir(map[e.key]); } };
    window.addEventListener('keydown', fn);
    return function () { window.removeEventListener('keydown', fn); };
  }

  // ---------------- 2048 ----------------
  function play2048() {
    var size = 4, grid, score = 0, over = false;
    var COLORS = { 2: '#eee4da', 4: '#ede0c8', 8: '#f2b179', 16: '#f59563', 32: '#f67c5f', 64: '#f65e3b', 128: '#edcf72', 256: '#edcc61', 512: '#edc850', 1024: '#edc53f', 2048: '#edc22e' };
    function empty() { var a = []; for (var i = 0; i < size; i++) { a.push([0, 0, 0, 0]); } return a; }
    function spawn() {
      var free = [];
      grid.forEach(function (r, y) { r.forEach(function (v, x) { if (!v) free.push([y, x]); }); });
      if (!free.length) return;
      var c = free[Math.floor(Math.random() * free.length)];
      grid[c[0]][c[1]] = Math.random() < 0.9 ? 2 : 4;
    }
    function slide(row) {
      var r = row.filter(Boolean);
      for (var i = 0; i < r.length - 1; i++) if (r[i] === r[i + 1]) { r[i] *= 2; score += r[i]; r.splice(i + 1, 1); }
      while (r.length < size) r.push(0);
      return r;
    }
    function move(dir) {
      if (over) return;
      var before = JSON.stringify(grid);
      for (var i = 0; i < size; i++) {
        var line = [];
        for (var j = 0; j < size; j++) line.push(dir === 'left' || dir === 'right' ? grid[i][j] : grid[j][i]);
        if (dir === 'right' || dir === 'down') line.reverse();
        line = slide(line);
        if (dir === 'right' || dir === 'down') line.reverse();
        for (j = 0; j < size; j++) { if (dir === 'left' || dir === 'right') grid[i][j] = line[j]; else grid[j][i] = line[j]; }
      }
      if (JSON.stringify(grid) !== before) spawn();
      over = !canMove();
      saveBest('g2048', score);
      draw();
    }
    function canMove() {
      for (var y = 0; y < size; y++) for (var x = 0; x < size; x++) {
        if (!grid[y][x]) return true;
        if (x < size - 1 && grid[y][x] === grid[y][x + 1]) return true;
        if (y < size - 1 && grid[y][x] === grid[y + 1][x]) return true;
      }
      return false;
    }
    var board = h('div', { style: 'display: grid; grid-template-columns: repeat(4, 1fr); gap: 8px; background: #bbada0; padding: 8px; border-radius: 12px; width: min(360px, 100%); margin: 0 auto; aspect-ratio: 1' });
    var info = h('div', { class: 'mi-title', style: 'text-align: center; margin-bottom: .6rem' });
    function draw() {
      UI.clear(board);
      grid.forEach(function (r) { r.forEach(function (v) {
        board.appendChild(h('div', { style: 'border-radius: 6px; display: flex; align-items: center; justify-content: center; font-weight: 900; font-size: ' + (v >= 1024 ? '1.2rem' : '1.6rem') + '; background: ' + (v ? (COLORS[v] || '#3c3a32') : 'rgba(238,228,218,.35)') + '; color: ' + (v > 4 ? '#fff' : '#776e65'), text: v || '' }));
      }); });
      info.textContent = 'Score ' + score + ' · Record ' + Math.max(score, best.g2048 || 0) + (over ? ' · Partie terminée' : '');
    }
    UI.clear(root);
    root.appendChild(h('div', { class: 'mi-header' }, h('div', null, back(), h('h1', { text: '2048' })), h('button', { type: 'button', class: 'mi-btn secondary', text: 'Rejouer', onclick: play2048 })));
    root.appendChild(info);
    root.appendChild(board);
    root.appendChild(pad(move));
    if (stop) stop();
    stop = keys(move);
    grid = empty(); spawn(); spawn(); draw();
  }

  // ---------------- Serpent ----------------
  function playSnake() {
    var N = 18, cell = 18, snake = [[9, 9], [9, 8], [9, 7]], dir = 'right', next = 'right', food = null, score = 0, dead = false;
    var canvas = h('canvas', { width: N * cell, height: N * cell, style: 'display: block; margin: 0 auto; border-radius: 12px; background: #111; max-width: 100%' });
    var info = h('div', { class: 'mi-title', style: 'text-align: center; margin-bottom: .6rem' });
    var ctx = canvas.getContext('2d');
    var OPP = { up: 'down', down: 'up', left: 'right', right: 'left' };
    function place() {
      do { food = [Math.floor(Math.random() * N), Math.floor(Math.random() * N)]; }
      while (snake.some(function (s) { return s[0] === food[0] && s[1] === food[1]; }));
    }
    function turn(d) { if (d !== OPP[dir]) next = d; }
    function step() {
      if (dead) return;
      dir = next;
      var head = snake[0].slice();
      if (dir === 'up') head[0]--; if (dir === 'down') head[0]++; if (dir === 'left') head[1]--; if (dir === 'right') head[1]++;
      if (head[0] < 0 || head[1] < 0 || head[0] >= N || head[1] >= N || snake.some(function (s) { return s[0] === head[0] && s[1] === head[1]; })) {
        dead = true;
        saveBest('snake', score);
      } else {
        snake.unshift(head);
        if (head[0] === food[0] && head[1] === food[1]) { score++; place(); } else snake.pop();
      }
      draw();
    }
    function draw() {
      ctx.fillStyle = '#111'; ctx.fillRect(0, 0, canvas.width, canvas.height);
      ctx.fillStyle = '#ff375f'; ctx.fillRect(food[1] * cell + 3, food[0] * cell + 3, cell - 6, cell - 6);
      snake.forEach(function (s, i) { ctx.fillStyle = i ? '#30d158' : '#7dff9a'; ctx.fillRect(s[1] * cell + 1, s[0] * cell + 1, cell - 2, cell - 2); });
      info.textContent = 'Score ' + score + ' · Record ' + Math.max(score, best.snake || 0) + (dead ? ' · Perdu !' : '');
    }
    UI.clear(root);
    root.appendChild(h('div', { class: 'mi-header' }, h('div', null, back(), h('h1', { text: 'Serpent' })), h('button', { type: 'button', class: 'mi-btn secondary', text: 'Rejouer', onclick: playSnake })));
    root.appendChild(info);
    root.appendChild(canvas);
    root.appendChild(pad(turn));
    if (stop) stop();
    var unkey = keys(turn);
    var timer = setInterval(step, 140);
    stop = function () { unkey(); clearInterval(timer); };
    place(); draw();
  }

  MI.storage.get('best').then(function (b) { if (b) best = b; }).catch(function () {}).then(menu);
})();
