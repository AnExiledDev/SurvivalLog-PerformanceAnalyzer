// Number formatting and small drawing helpers for the Performance Analyzer panel. Pure functions, no page access.
(function () {
  if (window.__slperfanalyzerFormat) return;

  const finite = (v) => (typeof v === 'number' && isFinite(v) ? v : 0);

  // Milliseconds with fewer decimals as the number grows: 0.004, 0.42, 3.1, 48.
  function ms(v) {
    const n = finite(v);
    const a = Math.abs(n);

    if (a === 0) return '0';
    if (a < 0.01) return n.toFixed(4);
    if (a < 1) return n.toFixed(3);
    if (a < 10) return n.toFixed(2);
    if (a < 100) return n.toFixed(1);

    return n.toFixed(0);
  }

  // Whole numbers with thousands separators: 12,345.
  function count(v) {
    const n = Math.round(finite(v));

    return String(n).replace(/\B(?=(\d{3})+(?!\d))/g, ',');
  }

  function percent(v) {
    const n = finite(v);

    return (n < 10 ? n.toFixed(1) : n.toFixed(0)) + '%';
  }

  function kb(v) {
    const n = finite(v);

    if (n >= 1024) return (n / 1024).toFixed(1) + ' MB';

    return (n < 10 ? n.toFixed(1) : n.toFixed(0)) + ' KB';
  }

  // Megabytes, '-' when the game build gives no reading.
  function mb(v) {
    const n = finite(v);

    if (n <= 0) return '-';
    if (n >= 1024) return (n / 1024).toFixed(2) + ' GB';

    return (n < 100 ? n.toFixed(1) : n.toFixed(0)) + ' MB';
  }

  // The top of a graph's scale: the largest value rounded up to 1, 2 or 5 times a power of ten, never below floor.
  function niceMax(series, floor) {
    let max = finite(floor) > 0 ? floor : 1;

    for (const list of series) for (const v of Array.isArray(list) ? list : []) max = Math.max(max, finite(v));

    const power = Math.pow(10, Math.floor(Math.log10(max)));

    for (const m of [1, 2, 5, 10]) if (max <= m * power + 1e-9) return m * power;

    return 10 * power;
  }

  function seconds(v) {
    const n = Math.floor(finite(v));
    const m = Math.floor(n / 60);

    return m > 0 ? m + 'm ' + String(n % 60).padStart(2, '0') + 's' : n + 's';
  }

  function esc(s) {
    return String(s == null ? '' : s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  }

  // How bad a frame time is: under 60 FPS is "warn", under 30 FPS is "bad".
  function frameClass(v) {
    const n = finite(v);

    return n >= 33.4 ? 'bad' : n >= 16.8 ? 'warn' : 'good';
  }

  // How bad a mod's average cost per frame is.
  function costClass(v) {
    const n = finite(v);

    return n >= 2 ? 'bad' : n >= 0.5 ? 'warn' : 'good';
  }

  // SVG polyline points for a series drawn into width x height, with max at the top. The newest value is on the right.
  function points(series, width, height, max) {
    const list = Array.isArray(series) ? series : [];
    const top = max > 0 ? max : 1;

    if (list.length === 0) return '';

    const step = list.length > 1 ? width / (list.length - 1) : 0;
    const out = [];

    for (let i = 0; i < list.length; i++) {
      const y = height - Math.min(1, Math.max(0, finite(list[i]) / top)) * height;

      out.push((i * step).toFixed(1) + ',' + y.toFixed(1));
    }

    return out.join(' ');
  }

  // A graph's top: the largest value rounded up to a step a reader can use, never below one 60 FPS frame.
  function graphMax(series) {
    let max = 16.7;

    for (const v of Array.isArray(series) ? series : []) max = Math.max(max, finite(v));

    for (const step of [16.7, 33.3, 50, 100, 200, 500, 1000, 2000, 5000]) if (max <= step) return step;

    return Math.ceil(max / 1000) * 1000;
  }

  // Sorts rows by a field; numbers high to low by default, text A to Z.
  function sortRows(rows, field, ascending) {
    const list = (rows || []).slice();

    list.sort((a, b) => {
      const x = a[field];
      const y = b[field];
      const order = typeof x === 'number' && typeof y === 'number' ? y - x : String(x).localeCompare(String(y));

      return ascending ? -order : order;
    });

    return list;
  }

  // Keeps rows whose text fields contain every word of the query, any case.
  function filterRows(rows, query, fields) {
    const words = String(query || '').toLowerCase().split(/\s+/).filter(Boolean);

    if (words.length === 0) return rows || [];

    return (rows || []).filter((row) => {
      const text = fields.map((f) => String(row[f] == null ? '' : row[f])).join(' ').toLowerCase();

      return words.every((w) => text.indexOf(w) >= 0);
    });
  }

  window.__slperfanalyzerFormat = { ms, count, percent, kb, mb, niceMax, seconds, esc, frameClass, costClass, points, graphMax, sortRows, filterRows };
})();
