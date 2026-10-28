// The Performance Analyzer's web probe. It watches the game's web UI from the inside: how long each mod's scripts
// run, the timers, listeners and observers they leave behind, the page's own frame rate, long tasks, memory and
// element counts, and how long the page takes to answer a click. Scripts the plugin sends are wrapped in
// ctx("owner") ... ctx(null), so the probe knows whose code is running; callbacks created while a mod's code runs keep
// that mod as their owner. The same wrappers go into every game page frame, since mods reach into those pages and
// leave listeners and observers there. Owners are tracked from the moment the probe arrives, which is the start of
// the game, so listeners and timers made long before recording still have one; time is only counted while recording.
(function () {
  if (window.__slperfprobe) return;

  const PREFIX = 'slmod|perfanalyzer|';
  const SELF = 'perfanalyzer';
  const TOOLKIT = 'toolkit';
  const MAX_SPANS = 2000;
  const MAINTAIN_MS = 2000;
  const now = () => performance.now();
  const native = {
    setTimeout: window.setTimeout,
    setInterval: window.setInterval,
    clearInterval: window.clearInterval,
    raf: window.requestAnimationFrame,
  };

  let on = false;
  let named = false;
  let aliases = {};
  let known = {};
  let current = null;
  let currentAt = 0;
  let spanAt = 0;
  let owners = {};
  let recordedMs = 0;
  let onAt = 0;
  let domCache = { total: 0, counts: {}, pages: [] };
  let domAt = -1e9;
  let frames = 0;
  let lastFrameAt = 0;
  let worstGapMs = 0;
  let longTasks = 0;
  let longTaskMs = 0;
  let longTaskUnownedMs = 0;
  let longTaskPages = {};
  let sendTimer = 0;
  let pageTimer = 0;
  let page = 'hud';
  let sentPage = '';

  // Start and end of each stretch of timed script, newest last, so a long task can say how much of it no script
  // explains.
  const spans = new Float64Array(MAX_SPANS * 2);
  let spanCount = 0;
  let spanNext = 0;

  const send = (m) => { try { if (window.vuplex) window.vuplex.postMessage(PREFIX + m); } catch (e) { /* the bridge is gone; the plugin sends the probe again */ } };

  function row(key) {
    return owners[key] || (owners[key] = { key, ms: 0, calls: 0, worstMs: 0, listeners: 0, timers: 0, intervals: 0, observers: 0, posts: 0 });
  }

  // One name per mod: the plugin sends the words some mods use for themselves ("pkgbank" for "packagebank") and the
  // keys of the loaded mods, so a mod's globals, messages and tagged scripts all land in one row.
  function key(k) {
    const s = String(k);

    return Object.prototype.hasOwnProperty.call(aliases, s) ? aliases[s] : s;
  }

  function names(table) {
    aliases = (table && table.aliases) || {};
    known = {};
    for (const k of (table && table.known) || []) known[key(k)] = true;
    named = true;
  }

  // The owner key of a window global: "__slcheatmenuLogic" gives "cheatmenu", "__sldefenseWords" gives
  // "defensemanager", and "__storagehubWords" gives "storagehub" when Storage Hub is loaded. A bare "__slmod" is the
  // shared message prefix, not a mod. Exiled's Toolkit names its globals "__exiledtoolkit...".
  function ownerOf(name) {
    if (name.indexOf('__exiledtoolkit') === 0) return TOOLKIT;

    let m = /^__sl([a-z0-9]+)/.exec(name);

    if (m) return m[1] === 'mod' ? '' : key(m[1]);

    m = /^__([a-z0-9]+)/.exec(name);
    if (!m) return '';

    const k = key(m[1]);

    return known[k] === true ? k : '';
  }

  function charge(owner, ms) {
    const r = row(owner);

    r.ms += ms;
    r.calls++;
    if (ms > r.worstMs) r.worstMs = ms;
  }

  function span(start, end) {
    spans[spanNext * 2] = start;
    spans[spanNext * 2 + 1] = end;
    spanNext = (spanNext + 1) % MAX_SPANS;
    if (spanCount < MAX_SPANS) spanCount++;
  }

  function ownedWithin(start, end) {
    let ms = 0;

    for (let i = 0; i < spanCount; i++) {
      const a = Math.max(start, spans[i * 2]);
      const b = Math.min(end, spans[i * 2 + 1]);

      if (b > a) ms += b - a;
    }

    return Math.min(ms, end - start);
  }

  // Runs fn as owner's code; an exception still reaches the caller. While recording it is timed, as self time: while
  // another owner's code runs inside, the clock is on that owner, so nothing is counted twice.
  function runAs(owner, fn, self, args) {
    if (owner === current) return fn.apply(self, args);

    const outer = current;

    if (!on) {
      current = owner;

      try {
        return fn.apply(self, args);
      } finally {
        current = outer;
      }
    }

    const start = now();

    if (outer !== null) row(outer).ms += start - currentAt;
    else spanAt = start;

    current = owner;
    currentAt = start;

    try {
      return fn.apply(self, args);
    } finally {
      const end = now();
      const r = row(owner);

      r.ms += end - currentAt;
      r.calls++;
      if (end - start > r.worstMs) r.worstMs = end - start;
      current = outer;
      currentAt = end;
      if (outer === null) span(spanAt, end);
    }
  }

  // Wraps a callback so it runs as the owner that was current when it was handed over.
  function owned(fn) {
    if (current === null || typeof fn !== 'function') return fn;

    const owner = current;

    return function () { return runAs(owner, fn, this, arguments); };
  }

  // The plugin's script tag. ctx(owner) opens, ctx(null) closes; a script that throws never closes, so a microtask
  // closes it after the script's turn ends.
  function ctx(owner) {
    const at = now();
    const next = owner === null || owner === undefined ? null : key(owner);

    if (on && current !== null) {
      charge(current, at - currentAt);
      if (next === null) span(spanAt, at);
    } else if (on && next !== null) {
      spanAt = at;
    }

    current = next;
    currentAt = at;

    if (next !== null) queueMicrotask(() => { if (current === next && currentAt === at) ctx(null); });
  }

  // A listener keeps one wrapper per owner, so removeEventListener can find what was added. Shared by every window.
  const wrappers = new WeakMap();

  // Puts the wrappers into one window: the top page or a game page frame. Each window has its own timers, its own
  // EventTarget and its own observers, so each needs its own copy; the owner and the time they charge are shared.
  function patchWindow(win) {
    try {
      if (!win || win.__slperfPatched) return;

      win.__slperfPatched = { listeners: 0 };
    } catch (e) {
      return;
    }

    const stats = win.__slperfPatched;
    const own = {
      setTimeout: win.setTimeout,
      setInterval: win.setInterval,
      clearInterval: win.clearInterval,
      raf: win.requestAnimationFrame,
      add: win.EventTarget.prototype.addEventListener,
      remove: win.EventTarget.prototype.removeEventListener,
      MutationObserver: win.MutationObserver,
    };

    win.setTimeout = function (fn) {
      if (on && current !== null) row(current).timers++;

      arguments[0] = owned(fn);

      return own.setTimeout.apply(win, arguments);
    };

    const intervals = new Map();

    win.setInterval = function (fn) {
      const owner = current;

      arguments[0] = owned(fn);

      const id = own.setInterval.apply(win, arguments);

      if (owner !== null) { row(owner).intervals++; intervals.set(id, owner); }

      return id;
    };

    win.clearInterval = function (id) {
      const owner = intervals.get(id);

      if (owner !== undefined) { intervals.delete(id); row(owner).intervals = Math.max(0, row(owner).intervals - 1); }

      return own.clearInterval.apply(win, arguments);
    };

    if (typeof own.raf === 'function') win.requestAnimationFrame = function (fn) { return own.raf.call(win, owned(fn)); };

    win.EventTarget.prototype.addEventListener = function (type, fn, options) {
      stats.listeners++;

      if (current !== null && typeof fn === 'function') {
        const owner = current;
        let byOwner = wrappers.get(fn);

        if (!byOwner) { byOwner = {}; wrappers.set(fn, byOwner); }

        const wrapped = byOwner[owner] || (byOwner[owner] = function () { return runAs(owner, fn, this, arguments); });

        row(owner).listeners++;

        return own.add.call(this, type, wrapped, options);
      }

      return own.add.apply(this, arguments);
    };

    win.EventTarget.prototype.removeEventListener = function (type, fn, options) {
      const byOwner = typeof fn === 'function' ? wrappers.get(fn) : null;

      if (stats.listeners > 0) stats.listeners--;

      if (byOwner) {
        for (const owner in byOwner) {
          own.remove.call(this, type, byOwner[owner], options);
          row(owner).listeners = Math.max(0, row(owner).listeners - 1);
        }
      }

      return own.remove.apply(this, arguments);
    };

    for (const name of ['MutationObserver', 'ResizeObserver']) {
      const Native = win[name];

      if (typeof Native !== 'function') continue;

      const Wrapped = function (callback) {
        if (current !== null) row(current).observers++;

        return new Native(owned(callback));
      };

      Wrapped.prototype = Native.prototype;
      win[name] = Wrapped;
    }

    watchInput(win, own);

    // A frame that loads a new page gets a new window; catch it as it loads, nested frames too.
    try { own.add.call(win.document, 'load', (e) => { if (e.target && e.target.tagName === 'IFRAME') patchFrames(win.document); }, true); } catch (e) { /* no document yet */ }
  }

  // Every same-origin page frame inside doc, at any depth. A frame from another origin throws and is skipped.
  function framesIn(doc, out) {
    let list;

    try { list = doc.getElementsByTagName('iframe'); } catch (e) { return out; }

    for (let i = 0; i < list.length; i++) {
      const frame = list[i];

      try {
        const win = frame.contentWindow;
        const inner = frame.contentDocument;

        if (!win || !inner) continue;

        out.push({ frame, win, doc: inner });
        framesIn(inner, out);
      } catch (e) { /* another origin */ }
    }

    return out;
  }

  function patchFrames(doc) {
    for (const f of framesIn(doc, [])) patchWindow(f.win);
  }

  // "CoreUI1" for ".../CoreUI1.html?x", cut to the characters the plugin takes as a page name.
  function frameName(frame) {
    const src = String(frame.getAttribute('src') || frame.src || frame.id || 'frame');
    const base = src.split(/[?#]/)[0].split('/').pop().replace(/\.html?$/i, '');

    return base.replace(/[^A-Za-z0-9_.-]/g, '').slice(0, 40) || 'frame';
  }

  // The game keeps a page invisible until it is ready and then sets its visibility inline. The HUD is CoreUI1 (the
  // prologue's is CoreUI0); a page above it that takes clicks is the one the player is looking at.
  function isShown(frame) {
    const s = frame.style;

    return s.display !== 'none' && s.visibility === 'visible';
  }

  function showingPage() {
    const list = [];
    const frames = document.getElementsByTagName('iframe');

    for (let i = 0; i < frames.length; i++) {
      const frame = frames[i];

      if (!isShown(frame)) continue;

      const name = frameName(frame);

      if (/^CoreUI[01]$/.test(name)) continue;

      const s = frame.style;

      if (s.pointerEvents === 'none' || (s.opacity !== '' && Number(s.opacity) <= 0.01)) continue;
      if (list.indexOf(name) < 0) list.push(name);
    }

    list.sort();

    return list.length ? list.join('+').slice(0, 120) : 'hud';
  }

  function checkPage() {
    try { page = showingPage(); } catch (e) { page = 'hud'; }

    if (on && page !== sentPage) { sentPage = page; send('page|' + page); }
  }

  // ---- Clicks. For each click or key press while recording, in the window it happened in: how long it waited
  // before its handlers ran, when the handlers were done, when the next frame came, when the page first changed (the
  // game's answer comes back as a change), and when every animation and transition the click started had finished.
  // A fade that takes 400 ms, or one that runs at a few frames a second, is lag the player feels even when no script
  // is slow.
  const MAX_CLICKS = 300;
  const CLICK_CAP_MS = 3000;
  const SCAN_AT = [100, 300, 700];
  let clicks = [];
  let fades = {};
  let openClicks = [];

  function describe(el) {
    if (!el || !el.tagName) return '';

    let d = String(el.tagName).toLowerCase();

    if (el.id) d += '#' + el.id;

    const cls = typeof el.className === 'string' ? el.className.trim().split(/\s+/)[0] : '';

    if (cls) d += '.' + cls;

    const text = String(el.textContent || '').replace(/\s+/g, ' ').trim().slice(0, 24);

    return (text ? d + ' "' + text + '"' : d).slice(0, 80);
  }

  function windowName(win) {
    if (win === window) return 'main';

    try { return win.frameElement ? frameName(win.frameElement) : 'frame'; } catch (e) { return 'frame'; }
  }

  function animationsOf(doc) {
    try { return typeof doc.getAnimations === 'function' ? doc.getAnimations() : []; } catch (e) { return []; }
  }

  // "transition opacity on div.panel" with its length; a looping animation never finishes and is left out.
  function animationInfo(a) {
    let ms = 0;

    try {
      const t = a.effect.getComputedTiming();

      ms = t.endTime;
    } catch (e) { /* no timing */ }

    if (ms === Infinity) return null;

    const what = a.transitionProperty ? 'transition ' + a.transitionProperty : a.animationName ? 'animation ' + a.animationName : 'animation';
    let where = '';

    try { where = a.effect && a.effect.target ? describe(a.effect.target).split(' "')[0] : ''; } catch (e) { /* no target */ }

    return { label: (where ? what + ' on ' + where : what).slice(0, 100), ms: typeof ms === 'number' && isFinite(ms) ? ms : 0 };
  }

  // A click in one page can fade another (the game shows a page by fading its frame in the top page), so the top
  // page and every page frame are watched.
  function allAnimations() {
    let list = animationsOf(document);

    for (const f of framesIn(document, [])) list = list.concat(animationsOf(f.doc));

    return list;
  }

  function watchInput(win, own) {
    const start = (e) => { if (on && !e.repeat && !/^(Shift|Control|Alt|Meta)$/.test(e.key || '')) startClick(win, own, e); };

    try {
      own.add.call(win, 'click', start, true);
      own.add.call(win, 'keydown', start, true);
    } catch (e) { return; }

    // The browser's own event timing, where it has it: input delay, handler time and time to the next paint, exact.
    try {
      new win.PerformanceObserver((list) => {
        for (const entry of list.getEntries()) {
          for (const c of openClicks) {
            if (c.win === win && c.type === entry.name && Math.abs(c.at - entry.startTime) < 2) {
              c.result.delayMs = entry.processingStart - entry.startTime;
              c.result.handlersMs = entry.processingEnd - entry.startTime;
              c.result.paintMs = entry.duration;
              c.exact = true;
            }
          }
        }
      }).observe({ type: 'event', durationThreshold: 16, buffered: false });
    } catch (e) { /* no event timing; the rough numbers below stand */ }
  }

  function startClick(win, own, e) {
    const perf = win.performance;
    let at = typeof e.timeStamp === 'number' ? e.timeStamp : perf.now();
    const t = perf.now();

    if (!(at > 0) || at > t + 1 || t - at > 10000) at = t;

    const doc = win.document;
    const path = typeof e.composedPath === 'function' ? e.composedPath() : [];
    const target = path.length && path[0].tagName ? path[0] : e.target;
    const result = {
      page: windowName(win),
      showing: page,
      what: e.type === 'keydown' ? 'key ' + String(e.key || '').slice(0, 12) : 'click',
      target: describe(target),
      delayMs: t - at,
      handlersMs: -1,
      paintMs: -1,
      changeMs: -1,
      settleMs: -1,
      changes: 0,
      animations: 0,
      fade: '',
      fadeMs: 0,
      frames: 0,
      worstGapMs: 0,
    };
    const c = { win, type: e.type, at, result, exact: false, before: new Set(allAnimations()), seen: new Set(), pending: 0, lastEndAt: 0, scans: 0, done: false, observer: null };
    const since = () => perf.now() - at;

    openClicks.push(c);

    try {
      c.observer = new own.MutationObserver((records) => {
        if (result.changeMs < 0) result.changeMs = since();

        result.changes += records.length;
      });

      const options = { subtree: true, childList: true, attributes: true, characterData: true };

      c.observer.observe(doc, options);
      if (doc !== document) c.observer.observe(document, options);
    } catch (err) { /* no observer; no change time */ }

    own.setTimeout.call(win, () => {
      if (!c.exact) result.handlersMs = since();
      scan(c);
    }, 0);

    let lastAt = 0;
    const frame = (frameAt) => {
      if (c.done) return;
      if (result.frames === 0 && !c.exact) result.paintMs = since();
      if (lastAt > 0) result.worstGapMs = Math.max(result.worstGapMs, frameAt - lastAt);

      lastAt = frameAt;
      result.frames++;
      if (since() < CLICK_CAP_MS) own.raf.call(win, frame);
    };

    if (typeof own.raf === 'function') own.raf.call(win, frame);

    for (const ms of SCAN_AT) own.setTimeout.call(win, () => { c.scans++; scan(c); settle(c); }, ms);

    own.setTimeout.call(win, () => finish(c), CLICK_CAP_MS);
  }

  // Animations and transitions that were not running at the click are the click's; each one is followed to its end.
  function scan(c) {
    if (c.done) return;

    for (const a of allAnimations()) {
      if (c.before.has(a) || c.seen.has(a)) continue;

      c.seen.add(a);

      const info = animationInfo(a);

      if (!info) continue;

      const f = fades[info.label] || (fades[info.label] = { label: info.label, page: c.result.page, count: 0, ms: 0, worstGapMs: 0 });

      f.count++;
      f.ms += info.ms;
      c.result.animations++;
      if (info.ms > c.result.fadeMs) { c.result.fadeMs = info.ms; c.result.fade = info.label; }
      c.fadesSeen = (c.fadesSeen || []).concat([f]);
      c.pending++;

      const ended = () => { c.pending--; c.lastEndAt = c.win.performance.now(); settle(c); };

      try { a.finished.then(ended, ended); } catch (e) { c.pending--; }
    }
  }

  function settle(c) {
    if (!c.done && c.scans >= SCAN_AT.length && c.pending <= 0) finish(c);
  }

  function finish(c) {
    if (c.done) return;

    c.done = true;
    if (c.observer) { try { c.observer.disconnect(); } catch (e) { /* gone */ } }

    const r = c.result;

    if (r.animations > 0) r.settleMs = c.pending > 0 ? CLICK_CAP_MS : Math.max(0, c.lastEndAt - c.at);
    for (const f of c.fadesSeen || []) f.worstGapMs = Math.max(f.worstGapMs, r.worstGapMs);

    openClicks = openClicks.filter((o) => o !== c);
    clicks.push(r);
    if (clicks.length > MAX_CLICKS) clicks.shift();
  }

  // The slowest of the three answers a player can see: the next frame, the first change, the end of the fades.
  function slowest(r) {
    return Math.max(r.paintMs, r.changeMs, r.settleMs);
  }

  function spread(values) {
    const v = values.filter((x) => x >= 0).sort((a, b) => a - b);

    if (!v.length) return { n: 0, p50: -1, p95: -1, max: -1 };

    const at = (p) => v[Math.min(v.length - 1, Math.floor(p * v.length))];

    return { n: v.length, p50: at(0.5), p95: at(0.95), max: v[v.length - 1] };
  }

  function interactions() {
    const byPage = {};

    for (const r of clicks) (byPage[r.page] || (byPage[r.page] = [])).push(r);

    const pages = Object.keys(byPage).map((p) => {
      const list = byPage[p];

      return { page: p, count: list.length, paint: spread(list.map((r) => r.paintMs)), change: spread(list.map((r) => r.changeMs)), settle: spread(list.map((r) => r.settleMs)), worst: spread(list.map(slowest)) };
    }).sort((a, b) => b.worst.max - a.worst.max);
    const slow = clicks.slice().sort((a, b) => slowest(b) - slowest(a)).slice(0, 15);
    const fadeList = Object.keys(fades).map((k) => fades[k]).sort((a, b) => b.ms - a.ms).slice(0, 12);

    return { count: clicks.length, pages, slowest: slow, fades: fadeList };
  }

  patchWindow(window);
  patchFrames(document);

  // Every function on a mod's window object runs as that mod. Found again every couple of seconds, since mods add
  // objects late and replace them on reload.
  const MAX_KEYS = 600;

  function wrapGlobals() {
    for (const name of Object.keys(window)) {
      if (name.indexOf('__slperf') === 0) continue;

      const owner = ownerOf(name);
      const value = owner ? window[name] : null;

      if (!value || typeof value !== 'object' || Array.isArray(value)) continue;

      try {
        const keys = Object.keys(value);

        for (let i = 0; i < keys.length && i < MAX_KEYS; i++) {
          const fn = value[keys[i]];

          if (typeof fn !== 'function' || fn.__slperfOwner) continue;

          const wrapped = function () { return runAs(owner, fn, this, arguments); };

          wrapped.__slperfOwner = owner;
          value[keys[i]] = wrapped;
        }

      } catch (e) { /* frozen or odd object; left alone */ }
    }
  }

  // Messages back to the plugin, counted by the mod key after "slmod|".
  function wrapBridge() {
    const bridge = window.vuplex;

    if (!bridge || typeof bridge.postMessage !== 'function' || bridge.__slperfOriginal) return;

    const original = bridge.postMessage;

    try {
      bridge.__slperfOriginal = original;
      bridge.postMessage = function (message) {
        if (on) {
          const text = typeof message === 'string' ? message : '';
          const m = /^slmod\|([a-z0-9]+)\|/.exec(text);

          if (!m || m[1] !== SELF) row(m ? key(m[1]) : 'game').posts++;
        }

        return original.apply(this, arguments);
      };
    } catch (e) { /* a read-only bridge; messages go uncounted */ }
  }

  // Whether or not it records: mods' new globals and new page frames get their wrappers.
  function maintain() {
    wrapBridge();
    wrapGlobals();
    patchFrames(document);
  }

  // A long task is any stretch of 50 ms or more on the page's thread. The part no timed script covers is the
  // browser's own work or the game's scripts; each one is charged to the page that was showing.
  function noteLongTask(start, duration) {
    const unowned = Math.max(0, duration - ownedWithin(start, start + duration));
    const p = longTaskPages[page] || (longTaskPages[page] = { page, count: 0, ms: 0, unownedMs: 0 });

    longTasks++;
    longTaskMs += duration;
    longTaskUnownedMs += unowned;
    p.count++;
    p.ms += duration;
    p.unownedMs += unowned;
  }

  try {
    new PerformanceObserver((list) => {
      if (!on) return;

      for (const entry of list.getEntries()) noteLongTask(entry.startTime, entry.duration);
    }).observe({ entryTypes: ['longtask'] });
  } catch (e) { /* no long-task timing in this browser */ }

  function frame(at) {
    if (!on) return;

    if (lastFrameAt > 0) worstGapMs = Math.max(worstGapMs, at - lastFrameAt);

    lastFrameAt = at;
    frames++;
    native.raf.call(window, frame);
  }

  function countElements(doc) {
    let n = 0;

    for (const el of doc.querySelectorAll('*')) {
      n++;
      if (el.shadowRoot) n += el.shadowRoot.querySelectorAll('*').length;
    }

    return n;
  }

  // Page elements per mod: a shadow host with the id "sl<key>-host" belongs to that mod, the rest to the game. Each
  // page frame also gets its own element count and the listeners added in it since the probe arrived.
  function elements() {
    if (now() - domAt < 5000) return domCache;

    const counts = {};
    let total = 0;
    let mods = 0;

    for (const el of document.querySelectorAll('*')) {
      total++;

      if (!el.shadowRoot) continue;

      const m = /^sl([a-z0-9]+)-host$/.exec(el.id || '');
      const n = el.shadowRoot.querySelectorAll('*').length;

      total += n;

      if (m) { const k = key(m[1]); counts[k] = (counts[k] || 0) + n; mods += n; }
    }

    counts.game = total - mods;

    const pages = [];

    for (const f of framesIn(document, [])) {
      try {
        patchWindow(f.win);
        pages.push({ name: frameName(f.frame), elements: countElements(f.doc), listeners: f.win.__slperfPatched ? f.win.__slperfPatched.listeners : 0 });
      } catch (e) { /* the frame went away */ }
    }

    pages.sort((a, b) => b.elements - a.elements);
    domAt = now();
    domCache = { total, counts, pages };

    return domCache;
  }

  function summary() {
    const seconds = Math.max(0.001, (recordedMs + (on ? now() - onAt : 0)) / 1000);
    const dom = elements();
    const list = [];
    const keys = new Set(Object.keys(owners).concat(Object.keys(dom.counts)));

    for (const k of keys) {
      const r = owners[k] || { key: k, ms: 0, calls: 0, worstMs: 0, listeners: 0, timers: 0, intervals: 0, observers: 0, posts: 0 };

      list.push({ key: k, ms: r.ms, calls: r.calls, worstMs: r.worstMs, dom: dom.counts[k] || 0, listeners: r.listeners, timers: r.timers, intervals: r.intervals, observers: r.observers, posts: r.posts });
    }

    list.sort((a, b) => b.ms - a.ms);

    const taskPages = Object.keys(longTaskPages).map((k) => longTaskPages[k]).sort((a, b) => b.ms - a.ms);
    const memory = performance.memory;

    return {
      seconds,
      fps: frames / seconds,
      worstGapMs,
      longTasks,
      longTaskMs,
      longTaskUnownedMs,
      longTaskPages: taskPages,
      heapMB: memory ? memory.usedJSHeapSize / 1048576 : 0,
      domNodes: dom.total,
      page,
      pages: dom.pages,
      owners: list,
      interactions: interactions(),
    };
  }

  function tick() {
    if (!on) return;

    send('web|' + JSON.stringify(summary()));
  }

  // Clears what was counted. The listeners, intervals and observers each mod has alive are not counts of this
  // recording, so they stay.
  function reset() {
    for (const k of Object.keys(owners)) {
      const r = owners[k];

      r.ms = 0;
      r.calls = 0;
      r.worstMs = 0;
      r.timers = 0;
      r.posts = 0;
    }

    recordedMs = 0;
    onAt = now();
    frames = 0;
    lastFrameAt = 0;
    worstGapMs = 0;
    longTasks = 0;
    longTaskMs = 0;
    longTaskUnownedMs = 0;
    longTaskPages = {};
    spanCount = 0;
    spanNext = 0;
    domAt = -1e9;
    clicks = [];
    fades = {};
  }

  // isOn: time owners and send a summary every second, and the showing page whenever it changes. Tagging the scripts
  // is the plugin's side.
  function record(isOn) {
    const was = on;

    on = !!isOn;

    if (on && !was) {
      onAt = now();
      currentAt = onAt;
      spanAt = onAt;
      lastFrameAt = 0;
      sentPage = '';
      maintain();
      checkPage();
      native.raf.call(window, frame);
      sendTimer = native.setInterval.call(window, tick, 1000);
      pageTimer = native.setInterval.call(window, checkPage, 250);
    } else if (!on && was) {
      native.clearInterval.call(window, sendTimer);
      native.clearInterval.call(window, pageTimer);
      recordedMs += now() - onAt;
    }
  }

  // What the plugin checks for every couple of seconds: "n" once it has the names, then "r" while recording.
  function state() {
    return (named ? 'n' : '') + (on ? 'r' : '');
  }

  maintain();
  native.setInterval.call(window, maintain, MAINTAIN_MS);

  window.__slperfprobe = { ctx, record, reset, summary, ownerOf, names, state, isRecording: () => on, showingPage, noteLongTask };
})();
