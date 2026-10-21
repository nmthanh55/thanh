const mysql = require('mysql2/promise');
const { EngineBase } = require('./base');
const { HttpError, checkName, qi, portableValue, timestamp } = require('../util');

const META = 'testdb_meta';
const SYSTEM_SCHEMAS = new Set(['mysql', 'information_schema', 'performance_schema', 'sys', META]);

// MySQL has no native branches, so we emulate HeliosDB's model: the "main"
// branch of database `shop` is the schema `shop`, and branch `dev` is a full
// copy in schema `shop__dev`. Snapshots are branches named snap_<time> that
// can be restored over main. Bookkeeping lives in testdb_meta.branches.
class MySqlEngine extends EngineBase {
  constructor(cfg) {
    super();
    this.id = 'mysql';
    this.kind = 'mysql';
    this.label = 'MySQL';
    this.cfg = cfg;
    this.features = { multiDb: true, snapshots: true, timeTravel: false, merge: 'overwrite' };
    this.pool = null;
    this.metaReady = null;
  }

  getPool() {
    if (!this.pool) {
      this.pool = mysql.createPool({
        host: this.cfg.host,
        port: this.cfg.port,
        user: this.cfg.user,
        password: this.cfg.password,
        connectionLimit: 5,
        dateStrings: true,
        supportBigNumbers: true,
        bigNumberStrings: true,
      });
    }
    return this.pool;
  }

  literal(v) {
    return mysql.escape(portableValue(v));
  }

  ident(name) {
    return qi(name);
  }

  async beginBulkWrite(conn) {
    await conn.query('SET FOREIGN_KEY_CHECKS = 0');
  }

  async endBulkWrite(conn) {
    await conn.query('SET FOREIGN_KEY_CHECKS = 1').catch(() => {});
  }

  async ensureMeta() {
    if (!this.metaReady) {
      this.metaReady = (async () => {
        const pool = this.getPool();
        await pool.query(`CREATE DATABASE IF NOT EXISTS ${qi(META)}`);
        await pool.query(`CREATE TABLE IF NOT EXISTS ${qi(META)}.branches (
          schema_name VARCHAR(64) PRIMARY KEY,
          db VARCHAR(64) NOT NULL,
          name VARCHAR(64) NOT NULL,
          kind VARCHAR(16) NOT NULL,
          parent VARCHAR(64) NOT NULL,
          created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
          UNIQUE KEY db_name (db, name)
        )`);
      })().catch((err) => {
        this.metaReady = null;
        throw err;
      });
    }
    return this.metaReady;
  }

  async status() {
    try {
      const [[row]] = await this.getPool().query('SELECT VERSION() AS v');
      return { ok: true, version: row.v };
    } catch (err) {
      return { ok: false, error: err.message };
    }
  }

  async schemaExists(schema) {
    const [rows] = await this.getPool().query('SELECT 1 FROM information_schema.schemata WHERE schema_name = ?', [schema]);
    return rows.length > 0;
  }

  async requireDb(db) {
    checkName(db, 'Tên database');
    if (!(await this.schemaExists(db))) throw new HttpError(404, `Không tìm thấy database ${db}`);
  }

  async schemaFor(db, branch) {
    await this.requireDb(db);
    if (branch === 'main') return db;
    checkName(branch, 'Tên nhánh');
    await this.ensureMeta();
    const [rows] = await this.getPool().query(`SELECT schema_name FROM ${qi(META)}.branches WHERE db = ? AND name = ?`, [db, branch]);
    if (!rows.length) throw new HttpError(404, `Không tìm thấy nhánh ${branch} của ${db}`);
    return rows[0].schema_name;
  }

  async withConn(db, branch, fn) {
    const schema = await this.schemaFor(db, branch);
    const conn = await this.getPool().getConnection();
    try {
      await conn.query(`USE ${qi(schema)}`);
      return await fn(conn);
    } finally {
      conn.release();
    }
  }

  async listDatabases() {
    await this.ensureMeta();
    const pool = this.getPool();
    const [schemas] = await pool.query('SELECT schema_name AS name FROM information_schema.schemata ORDER BY schema_name');
    const [branches] = await pool.query(`SELECT schema_name, db FROM ${qi(META)}.branches`);
    const hidden = new Set(branches.map((b) => b.schema_name));
    const counts = {};
    for (const b of branches) counts[b.db] = (counts[b.db] || 0) + 1;
    return schemas
      .map((s) => s.name || s.NAME || s.SCHEMA_NAME)
      .filter((name) => !SYSTEM_SCHEMAS.has(name) && !hidden.has(name))
      .map((name) => ({ name, branches: counts[name] || 0 }));
  }

  async createDatabase(name) {
    checkName(name, 'Tên database');
    if (name.includes('__')) throw new HttpError(400, 'Tên database không được chứa "__" (dành cho nhánh)');
    if (await this.schemaExists(name)) throw new HttpError(409, `Database ${name} đã tồn tại`);
    await this.getPool().query(`CREATE DATABASE ${qi(name)} CHARACTER SET utf8mb4`);
  }

  async dropDatabase(name) {
    await this.requireDb(name);
    await this.ensureMeta();
    const pool = this.getPool();
    const [branches] = await pool.query(`SELECT schema_name FROM ${qi(META)}.branches WHERE db = ?`, [name]);
    for (const b of branches) await pool.query(`DROP DATABASE IF EXISTS ${qi(b.schema_name)}`);
    await pool.query(`DELETE FROM ${qi(META)}.branches WHERE db = ?`, [name]);
    await pool.query(`DROP DATABASE ${qi(name)}`);
  }

  async listBranches(db) {
    await this.requireDb(db);
    await this.ensureMeta();
    const [rows] = await this.getPool().query(
      `SELECT name, kind, parent, created_at AS createdAt FROM ${qi(META)}.branches WHERE db = ? ORDER BY created_at, name`,
      [db],
    );
    return [{ name: 'main', kind: 'main', parent: null }, ...rows];
  }

  async createBranch(db, name, { from = 'main', kind = 'branch' } = {}) {
    checkName(name, 'Tên nhánh');
    if (name === 'main') throw new HttpError(400, 'Nhánh main đã tồn tại');
    await this.ensureMeta();
    const source = await this.schemaFor(db, from);
    const schema = `${db}__${name}`;
    if (schema.length > 64) throw new HttpError(400, 'Tên database + tên nhánh quá dài (MySQL giới hạn 64 ký tự)');
    if (await this.schemaExists(schema)) throw new HttpError(409, `Nhánh ${name} đã tồn tại`);
    const pool = this.getPool();
    await pool.query(`CREATE DATABASE ${qi(schema)} CHARACTER SET utf8mb4`);
    try {
      await this.copyTables(source, schema);
      await pool.query(`INSERT INTO ${qi(META)}.branches (schema_name, db, name, kind, parent) VALUES (?, ?, ?, ?, ?)`, [
        schema, db, name, kind, from,
      ]);
    } catch (err) {
      await pool.query(`DROP DATABASE IF EXISTS ${qi(schema)}`);
      throw err;
    }
    return { name };
  }

  async createSnapshot(db) {
    return this.createBranch(db, `snap_${timestamp()}`, { kind: 'snapshot' });
  }

  async dropBranch(db, name) {
    if (name === 'main') throw new HttpError(400, 'Không thể xoá nhánh main');
    const schema = await this.schemaFor(db, name);
    await this.getPool().query(`DROP DATABASE IF EXISTS ${qi(schema)}`);
    await this.getPool().query(`DELETE FROM ${qi(META)}.branches WHERE schema_name = ?`, [schema]);
  }

  // MySQL "merge": main is replaced by the branch's tables. Used both for
  // promoting a tested branch and for restoring a snapshot.
  async mergeBranch(db, name) {
    if (name === 'main') throw new HttpError(400, 'Hãy chọn một nhánh khác main');
    const schema = await this.schemaFor(db, name);
    await this.copyTables(schema, db, { replace: true });
    return { message: `Đã ghi đè main của ${db} bằng nội dung nhánh ${name}` };
  }

  async copyTables(src, dst, { replace = false } = {}) {
    const conn = await this.getPool().getConnection();
    try {
      await conn.query('SET FOREIGN_KEY_CHECKS = 0');
      if (replace) {
        const [existing] = await conn.query(
          'SELECT table_name AS t, table_type AS type FROM information_schema.tables WHERE table_schema = ?',
          [dst],
        );
        for (const row of existing) {
          const kw = row.type === 'VIEW' ? 'VIEW' : 'TABLE';
          await conn.query(`DROP ${kw} IF EXISTS ${qi(dst)}.${qi(row.t)}`);
        }
      }
      const [tables] = await conn.query(
        "SELECT table_name AS t FROM information_schema.tables WHERE table_schema = ? AND table_type = 'BASE TABLE' ORDER BY table_name",
        [src],
      );
      for (const { t } of tables) {
        await conn.query(`CREATE TABLE ${qi(dst)}.${qi(t)} LIKE ${qi(src)}.${qi(t)}`);
        const [cols] = await conn.query(
          "SELECT column_name AS c FROM information_schema.columns WHERE table_schema = ? AND table_name = ? AND extra NOT LIKE '%GENERATED%' ORDER BY ordinal_position",
          [src, t],
        );
        const list = cols.map((c) => qi(c.c)).join(', ');
        await conn.query(`INSERT INTO ${qi(dst)}.${qi(t)} (${list}) SELECT ${list} FROM ${qi(src)}.${qi(t)}`);
      }
    } finally {
      await conn.query('SET FOREIGN_KEY_CHECKS = 1').catch(() => {});
      conn.release();
    }
  }

  async exportDdl(db, branch, table) {
    const schema = await this.schemaFor(db, branch);
    const [[row]] = await this.getPool().query(`SHOW CREATE TABLE ${qi(schema)}.${qi(table)}`);
    return row['Create Table'];
  }

  async listTables(db, branch) {
    const schema = await this.schemaFor(db, branch);
    const [rows] = await this.getPool().query(
      'SELECT table_name AS name, table_type AS type, table_rows AS approxRows FROM information_schema.tables WHERE table_schema = ? ORDER BY table_name',
      [schema],
    );
    return rows.map((r) => ({ name: r.name, type: r.type === 'VIEW' ? 'VIEW' : 'TABLE', approxRows: r.approxRows }));
  }

  async describeTable(db, branch, table) {
    const schema = await this.schemaFor(db, branch);
    const [rows] = await this.getPool().query(
      `SELECT column_name AS name, column_type AS type, is_nullable AS nullable, column_key AS \`key\`, column_default AS \`default\`, extra AS extra
       FROM information_schema.columns WHERE table_schema = ? AND table_name = ? ORDER BY ordinal_position`,
      [schema, table],
    );
    return rows;
  }

  async close() {
    if (this.pool) await this.pool.end();
  }
}

module.exports = { MySqlEngine };
