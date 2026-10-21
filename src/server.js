const path = require('path');
const express = require('express');
const config = require('./config');
const { MySqlEngine } = require('./engines/mysql');
const { HeliosEngine } = require('./engines/helios');
const { HttpError, checkName } = require('./util');

const engines = {};
if (config.mysql.enabled) engines.mysql = new MySqlEngine(config.mysql);
if (config.helios.enabled) engines.helios = new HeliosEngine(config.helios);

const app = express();
app.use(express.json({ limit: '20mb' }));
app.use(express.static(path.join(__dirname, '..', 'public')));

// Wrap async handlers so thrown errors reach the error middleware.
const h = (fn) => (req, res, next) => Promise.resolve(fn(req, res)).then((data) => {
  if (data !== undefined && !res.headersSent) res.json(data);
}).catch(next);

function engineOf(req) {
  const engine = engines[req.params.engine];
  if (!engine) throw new HttpError(404, `Không có engine ${req.params.engine}`);
  return engine;
}

app.get('/api/engines', h(async () => {
  return Promise.all(Object.values(engines).map(async (e) => ({
    id: e.id,
    label: e.label,
    features: e.features,
    status: await e.status(),
  })));
}));

const base = '/api/:engine/databases';
const branchBase = `${base}/:db/branches/:branch`;

app.get(base, h(async (req) => engineOf(req).listDatabases()));
app.post(base, h(async (req) => {
  await engineOf(req).createDatabase(req.body.name);
  return { ok: true };
}));
app.delete(`${base}/:db`, h(async (req) => {
  await engineOf(req).dropDatabase(req.params.db);
  return { ok: true };
}));

app.get(`${base}/:db/branches`, h(async (req) => engineOf(req).listBranches(req.params.db)));
app.post(`${base}/:db/branches`, h(async (req) => {
  const { name, from, asOf } = req.body;
  return engineOf(req).createBranch(req.params.db, name, { from: from || 'main', asOf: asOf || undefined });
}));
app.post(`${base}/:db/snapshots`, h(async (req) => {
  const engine = engineOf(req);
  if (!engine.features.snapshots) throw new HttpError(400, `${engine.label} dùng time-travel (AS OF) thay cho snapshot`);
  return engine.createSnapshot(req.params.db);
}));
app.delete(branchBase, h(async (req) => {
  await engineOf(req).dropBranch(req.params.db, req.params.branch);
  return { ok: true };
}));
app.post(`${branchBase}/merge`, h(async (req) => engineOf(req).mergeBranch(req.params.db, req.params.branch)));

app.get(`${branchBase}/tables`, h(async (req) => engineOf(req).listTables(req.params.db, req.params.branch)));
app.get(`${branchBase}/tables/:table`, h(async (req) => {
  checkName(req.params.table, 'Tên bảng');
  return engineOf(req).describeTable(req.params.db, req.params.branch, req.params.table);
}));
app.post(`${branchBase}/query`, h(async (req) => engineOf(req).query(req.params.db, req.params.branch, req.body.sql)));
app.get(`${branchBase}/export`, h(async (req, res) => {
  const { db, branch } = req.params;
  const sql = await engineOf(req).exportSql(db, branch);
  res.setHeader('Content-Type', 'application/sql; charset=utf-8');
  res.setHeader('Content-Disposition', `attachment; filename="${req.params.engine}-${db}-${branch}.sql"`);
  res.send(sql);
}));

// Copy tables between any two (engine, database, branch) locations, e.g. from
// a MySQL test database into a HeliosDB branch or back.
app.post('/api/copy', h(async (req) => {
  const { from, to, tables } = req.body;
  const src = engines[from?.engine];
  const dst = engines[to?.engine];
  if (!src || !dst) throw new HttpError(400, 'Nguồn hoặc đích không hợp lệ');
  let names = tables;
  if (!Array.isArray(names) || !names.length) {
    names = (await src.listTables(from.db, from.branch)).filter((t) => t.type !== 'VIEW').map((t) => t.name);
  }
  const copied = [];
  for (const table of names) {
    checkName(table, 'Tên bảng');
    const { fields, rows } = await src.readTable(from.db, from.branch, table);
    // Same engine: keep the exact table definition (keys, types).
    const ddl = src === dst ? await src.exportDdl(from.db, from.branch, table, fields) : undefined;
    await dst.writeTable(to.db, to.branch, table, fields, rows, ddl);
    copied.push({ table, rows: rows.length });
  }
  return { copied };
}));

app.use('/api', (req, res) => res.status(404).json({ error: 'Không tìm thấy API' }));

app.use((err, req, res, next) => {
  const status = err.status || 500;
  if (status >= 500) console.error(err);
  res.status(status).json({ error: err.sqlMessage || err.message || 'Lỗi không xác định' });
});

if (require.main === module) {
  app.listen(config.port, config.host, () => {
    console.log(`TestDB Hub đang chạy tại http://${config.host}:${config.port}`);
    for (const e of Object.values(engines)) console.log(`  - ${e.label}: ${e.cfg.host}:${e.cfg.port}`);
  });
}

module.exports = { app, engines };
