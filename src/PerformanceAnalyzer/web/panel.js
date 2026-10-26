// The Performance Analyzer panel, drawn over the game's web UI in its own shadow root. It only shows what the plugin
// sends twice a second and sends back what the buttons ask for; the measuring happens in the plugin and in the web
// probe. The small overlay in the top-right corner lives in the same root and draws even with the panel closed.
(function () {
  if (window.__slperfanalyzer) return;

  const PREFIX = 'slmod|perfanalyzer|';
  const F = window.__slperfanalyzerFormat;
  const TABS = [['overview', 'Overview'], ['game', 'Game'], ['mods', 'Mods'], ['methods', 'Methods'], ['hitches', 'Hitches'], ['web', 'Web UI'], ['report', 'Report']];
  const send = (m) => { try { if (window.vuplex) window.vuplex.postMessage(PREFIX + m); } catch (e) { /* the bridge is gone; the plugin sends the panel again */ } };
  const esc = F.esc;

  const host = document.createElement('div');

  host.id = 'slperfanalyzer-host';
  host.style.cssText = 'position:fixed;left:0;top:0;right:0;bottom:0;z-index:2147483000;pointer-events:none;';

  const root = host.attachShadow({ mode: 'open' });
  const $ = (s) => root.querySelector(s);

  root.innerHTML = '<style>' + (window.__slperfanalyzerCss || '') + '</style>' +
    '<div id="overlay" hidden></div>' +
    '<div id="panel" hidden>' +
    '<header><div class="brand"><b>PERFORMANCE ANALYZER</b><span id="ver"></span></div>' +
    '<div id="rec"></div>' +
    '<div class="actions">' +
    '<button data-act="toggle" id="toggle">Start</button>' +
    '<button data-act="reset" title="Clear every number and start counting again">Reset</button>' +
    '<button data-act="unhook" title="Stop and remove every probe, so the other mods run untouched">Unhook</button>' +
    '<button data-act="copy" title="Put the plain-text report on the clipboard">Copy report</button>' +
    '<button data-act="save" title="Save the report and a per-frame file to BepInEx/performance-analyzer">Save report</button>' +
    '<button data-act="overlay" id="ovl" title="Show frame time in the top-right corner with the panel closed">Overlay</button>' +
    '<button data-act="close" class="x" title="Close (Esc)">X</button>' +
    '</div></header>' +
    '<nav id="tabs"></nav>' +
    '<main id="main"></main>' +
    '<footer id="foot"></footer>' +
    '<div id="toast" hidden></div>' +
    '</div>';

  const state = { open: false, tab: 'overview', key: '', version: '', snap: null, report: '', sort: { methods: ['selfPerFrame', false], mods: ['avg', false] }, filter: '', side: 'mods', toastTimer: 0 };
  const RATING = { fine: 'good', watch: 'warn', heavy: 'bad' };
  const rating = (r) => `<td class="${RATING[r] || ''}">${esc(r || '')}</td>`;

  $('#tabs').innerHTML = TABS.map(([key, label]) => `<button data-tab="${key}">${label}</button>`).join('');

  root.addEventListener('click', (e) => {
    const el = e.target.closest ? e.target.closest('[data-act],[data-tab],[data-deep],[data-sort],[data-side]') : null;

    if (!el) return;

    if (el.dataset.tab) return showTab(el.dataset.tab, true);
    if (el.dataset.side) { state.side = el.dataset.side; return drawMethods(false); }
    if (el.dataset.deep !== undefined) return send('deep|' + el.dataset.deep);
    if (el.dataset.sort) return sortBy(el.dataset.sort);

    const act = el.dataset.act;
    const snap = state.snap;

    if (act === 'toggle') send(snap && snap.recording ? 'stop' : 'start');
    else if (act === 'overlay') send('overlay|' + (snap && snap.overlay ? '0' : '1'));
    else if (act === 'close') send('close');
    else if (act === 'refresh') send('tab|report');
    else send(act);
  });

  root.addEventListener('input', (e) => {
    if (e.target.id !== 'filter') return;

    state.filter = e.target.value;
    drawMethods(true);
  });

  document.addEventListener('keydown', (e) => {
    if (state.open && e.key === 'Escape') { e.stopPropagation(); send('close'); }
  }, true);

  function showTab(key, tell) {
    state.tab = key;

    for (const b of root.querySelectorAll('#tabs button')) b.classList.toggle('on', b.dataset.tab === key);

    if (tell) send('tab|' + key);

    draw();
  }

  function sortBy(spec) {
    const [table, field] = spec.split(':');
    const current = state.sort[table];

    state.sort[table] = current && current[0] === field ? [field, !current[1]] : [field, false];
    draw();
  }

  // ---------- drawing ----------

  function draw() {
    const snap = state.snap;

    drawOverlay();

    if (!state.open) return;

    drawHeader(snap);

    if (!snap) { $('#main').innerHTML = '<p class="dim">Waiting for the first numbers...</p>'; return; }

    if (state.tab === 'overview') drawOverview(snap);
    else if (state.tab === 'game') drawGame(snap);
    else if (state.tab === 'mods') drawMods(snap);
    else if (state.tab === 'methods') drawMethods(false);
    else if (state.tab === 'hitches') drawHitches(snap);
    else if (state.tab === 'web') drawWeb(snap);
    else if (state.tab === 'report') drawReport();

    drawFooter(snap);
  }

  function drawHeader(snap) {
    $('#ver').textContent = state.version ? ' ' + state.version : '';

    const recording = !!(snap && snap.recording);

    $('#toggle').textContent = recording ? 'Stop' : 'Start';
    $('#toggle').classList.toggle('live', recording);
    $('#ovl').classList.toggle('on', !!(snap && snap.overlay));
    $('#rec').innerHTML = snap
      ? `<i class="${recording ? 'live' : ''}"></i>${recording ? 'Recording' : 'Stopped'} <b>${F.seconds(snap.seconds)}</b> <span>${F.count(snap.frames)} frames, ${F.count(snap.hitchCount)} hitches over ${F.ms(snap.hitchMs)} ms</span>`
      : '';
  }

  function tile(label, value, cls, hint, sub) {
    return `<div class="tile ${cls || ''}" title="${esc(hint || '')}"><span>${label}</span><b>${value}</b>${sub ? `<em>${sub}</em>` : ''}</div>`;
  }

  // Frames per second as a class: under 60 is "warn", under 30 is "bad".
  function fpsClass(v) {
    return v <= 0 ? '' : v < 30 ? 'bad' : v < 59 ? 'warn' : 'good';
  }

  function speedText(v) {
    return v > 0 ? (Math.round(v * 100) / 100) + 'x' : '-';
  }

  function drawOverview(snap) {
    const f = snap.frame || {};
    const s = snap.split || {};
    const verdict = (snap.verdict || []).map((line, i) => `<p class="${i === 0 ? 'lead' : ''}">${esc(line)}</p>`).join('');
    const total = Math.max(0.0001, s.frame || 0);
    const parts = [['mods', 'Mods', s.mods], ['hook', 'Hook overhead', s.hook], ['game', 'Game code in hooks', s.game], ['analyzer', 'This analyzer', s.analyzer], ['rest', 'Rest of the frame', s.rest]];
    const bar = parts.map(([cls, label, v]) => `<i class="${cls}" style="width:${(100 * (v || 0) / total).toFixed(2)}%" title="${label}: ${F.ms(v)} ms"></i>`).join('');
    const legend = parts.map(([cls, label, v]) => `<span><i class="${cls}"></i>${label} <b>${F.ms(v)} ms</b></span>`).join('');
    const mods = (snap.mods || []).filter((m) => !m.idle).map((m) => `<tr><td>${esc(m.name)}${m.self ? ' <em>(this tool)</em>' : ''}</td>${rating(m.rating)}<td class="n ${F.costClass(m.avg)}">${F.ms(m.avg)}</td><td class="n">${F.ms(m.p99)}</td><td class="n">${F.ms(m.worst)}</td><td class="n">${F.percent(m.share)}</td></tr>`).join('');

    $('#main').innerHTML =
      `<section class="verdict">${verdict}</section>` +
      '<section class="tiles">' +
      tile('Average', F.ms(f.mean) + ' ms', F.frameClass(f.mean), 'Mean frame time over the window') +
      tile('Median', F.ms(f.p50) + ' ms', F.frameClass(f.p50)) +
      tile('95th', F.ms(f.p95) + ' ms', F.frameClass(f.p95), '1 frame in 20 is slower than this') +
      tile('99th', F.ms(f.p99) + ' ms', F.frameClass(f.p99), '1 frame in 100 is slower than this') +
      tile('Worst', F.ms(f.max) + ' ms', F.frameClass(f.max)) +
      tile('FPS', f.mean > 0 ? (1000 / f.mean).toFixed(0) : '-', '') +
      overviewGameTiles(snap) +
      '</section>' +
      `<section><h3>Where an average frame goes <span class="dim">(${F.ms(s.frame)} ms)</span></h3><div class="split">${bar}</div><div class="legend">${legend}</div></section>` +
      `<section><h3>Frame time, last ${(snap.graph && snap.graph.ms ? snap.graph.ms.length : 0)} frames</h3>${graph(snap.graph)}</section>` +
      stutterSection(snap.stutters, 'Stutter makers') +
      `<section><h3>Mods costing the most</h3><table><tr><th>Mod</th><th>rating</th><th class="n">ms / frame</th><th class="n">99th</th><th class="n">worst frame</th><th class="n">share</th></tr>${mods || '<tr><td colspan="6" class="dim">No mod has run any timed code yet.</td></tr>'}</table></section>`;
  }

  // Mods that made frames slow on their own, with the method that did it. Empty when none did.
  function stutterSection(list, title) {
    const rows = (list || []).map((s) => `<tr><td>${esc(s.name)}</td>${rating(s.rating)}<td class="n">${F.count(s.caused)}</td><td class="n">${F.ms(s.typical)}</td><td class="n ${F.frameClass(s.worst)}">${F.ms(s.worst)}</td><td class="name">${esc(s.method)}</td></tr>`).join('');

    if (!rows) return '';

    return `<section><h3>${title}</h3><table><tr><th>Mod</th><th>rating</th><th class="n">stutters</th><th class="n">typical ms</th><th class="n">worst ms</th><th>slowest method</th></tr>${rows}</table></section>`;
  }

  // The game side on the Overview: logic updates and the process's memory. The Game tab has the rest.
  function overviewGameTiles(snap) {
    const g = snap.game || {};
    const mem = snap.memory || {};
    const process = mem.process || {};

    return (g.seenGame ? tile('Logic / s', g.logic.toFixed(0), '', 'Game world updates per second while in a game. The game updates its world once per frame, so this follows the frame rate.') : '') +
      (g.inGame ? tile('Game speed', speedText(g.speed), '', g.paused ? 'Paused' : 'The game speed setting') : '') +
      tile('Memory', F.mb(process.now), '', 'The game process in RAM. The Game tab splits it up.');
  }

  function drawGame(snap) {
    const g = snap.game || {};
    const mem = snap.memory || {};
    const t = snap.timeline || {};
    const lost = g.cappedPct || 0;
    const scenes = (snap.scenes || []).map((s) => `<tr><td>${esc(s.name || '(none)')}</td><td class="n">${F.seconds(s.seconds)}</td><td class="n ${fpsClass(s.fps)}">${s.fps.toFixed(1)}</td>` +
      `<td class="n">${F.ms(s.mean)}</td><td class="n">${F.ms(s.p50)}</td><td class="n">${F.ms(s.p95)}</td><td class="n ${F.frameClass(s.p99)}">${F.ms(s.p99)}</td><td class="n">${F.ms(s.max)}</td>` +
      `<td class="n">${F.percent(s.over16)}</td><td class="n">${F.percent(s.over33)}</td><td class="n">${F.count(s.hitches)}</td><td class="n">${s.logic.toFixed(1)}</td></tr>`).join('');
    const speeds = (snap.speeds || []).map((s) => `<span>${speedText(s.speed)} <b>${F.percent(s.pct)}</b></span>`).join('');
    const memTiles = [['game', 'Game script heap', 'The game\'s own script memory. Growing every minute in a long session can mean a leak.'],
      ['mods', 'Mods\' heap', 'Memory of the mods\' .NET runtime, shared by every mod.'],
      ['unity', 'Engine allocated', 'Memory the engine has handed out.'],
      ['reserved', 'Engine reserved', 'Memory the engine has taken from the system.'],
      ['textures', 'Textures', 'Memory the loaded textures use.'],
      ['process', 'Process in RAM', 'The whole game process in RAM.']]
      .map(([key, label, hint]) => {
        const m = mem[key] || {};
        const per = m.perMin ? `${m.perMin > 0 ? '+' : ''}${m.perMin.toFixed(1)} MB/min` : '';

        return tile(label, F.mb(m.now), '', hint, m.now > 0 ? `peak ${F.mb(m.peak)}${per ? ', ' + per : ''}` : 'not available');
      }).join('');

    $('#main').innerHTML =
      '<p class="dim">The game has no fixed tick: it updates its world once per frame, so logic updates per second follow the frame rate. When one frame runs longer than the engine allows, the game itself slows down; that is the game time lost.</p>' +
      '<section class="tiles">' +
      tile('FPS now', g.fpsNow > 0 ? g.fpsNow.toFixed(0) : '-', fpsClass(g.fpsNow), 'Frames in the last whole second') +
      tile('Logic / s', g.seenGame ? g.logicNow.toFixed(0) : '-', '', 'Game world updates in the last whole second', g.seenGame ? `average ${g.logic.toFixed(1)}` : 'no game loaded') +
      tile('Physics / s', g.fixedNow.toFixed(0), '', 'Physics steps the engine ran in the last whole second', `set to ${g.physicsRate.toFixed(0)}, average ${g.fixed.toFixed(1)}`) +
      tile('Game speed', g.inGame ? (g.paused ? 'paused' : speedText(g.speed)) : '-', '', 'The game speed setting', `engine time scale ${g.timeScale}`) +
      tile('Game clock', g.seenGame ? g.clockNow.toFixed(0) : '-', '', 'In-game seconds per real second in the last whole second', g.seenGame ? `average ${g.clockRate.toFixed(1)} while running` : '') +
      tile('Game time lost', F.percent(lost), lost >= 5 ? 'bad' : lost >= 1 ? 'warn' : '', `Game time dropped because a frame took longer than ${g.maxDeltaMs} ms`, `${F.count(g.cappedFrames)} frames, ${F.ms(g.cappedMs / 1000)} s`) +
      '</section>' +
      (speeds ? `<section><h3>Time at each game speed</h3><div class="chips">${speeds}</div></section>` : '') +
      `<section><h3>Frames and logic updates per second, last ${(t.fps || []).length} seconds</h3>${secondsGraph([['ms', 'Frames per second', t.fps], ['game', 'Logic updates per second', t.logic]], 60, '/s')}</section>` +
      `<section><h3>Memory</h3><div class="tiles">${memTiles}</div></section>` +
      `<section><h3>Memory, last ${(t.process || []).length} seconds</h3>${secondsGraph([['ms', 'Process in RAM', t.process], ['game', 'Engine allocated', t.unity], ['mods', 'Game script heap', t.gameHeap]], 100, ' MB')}</section>` +
      '<section><h3>Scenes</h3><table><tr><th>Scene</th><th class="n">time</th><th class="n">fps</th><th class="n">mean</th><th class="n">median</th><th class="n">95th</th><th class="n">99th</th><th class="n">worst</th><th class="n">over 16.7</th><th class="n">over 33.3</th><th class="n">hitches</th><th class="n">logic / s</th></tr>' +
      (scenes || '<tr><td colspan="12" class="dim">Nothing recorded yet.</td></tr>') + '</table><p class="dim">Frame times in ms since the last reset. "Over 16.7" is the share of frames under 60 frames per second, "over 33.3" under 30.</p></section>';
  }

  // One line per series over the last seconds; a series with no readings is left out.
  function secondsGraph(series, floor, unit) {
    const shown = series.filter(([, , list]) => Array.isArray(list) && list.some((v) => v > 0));

    if (shown.length === 0 || shown[0][2].length < 2) return '<p class="dim">Record for a few seconds to draw the graph.</p>';

    const W = 1000;
    const H = 160;
    const max = F.niceMax(shown.map((s) => s[2]), floor);

    return `<svg class="graph" viewBox="0 0 ${W} ${H}" preserveAspectRatio="none">` +
      shown.map(([cls, , list]) => `<polyline class="${cls}" points="${F.points(list, W, H, max)}"/>`).join('') +
      `<text x="${W - 4}" y="12" class="r">${max}${unit}</text></svg>` +
      '<div class="legend">' + shown.map(([cls, label]) => `<span><i class="${cls === 'ms' ? 'msl' : cls}"></i>${label}</span>`).join('') + '</div>';
  }

  function graph(g) {
    if (!g || !g.ms || g.ms.length < 2) return '<p class="dim">Start recording to draw the graph.</p>';

    const W = 1000;
    const H = 160;
    const max = F.graphMax(g.ms);
    const y = (v) => (H - Math.min(1, v / max) * H).toFixed(1);
    const step = W / (g.ms.length - 1);
    const gcs = g.gc.map((n, i) => (n > 0 ? `<line class="gc" x1="${(i * step).toFixed(1)}" x2="${(i * step).toFixed(1)}" y1="0" y2="${H}"/>` : '')).join('');
    const lines = [16.7, 33.3].filter((v) => v < max).map((v) => `<line class="guide" x1="0" x2="${W}" y1="${y(v)}" y2="${y(v)}"/><text x="4" y="${Number(y(v)) - 3}">${v} ms</text>`).join('');

    return `<svg class="graph" viewBox="0 0 ${W} ${H}" preserveAspectRatio="none">${gcs}${lines}` +
      `<polyline class="ms" points="${F.points(g.ms, W, H, max)}"/>` +
      `<polyline class="mods" points="${F.points(g.mods, W, H, max)}"/>` +
      `<polyline class="game" points="${F.points(g.game, W, H, max)}"/>` +
      `<text x="${W - 4}" y="12" class="r">${max} ms</text></svg>` +
      '<div class="legend"><span><i class="msl"></i>Whole frame</span><span><i class="mods"></i>Mods</span><span><i class="game"></i>Game code in hooks</span><span><i class="gcl"></i>Garbage collection</span></div>';
  }

  function header(table, cols) {
    const [field, ascending] = state.sort[table] || [];

    return '<tr>' + cols.map(([key, label, num]) => `<th class="${num ? 'n' : ''} sort ${field === key ? 'on' : ''}" data-sort="${table}:${key}">${label}${field === key ? (ascending ? ' ^' : ' v') : ''}</th>`).join('') + '</tr>';
  }

  function drawMods(snap) {
    const [field, ascending] = state.sort.mods;
    const rows = F.sortRows((snap.mods || []).filter((m) => !m.idle), field, ascending);
    const idle = (snap.mods || []).filter((m) => m.idle).map((m) => esc(m.name));
    const deep = (snap.status && snap.status.deepMod) || '';
    const cols = [['name', 'Mod'], ['rating', 'rating'], ['avg', 'ms / frame', 1], ['p99', '99th', 1], ['worst', 'worst frame', 1], ['share', 'share', 1], ['allocKbS', 'alloc / s', 1], ['hitches', 'hitches caused', 1], ['probes', 'timed', 1], ['calls', 'calls', 1]];
    const body = rows.map((m) => `<tr><td>${esc(m.name)} <span class="dim">${esc(m.version)}</span>${m.self ? ' <em>(this tool)</em>' : ''}</td>${rating(m.rating)}` +
      `<td class="n ${F.costClass(m.avg)}">${F.ms(m.avg)}</td><td class="n">${F.ms(m.p99)}</td><td class="n">${F.ms(m.worst)}</td><td class="n">${F.percent(m.share)}</td>` +
      `<td class="n">${F.kb(m.allocKbS)}/s</td><td class="n">${F.count(m.hitches)}</td><td class="n">${F.count(m.probes)}</td><td class="n">${F.count(m.calls)}</td>` +
      `<td>${m.self ? '' : deep === m.key ? '<button data-deep="" class="on" title="Stop timing every method of this mod">Deep on</button>' : `<button data-deep="${esc(m.key)}" title="Time every method of this mod, not only its hooks and updates">Deep</button>`}</td></tr>`).join('');

    $('#main').innerHTML =
      stutterSection(snap.stutters, 'Stutter makers: mods that made frames slow on their own') +
      '<p class="dim">Time each mod spent in its own code per frame, averaged over every recorded frame. "Hitches caused" counts slow frames where the mod took 40% of the frame or more. Rating: heavy is 1 ms a frame or more, 3 stutters, or one frame as slow as a hitch; watch is 0.25 ms a frame, any stutter, a frame of half a hitch, or 2 ms in its slowest 1% of frames. Deep times every method of one mod, so the Methods tab shows what inside it is slow; it adds overhead to that mod while on.</p>' +
      `<table class="wide">${header('mods', cols)}${body || '<tr><td colspan="11" class="dim">No mod has run any timed code yet.</td></tr>'}</table>` +
      (idle.length ? `<p class="dim">${idle.length} more with no measurable time: ${idle.join(', ')}</p>` : '');
  }

  function drawMethods(keepFocus) {
    const snap = state.snap;

    if (!snap || state.tab !== 'methods') return;

    const [field, ascending] = state.sort.methods;
    const side = (snap.methods || []).filter((m) => state.side === 'all' || (state.side === 'game') === !!m.game);
    const rows = F.sortRows(F.filterRows(side, state.filter, ['name', 'kind', 'target', 'owners']), field, ascending);
    const cols = [['name', 'Method'], ['kind', 'Kind'], ['owners', 'Mod'], ['perFrame', 'calls / frame', 1], ['selfPerFrame', 'self ms / frame', 1], ['self', 'self ms', 1], ['total', 'total ms', 1], ['maxCall', 'slowest call', 1], ['worstFrame', 'worst frame', 1], ['allocKbS', 'alloc / s', 1], ['offThread', 'off thread', 1]];
    const sides = [['mods', 'Mod code'], ['game', 'Game code in hooks'], ['all', 'All']].map(([key, label]) => `<button data-side="${key}" class="${state.side === key ? 'on' : ''}">${label}</button>`).join('');
    const body = rows.slice(0, 300).map((m) => `<tr title="${esc(m.target ? 'on ' + m.target : '')}"><td class="name">${esc(m.name)}</td><td>${esc(m.kind)}</td><td>${esc(m.owners)}</td>` +
      `<td class="n">${F.ms(m.perFrame)}</td><td class="n ${F.costClass(m.selfPerFrame)}">${F.ms(m.selfPerFrame)}</td><td class="n">${F.ms(m.self)}</td><td class="n">${F.ms(m.total)}</td>` +
      `<td class="n">${F.ms(m.maxCall)}</td><td class="n">${F.ms(m.worstFrame)}</td><td class="n">${m.allocKbS ? F.kb(m.allocKbS) + '/s' : ''}</td><td class="n">${m.offThread ? F.count(m.offThread) : ''}</td></tr>`).join('');
    const table = `<table class="wide">${header('methods', cols)}${body || '<tr><td colspan="11" class="dim">Nothing timed matches.</td></tr>'}</table>`;

    if (keepFocus && $('#methods')) { $('#methods').innerHTML = table; return; }

    $('#main').innerHTML =
      `<div class="bar">${sides}<input id="filter" placeholder="Filter by method, kind, target or mod" value="${esc(state.filter)}"><span class="dim">Self time leaves out timed code called inside. Hover a row for the method it hooks.</span></div>` +
      `<div id="methods">${table}</div>`;
  }

  function drawHitches(snap) {
    const list = snap.hitches || [];
    const cards = list.map((h) => {
      const top = (h.top || []).map((t) => `<tr><td class="name">${esc(t.name)}</td><td>${esc(t.kind)}</td><td>${esc(t.owner)}</td><td class="n">${F.ms(t.ms)}</td><td class="n">${F.count(t.calls)}</td></tr>`).join('');
      const mods = (h.mods || []).map((m) => `<span>${esc(m.name)} <b>${F.ms(m.ms)} ms</b></span>`).join('');
      const engine = h.engine ? ` <span class="dim">engine: main ${F.ms(h.engine.main)}, render ${F.ms(h.engine.render)}, GPU ${F.ms(h.engine.gpu)} ms</span>` : '';
      const gc = h.gcManaged || h.gcGame ? ` <span class="warn">GC: ${h.gcManaged} mod, ${h.gcGame} game</span>` : '';

      return `<article><h4><b class="${F.frameClass(h.ms)}">${F.ms(h.ms)} ms</b> #${h.n} at ${F.seconds(h.at)}${h.scene ? ' in ' + esc(h.scene) : ''}${gc}${engine}</h4>` +
        `<p>${esc(h.cause)}</p>` +
        `<p class="dim">mods ${F.ms(h.mods)} ms, hook overhead ${F.ms(h.hook)} ms, game code in hooks ${F.ms(h.game)} ms, this analyzer ${F.ms(h.analyzer)} ms, rest ${F.ms(h.rest)} ms</p>` +
        (mods ? `<div class="chips">${mods}</div>` : '') +
        (top ? `<table><tr><th>Slowest in this frame</th><th>Kind</th><th>Mod</th><th class="n">ms</th><th class="n">calls</th></tr>${top}</table>` : '') +
        '</article>';
    }).join('');

    const groups = (snap.hitchGroups || []).map((g) => `<tr><td class="n">${F.count(g.count)}</td><td class="n">${F.ms(g.typical)}</td><td class="n ${F.frameClass(g.worst)}">${F.ms(g.worst)}</td><td class="n">#${g.worstN}</td><td>${esc(g.cause)}</td><td class="name">${esc(g.method)}</td></tr>`).join('');
    const kept = snap.hitchesKept < snap.hitchCount ? ` <span class="dim">(the last ${F.count(snap.hitchesKept)} of ${F.count(snap.hitchCount)})</span>` : '';

    $('#main').innerHTML = (groups ? `<section><h3>Hitches by cause${kept}</h3><table><tr><th class="n">count</th><th class="n">typical ms</th><th class="n">worst ms</th><th class="n">worst</th><th>Cause</th><th>Slowest method</th></tr>${groups}</table><p class="dim">Typical is the median frame time of the group. The slowest method is named when it took a fifth of the frame or more.</p></section>` : '') +
      `<p class="dim">Frames slower than ${F.ms(snap.hitchMs)} ms, newest first. "Rest" is time no probe saw: the game's own updates, rendering, loading, or garbage collection.</p>` + (cards || '<p class="dim">No hitches yet.</p>');
  }

  function drawWeb(snap) {
    const scripts = (snap.scripts || []).map((s) => `<tr><td>${esc(s.owner)}</td><td class="n">${F.count(s.calls)}</td><td class="n">${s.perSecond.toFixed(1)}</td><td class="n">${F.kb(s.chars / 1024)}</td><td class="n">${F.kb(s.kbPerSecond)}</td><td class="n">${F.kb(s.largest / 1024)}</td></tr>`).join('');
    const probe = window.__slperfprobe;
    const w = probe && probe.summary ? probe.summary() : null;
    const owners = w ? w.owners.map((o) => `<tr><td>${esc(o.key)}</td><td class="n ${F.costClass(o.ms / Math.max(1, w.seconds * w.fps))}">${F.ms(o.ms)}</td><td class="n">${F.count(o.calls)}</td><td class="n">${F.ms(o.worstMs)}</td><td class="n">${F.count(o.dom)}</td><td class="n">${F.count(o.listeners)}</td><td class="n">${F.count(o.timers)}</td><td class="n">${F.count(o.intervals)}</td><td class="n">${F.count(o.observers)}</td><td class="n">${F.count(o.posts)}</td></tr>`).join('') : '';

    $('#main').innerHTML =
      '<section><h3>Scripts sent to the web UI</h3><p class="dim">Every script a mod or the game hands the web view. Many small scripts a second, or large ones, keep the page busy.</p>' +
      `<table><tr><th>Sender</th><th class="n">scripts</th><th class="n">per second</th><th class="n">size</th><th class="n">per second</th><th class="n">largest</th></tr>${scripts || '<tr><td colspan="6" class="dim">None counted. Start recording.</td></tr>'}</table></section>` +
      '<section><h3>Inside the page</h3>' +
      (w
        ? `<div class="tiles">${tile('Page FPS', w.fps.toFixed(0), '')}${tile('Longest gap', F.ms(w.worstGapMs) + ' ms', F.frameClass(w.worstGapMs))}${tile('Long tasks', F.count(w.longTasks) + ' / ' + F.ms(w.longTaskMs) + ' ms', '')}${tile('Script heap', w.heapMB.toFixed(1) + ' MB', '')}${tile('Elements', F.count(w.domNodes), '')}</div>` +
          `<table><tr><th>Script owner</th><th class="n">run ms</th><th class="n">runs</th><th class="n">slowest</th><th class="n">elements</th><th class="n">listeners</th><th class="n">timeouts</th><th class="n">intervals</th><th class="n">observers</th><th class="n">messages</th></tr>${owners}</table>` +
          '<p class="dim">Run time counts scripts the plugin sent, and timers, listeners and observers they set up while recording. Listeners and timers a mod made before recording started show under no owner.</p>'
        : '<p class="dim">The web probe is not in the page. It is sent when recording starts, unless [Web] Probe is off.</p>') +
      '</section>';
  }

  function drawReport() {
    $('#main').innerHTML = '<div class="bar"><button data-act="refresh">Refresh</button><button data-act="copy">Copy report</button><button data-act="save">Save report</button><span class="dim">The same text Copy report puts on the clipboard.</span></div>' +
      `<pre>${esc(state.report || 'Building the report...')}</pre>`;
  }

  function drawFooter(snap) {
    const st = snap.status || {};
    const notes = (st.notes || []).map((n) => `<li>${esc(n)}</li>`).join('');

    $('#foot').innerHTML =
      `<span>Timed: <b>${F.count(st.hooks)}</b> hooked methods, <b>${F.count(st.patches)}</b> patches, <b>${F.count(st.tickers)}</b> update methods${st.deepMethods ? `, <b>${F.count(st.deepMethods)}</b> deep (${esc(st.deepMod)})` : ''}</span>` +
      (st.pending ? `<span class="warn">${F.count(st.pending)} still being added</span>` : '') +
      (st.failed ? `<span class="bad">${F.count(st.failed)} could not be timed</span>` : '') +
      (st.untimeable ? `<span title="Transpilers change a method's code in place; their cost lands in the hooked method">${F.count(st.untimeable)} transpilers</span>` : '') +
      (st.overflow ? `<span class="warn">${F.count(st.overflow)} too deep</span>` : '') +
      `<span>probe cost ${(snap.overheadUs || 0).toFixed(3)} us</span>` +
      `<span>${snap.engineTimings ? 'engine timings on' : 'engine timings off'}</span>` +
      (notes ? `<details><summary>${(st.notes || []).length} notes</summary><ul>${notes}</ul></details>` : '');
  }

  function drawOverlay() {
    const el = $('#overlay');
    const snap = state.snap;

    if (!snap || !snap.overlay || state.open) { el.hidden = true; return; }

    const f = snap.frame || {};
    const top = snap.topMod || {};
    const g = snap.game || {};
    const process = (snap.memory || {}).process || {};

    el.hidden = false;
    el.innerHTML = `<b class="${F.frameClass(snap.lastMs)}">${F.ms(snap.lastMs)} ms</b> <span>avg ${F.ms(f.mean)}, 99th ${F.ms(f.p99)}</span>` +
      `<span>${snap.recording ? '<i class="live"></i>' : 'stopped, '}hitches ${F.count(snap.hitchCount)}</span>` +
      (top.name ? `<span>top mod ${esc(top.name)} ${F.ms(top.avg)} ms</span>` : '') +
      (g.inGame ? `<span>logic ${(g.logicNow || 0).toFixed(0)}/s, ${g.paused ? 'paused' : 'speed ' + speedText(g.speed)}${g.cappedPct >= 1 ? `, <b class="warn">${F.percent(g.cappedPct)} lost</b>` : ''}</span>` : '') +
      (process.now > 0 ? `<span>memory ${F.mb(process.now)}</span>` : '');
  }

  function toast(text) {
    const el = $('#toast');

    el.textContent = String(text);
    el.hidden = false;
    clearTimeout(state.toastTimer);
    state.toastTimer = setTimeout(() => { el.hidden = true; }, 4000);
  }

  // ---------- the plugin's calls ----------

  function open(s) {
    state.open = true;
    state.key = (s && s.key) || '';
    state.version = (s && s.version) || '';

    if (!document.body.contains(host)) document.body.appendChild(host);

    $('#panel').hidden = false;
    host.style.pointerEvents = 'auto';
    showTab((s && s.tab) || state.tab, false);
  }

  function close() {
    state.open = false;
    $('#panel').hidden = true;
    host.style.pointerEvents = 'none';
    draw();
  }

  function update(snap) {
    state.snap = snap;

    if (!document.body.contains(host)) document.body.appendChild(host);

    // Methods redraw keeps the filter box, so typing is not interrupted by an update.
    if (state.open && state.tab === 'methods' && $('#methods')) { drawHeader(snap); drawMethods(true); drawFooter(snap); drawOverlay(); return; }

    draw();
  }

  function report(text) {
    state.report = String(text || '');

    if (state.tab === 'report') draw();
  }

  window.__slperfanalyzer = { open, close, update, toast, report };
  (document.body || document.documentElement).appendChild(host);
  send('ready');
})();
