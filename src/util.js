const { Types } = require('mysql2');

class HttpError extends Error {
  constructor(status, message) {
    super(message);
    this.status = status;
  }
}

// Database, branch and table names: letters, digits, underscore. Keeping them
// this strict lets us embed them in SQL without worrying about quoting rules
// that differ between MySQL and HeliosDB.
const NAME_RE = /^[A-Za-z][A-Za-z0-9_]{0,39}$/;

function checkName(name, what = 'Tên') {
  if (typeof name !== 'string' || !NAME_RE.test(name)) {
    throw new HttpError(400, `${what} không hợp lệ: chỉ dùng chữ, số, dấu _ (bắt đầu bằng chữ, tối đa 40 ký tự)`);
  }
  return name;
}

// Quote an identifier for MySQL.
function qi(name) {
  return '`' + String(name).replace(/`/g, '``') + '`';
}

// Split a SQL script into statements, ignoring semicolons inside quotes and
// comments. Good enough for hand-written test scripts and dumps.
function splitSql(script) {
  const out = [];
  let cur = '';
  let i = 0;
  const n = script.length;
  while (i < n) {
    const c = script[i];
    const next = script[i + 1];
    if (c === '-' && next === '-' || c === '#') {
      const end = script.indexOf('\n', i);
      i = end === -1 ? n : end + 1;
      cur += '\n';
      continue;
    }
    if (c === '/' && next === '*') {
      const end = script.indexOf('*/', i + 2);
      i = end === -1 ? n : end + 2;
      cur += ' ';
      continue;
    }
    if (c === "'" || c === '"' || c === '`') {
      let j = i + 1;
      while (j < n) {
        if (script[j] === '\\' && c !== '`') { j += 2; continue; }
        if (script[j] === c) {
          if (script[j + 1] === c) { j += 2; continue; }
          break;
        }
        j++;
      }
      cur += script.slice(i, j + 1);
      i = j + 1;
      continue;
    }
    if (c === ';') {
      if (cur.trim()) out.push(cur.trim());
      cur = '';
      i++;
      continue;
    }
    cur += c;
    i++;
  }
  if (cur.trim()) out.push(cur.trim());
  return out;
}

const BINARY_CHARSET = 63;

// Map a mysql2 result field to a portable column type for the target engine.
// Used when copying tables between MySQL and HeliosDB.
function portableType(field, target) {
  const t = field.columnType;
  const binary = field.characterSet === BINARY_CHARSET;
  switch (t) {
    case Types.TINY:
    case Types.SHORT:
    case Types.LONG:
    case Types.INT24:
    case Types.YEAR:
      return 'INT';
    case Types.LONGLONG:
      return 'BIGINT';
    case Types.FLOAT:
    case Types.DOUBLE:
      return 'DOUBLE PRECISION';
    case Types.DECIMAL:
    case Types.NEWDECIMAL: {
      const scale = Math.min(field.decimals || 0, 30);
      const precision = Math.max(Math.min((field.columnLength || 10) - (scale > 0 ? 1 : 0) - 1, 65), scale + 1);
      return `DECIMAL(${precision},${scale})`;
    }
    case Types.DATE:
    case Types.NEWDATE:
      return 'DATE';
    case Types.DATETIME:
    case Types.TIMESTAMP:
      return target === 'mysql' ? 'DATETIME' : 'TIMESTAMP';
    case Types.TIME:
      return 'TIME';
    case Types.JSON:
      return 'JSON';
    case Types.TINY_BLOB:
    case Types.MEDIUM_BLOB:
    case Types.LONG_BLOB:
    case Types.BLOB:
    case Types.STRING:
    case Types.VAR_STRING:
    case Types.VARCHAR:
      if (binary) return target === 'mysql' ? 'LONGBLOB' : 'BYTEA';
      return target === 'mysql' ? 'LONGTEXT' : 'TEXT';
    default:
      return target === 'mysql' ? 'LONGTEXT' : 'TEXT';
  }
}

// Values read with dateStrings/supportBigNumbers come back as strings, numbers,
// Buffers, null or (for JSON) plain objects. Objects must be serialised before
// escaping, otherwise mysql2 would turn them into `key` = value lists.
function portableValue(v) {
  if (v !== null && typeof v === 'object' && !Buffer.isBuffer(v)) return JSON.stringify(v);
  return v;
}

function timestamp() {
  const d = new Date();
  const p = (x) => String(x).padStart(2, '0');
  return `${d.getFullYear()}${p(d.getMonth() + 1)}${p(d.getDate())}_${p(d.getHours())}${p(d.getMinutes())}${p(d.getSeconds())}`;
}

module.exports = { HttpError, checkName, qi, splitSql, portableType, portableValue, timestamp, NAME_RE };
