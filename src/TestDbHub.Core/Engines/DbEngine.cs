using System.Diagnostics;
using System.Text;
using TestDbHub.Core.Sessions;

namespace TestDbHub.Core.Engines;

/// <summary>
/// A database server. Subclasses decide how databases and branches map onto
/// the server and implement branch management; running SQL, reading/writing
/// whole tables and exports are shared here.
/// </summary>
public abstract class DbEngine : IAsyncDisposable
{
    public const int MaxRows = 1000;
    private const int InsertBatch = 200;

    public abstract string Id { get; }
    public abstract string Label { get; }
    public abstract EngineFeatures Features { get; }
    public abstract SqlDialect Dialect { get; }

    /// <summary>Open a session already pointed at the database/branch.</summary>
    internal abstract Task<ISqlSession> OpenSessionAsync(string db, string branch, CancellationToken ct);

    public abstract Task<EngineStatus> GetStatusAsync(CancellationToken ct = default);
    public abstract Task<IReadOnlyList<DatabaseInfo>> ListDatabasesAsync(CancellationToken ct = default);
    public abstract Task CreateDatabaseAsync(string name, CancellationToken ct = default);
    public abstract Task DropDatabaseAsync(string name, CancellationToken ct = default);
    public abstract Task<IReadOnlyList<BranchInfo>> ListBranchesAsync(string db, CancellationToken ct = default);
    public abstract Task CreateBranchAsync(string db, string name, string from = BranchInfo.MainName, DateTime? asOf = null, CancellationToken ct = default);
    public abstract Task DropBranchAsync(string db, string branch, CancellationToken ct = default);
    public abstract Task<string> MergeBranchAsync(string db, string branch, CancellationToken ct = default);
    public abstract Task<IReadOnlyList<TableInfo>> ListTablesAsync(string db, string branch, CancellationToken ct = default);
    public abstract Task<IReadOnlyList<ColumnInfo>> DescribeTableAsync(string db, string branch, string table, CancellationToken ct = default);

    public virtual Task<BranchInfo> CreateSnapshotAsync(string db, CancellationToken ct = default) =>
        throw new TestDbException($"{Label} dùng time-travel (AS OF) thay cho snapshot");

    /// <summary>Quote an identifier for this engine.</summary>
    public abstract string Ident(string name);

    /// <summary>A ready-to-run SELECT for previewing a table.</summary>
    public string PreviewSql(string table, int limit = 100) => $"SELECT * FROM {Ident(table)} LIMIT {limit};";

    /// <summary>Run a script statement by statement; stops at the first error.</summary>
    public async Task<IReadOnlyList<QueryResult>> QueryAsync(string db, string branch, string sql, CancellationToken ct = default)
    {
        var statements = SqlSplitter.Split(sql);
        var results = new List<QueryResult>();
        if (statements.Count == 0) return results;
        await using var session = await OpenSessionAsync(db, branch, ct);
        foreach (var stmt in statements)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var r = await session.RunAsync(stmt, MaxRows, ct);
                results.Add(new QueryResult
                {
                    Sql = stmt,
                    Elapsed = sw.Elapsed,
                    Columns = r.Columns?.Select(c => c.Name).ToList(),
                    Rows = r.Columns is null ? null : r.Rows,
                    RowCount = r.RowCount,
                    Truncated = r.RowCount > MaxRows,
                    AffectedRows = r.AffectedRows,
                });
            }
            catch (SqlExecutionException ex)
            {
                results.Add(new QueryResult { Sql = stmt, Error = ex.Message, Elapsed = sw.Elapsed });
                break;
            }
        }
        return results;
    }

    public async Task<TableData> ReadTableAsync(string db, string branch, string table, CancellationToken ct = default)
    {
        Names.Validate(table, "Tên bảng");
        await using var session = await OpenSessionAsync(db, branch, ct);
        var r = await session.RunAsync($"SELECT * FROM {Ident(table)}", int.MaxValue, ct);
        var columns = r.Columns ?? [];
        return new TableData(PortableSql.InferDecimals(columns, r.Rows), r.Rows);
    }

    public string CreateTableSql(string table, IReadOnlyList<PortableColumn> columns) =>
        $"CREATE TABLE {Ident(table)} ({string.Join(", ", columns.Select(c => $"{Ident(c.Name)} {PortableSql.ColumnType(c, Dialect)}"))})";

    public string InsertSql(string table, IReadOnlyList<PortableColumn> columns, IEnumerable<object?[]> rows)
    {
        var sb = new StringBuilder();
        sb.Append("INSERT INTO ").Append(Ident(table)).Append(" (")
          .Append(string.Join(", ", columns.Select(c => Ident(c.Name)))).Append(") VALUES");
        bool first = true;
        foreach (var row in rows)
        {
            sb.Append(first ? "\n  (" : ",\n  (");
            for (int i = 0; i < row.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(PortableSql.Literal(row[i], i < columns.Count ? columns[i] : null, Dialect));
            }
            sb.Append(')');
            first = false;
        }
        return sb.ToString();
    }

    /// <summary>DDL used in exports and same-engine copies; MySQL keeps its exact definition.</summary>
    public virtual Task<string> GetTableDdlAsync(string db, string branch, string table, IReadOnlyList<PortableColumn> columns, CancellationToken ct = default) =>
        Task.FromResult(CreateTableSql(table, columns));

    internal virtual Task BeginBulkWriteAsync(ISqlSession session, CancellationToken ct) => Task.CompletedTask;

    internal virtual Task EndBulkWriteAsync(ISqlSession session, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Replace <paramref name="table"/> with the given data. <paramref name="ddl"/> overrides the generated CREATE TABLE.</summary>
    public async Task WriteTableAsync(string db, string branch, string table, TableData data, string? ddl = null, CancellationToken ct = default)
    {
        Names.Validate(table, "Tên bảng");
        await using var session = await OpenSessionAsync(db, branch, ct);
        await BeginBulkWriteAsync(session, ct);
        try
        {
            await session.RunAsync($"DROP TABLE IF EXISTS {Ident(table)}", 0, ct);
            await session.RunAsync(ddl ?? CreateTableSql(table, data.Columns), 0, ct);
            for (int i = 0; i < data.Rows.Count; i += InsertBatch)
            {
                await session.RunAsync(InsertSql(table, data.Columns, data.Rows.Skip(i).Take(InsertBatch)), 0, ct);
            }
        }
        finally
        {
            await EndBulkWriteAsync(session, ct);
        }
    }

    /// <summary>Portable SQL dump of every table in a branch.</summary>
    public async Task<string> ExportSqlAsync(string db, string branch, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"-- TestDB Hub export: {Label} / {db} / {branch}");
        sb.AppendLine($"-- {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        foreach (var t in (await ListTablesAsync(db, branch, ct)).Where(t => !t.IsView))
        {
            var data = await ReadTableAsync(db, branch, t.Name, ct);
            sb.AppendLine($"DROP TABLE IF EXISTS {Ident(t.Name)};");
            sb.AppendLine(await GetTableDdlAsync(db, branch, t.Name, data.Columns, ct) + ";");
            for (int i = 0; i < data.Rows.Count; i += InsertBatch)
            {
                sb.AppendLine(InsertSql(t.Name, data.Columns, data.Rows.Skip(i).Take(InsertBatch)) + ";");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
