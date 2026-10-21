const test = require('node:test');
const assert = require('node:assert');
const { Types } = require('mysql2');
const { splitSql, checkName, portableType, portableValue } = require('../src/util');

test('splitSql splits on semicolons outside quotes and comments', () => {
  const sql = `
    -- comment; not a statement
    INSERT INTO t VALUES ('a;b', "c;d", 'it''s');
    /* block; comment */ SELECT \`x;y\` FROM t # trailing; comment
    ;
    SELECT 'back\\'slash;'
  `;
  assert.deepStrictEqual(splitSql(sql), [
    `INSERT INTO t VALUES ('a;b', "c;d", 'it''s')`,
    'SELECT `x;y` FROM t',
    "SELECT 'back\\'slash;'",
  ]);
});

test('splitSql ignores empty statements', () => {
  assert.deepStrictEqual(splitSql(' ; ;\n'), []);
});

test('checkName accepts simple identifiers only', () => {
  assert.strictEqual(checkName('shop_test1'), 'shop_test1');
  for (const bad of ['', '1abc', 'a b', 'a-b', 'a`b', 'x'.repeat(41), undefined]) {
    assert.throws(() => checkName(bad), (err) => err.status === 400);
  }
});

test('portableType maps per target dialect', () => {
  assert.strictEqual(portableType({ columnType: Types.LONG }, 'mysql'), 'INT');
  assert.strictEqual(portableType({ columnType: Types.DATETIME }, 'mysql'), 'DATETIME');
  assert.strictEqual(portableType({ columnType: Types.DATETIME }, 'helios'), 'TIMESTAMP');
  assert.strictEqual(portableType({ columnType: Types.VAR_STRING, characterSet: 224 }, 'helios'), 'TEXT');
  assert.strictEqual(portableType({ columnType: Types.BLOB, characterSet: 63 }, 'helios'), 'BYTEA');
  assert.strictEqual(portableType({ columnType: Types.NEWDECIMAL, columnLength: 12, decimals: 2 }, 'mysql'), 'DECIMAL(10,2)');
});

test('portableValue serialises JSON objects', () => {
  assert.strictEqual(portableValue({ a: 1 }), '{"a":1}');
  assert.strictEqual(portableValue(null), null);
  assert.ok(Buffer.isBuffer(portableValue(Buffer.from('x'))));
});
