const { splitSql, portableType, portableValue } = require('../util');

const MAX_ROWS = 1000;
const INSERT_BATCH = 200;

// Shared behaviour for engines reached through the mysql2 driver. Subclasses
// implement withConn(db, branch, fn), which hands fn a connection already
// pointed at the right database/branch, plus the branch management methods.
class EngineBase {
  // Standard SQL string literal; subclasses may override (MySQL uses mysql2's escaping).
  literal(v) {
    v = portableValue(v);
    if (v === null || v === undefined) return 'NULL';
    if (typeof v === 'number' || typeof v === 'bigint') return String(v);
    if (typeof v === 'boolean') return v ? 'TRUE' : 'FALSE';
    if (Buffer.isBuffer(v)) return `'\\x${v.toString('hex')}'`;
    return `'${String(v).replace(/'/g, "''")}'`;
  }

  ident(name) {
    return name;
  }

  async query(db, branch, sql) {
    const statements = splitSql(sql || '');
    if (!statements.length) return [];
    return this.withConn(db, branch, async (conn) => {
      const results = [];
      for (const stmt of statements) {
        const started = Date.now();
        try {
          const [rows, fields] = await conn.query({ sql: stmt, rowsAsArray: true });
          results.push(formatResult(stmt, rows, fields, Date.now() - started));
        } catch (err) {
          results.push({ sql: stmt, error: err.sqlMessage || err.message, ms: Date.now() - started });
          break;
        }
      }
      return results;
    });
  }

  async readTable(db, branch, table) {
    return this.withConn(db, branch, async (conn) => {
      const [rows, fields] = await conn.query({ sql: `SELECT * FROM ${this.ident(table)}`, rowsAsArray: true });
      return { fields, rows };
    });
  }

  createTableSql(table, fields) {
    const cols = fields.map((f) => `${this.ident(f.name)} ${portableType(f, this.kind)}`);
    return `CREATE TABLE ${this.ident(table)} (${cols.join(', ')})`;
  }

  // DDL used in exports; MySQL overrides this to keep keys and exact types.
  async exportDdl(db, branch, table, fields) {
    return this.createTableSql(table, fields);
  }

  insertSql(table, fields, rows) {
    const cols = fields.map((f) => this.ident(f.name)).join(', ');
    const values = rows.map((r) => `(${r.map((v) => this.literal(v)).join(', ')})`).join(',\n  ');
    return `INSERT INTO ${this.ident(table)} (${cols}) VALUES\n  ${values}`;
  }

  // Replace `table` on this engine with the given columns and rows. `ddl`
  // overrides the generated CREATE TABLE (used for same-engine copies).
  async writeTable(db, branch, table, fields, rows, ddl) {
    await this.withConn(db, branch, async (conn) => {
      await this.beginBulkWrite(conn);
      try {
        await conn.query(`DROP TABLE IF EXISTS ${this.ident(table)}`);
        await conn.query(ddl || this.createTableSql(table, fields));
        for (let i = 0; i < rows.length; i += INSERT_BATCH) {
          await conn.query(this.insertSql(table, fields, rows.slice(i, i + INSERT_BATCH)));
        }
      } finally {
        await this.endBulkWrite(conn);
      }
    });
    return rows.length;
  }

  async beginBulkWrite() {}

  async endBulkWrite() {}

  // Portable SQL dump of every table in a branch: CREATE TABLE + INSERTs.
  async exportSql(db, branch) {
    const tables = await this.listTables(db, branch);
    const parts = [`-- TestDB Hub export: ${this.label} / ${db} / ${branch}`, `-- ${new Date().toISOString()}`, ''];
    for (const t of tables.filter((x) => x.type !== 'VIEW')) {
      const { fields, rows } = await this.readTable(db, branch, t.name);
      parts.push(`DROP TABLE IF EXISTS ${this.ident(t.name)};`);
      parts.push((await this.exportDdl(db, branch, t.name, fields)) + ';');
      for (let i = 0; i < rows.length; i += INSERT_BATCH) {
        parts.push(this.insertSql(t.name, fields, rows.slice(i, i + INSERT_BATCH)) + ';');
      }
      parts.push('');
    }
    return parts.join('\n');
  }
}

function formatResult(sql, rows, fields, ms) {
  if (Array.isArray(rows) && fields) {
    return {
      sql,
      ms,
      columns: fields.map((f) => f.name),
      rows: rows.slice(0, MAX_ROWS),
      rowCount: rows.length,
      truncated: rows.length > MAX_ROWS,
    };
  }
  // DDL/DML: mysql2 returns a ResultSetHeader.
  return { sql, ms, affectedRows: rows && rows.affectedRows, info: rows && rows.info };
}

module.exports = { EngineBase };
