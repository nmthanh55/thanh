'use strict';

const state = {
  engines: [],          // [{id, label, features, status}]
  databases: {},        // engineId -> [{name, branches}]
  branches: {},         // `${engine}/${db}` -> [{name, kind, parent, createdAt}]
  expanded: new Set(),  // `${engine}/${db}`
  current: null,        // {engine, db, branch}
  tables: [],
};

const SAMPLE_SQL = `-- Dữ liệu mẫu: chạy được trên cả MySQL và HeliosDB-Nano
CREATE TABLE customers (id INT PRIMARY KEY, name VARCHAR(100), email VARCHAR(120), city VARCHAR(60));
CREATE TABLE orders (id INT PRIMARY KEY, customer_id INT, total DECIMAL(10,2), created_at DATE);

INSERT INTO customers VALUES
  (1, 'Nguyễn Văn An', 'an@example.com', 'Hà Nội'),
  (2, 'Trần Thị Bình', 'binh@example.com', 'TP.HCM'),
  (3, 'Lê Minh Châu', 'chau@example.com', 'Đà Nẵng'),
  (4, 'Phạm Quốc Dũng', 'dung@example.com', 'Hà Nội');

INSERT INTO orders VALUES
  (1, 1, 250000.00, '2026-01-05'),
  (2, 1, 120000.00, '2026-02-11'),
  (3, 2, 990000.00, '2026-02-14'),
  (4, 3, 75000.00, '2026-03-01'),
  (5, 4, 430000.00, '2026-03-09');

SELECT c.name, COUNT(o.id) AS so_don, SUM(o.total) AS tong_tien
FROM customers c LEFT JOIN orders o ON o.customer_id = c.id
GROUP BY c.name
ORDER BY tong_tien DESC;`;

const $ = (sel) => document.querySelector(sel);

function el(tag, attrs = {}, ...children) {
  const node = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs)) {
    if (v === undefined || v === null || v === false) continue;
    if (k.startsWith('on')) node.addEventListener(k.slice(2), v);
    else if (k === 'class') node.className = v;
    else if (k === 'value') node.value = v;
    else node.setAttribute(k, v === true ? '' : v);
  }
  for (const c of children.flat()) {
    if (c === null || c === undefined || c === false) continue;
    node.append(c instanceof Node ? c : document.createTextNode(String(c)));
  }
  return node;
}

async function api(method, url, body) {
  const res = await fetch(url, {
    method,
    headers: body ? { 'Content-Type': 'application/json' } : {},
    body: body ? JSON.stringify(body) : undefined,
  });
  const data = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error(data.error || `Lỗi ${res.status}`);
  return data;
}

const enc = encodeURIComponent;
const dbUrl = (engine, db) => `/api/${enc(engine)}/databases/${enc(db)}`;
const branchUrl = (c) => `${dbUrl(c.engine, c.db)}/branches/${enc(c.branch)}`;
const engineById = (id) => state.engines.find((e) => e.id === id);

function toast(msg, isError = false) {
  const t = el('div', { class: 'toast' + (isError ? ' error' : '') }, msg);
  $('#toasts').append(t);
  setTimeout(() => t.remove(), isError ? 7000 : 3500);
}

async function guard(fn) {
  try {
    return await fn();
  } catch (err) {
    toast(err.message, true);
    return undefined;
  }
}

// ---------- Loading ----------

async function loadEngines() {
  state.engines = await api('GET', '/api/engines');
  const row = $('#engine-status');
  row.replaceChildren(...state.engines.map((e) => el('span', { class: 'pill', title: e.status.ok ? e.status.version : e.status.error },
    el('span', { class: 'dot' + (e.status.ok ? ' ok' : '') }), `${e.label}${e.status.ok ? ' · ' + e.status.version : ' · mất kết nối'}`)));
  await Promise.all(state.engines.filter((e) => e.status.ok).map((e) => loadDatabases(e.id)));
  renderSidebar();
}

async function loadDatabases(engine) {
  state.databases[engine] = await guard(() => api('GET', `/api/${engine}/databases`)) || [];
  for (const db of state.databases[engine]) {
    if (state.expanded.has(`${engine}/${db.name}`)) await loadBranches(engine, db.name);
  }
}

async function loadBranches(engine, db) {
  state.branches[`${engine}/${db}`] = await guard(() => api('GET', `${dbUrl(engine, db)}/branches`)) || [];
}

async function refreshTree() {
  await loadEngines();
  if (state.current) {
    const list = state.branches[`${state.current.engine}/${state.current.db}`] || [];
    if (!list.some((b) => b.name === state.current.branch)) {
      const dbs = state.databases[state.current.engine] || [];
      if (dbs.some((d) => d.name === state.current.db)) await select({ ...state.current, branch: 'main' });
      else clearSelection();
    } else {
      renderSidebar();
      await loadTables();
    }
  }
}

// ---------- Sidebar ----------

function renderSidebar() {
  const side = $('#sidebar');
  side.replaceChildren(...state.engines.map((e) => {
    const head = el('div', { class: 'engine-head' },
      el('span', { class: 'name' }, el('span', { class: 'tag', style: `background: var(--${e.id === 'helios' ? 'helios' : 'mysql'})` }), e.label),
      e.features.multiDb && e.status.ok ? el('button', { title: 'Tạo database test mới', onclick: () => createDatabase(e) }, '+ Database') : null);
    if (!e.status.ok) {
      return el('div', { class: 'engine' }, head, el('div', { class: 'engine-error' }, 'Không kết nối được: ', e.status.error));
    }
    const dbs = state.databases[e.id] || [];
    const tree = el('ul', { class: 'tree' }, dbs.length ? dbs.map((d) => renderDb(e, d)) : el('li', { class: 'engine-error' }, 'Chưa có database nào'));
    return el('div', { class: 'engine' }, head, tree);
  }));
}

function renderDb(engine, db) {
  const key = `${engine.id}/${db.name}`;
  const open = state.expanded.has(key);
  const row = el('div', { class: 'row', onclick: () => toggleDb(engine.id, db.name) },
    el('span', { class: 'caret' }, open ? '▼' : '▶'), '🗄 ', db.name,
    db.branches ? el('span', { class: 'kind' }, `${db.branches} nhánh`) : null);
  const li = el('li', { class: 'db' }, row);
  if (open) {
    const branches = state.branches[key] || [];
    li.append(el('ul', { class: 'branches' }, branches.map((b) => {
      const active = state.current && state.current.engine === engine.id && state.current.db === db.name && state.current.branch === b.name;
      const icon = b.kind === 'snapshot' ? '📸 ' : b.kind === 'main' ? '★ ' : '⑂ ';
      return el('li', { class: 'branch' }, el('div', {
        class: 'row' + (active ? ' active' : ''),
        title: b.createdAt ? `Tạo lúc ${b.createdAt}${b.parent ? ' từ ' + b.parent : ''}` : '',
        onclick: () => select({ engine: engine.id, db: db.name, branch: b.name }),
      }, el('span', { class: 'caret' }), icon, b.name, el('span', { class: 'kind' }, kindLabel(b.kind))));
    })));
  }
  return li;
}

function kindLabel(kind) {
  return { main: 'chính', snapshot: 'snap', branch: 'nhánh' }[kind] || '';
}

async function toggleDb(engine, db) {
  const key = `${engine}/${db}`;
  if (state.expanded.has(key)) {
    state.expanded.delete(key);
    renderSidebar();
    return;
  }
  state.expanded.add(key);
  await loadBranches(engine, db);
  renderSidebar();
  if (!state.current) await select({ engine, db, branch: 'main' });
}

// ---------- Workspace ----------

function clearSelection() {
  state.current = null;
  $('#workspace').hidden = true;
  $('#empty').hidden = false;
  renderSidebar();
}

async function select(ctx) {
  state.current = ctx;
  state.expanded.add(`${ctx.engine}/${ctx.db}`);
  if (!state.branches[`${ctx.engine}/${ctx.db}`]) await loadBranches(ctx.engine, ctx.db);
  $('#empty').hidden = true;
  $('#workspace').hidden = false;
  renderSidebar();
  renderContext();
  await loadTables();
}

function currentBranchInfo() {
  const c = state.current;
  return (state.branches[`${c.engine}/${c.db}`] || []).find((b) => b.name === c.branch) || { name: c.branch, kind: 'branch' };
}

function renderContext() {
  const c = state.current;
  const engine = engineById(c.engine);
  const info = currentBranchInfo();
  const isMain = c.branch === 'main';
  $('#crumbs').replaceChildren(
    el('span', { style: `color: var(--${c.engine === 'helios' ? 'helios' : 'mysql'})` }, engine.label),
    el('span', { class: 'sep' }, '›'), c.db,
    el('span', { class: 'sep' }, '›'), `${info.kind === 'snapshot' ? '📸' : '⑂'} ${c.branch}`,
  );

  const actions = [
    el('button', { onclick: createBranch }, '⑂ Tạo nhánh'),
  ];
  if (engine.features.snapshots && isMain) actions.push(el('button', { onclick: createSnapshot }, '📸 Snapshot'));
  if (engine.features.timeTravel) actions.push(el('button', { onclick: timeTravel }, '🕒 Time-travel'));
  if (!isMain) {
    const label = info.kind === 'snapshot' ? '↺ Khôi phục vào main' : engine.features.merge === 'native' ? '⤴ Merge vào main' : '⤴ Ghi đè main';
    actions.push(el('button', { onclick: mergeBranch }, label));
  }
  actions.push(el('button', { onclick: copyDialog }, '⇄ Sao chép sang...'));
  actions.push(el('button', { onclick: exportSql }, '⬇ Xuất .sql'));
  if (!isMain) actions.push(el('button', { class: 'danger', onclick: dropBranch }, '✕ Xoá nhánh'));
  else if (engine.features.multiDb) actions.push(el('button', { class: 'danger', onclick: dropDatabase }, '✕ Xoá database'));
  $('#actions').replaceChildren(...actions);
}

async function loadTables() {
  const c = state.current;
  const box = $('#tables');
  box.replaceChildren(el('span', { class: 'muted' }, 'Đang tải...'));
  const tables = await guard(() => api('GET', `${branchUrl(c)}/tables`));
  if (state.current !== c) return;
  state.tables = tables || [];
  if (!state.tables.length) {
    box.replaceChildren(el('span', { class: 'muted' }, 'Chưa có bảng nào — thử bấm "Dữ liệu mẫu" rồi Chạy'));
    return;
  }
  box.replaceChildren(...state.tables.map((t) => el('span', {
    class: 'chip',
    title: 'Bấm để xem dữ liệu, Shift+bấm để xem cấu trúc',
    onclick: (ev) => (ev.shiftKey ? describeTable(t.name) : previewTable(t.name)),
  }, t.name, t.type === 'VIEW' ? el('small', {}, ' view') : t.approxRows != null ? el('small', {}, ` ~${t.approxRows}`) : null)));
}

function quoteIdent(name) {
  return state.current.engine === 'mysql' ? '`' + name + '`' : name;
}

async function previewTable(name) {
  $('#sql').value = `SELECT * FROM ${quoteIdent(name)} LIMIT 100;`;
  await runSql();
}

async function describeTable(name) {
  const cols = await guard(() => api('GET', `${branchUrl(state.current)}/tables/${enc(name)}`));
  if (!cols) return;
  const keys = ['name', 'type', 'nullable', 'key', 'default', 'extra'];
  renderResults([{
    sql: `Cấu trúc bảng ${name}`,
    columns: ['cột', 'kiểu', 'null', 'khoá', 'mặc định', 'extra'],
    rows: cols.map((c) => keys.map((k) => c[k] ?? null)),
    rowCount: cols.length,
  }]);
}

async function runSql() {
  const sql = $('#sql').value;
  if (!sql.trim() || !state.current) return;
  const c = state.current;
  const btn = $('#run');
  btn.disabled = true;
  $('#run-hint').textContent = 'Đang chạy...';
  const started = performance.now();
  const results = await guard(() => api('POST', `${branchUrl(c)}/query`, { sql }));
  btn.disabled = false;
  $('#run-hint').textContent = results ? `Xong trong ${Math.round(performance.now() - started)} ms` : '';
  if (!results) return;
  renderResults(results);
  // Table list may have changed after DDL.
  if (results.some((r) => /^\s*(create|drop|alter|rename)\b/i.test(r.sql))) await loadTables();
}

function renderResults(results) {
  const box = $('#results');
  box.replaceChildren(...results.map((r) => {
    const meta = r.error ? 'Lỗi' : r.columns ? `${r.rowCount} dòng${r.truncated ? ' (hiển thị 1000 dòng đầu)' : ''}` : `${r.affectedRows ?? 0} dòng bị ảnh hưởng`;
    const head = el('div', { class: 'result-head' }, el('code', { title: r.sql }, r.sql), el('span', {}, `${meta}${r.ms != null ? ' · ' + r.ms + ' ms' : ''}`));
    let body;
    if (r.error) body = el('div', { class: 'result-body' }, r.error);
    else if (r.columns) body = renderGrid(r.columns, r.rows);
    else body = el('div', { class: 'result-body muted' }, r.info || 'OK');
    return el('div', { class: 'result' + (r.error ? ' error' : '') }, head, body);
  }));
}

function renderGrid(columns, rows) {
  const fmt = (v) => (v === null ? null : typeof v === 'object' ? JSON.stringify(v) : String(v));
  return el('div', { class: 'grid-wrap' }, el('table', { class: 'grid' },
    el('thead', {}, el('tr', {}, columns.map((c) => el('th', {}, c)))),
    el('tbody', {}, rows.map((row) => el('tr', {}, row.map((v) => {
      const s = fmt(v);
      return s === null ? el('td', { class: 'null' }, 'NULL') : el('td', { title: s.length > 40 ? s : null }, s);
    }))))));
}

// ---------- Modal helper ----------

function field(label, input, help) {
  return el('div', { class: 'field' }, el('label', {}, label), input, help ? el('div', { class: 'help' }, help) : null);
}

function openModal(title, bodyNodes, okLabel = 'OK') {
  const dlg = $('#modal');
  $('#modal-title').textContent = title;
  $('#modal-body').replaceChildren(...bodyNodes);
  $('#modal-ok').textContent = okLabel;
  dlg.returnValue = '';
  dlg.showModal();
  const first = dlg.querySelector('#modal-body input, #modal-body select');
  if (first) first.focus();
  return new Promise((resolve) => dlg.addEventListener('close', () => resolve(dlg.returnValue === 'ok'), { once: true }));
}

const NAME_PATTERN = '[A-Za-z][A-Za-z0-9_]{0,39}';
const nameInput = (placeholder) => el('input', { required: true, pattern: NAME_PATTERN, placeholder, autocomplete: 'off' });

// ---------- Actions ----------

async function createDatabase(engine) {
  const name = nameInput('vd: shop_test');
  if (!(await openModal(`Tạo database trên ${engine.label}`, [field('Tên database', name, 'Chữ, số và dấu _ ; bắt đầu bằng chữ')], 'Tạo'))) return;
  const ok = await guard(() => api('POST', `/api/${engine.id}/databases`, { name: name.value }));
  if (!ok) return;
  toast(`Đã tạo database ${name.value}`);
  await loadDatabases(engine.id);
  await select({ engine: engine.id, db: name.value, branch: 'main' });
}

async function dropDatabase() {
  const c = state.current;
  if (!confirm(`Xoá database ${c.db} cùng tất cả nhánh và snapshot? Không thể hoàn tác.`)) return;
  if (!(await guard(() => api('DELETE', dbUrl(c.engine, c.db))))) return;
  toast(`Đã xoá database ${c.db}`);
  state.expanded.delete(`${c.engine}/${c.db}`);
  clearSelection();
  await refreshTree();
}

async function createBranch() {
  const c = state.current;
  const engine = engineById(c.engine);
  const name = nameInput('vd: thu_migration');
  const branches = (state.branches[`${c.engine}/${c.db}`] || []).filter((b) => b.kind !== 'snapshot' || engine.id === 'mysql');
  const from = el('select', {}, branches.map((b) => el('option', { value: b.name, selected: b.name === c.branch }, b.name)));
  const nodes = [field('Tên nhánh mới', name), field('Tạo từ nhánh', from)];
  let asOf;
  if (engine.features.timeTravel) {
    asOf = el('input', { type: 'datetime-local', step: 1 });
    nodes.push(field('Tại thời điểm (tuỳ chọn)', asOf, 'Để trống = trạng thái hiện tại (AS OF NOW)'));
  } else {
    nodes.push(el('div', { class: 'note' }, 'MySQL không có nhánh thật: app sẽ sao chép toàn bộ bảng sang một schema riêng. Khoá ngoại và view không được sao chép.'));
  }
  if (!(await openModal('Tạo nhánh', nodes, 'Tạo nhánh'))) return;
  const res = await guard(() => api('POST', `${dbUrl(c.engine, c.db)}/branches`, {
    name: name.value, from: from.value, asOf: asOf && asOf.value ? asOf.value.replace('T', ' ') : undefined,
  }));
  if (!res) return;
  toast(`Đã tạo nhánh ${name.value}`);
  await loadBranches(c.engine, c.db);
  await loadDatabases(c.engine);
  await select({ ...c, branch: name.value });
}

async function createSnapshot() {
  const c = state.current;
  const res = await guard(() => api('POST', `${dbUrl(c.engine, c.db)}/snapshots`));
  if (!res) return;
  toast(`Đã chụp snapshot ${res.name}`);
  await loadBranches(c.engine, c.db);
  await loadDatabases(c.engine);
  renderSidebar();
}

async function mergeBranch() {
  const c = state.current;
  const engine = engineById(c.engine);
  const info = currentBranchInfo();
  const msg = engine.features.merge === 'native'
    ? `MERGE BRANCH ${c.branch} INTO main?\n\nHeliosDB merge theo kiểu last-writer-wins: nếu cùng một dòng bị sửa ở cả hai nhánh, một giá trị sẽ thắng mà không cảnh báo.`
    : `${info.kind === 'snapshot' ? 'Khôi phục' : 'Ghi đè'} main của ${c.db} bằng nội dung "${c.branch}"?\n\nToàn bộ bảng hiện có trong main sẽ bị thay thế. Mẹo: chụp snapshot main trước.`;
  if (!confirm(msg)) return;
  const res = await guard(() => api('POST', `${branchUrl(c)}/merge`));
  if (!res) return;
  toast(res.message || 'Đã merge');
  await select({ ...c, branch: 'main' });
}

async function dropBranch() {
  const c = state.current;
  if (!confirm(`Xoá nhánh ${c.branch}? Dữ liệu trong nhánh sẽ mất.`)) return;
  if (!(await guard(() => api('DELETE', branchUrl(c))))) return;
  toast(`Đã xoá nhánh ${c.branch}`);
  await loadBranches(c.engine, c.db);
  await loadDatabases(c.engine);
  await select({ ...c, branch: 'main' });
}

async function timeTravel() {
  const c = state.current;
  const table = el('select', {}, state.tables.map((t) => el('option', { value: t.name }, t.name)));
  const when = el('input', { type: 'datetime-local', step: 1, required: true });
  const d = new Date(Date.now() - 5 * 60 * 1000);
  when.value = new Date(d.getTime() - d.getTimezoneOffset() * 60000).toISOString().slice(0, 19);
  const nodes = [
    el('div', { class: 'note' }, 'Xem dữ liệu của bảng tại một thời điểm trong quá khứ (SELECT ... AS OF TIMESTAMP). Giờ tính theo giờ của máy chủ HeliosDB.'),
    field('Bảng', table), field('Thời điểm', when),
  ];
  if (!state.tables.length) return toast('Nhánh này chưa có bảng nào', true);
  if (!(await openModal('Time-travel', nodes, 'Xem'))) return;
  $('#sql').value = `SELECT * FROM ${table.value} AS OF TIMESTAMP '${when.value.replace('T', ' ')}' LIMIT 100;`;
  await runSql();
}

async function copyDialog() {
  const c = state.current;
  const engines = state.engines.filter((e) => e.status.ok);
  const target = el('select', {}, engines.map((e) => el('option', { value: e.id, selected: e.id !== c.engine }, e.label)));
  const dbSel = el('select');
  const branchSel = el('select');

  async function fillDbs() {
    if (!state.databases[target.value]) await loadDatabases(target.value);
    const dbs = state.databases[target.value] || [];
    dbSel.replaceChildren(...dbs.map((d) => el('option', { value: d.name }, d.name)));
    await fillBranches();
  }
  async function fillBranches() {
    if (!dbSel.value) return branchSel.replaceChildren();
    await loadBranches(target.value, dbSel.value);
    const list = (state.branches[`${target.value}/${dbSel.value}`] || []).filter((b) => b.kind !== 'snapshot');
    branchSel.replaceChildren(...list.map((b) => el('option', { value: b.name }, b.name)));
  }
  target.addEventListener('change', fillDbs);
  dbSel.addEventListener('change', fillBranches);
  await fillDbs();

  const checks = state.tables.filter((t) => t.type !== 'VIEW').map((t) => el('input', { type: 'checkbox', value: t.name, checked: true }));
  const nodes = [
    el('div', { class: 'note' }, `Sao chép bảng từ ${engineById(c.engine).label} › ${c.db} › ${c.branch}. Bảng trùng tên ở đích sẽ bị thay thế. Khi sao chép sang engine khác, kiểu dữ liệu được quy đổi tương đương và khoá chính/chỉ mục không được chép theo.`),
    field('Engine đích', target), field('Database đích', dbSel), field('Nhánh đích', branchSel),
    field('Bảng', el('div', { class: 'checks' }, checks.length ? checks.map((cb) => el('label', {}, cb, ' ', cb.value)) : el('span', { class: 'muted' }, 'Không có bảng'))),
  ];
  if (!checks.length) return toast('Nhánh này chưa có bảng nào', true);
  if (!(await openModal('Sao chép bảng', nodes, 'Sao chép'))) return;
  const tables = checks.filter((cb) => cb.checked).map((cb) => cb.value);
  if (!tables.length || !dbSel.value || !branchSel.value) return toast('Chưa chọn đủ đích hoặc bảng', true);
  const to = { engine: target.value, db: dbSel.value, branch: branchSel.value };
  if (to.engine === c.engine && to.db === c.db && to.branch === c.branch) return toast('Nguồn và đích trùng nhau', true);
  toast('Đang sao chép...');
  const res = await guard(() => api('POST', '/api/copy', { from: c, to, tables }));
  if (!res) return;
  const total = res.copied.reduce((s, x) => s + x.rows, 0);
  toast(`Đã sao chép ${res.copied.length} bảng (${total} dòng) sang ${engineById(to.engine).label} › ${to.db} › ${to.branch}`);
}

function exportSql() {
  const a = el('a', { href: `${branchUrl(state.current)}/export` });
  document.body.append(a);
  a.click();
  a.remove();
}

// ---------- Wiring ----------

$('#run').addEventListener('click', runSql);
$('#sql').addEventListener('keydown', (ev) => {
  if ((ev.ctrlKey || ev.metaKey) && ev.key === 'Enter') {
    ev.preventDefault();
    runSql();
  }
});
$('#sample').addEventListener('click', () => {
  $('#sql').value = SAMPLE_SQL;
  $('#sql').focus();
});
$('#import-file').addEventListener('change', async (ev) => {
  const file = ev.target.files[0];
  if (!file) return;
  $('#sql').value = await file.text();
  ev.target.value = '';
  toast(`Đã mở ${file.name} — bấm Chạy để nạp vào nhánh hiện tại`);
});

guard(loadEngines);
