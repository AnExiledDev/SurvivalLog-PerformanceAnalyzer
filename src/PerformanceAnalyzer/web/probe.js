// The Performance Analyzer's web probe. It watches the game's web UI from the inside: how long each mod's scripts
// run, the timers, listeners and observers they leave behind, the page's own frame rate, long tasks, memory and
// element counts. Scripts the plugin sends are wrapped in ctx("owner") ... ctx(null), so the probe knows whose code
// is running; callbacks created while a mod's code runs keep that mod as their owner. While not recording every
// wrapper calls straight through.
(function () {
  if (window.__slperfprobe) return;

  const PREFIX = 'slmod|perfanalyzer|';
  const SELF = 'perfanalyzer';
  const now = () => performance.now();
  const native = {
    setTimeout: window.setTimeout,
    setInterval: window.setInterval,
    clearInterval: window.clearInterval,
    raf: window.requestAnimationFrame,
    add: EventTarget.prototype.addEventListener,
    remove: EventTarget.prototype.removeEventListener,
  };

  let on = false;
  let current = null;
  let currentAt = 0;
  let owners = {};
  let recordedMs = 0;
  let onAt = 0;
  let domCache = { total: 0, counts: {} };
  let domAt = -1e9;
  let frames = 0;
  let lastFrameAt = 0;
  let worstGapMs = 0;
  let longTasks = 0;
  let longTaskMs = 0;
  let sendTimer = 0;

  const send = (m) => { try { if (window.vuplex) window.vuplex.postMessage(PREFIX + m); } catch (e) { /* the bridge is gone; the plugin sends the probe again */ } };

  function row(key) {
    return owners[key] || (owners[key] = { key, ms: 0, calls: 0, worstMs: 0, listeners: 0, timers: 0, intervals: 0, observers: 0, posts: 0 });
  }

  // The owner key of a window global: "__slcheatmenuLogic" gives "cheatmenu" and "__slmodmenu" gives "modmenu", as
  // the plugin reads it. A bare "__slmod" is the shared message prefix, not a mod.
  function ownerOf(name) {
    const m = /^__sl([a-z0-9]+)/.exec(name);

    return m && m[1] !== 'mod' ? m[1] : '';
  }

  function charge(key, ms) {
    const r = row(key);

    r.ms += ms;
    r.calls++;
    if (ms > r.worstMs) r.worstMs = ms;
  }

  // Runs fn as owner's code and times it; an exception still reaches the caller. Time is self time: while another
  // owner's code runs inside, the clock is on that owner, so nothing is counted twice.
  function runAs(owner, fn, self, args) {
    if (!on || owner === current) return fn.apply(self, args);

    const outer = current;
    const start = now();

    if (outer !== null) row(outer).ms += start - currentAt;

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

    if (owner === null || owner === undefined) {
      if (current !== null) charge(current, now() - currentAt);
      current = null;

      return;
    }

    if (current !== null) charge(current, now() - currentAt);

    current = String(owner);
    currentAt = now();

    const opened = current;
    const openedAt = currentAt;

    queueMicrotask(() => { if (current === opened && currentAt === openedAt) ctx(null); });
  }

  window.setTimeout = function (fn) {
    if (on && current !== null) row(current).timers++;

    arguments[0] = owned(fn);

    return native.setTimeout.apply(window, arguments);
  };

  const intervals = new Map();

  window.setInterval = function (fn) {
    const owner = on ? current : null;

    arguments[0] = owned(fn);

    const id = native.setInterval.apply(window, arguments);

    if (owner !== null) { row(owner).intervals++; intervals.set(id, owner); }

    return id;
  };

  window.clearInterval = function (id) {
    const owner = intervals.get(id);

    if (owner !== undefined) { intervals.delete(id); row(owner).intervals = Math.max(0, row(owner).intervals - 1); }

    return native.clearInterval.apply(window, arguments);
  };

  window.requestAnimationFrame = function (fn) { return native.raf.call(window, owned(fn)); };

  // A listener keeps one wrapper per owner, so removeEventListener can find what was added.
  const wrappers = new WeakMap();

  EventTarget.prototype.addEventListener = function (type, fn, options) {
    if (on && current !== null && typeof fn === 'function') {
      const owner = current;
      let byOwner = wrappers.get(fn);

      if (!byOwner) { byOwner = {}; wrappers.set(fn, byOwner); }

      const wrapped = byOwner[owner] || (byOwner[owner] = function () { return runAs(owner, fn, this, arguments); });

      row(owner).listeners++;

      return native.add.call(this, type, wrapped, options);
    }

    return native.add.apply(this, arguments);
  };

  EventTarget.prototype.removeEventListener = function (type, fn, options) {
    const byOwner = typeof fn === 'function' ? wrappers.get(fn) : null;

    if (byOwner) {
      for (const owner in byOwner) {
        native.remove.call(this, type, byOwner[owner], options);
        row(owner).listeners = Math.max(0, row(owner).listeners - 1);
      }
    }

    return native.remove.apply(this, arguments);
  };

  function wrapObserver(name) {
    const Native = window[name];

    if (typeof Native !== 'function') return;

    const Wrapped = function (callback) {
      if (on && current !== null) row(current).observers++;

      return new Native(owned(callback));
    };

    Wrapped.prototype = Native.prototype;
    window[name] = Wrapped;
  }

  wrapObserver('MutationObserver');
  wrapObserver('ResizeObserver');

  // Every function on a mod's window object runs as that mod. Found again every couple of seconds, since mods add
  // objects late and replace them on reload.
  const MAX_KEYS = 600;

  function wrapGlobals() {
    for (const name of Object.keys(window)) {
      if (name.indexOf('__sl') !== 0 || name.indexOf('__slperf') === 0) continue;

      const owner = ownerOf(name);
      const value = window[name];

      if (!owner || !value || typeof value !== 'object' || Array.isArray(value)) continue;

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

  try {
    new PerformanceObserver((list) => {
      if (!on) return;

      for (const entry of list.getEntries()) { longTasks++; longTaskMs += entry.duration; }
    }).observe({ entryTypes: ['longtask'] });
  } catch (e) { /* no long-task timing in this browser */ }

  function frame(at) {
    if (!on) return;

    if (lastFrameAt > 0) worstGapMs = Math.max(worstGapMs, at - lastFrameAt);

    lastFrameAt = at;
    frames++;
    native.raf.call(window, frame);
  }

  // Page elements per mod: a shadow host with the id "sl<key>-host" belongs to that mod, the rest to the game.
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
    domAt = now();
    domCache = { total, counts };

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

    const memory = performance.memory;

    return {
      seconds,
      fps: frames / seconds,
      worstGapMs,
      longTasks,
      longTaskMs,
      heapMB: memory ? memory.usedJSHeapSize / 1048576 : 0,
      domNodes: dom.total,
      owners: list,
    };
  }

  function tick() {
    if (!on) return;

    wrapBridge();
    wrapGlobals();
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
  }

  // isOn: time owners and send a summary every second. Tagging the scripts is the plugin's side.
  function record(isOn) {
    const was = on;

    on = !!isOn;

    if (on && !was) {
      onAt = now();
      lastFrameAt = 0;
      wrapBridge();
      wrapGlobals();
      native.raf.call(window, frame);
      sendTimer = native.setInterval.call(window, tick, 1000);
    } else if (!on && was) {
      native.clearInterval.call(window, sendTimer);
      recordedMs += now() - onAt;
      current = null;
    }
  }

  window.__slperfprobe = { ctx, record, reset, summary, ownerOf, isRecording: () => on };
})();
