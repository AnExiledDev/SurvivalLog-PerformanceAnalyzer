// The Performance Analyzer's web probe. It watches the game's web UI from the inside: how long each mod's scripts
// run, the timers, listeners and observers they leave behind, the page's own frame rate, long tasks, memory and
// element counts. Scripts the plugin sends are wrapped in ctx("owner") ... ctx(null), so the probe knows whose code
// is running; callbacks created while a mod's code runs keep that mod as their owner. The same wrappers go into
// every game page frame, since mods reach into those pages and leave listeners and observers there. While not
// recording every wrapper calls straight through.
(function () {
  if (window.__slperfprobe) return;

  const PREFIX = 'slmod|perfanalyzer|';
  const SELF = 'perfanalyzer';
  const TOOLKIT = 'toolkit';
  const TOOLKIT_MARKER = '__exiledtoolkit';
  const MAX_SPANS = 2000;
  const now = () => performance.now();
  const native = {
    setTimeout: window.setTimeout,
    setInterval: window.setInterval,
    clearInterval: window.clearInterval,
    raf: window.requestAnimationFrame,
  };

  let on = false;
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

  // The owner key of a window global: "__slcheatmenuLogic" gives "cheatmenu" and "__slmodmenu" gives "modmenu", as
  // the plugin reads it. A bare "__slmod" is the shared message prefix, not a mod. Exiled's Toolkit names its globals
  // "__exiledtoolkit...".
  function ownerOf(name) {
    if (name.indexOf(TOOLKIT_MARKER) === 0) return TOOLKIT;

    const m = /^__sl([a-z0-9]+)/.exec(name);

    return m && m[1] !== 'mod' ? m[1] : '';
  }

  function charge(key, ms) {
    const r = row(key);

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

  // Runs fn as owner's code and times it; an exception still reaches the caller. Time is self time: while another
  // owner's code runs inside, the clock is on that owner, so nothing is counted twice.
  function runAs(owner, fn, self, args) {
    if (!on || owner === current) return fn.apply(self, args);

    const outer = current;
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
    if (!on || current === null || typeof fn !== 'function') return fn;

    const owner = current;

    return function () { return runAs(owner, fn, this, arguments); };
  }

  // The plugin's script tag. ctx(owner) opens, ctx(null) closes; a script that throws never closes, so a microtask
  // closes it after the script's turn ends.
  function ctx(owner) {
    if (!on) { current = null; return; }

    const at = now();

    if (owner === null || owner === undefined) {
      if (current !== null) { charge(current, at - currentAt); span(spanAt, at); }
      current = null;

      return;
    }

    if (current !== null) charge(current, at - currentAt);
    else spanAt = at;

    current = String(owner);
    currentAt = at;

    const opened = current;
    const openedAt = currentAt;

    queueMicrotask(() => { if (current === opened && currentAt === openedAt) ctx(null); });
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
    };

    win.setTimeout = function (fn) {
      if (on && current !== null) row(current).timers++;

      arguments[0] = owned(fn);

      return own.setTimeout.apply(win, arguments);
    };

    const intervals = new Map();

    win.setInterval = function (fn) {
      const owner = on ? current : null;

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

      if (on && current !== null && typeof fn === 'function') {
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
        if (on && current !== null) row(current).observers++;

        return new Native(owned(callback));
      };

      Wrapped.prototype = Native.prototype;
      win[name] = Wrapped;
    }

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
    const names = [];
    const list = document.getElementsByTagName('iframe');

    for (let i = 0; i < list.length; i++) {
      const frame = list[i];

      if (!isShown(frame)) continue;

      const name = frameName(frame);

      if (/^CoreUI[01]$/.test(name)) continue;

      const s = frame.style;

      if (s.pointerEvents === 'none' || (s.opacity !== '' && Number(s.opacity) <= 0.01)) continue;
      if (names.indexOf(name) < 0) names.push(name);
    }

    names.sort();

    return names.length ? names.join('+').slice(0, 120) : 'hud';
  }

  function checkPage() {
    try { page = showingPage(); } catch (e) { page = 'hud'; }

    if (on && page !== sentPage) { sentPage = page; send('page|' + page); }
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

          if (!m || m[1] !== SELF) row(m ? m[1] : 'game').posts++;
        }

        return original.apply(this, arguments);
      };
    } catch (e) { /* a read-only bridge; messages go uncounted */ }
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

      if (m) { counts[m[1]] = (counts[m[1]] || 0) + n; mods += n; }
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

    for (const key of keys) {
      const r = owners[key] || { key, ms: 0, calls: 0, worstMs: 0, listeners: 0, timers: 0, intervals: 0, observers: 0, posts: 0 };

      list.push({ key, ms: r.ms, calls: r.calls, worstMs: r.worstMs, dom: dom.counts[key] || 0, listeners: r.listeners, timers: r.timers, intervals: r.intervals, observers: r.observers, posts: r.posts });
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
    };
  }

  function tick() {
    if (!on) return;

    wrapBridge();
    wrapGlobals();
    patchFrames(document);
    send('web|' + JSON.stringify(summary()));
  }

  function reset() {
    owners = {};
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
  }

  // isOn: time owners and send a summary every second, and the showing page whenever it changes. Tagging the scripts
  // is the plugin's side.
  function record(isOn) {
    const was = on;

    on = !!isOn;

    if (on && !was) {
      onAt = now();
      lastFrameAt = 0;
      sentPage = '';
      wrapBridge();
      wrapGlobals();
      patchFrames(document);
      checkPage();
      native.raf.call(window, frame);
      sendTimer = native.setInterval.call(window, tick, 1000);
      pageTimer = native.setInterval.call(window, checkPage, 250);
    } else if (!on && was) {
      native.clearInterval.call(window, sendTimer);
      native.clearInterval.call(window, pageTimer);
      recordedMs += now() - onAt;
      current = null;
    }
  }

  window.__slperfprobe = { ctx, record, reset, summary, ownerOf, isRecording: () => on, showingPage, noteLongTask };
})();
