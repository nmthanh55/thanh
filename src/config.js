const env = process.env;
const bool = (v, dflt) => (v === undefined || v === '' ? dflt : !/^(0|false|no|off)$/i.test(v));

module.exports = {
  host: env.HOST || '127.0.0.1',
  port: Number(env.PORT || 3000),
  mysql: {
    enabled: bool(env.MYSQL_ENABLED, true),
    host: env.MYSQL_HOST || '127.0.0.1',
    port: Number(env.MYSQL_PORT || 3306),
    user: env.MYSQL_USER || 'root',
    password: env.MYSQL_PASSWORD || '',
  },
  helios: {
    enabled: bool(env.HELIOS_ENABLED, true),
    host: env.HELIOS_HOST || '127.0.0.1',
    port: Number(env.HELIOS_PORT || 3307),
    user: env.HELIOS_USER || 'root',
    password: env.HELIOS_PASSWORD || '',
    dbName: env.HELIOS_DB_NAME || 'helios',
  },
};
