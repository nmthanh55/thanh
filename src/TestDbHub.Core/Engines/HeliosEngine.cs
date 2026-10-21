using System.Globalization;
using TestDbHub.Core.Sessions;

namespace TestDbHub.Core.Engines;

/// <summary>
/// HeliosDB-Nano over its PostgreSQL wire protocol. A Nano instance is one
/// database with native copy-on-write branches, time-travel (AS OF) reads and
/// MERGE BRANCH, so here branches are real rather than emulated.
/// </summary>
public sealed class HeliosEngine(string host, int port, string user, string password, string database) : DbEngine
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    public override string Id => "helios";
    public override string Label => "HeliosDB-Nano";
    public override EngineFeatures Features { get; } = new(MultipleDatabases: false, Snapshots: false, TimeTravel: true, NativeMerge: true);
    public override SqlDialect Dialect => SqlDialect.Helios;

    /// <summary>The single database a Nano instance holds.</summary>
    public string DatabaseName => database;

    public override string Ident(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    private Task<PgSimpleSession> ConnectAsync(CancellationToken ct) =>
        PgSimpleSession.ConnectAsync(host, port, user, password, database, ConnectTimeout, ct);

    private void RequireDb(string db)
    {
        if (db != database) throw new TestDbException($"HeliosDB-Nano chỉ có database \"{database}\"");
    }

    // In Nano 4.41 the branch picked by USE BRANCH is server-wide: a new
    // connection starts on whatever branch the previous one chose. So every
    // session selects its branch explicitly (main included) and switches back
    // to main when it closes, leaving other clients where they expect to be.
    internal override async Task<ISqlSession> OpenSessionAsync(string db, string branch, CancellationToken ct)
    {
        RequireDb(db);
        Names.Validate(branch, "Tên nhánh");
        var session = await ConnectAsync(ct);
        try
        {
            await session.RunAsync($"USE BRANCH {branch}", 0, ct);
            if (branch != BranchInfo.MainName) session.OnCloseSql = $"USE BRANCH {BranchInfo.MainName}";
            return session;
        }
        catch (SqlExecutionException ex)
        {
            await session.DisposeAsync();
            throw new TestDbException($"Không mở được nhánh {branch}: {ex.Message}");
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    private async Task<RawResult> RunAsync(string sql, CancellationToken ct)
    {
        await using var session = await OpenSessionAsync(database, BranchInfo.MainName, ct);
        return await session.RunAsync(sql, int.MaxValue, ct);
    }

    public override async Task<EngineStatus> GetStatusAsync(CancellationToken ct = default)
    {
        try
        {
            await using var session = await ConnectAsync(ct);
            return new EngineStatus(true, session.ServerVersion ?? "HeliosDB-Nano", null);
        }
        catch (Exception ex) when (ex is TestDbException or System.Net.Sockets.SocketException or IOException)
        {
            return new EngineStatus(false, null, ex.Message);
        }
    }

    public override async Task<IReadOnlyList<DatabaseInfo>> ListDatabasesAsync(CancellationToken ct = default)
    {
        var branches = await ListBranchesAsync(database, ct);
        return [new DatabaseInfo(database, branches.Count - 1)];
    }

    public override Task CreateDatabaseAsync(string name, CancellationToken ct = default) =>
        throw new TestDbException("HeliosDB-Nano chỉ có một database — hãy tạo nhánh thay vì database mới");

    public override Task DropDatabaseAsync(string name, CancellationToken ct = default) =>
        throw new TestDbException("Không thể xoá database của HeliosDB-Nano — hãy xoá nhánh");

    public override async Task<IReadOnlyList<BranchInfo>> ListBranchesAsync(string db, CancellationToken ct = default)
    {
        RequireDb(db);
        var r = await RunAsync("SELECT branch_name, branch_id, parent_id, status FROM pg_database_branches()", ct);
        var rows = r.Rows
            .Where(x => x[0] is string && !string.Equals(x[3] as string, "Dropped", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var namesById = rows.Where(x => x[1] is long).ToDictionary(x => (long)x[1]!, x => (string)x[0]!);
        var branches = rows
            .Where(x => (string)x[0]! != BranchInfo.MainName)
            .Select(x => new BranchInfo(
                (string)x[0]!,
                BranchKind.Branch,
                x[2] is long pid && namesById.TryGetValue(pid, out var parent) ? parent : null))
            .OrderBy(b => b.Name, StringComparer.Ordinal);
        return [new BranchInfo(BranchInfo.MainName, BranchKind.Main), .. branches];
    }

    public override async Task CreateBranchAsync(string db, string name, string from = BranchInfo.MainName, DateTime? asOf = null, CancellationToken ct = default)
    {
        RequireDb(db);
        Names.Validate(name, "Tên nhánh");
        Names.Validate(from, "Nhánh nguồn");
        if (name == BranchInfo.MainName) throw new TestDbException("Nhánh main đã tồn tại");
        string anchor = asOf is DateTime t ? $"TIMESTAMP '{t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}'" : "NOW";
        await RunOrThrowAsync($"CREATE DATABASE BRANCH {name} FROM {from} AS OF {anchor}", ct);
    }

    public override async Task DropBranchAsync(string db, string branch, CancellationToken ct = default)
    {
        RequireDb(db);
        Names.Validate(branch, "Tên nhánh");
        if (branch == BranchInfo.MainName) throw new TestDbException("Không thể xoá nhánh main");
        await RunOrThrowAsync($"DROP DATABASE BRANCH {branch}", ct);
    }

    public override async Task<string> MergeBranchAsync(string db, string branch, CancellationToken ct = default)
    {
        RequireDb(db);
        Names.Validate(branch, "Tên nhánh");
        if (branch == BranchInfo.MainName) throw new TestDbException("Hãy chọn một nhánh khác main");
        await RunOrThrowAsync($"MERGE BRANCH {branch} INTO main", ct);
        return $"Đã MERGE BRANCH {branch} INTO main (last-writer-wins, không phát hiện xung đột)";
    }

    private async Task RunOrThrowAsync(string sql, CancellationToken ct)
    {
        try
        {
            await RunAsync(sql, ct);
        }
        catch (SqlExecutionException ex)
        {
            throw new TestDbException(ex.Message);
        }
    }

    public override async Task<IReadOnlyList<TableInfo>> ListTablesAsync(string db, string branch, CancellationToken ct = default)
    {
        await using var session = await OpenSessionAsync(db, branch, ct);
        var r = await session.RunAsync(
            "SELECT table_name, table_type FROM information_schema.tables WHERE table_schema = 'public' ORDER BY table_name", int.MaxValue, ct);
        return r.Rows
            .Where(x => x[0] is string)
            .Select(x => new TableInfo((string)x[0]!, string.Equals(x[1] as string, "VIEW", StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    public override async Task<IReadOnlyList<ColumnInfo>> DescribeTableAsync(string db, string branch, string table, CancellationToken ct = default)
    {
        Names.Validate(table, "Tên bảng");
        await using var session = await OpenSessionAsync(db, branch, ct);
        var r = await session.RunAsync(
            "SELECT column_name, data_type, is_nullable, column_default FROM information_schema.columns " +
            $"WHERE table_schema = 'public' AND table_name = {PortableSql.Quote(table, Dialect)} ORDER BY ordinal_position",
            int.MaxValue, ct);
        return r.Rows
            .Select(x => new ColumnInfo(x[0] as string ?? "", x[1] as string ?? "", x[2] as string, null, x[3] as string, null))
            .ToList();
    }
}
