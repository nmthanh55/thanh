const mysql = require('mysql2/promise');
const { EngineBase } = require('./base');
const { HttpError, checkName } = require('../util');

const TS_RE = /^\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}(:\d{2}(\.\d+)?)?([+-]\d{2}:?\d{2}|Z)?$/;

// HeliosDB-Nano spoken to over its MySQL wire protocol. A Nano instance is one
// database with native copy-on-write branches, time-travel (AS OF) reads and
// MERGE BRANCH, so here branches are real rather than emulated.
class HeliosEngine extends EngineBase {
  constructor(cfg) {
    super();
    this.id = 'helios';
    this.kind = 'helios';
    this.label = 'HeliosDB-Nano';
    this.cfg = cfg;
    this.dbName = cfg.dbName;
    this.features = { multiDb: false, snapshots: false, timeTravel: true, merge: 'native' };
  }

  // USE BRANCH is per session, so every request gets its own short-lived
  // connection instead of sharing a pool whose sessions could leak a branch.
  async connect() {
    return mysql.createConnection({
      host: this.cfg.host,
      port: this.cfg.port,
      user: this.cfg.user,
      password: this.cfg.password || undefined,
      dateStrings: true,
      supportBigNumbers: true,
      bigNumberStrings: true,
      connectTimeout: 5000,
    });
  }

  requireDb(db) {
    if (db !== this.dbName) throw new HttpError(404, `HeliosDB-Nano chỉ có database "${this.dbName}"`);
  }

  async withConn(db, branch, fn) {
    this.requireDb(db);
    checkName(branch, 'Tên nhánh');
    const conn = await this.connect();
    try {
      if (branch !== 'main') await conn.query(`USE BRANCH ${branch}`);
      return await fn(conn);
    } finally {
      conn.end().catch(() => conn.destroy());
    }
  }

  async run(sql) {
    const conn = await this.connect();
    try {
      const [rows] = await conn.query(sql);
      return rows;
    } finally {
      conn.end().catch(() => conn.destroy());
    }
  }

  async status() {
    try {
      const conn = await this.connect();
      const version = conn.connection.serverVersion || conn.connection._handshakePacket?.serverVersion;
      await conn.end();
      return { ok: true, version: version || 'HeliosDB-Nano' };
    } catch (err) {
      return { ok: false, error: err.message };
    }
  }

  async listDatabases() {
    const branches = await this.listBranches(this.dbName);
    return [{ name: this.dbName, branches: branches.length - 1 }];
  }

  async createDatabase() {
    throw new HttpError(400, 'HeliosDB-Nano chỉ có một database — hãy tạo nhánh (branch) thay vì database mới');
  }

  async dropDatabase() {
    throw new HttpError(400, 'Không thể xoá database của HeliosDB-Nano — hãy xoá nhánh');
  }

  async listBranches(db) {
    this.requireDb(db);
    const rows = await this.run('SHOW BRANCHES');
    const branches = rows.map((r) => normalizeBranch(r)).filter((b) => b.name && b.status !== 'Dropped');
    const main = branches.find((b) => b.name === 'main');
    const rest = branches.filter((b) => b.name !== 'main');
    return [{ ...main, name: 'main', kind: 'main', parent: null }, ...rest];
  }

  async createBranch(db, name, { from = 'main', asOf } = {}) {
    this.requireDb(db);
    checkName(name, 'Tên nhánh');
    checkName(from, 'Nhánh nguồn');
    let anchor = 'NOW';
    if (asOf) {
      if (!TS_RE.test(asOf)) throw new HttpError(400, 'Thời điểm không hợp lệ, dùng dạng YYYY-MM-DD HH:MM:SS');
      anchor = `TIMESTAMP '${asOf.replace('T', ' ')}'`;
    }
    await this.run(`CREATE DATABASE BRANCH ${name} FROM ${from} AS OF ${anchor}`);
    return { name };
  }

  async dropBranch(db, name) {
    this.requireDb(db);
    checkName(name, 'Tên nhánh');
    if (name === 'main') throw new HttpError(400, 'Không thể xoá nhánh main');
    await this.run(`DROP DATABASE BRANCH ${name}`);
  }

  async mergeBranch(db, name) {
    this.requireDb(db);
    checkName(name, 'Tên nhánh');
    if (name === 'main') throw new HttpError(400, 'Hãy chọn một nhánh khác main');
    await this.run(`MERGE BRANCH ${name} INTO main`);
    return { message: `Đã MERGE BRANCH ${name} INTO main (last-writer-wins, không phát hiện xung đột)` };
  }

  async listTables(db, branch) {
    return this.withConn(db, branch, async (conn) => {
      const [rows] = await conn.query({ sql: 'SHOW TABLES', rowsAsArray: true });
      return rows.map((r) => ({ name: String(r[0]), type: 'TABLE' })).sort((a, b) => a.name.localeCompare(b.name));
    });
  }

  async describeTable(db, branch, table) {
    checkName(table, 'Tên bảng');
    return this.withConn(db, branch, async (conn) => {
      const [rows] = await conn.query(`SHOW COLUMNS FROM ${table}`);
      return rows.map((r) => ({
        name: r.Field ?? r.field,
        type: r.Type ?? r.type,
        nullable: r.Null ?? r.null,
        key: r.Key ?? r.key,
        default: r.Default ?? r.default,
        extra: r.Extra ?? r.extra,
      }));
    });
  }

  async close() {}
}

// SHOW BRANCHES column names vary between versions; pick what we need.
function normalizeBranch(row) {
  const get = (...keys) => {
    for (const k of Object.keys(row)) {
      if (keys.includes(k.toLowerCase())) return row[k];
    }
    return undefined;
  };
  return {
    name: get('name', 'branch_name', 'branch'),
    kind: 'branch',
    parent: get('parent', 'parent_name', 'parent_branch') ?? null,
    createdAt: get('created_at', 'created') ?? null,
    status: get('status', 'state'),
  };
}

module.exports = { HeliosEngine };
