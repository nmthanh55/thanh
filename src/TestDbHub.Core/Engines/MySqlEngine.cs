using MySqlConnector;
using TestDbHub.Core.Sessions;

namespace TestDbHub.Core.Engines;

/// <summary>
/// MySQL has no native branches, so HeliosDB's model is emulated: the "main"
/// branch of database <c>shop</c> is the schema <c>shop</c>, and branch
/// <c>dev</c> is a full copy in schema <c>shop__dev</c>. Snapshots are
/// branches named <c>snap_&lt;time&gt;</c> that can be restored over main.
/// Bookkeeping lives in <c>testdb_meta.branches</c>.
/// </summary>
public sealed class MySqlEngine : DbEngine
{
    private const string Meta = "testdb_meta";
    private static readonly HashSet<string> SystemSchemas =
        new(StringComparer.OrdinalIgnoreCase) { "mysql", "information_schema", "performance_schema", "sys", Meta };

    private readonly string _connectionString;
    private Task? _metaReady;

    public MySqlEngine(string host, int port, string user, string password)
    {
        _connectionString = new MySqlConnectionStringBuilder
        {
            Server = host,
            Port = (uint)port,
            UserID = user,
            Password = password,
            AllowUserVariables = true,
            ConvertZeroDateTime = true,
            ConnectionTimeout = 5,
            DefaultCommandTimeout = 120,
        }.ConnectionString;
    }

    public override string Id => "mysql";
    public override string Label => "MySQL";
    public override EngineFeatures Features { get; } = new(MultipleDatabases: true, Snapshots: true, TimeTravel: false, NativeMerge: false);
    public override SqlDialect Dialect => SqlDialect.MySql;

    public override string Ident(string name) => "`" + name.Replace("`", "``") + "`";

    private async Task<MySqlConnection> ConnectAsync(CancellationToken ct)
    {
        var conn = new MySqlConnection(_connectionString);
        try
        {
            await conn.OpenAsync(ct);
            return conn;
        }
        catch (MySqlException ex)
        {
            await conn.DisposeAsync();
            throw new TestDbException($"Không kết nối được MySQL: {ex.Message}");
        }
    }

    private static async Task ExecAsync(MySqlConnection conn, string sql, CancellationToken ct, params object[] args)
    {
        await using var cmd = Command(conn, sql, args);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<List<T>> ReadAsync<T>(MySqlConnection conn, string sql, Func<MySqlDataReader, T> map, CancellationToken ct, params object[] args)
    {
        await using var cmd = Command(conn, sql, args);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<T>();
        while (await reader.ReadAsync(ct)) list.Add(map(reader));
        return list;
    }

    // Positional @p0, @p1... parameters.
    private static MySqlCommand Command(MySqlConnection conn, string sql, object[] args)
    {
        var cmd = new MySqlCommand(sql, conn);
        for (int i = 0; i < args.Length; i++) cmd.Parameters.AddWithValue($"@p{i}", args[i]);
        return cmd;
    }

    private Task EnsureMetaAsync(CancellationToken ct)
    {
        return _metaReady ??= Create();

        async Task Create()
        {
            try
            {
                await using var conn = await ConnectAsync(ct);
                await ExecAsync(conn, $"CREATE DATABASE IF NOT EXISTS {Ident(Meta)}", ct);
                await ExecAsync(conn, $"""
                    CREATE TABLE IF NOT EXISTS {Ident(Meta)}.branches (
                      schema_name VARCHAR(64) PRIMARY KEY,
                      db VARCHAR(64) NOT NULL,
                      name VARCHAR(64) NOT NULL,
                      kind VARCHAR(16) NOT NULL,
                      parent VARCHAR(64) NOT NULL,
                      created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                      UNIQUE KEY db_name (db, name)
                    )
                    """, ct);
            }
            catch
            {
                _metaReady = null;
                throw;
            }
        }
    }

    public override async Task<EngineStatus> GetStatusAsync(CancellationToken ct = default)
    {
        try
        {
            await using var conn = await ConnectAsync(ct);
            return new EngineStatus(true, conn.ServerVersion, null);
        }
        catch (TestDbException ex)
        {
            return new EngineStatus(false, null, ex.Message);
        }
    }

    private static async Task<bool> SchemaExistsAsync(MySqlConnection conn, string schema, CancellationToken ct) =>
        (await ReadAsync(conn, "SELECT 1 FROM information_schema.schemata WHERE schema_name = @p0", _ => 1, ct, schema)).Count > 0;

    private async Task RequireDbAsync(MySqlConnection conn, string db, CancellationToken ct)
    {
        Names.Validate(db, "Tên database");
        if (!await SchemaExistsAsync(conn, db, ct)) throw new TestDbException($"Không tìm thấy database {db}");
    }

    private async Task<string> SchemaForAsync(MySqlConnection conn, string db, string branch, CancellationToken ct)
    {
        await RequireDbAsync(conn, db, ct);
        if (branch == BranchInfo.MainName) return db;
        Names.Validate(branch, "Tên nhánh");
        await EnsureMetaAsync(ct);
        var rows = await ReadAsync(conn, $"SELECT schema_name FROM {Ident(Meta)}.branches WHERE db = @p0 AND name = @p1",
            r => r.GetString(0), ct, db, branch);
        return rows.Count > 0 ? rows[0] : throw new TestDbException($"Không tìm thấy nhánh {branch} của {db}");
    }

    internal override async Task<ISqlSession> OpenSessionAsync(string db, string branch, CancellationToken ct)
    {
        var conn = await ConnectAsync(ct);
        try
        {
            var schema = await SchemaForAsync(conn, db, branch, ct);
            await conn.ChangeDatabaseAsync(schema, ct);
            return new MySqlSession(conn);
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    internal override Task BeginBulkWriteAsync(ISqlSession session, CancellationToken ct) =>
        session.RunAsync("SET FOREIGN_KEY_CHECKS = 0", 0, ct);

    internal override async Task EndBulkWriteAsync(ISqlSession session, CancellationToken ct)
    {
        try { await session.RunAsync("SET FOREIGN_KEY_CHECKS = 1", 0, CancellationToken.None); }
        catch (TestDbException) { }
    }

    public override async Task<IReadOnlyList<DatabaseInfo>> ListDatabasesAsync(CancellationToken ct = default)
    {
        await EnsureMetaAsync(ct);
        await using var conn = await ConnectAsync(ct);
        var schemas = await ReadAsync(conn, "SELECT schema_name FROM information_schema.schemata ORDER BY schema_name", r => r.GetString(0), ct);
        var branches = await ReadAsync(conn, $"SELECT schema_name, db FROM {Ident(Meta)}.branches", r => (Schema: r.GetString(0), Db: r.GetString(1)), ct);
        var hidden = branches.Select(b => b.Schema).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return schemas
            .Where(s => !SystemSchemas.Contains(s) && !hidden.Contains(s))
            .Select(s => new DatabaseInfo(s, branches.Count(b => b.Db == s)))
            .ToList();
    }

    public override async Task CreateDatabaseAsync(string name, CancellationToken ct = default)
    {
        Names.Validate(name, "Tên database");
        if (name.Contains("__", StringComparison.Ordinal)) throw new TestDbException("Tên database không được chứa \"__\" (dành cho nhánh)");
        await using var conn = await ConnectAsync(ct);
        if (await SchemaExistsAsync(conn, name, ct)) throw new TestDbException($"Database {name} đã tồn tại");
        await ExecAsync(conn, $"CREATE DATABASE {Ident(name)} CHARACTER SET utf8mb4", ct);
    }

    public override async Task DropDatabaseAsync(string name, CancellationToken ct = default)
    {
        await EnsureMetaAsync(ct);
        await using var conn = await ConnectAsync(ct);
        await RequireDbAsync(conn, name, ct);
        var schemas = await ReadAsync(conn, $"SELECT schema_name FROM {Ident(Meta)}.branches WHERE db = @p0", r => r.GetString(0), ct, name);
        foreach (var s in schemas) await ExecAsync(conn, $"DROP DATABASE IF EXISTS {Ident(s)}", ct);
        await ExecAsync(conn, $"DELETE FROM {Ident(Meta)}.branches WHERE db = @p0", ct, name);
        await ExecAsync(conn, $"DROP DATABASE {Ident(name)}", ct);
    }

    public override async Task<IReadOnlyList<BranchInfo>> ListBranchesAsync(string db, CancellationToken ct = default)
    {
        await EnsureMetaAsync(ct);
        await using var conn = await ConnectAsync(ct);
        await RequireDbAsync(conn, db, ct);
        var rows = await ReadAsync(conn,
            $"SELECT name, kind, parent, created_at FROM {Ident(Meta)}.branches WHERE db = @p0 ORDER BY created_at, name",
            r => new BranchInfo(r.GetString(0), r.GetString(1) == "snapshot" ? BranchKind.Snapshot : BranchKind.Branch, r.GetString(2), r.GetDateTime(3)),
            ct, db);
        return [new BranchInfo(BranchInfo.MainName, BranchKind.Main), .. rows];
    }

    public override Task CreateBranchAsync(string db, string name, string from = BranchInfo.MainName, DateTime? asOf = null, CancellationToken ct = default)
    {
        if (asOf is not null) throw new TestDbException("MySQL không hỗ trợ tạo nhánh từ thời điểm trong quá khứ — hãy dùng snapshot");
        return CreateBranchCoreAsync(db, name, from, BranchKind.Branch, ct);
    }

    public override async Task<BranchInfo> CreateSnapshotAsync(string db, CancellationToken ct = default)
    {
        var name = "snap_" + Names.Timestamp(DateTime.Now);
        await CreateBranchCoreAsync(db, name, BranchInfo.MainName, BranchKind.Snapshot, ct);
        return new BranchInfo(name, BranchKind.Snapshot, BranchInfo.MainName, DateTime.Now);
    }

    private async Task CreateBranchCoreAsync(string db, string name, string from, BranchKind kind, CancellationToken ct)
    {
        Names.Validate(name, "Tên nhánh");
        if (name == BranchInfo.MainName) throw new TestDbException("Nhánh main đã tồn tại");
        await EnsureMetaAsync(ct);
        await using var conn = await ConnectAsync(ct);
        var source = await SchemaForAsync(conn, db, from, ct);
        var schema = $"{db}__{name}";
        if (schema.Length > 64) throw new TestDbException("Tên database + tên nhánh quá dài (MySQL giới hạn 64 ký tự)");
        if (await SchemaExistsAsync(conn, schema, ct)) throw new TestDbException($"Nhánh {name} đã tồn tại");
        await ExecAsync(conn, $"CREATE DATABASE {Ident(schema)} CHARACTER SET utf8mb4", ct);
        try
        {
            await CopyTablesAsync(conn, source, schema, replace: false, ct);
            await ExecAsync(conn, $"INSERT INTO {Ident(Meta)}.branches (schema_name, db, name, kind, parent) VALUES (@p0, @p1, @p2, @p3, @p4)",
                ct, schema, db, name, kind == BranchKind.Snapshot ? "snapshot" : "branch", from);
        }
        catch
        {
            await ExecAsync(conn, $"DROP DATABASE IF EXISTS {Ident(schema)}", CancellationToken.None);
            throw;
        }
    }

    public override async Task DropBranchAsync(string db, string branch, CancellationToken ct = default)
    {
        if (branch == BranchInfo.MainName) throw new TestDbException("Không thể xoá nhánh main");
        await using var conn = await ConnectAsync(ct);
        var schema = await SchemaForAsync(conn, db, branch, ct);
        await ExecAsync(conn, $"DROP DATABASE IF EXISTS {Ident(schema)}", ct);
        await ExecAsync(conn, $"DELETE FROM {Ident(Meta)}.branches WHERE schema_name = @p0", ct, schema);
    }

    /// <summary>MySQL "merge": main is replaced by the branch's tables (also used to restore a snapshot).</summary>
    public override async Task<string> MergeBranchAsync(string db, string branch, CancellationToken ct = default)
    {
        if (branch == BranchInfo.MainName) throw new TestDbException("Hãy chọn một nhánh khác main");
        await using var conn = await ConnectAsync(ct);
        var schema = await SchemaForAsync(conn, db, branch, ct);
        await CopyTablesAsync(conn, schema, db, replace: true, ct);
        return $"Đã ghi đè main của {db} bằng nội dung nhánh {branch}";
    }

    private async Task CopyTablesAsync(MySqlConnection conn, string src, string dst, bool replace, CancellationToken ct)
    {
        await ExecAsync(conn, "SET FOREIGN_KEY_CHECKS = 0", ct);
        try
        {
            if (replace)
            {
                var existing = await ReadAsync(conn, "SELECT table_name, table_type FROM information_schema.tables WHERE table_schema = @p0",
                    r => (Name: r.GetString(0), IsView: r.GetString(1) == "VIEW"), ct, dst);
                foreach (var t in existing)
                    await ExecAsync(conn, $"DROP {(t.IsView ? "VIEW" : "TABLE")} IF EXISTS {Ident(dst)}.{Ident(t.Name)}", ct);
            }
            var tables = await ReadAsync(conn,
                "SELECT table_name FROM information_schema.tables WHERE table_schema = @p0 AND table_type = 'BASE TABLE' ORDER BY table_name",
                r => r.GetString(0), ct, src);
            foreach (var t in tables)
            {
                await ExecAsync(conn, $"CREATE TABLE {Ident(dst)}.{Ident(t)} LIKE {Ident(src)}.{Ident(t)}", ct);
                var cols = await ReadAsync(conn,
                    "SELECT column_name FROM information_schema.columns WHERE table_schema = @p0 AND table_name = @p1 AND extra NOT LIKE '%GENERATED%' ORDER BY ordinal_position",
                    r => Ident(r.GetString(0)), ct, src, t);
                var list = string.Join(", ", cols);
                await ExecAsync(conn, $"INSERT INTO {Ident(dst)}.{Ident(t)} ({list}) SELECT {list} FROM {Ident(src)}.{Ident(t)}", ct);
            }
        }
        finally
        {
            await ExecAsync(conn, "SET FOREIGN_KEY_CHECKS = 1", CancellationToken.None);
        }
    }

    public override async Task<IReadOnlyList<TableInfo>> ListTablesAsync(string db, string branch, CancellationToken ct = default)
    {
        await using var conn = await ConnectAsync(ct);
        var schema = await SchemaForAsync(conn, db, branch, ct);
        return await ReadAsync(conn,
            "SELECT table_name, table_type, table_rows FROM information_schema.tables WHERE table_schema = @p0 ORDER BY table_name",
            r => new TableInfo(r.GetString(0), r.GetString(1) == "VIEW", r.IsDBNull(2) ? null : Convert.ToInt64(r.GetValue(2))),
            ct, schema);
    }

    public override async Task<IReadOnlyList<ColumnInfo>> DescribeTableAsync(string db, string branch, string table, CancellationToken ct = default)
    {
        await using var conn = await ConnectAsync(ct);
        var schema = await SchemaForAsync(conn, db, branch, ct);
        return await ReadAsync(conn,
            """
            SELECT column_name, column_type, is_nullable, column_key, column_default, extra
            FROM information_schema.columns WHERE table_schema = @p0 AND table_name = @p1 ORDER BY ordinal_position
            """,
            r => new ColumnInfo(r.GetString(0), r.GetString(1), Str(r, 2), Str(r, 3), Str(r, 4), Str(r, 5)),
            ct, schema, table);
    }

    public override async Task<string> GetTableDdlAsync(string db, string branch, string table, IReadOnlyList<PortableColumn> columns, CancellationToken ct = default)
    {
        await using var conn = await ConnectAsync(ct);
        var schema = await SchemaForAsync(conn, db, branch, ct);
        var rows = await ReadAsync(conn, $"SHOW CREATE TABLE {Ident(schema)}.{Ident(table)}", r => r.GetString(1), ct);
        return rows[0];
    }

    private static string? Str(MySqlDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i));
}
