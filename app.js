const $ = id => document.getElementById(id);
const worker = new Worker('./data-worker.js', { type: 'module' });
const format = n => n.toLocaleString('en-US');
const decimal = n => n === null ? '—' : n.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
const state = { size: 65536, search: '', type: 'all', branch: null, sort: 'source', version: 0, request: 0, summary: null, selected: null, firstRows: [], busy: false };
const height = 34, overscan = 8;
// Keep physical layout bounded: browser pixel limits can break very large trees.
const maxScrollHeight = 1000000;
let timer, frame, toastTimer;
function limit() { return Math.ceil($('viewport').clientHeight / height) + overscan * 2; }
function runQuery() {
  state.version++; state.busy = true; state.selected = null;
  $('query-status').textContent = 'Updating view…';
  $('rows').replaceChildren(); $('spacer').style.height = '0px'; $('empty').hidden = true; $('viewport').scrollTop = 0;
  renderSelection();
  const { size, search, type, branch, sort } = state;
  worker.postMessage({ kind: 'query', version: state.version, options: { size, search, type, branch, sort }, limit: limit() });
}
worker.onmessage = ({ data: message }) => {
  if (message.version !== state.version) return;
  if (message.kind === 'result') {
    state.summary = message.summary; state.busy = false; state.firstRows = message.rows.slice(0, 7);
    $('spacer').style.height = `${Math.min(maxScrollHeight, message.summary.count * height)}px`;
    $('empty').hidden = message.summary.count !== 0;
    renderSummary(); renderTree(); renderCompact(); renderRows(message.rows, 0);
    $('query-status').textContent = `Query ${message.summary.elapsed.toFixed(1)} ms · sample data`;
  } else if (message.kind === 'page' && message.request === state.request) renderRows(message.rows, message.offset);
};
worker.onerror = () => { $('query-status').textContent = 'Could not load sample. Run npm start and open localhost:5173.'; };
function renderRows(rows, offset) {
  $('rows').style.transform = `translateY(${$('viewport').scrollTop + offset * height - logicalScrollTop()}px)`;
  const fragment = document.createDocumentFragment();
  rows.forEach(row => {
    const button = document.createElement('button'); button.className = 'data-row' + (state.selected?.id === row.id ? ' selected' : '');
    button.dataset.type = row.type; button.dataset.id = row.id; button.setAttribute('aria-label', `${row.path} index ${row.index}: ${row.value}, ${row.type}`); button.setAttribute('aria-pressed', state.selected?.id === row.id ? 'true' : 'false');
    for (const [className, value] of [['row-path', row.path], ['row-index', row.index], ['row-value', row.value], [`badge ${row.type.toLowerCase()}`, row.type]]) {
      const cell = document.createElement('span'); cell.className = className; cell.textContent = value; button.append(cell);
    }
    button.addEventListener('click', () => {
      state.selected = row;
      document.querySelectorAll('.data-row').forEach(el => { const selected = Number(el.dataset.id) === row.id; el.classList.toggle('selected', selected); el.setAttribute('aria-pressed', String(selected)); });
      renderSelection();
    });
    fragment.append(button);
  });
  $('rows').replaceChildren(fragment);
  const start = Math.min(state.summary.count, Math.floor(logicalScrollTop() / height) + 1);
  const end = Math.min(state.summary.count, start + Math.ceil($('viewport').clientHeight / height) - 1);
  $('range').textContent = `${format(start)}–${format(end)} of ${format(state.summary.count)}`;
}
function logicalScrollTop() {
  if (!state.summary) return 0;
  const viewport = $('viewport');
  const logicalMax = Math.max(0, state.summary.count * height - viewport.clientHeight);
  const physicalMax = Math.max(0, Math.min(maxScrollHeight, state.summary.count * height) - viewport.clientHeight);
  return physicalMax ? viewport.scrollTop / physicalMax * logicalMax : 0;
}
function requestPage() {
  if (state.busy || !state.summary) return;
  const offset = Math.max(0, Math.floor(logicalScrollTop() / height) - overscan);
  worker.postMessage({ kind: 'page', version: state.version, request: ++state.request, offset, limit: limit() });
}
$('viewport').addEventListener('scroll', () => { cancelAnimationFrame(frame); frame = requestAnimationFrame(requestPage); });
new ResizeObserver(requestPage).observe($('viewport'));
function renderSummary() {
  const s = state.summary;
  $('result-count').textContent = format(s.count); $('all-count').textContent = format(s.total);
  $('result-label').textContent = state.branch === null ? 'items in all branches' : `items in {0;${Math.floor(state.branch / 16)};${state.branch % 16}}`;
  $('branch-count').textContent = format(Math.ceil(s.total / 256)); $('root-count').textContent = `${format(Math.ceil(s.total / 256))} branches`;
  $('stat-items').textContent = format(s.count); $('stat-branches').textContent = format(s.branches.length);
  $('stats-scope').textContent = `Current view · ${state.branch === null ? 'all branches' : 'selected branch'}`;
  $('type-total').textContent = `${Object.values(s.counts).filter(Boolean).length} types`;
  const bars = document.createDocumentFragment(), lines = document.createDocumentFragment();
  Object.entries(s.counts).forEach(([type, count]) => {
    if (!count) return;
    const bar = document.createElement('span'); bar.className = type.toLowerCase(); bar.style.width = `${count / s.count * 100}%`; bar.title = `${type}: ${format(count)}`; bars.append(bar);
    const line = document.createElement('div'); line.className = 'type-line';
    const dot = document.createElement('i'); dot.className = `type-dot ${type.toLowerCase()}`;
    const name = document.createElement('span'); name.textContent = type;
    const number = document.createElement('strong'); number.textContent = format(count);
    line.append(dot, name, number); lines.append(line);
  });
  $('type-chart').replaceChildren(bars); $('type-breakdown').replaceChildren(lines);
  $('numeric-count').textContent = `${format(s.numberCount)} numbers`;
  const histogram = document.createDocumentFragment(); const maxBin = Math.max(...s.histogram, 1);
  s.histogram.forEach((n, index) => { const bar = document.createElement('span'); bar.style.height = `${n / maxBin * 100}%`; bar.title = `Bin ${index + 1}: ${format(n)} numbers`; histogram.append(bar); });
  $('histogram').replaceChildren(histogram); $('hist-min').textContent = decimal(s.min); $('hist-max').textContent = decimal(s.max);
  $('stat-min').textContent = decimal(s.min); $('stat-max').textContent = decimal(s.max); $('stat-mean').textContent = decimal(s.mean);
  document.querySelectorAll('[data-type]').forEach(el => { if (el.tagName === 'BUTTON' && !el.classList.contains('data-row')) { el.classList.toggle('active', el.dataset.type === state.type); el.setAttribute('aria-pressed', String(el.dataset.type === state.type)); } });
  $('all-branches').classList.toggle('active', state.branch === null);
}
function renderTree() {
  const needle = $('path-search').value.toLowerCase().replace(/\s/g, '');
  const matched = new Map(state.summary.branches.map(b => [b.id, b.count]));
  const fragment = document.createDocumentFragment();
  for (let id = 0; id < Math.ceil(state.size / 256); id++) {
    const path = `{0;${Math.floor(id / 16)};${id % 16}}`;
    if (needle && !path.includes(needle)) continue;
    const button = document.createElement('button'); button.className = 'branch-button' + (state.branch === id ? ' active' : '');
    button.setAttribute('aria-label', `Branch ${path}`); button.setAttribute('aria-pressed', String(state.branch === id));
    const label = document.createElement('span'), count = document.createElement('span'); label.textContent = path; count.textContent = format(matched.get(id) || 0); button.append(label, count);
    button.onclick = () => { state.branch = id; runQuery(); }; fragment.append(button);
  }
  if (!fragment.childNodes.length) { const none = document.createElement('div'); none.className = 'branch-none'; none.textContent = 'No matching paths'; fragment.append(none); }
  $('tree-list').replaceChildren(fragment);
}
function renderSelection() {
  const card = $('selection-card'); card.replaceChildren();
  if (!state.selected) {
    const icon = document.createElement('span'); icon.className = 'selection-icon'; icon.textContent = '↖';
    const title = document.createElement('strong'); title.textContent = 'A closer look';
    const text = document.createElement('p'); text.textContent = 'Select a row to see its full value and original address.'; card.append(icon, title, text); return;
  }
  const row = state.selected;
  const title = document.createElement('strong'); title.textContent = `${row.type} · original address`;
  const address = document.createElement('div'); address.className = 'selection-address'; address.textContent = `${row.path} [${row.index}]`;
  const value = document.createElement('code'); value.textContent = row.value;
  const copy = document.createElement('button'); copy.textContent = 'Copy value'; copy.onclick = async () => {
    try { await navigator.clipboard.writeText(row.value); toast('Value copied'); } catch { toast('Clipboard unavailable in this browser'); }
  }; card.append(title, address, value, copy);
}
function renderCompact() {
  const s = state.summary; $('compact-count').textContent = format(s.count); $('compact-branches').textContent = `${format(s.branches.length)} branches`;
  const fragment = document.createDocumentFragment();
  state.firstRows.forEach(row => { const line = document.createElement('div'); line.className = 'compact-row'; const address = document.createElement('span'), value = document.createElement('span'); address.textContent = `${row.path} [${row.index}]`; value.textContent = row.value; line.append(address, value); fragment.append(line); });
  if (!state.firstRows.length) { const line = document.createElement('div'); line.className = 'compact-row'; line.textContent = 'No matching items'; fragment.append(line); }
  $('compact-rows').replaceChildren(fragment); $('compact-status').textContent = `${s.elapsed.toFixed(1)} ms · view only`;
}
function search(value) { state.search = value; $('search').value = value; $('compact-search').value = value; clearTimeout(timer); timer = setTimeout(runQuery, 100); }
$('search').oninput = e => search(e.target.value); $('compact-search').oninput = e => search(e.target.value);
$('clear-search').onclick = () => { clearTimeout(timer); state.search = ''; $('search').value = ''; $('compact-search').value = ''; runQuery(); };
document.querySelectorAll('.type-filters button').forEach(button => { button.onclick = () => { state.type = button.dataset.type; runQuery(); }; });
$('sort').onchange = e => { state.sort = e.target.value; runQuery(); };
$('dataset').onchange = e => { state.size = Number(e.target.value); state.branch = null; runQuery(); };
$('all-branches').onclick = () => { state.branch = null; runQuery(); };
$('path-search').oninput = () => { if (state.summary) renderTree(); };
$('path-search').onkeydown = e => { if (e.key === 'Enter') { const buttons = $('tree-list').querySelectorAll('button'); if (buttons.length === 1) buttons[0].click(); else if (buttons.length) { const exact = Array.from(buttons).find(b => b.firstElementChild.textContent === $('path-search').value.trim()); if (exact) exact.click(); else toast('Choose a branch below'); } } };
function reset() {
  clearTimeout(timer); Object.assign(state, { search: '', type: 'all', branch: null, sort: 'source' }); $('search').value = ''; $('compact-search').value = ''; $('path-search').value = ''; $('sort').value = 'source'; runQuery();
}
$('reset').onclick = reset; $('empty-reset').onclick = reset;
function mode(canvas) {
  $('workspace').hidden = canvas; $('canvas-view').hidden = !canvas;
  $('canvas-mode').classList.toggle('active', canvas); $('inspector-mode').classList.toggle('active', !canvas);
  $('canvas-mode').setAttribute('aria-pressed', String(canvas)); $('inspector-mode').setAttribute('aria-pressed', String(!canvas));
  if (!canvas) requestAnimationFrame(requestPage);
}
$('canvas-mode').onclick = () => mode(true); $('inspector-mode').onclick = () => mode(false); $('expand').onclick = () => mode(false); $('explore').onclick = () => mode(false);
function toast(message) { $('toast').textContent = message; $('toast').hidden = false; clearTimeout(toastTimer); toastTimer = setTimeout(() => $('toast').hidden = true, 2500); }
document.addEventListener('keydown', e => {
  if (e.key === 'Escape') { reset(); document.activeElement.blur(); return; }
  if (['INPUT', 'SELECT', 'TEXTAREA'].includes(document.activeElement.tagName) || e.ctrlKey || e.metaKey || e.altKey) return;
  if (e.key === '/') { e.preventDefault(); ($('workspace').hidden ? $('compact-search') : $('search')).focus(); }
  if (e.key.toLowerCase() === 'g') { e.preventDefault(); mode(false); $('path-search').focus(); }
});
runQuery();
