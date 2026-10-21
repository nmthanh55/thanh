using System.Text.Json;
using Microsoft.Data.Sqlite;
using TestDbHub.Core.Sessions;

namespace TestDbHub.Core.Engines;

/// <summary>
/// Built-in storage that needs no server: every database is a folder under
/// <see cref="RootFolder"/> and every branch or snapshot is its own SQLite
/// file (<c>main.sqlite</c>, <c>dev.sqlite</c>, ...). Branches are made with
/// SQLite's online backup API, so each copy is consistent; "merge" copies a
/// branch back over main, like the MySQL engine.
/// </summary>
public sealed class SqliteEngine : DbEngine
{
    private const string FileExtension = ".sqlite";
    private const string MetaFile = "branches.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public SqliteEngine(string rootFolder)
    {
        RootFolder = string.IsNullOrWhiteSpace(rootFolder) ? DefaultFolder : rootFolder;
    }

    public static string DefaultFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TestDbHub", "databases");

    public string RootFolder { get; }

    public override string Id => "local";
    public override string Label => "Tích hợp (SQLite)";
    public override EngineFeatures Features { get; } = new(MultipleDatabases: true, Snapshots: true, TimeTravel: false, NativeMerge: false);
    public override SqlDialect Dialect => SqlDialect.Sqlite;

    public override string Ident(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    private sealed record BranchMeta(string Name, string Kind, string Parent, DateTime CreatedAt);

    private string DbFolder(string db) => Path.Combine(RootFolder, Names.Validate(db, "Tên database"));

    private string BranchFile(string db, string branch) => Path.Combine(DbFolder(db), Names.Validate(branch, "Tên nhánh") + FileExtension);

    private static SqliteConnection Connection(string file, SqliteOpenMode mode) =>
        // No pooling, so files are released as soon as a session ends and can be deleted.
        new(new SqliteConnectionStringBuilder { DataSource = file, Mode = mode, Pooling = false }.ConnectionString);

    private void RequireDb(string db)
    {
        if (!File.Exists(BranchFile(db, BranchInfo.MainName))) throw new TestDbException($"Không tìm thấy database {db}");
    }

    private string RequireBranchFile(string db, string branch)
    {
        RequireDb(db);
        var file = BranchFile(db, branch);
        return File.Exists(file) ? file : throw new TestDbException($"Không tìm thấy nhánh {branch} của {db}");
    }

    private List<BranchMeta> ReadMeta(string db)
    {
        var path = Path.Combine(DbFolder(db), MetaFile);
        if (!File.Exists(path)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<BranchMeta>>(File.ReadAllText(path)) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void WriteMeta(string db, List<BranchMeta> meta) =>
        File.WriteAllText(Path.Combine(DbFolder(db), MetaFile), JsonSerializer.Serialize(meta, JsonOptions));

    internal override async Task<ISqlSession> OpenSessionAsync(string db, string branch, CancellationToken ct)
    {
        var conn = Connection(RequireBranchFile(db, branch), SqliteOpenMode.ReadWrite);
        await conn.OpenAsync(ct);
        return new SqliteSession(conn);
    }

    public override async Task<EngineStatus> GetStatusAsync(CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(RootFolder);
            await using var conn = Connection(":memory:", SqliteOpenMode.Memory);
            await conn.OpenAsync(ct);
            return new EngineStatus(true, $"SQLite {conn.ServerVersion} · {RootFolder}", null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            return new EngineStatus(false, null, $"Không dùng được thư mục {RootFolder}: {ex.Message}");
        }
    }

    public override Task<IReadOnlyList<DatabaseInfo>> ListDatabasesAsync(CancellationToken ct = default)
    {
        IReadOnlyList<DatabaseInfo> result = !Directory.Exists(RootFolder) ? [] : Directory.GetDirectories(RootFolder)
            .Select(Path.GetFileName)
            .Where(name => Names.IsValid(name) && File.Exists(BranchFile(name!, BranchInfo.MainName)))
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(name => new DatabaseInfo(name!, ReadMeta(name!).Count))
            .ToList();
        return Task.FromResult(result);
    }

    public override async Task CreateDatabaseAsync(string name, CancellationToken ct = default)
    {
        var folder = DbFolder(name);
        if (Directory.Exists(folder)) throw new TestDbException($"Database {name} đã tồn tại");
        Directory.CreateDirectory(folder);
        await using var conn = Connection(BranchFile(name, BranchInfo.MainName), SqliteOpenMode.ReadWriteCreate);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version = 1";
        await cmd.ExecuteNonQueryAsync(ct);
        WriteMeta(name, []);
    }

    public override Task DropDatabaseAsync(string name, CancellationToken ct = default)
    {
        RequireDb(name);
        Directory.Delete(DbFolder(name), recursive: true);
        return Task.CompletedTask;
    }

    public override Task<IReadOnlyList<BranchInfo>> ListBranchesAsync(string db, CancellationToken ct = default)
    {
        RequireDb(db);
        var branches = ReadMeta(db)
            .Where(m => File.Exists(BranchFile(db, m.Name)))
            .OrderBy(m => m.CreatedAt)
            .Select(m => new BranchInfo(m.Name, m.Kind == "snapshot" ? BranchKind.Snapshot : BranchKind.Branch, m.Parent, m.CreatedAt));
        IReadOnlyList<BranchInfo> result = [new BranchInfo(BranchInfo.MainName, BranchKind.Main), .. branches];
        return Task.FromResult(result);
    }

    public override Task CreateBranchAsync(string db, string name, string from = BranchInfo.MainName, DateTime? asOf = null, CancellationToken ct = default)
    {
        if (asOf is not null) throw new TestDbException("Database tích hợp không hỗ trợ tạo nhánh từ thời điểm trong quá khứ — hãy dùng snapshot");
        return CreateBranchCoreAsync(db, name, from, "branch", ct);
    }

    public override async Task<BranchInfo> CreateSnapshotAsync(string db, CancellationToken ct = default)
    {
        var name = "snap_" + Names.Timestamp(DateTime.Now);
        await CreateBranchCoreAsync(db, name, BranchInfo.MainName, "snapshot", ct);
        return new BranchInfo(name, BranchKind.Snapshot, BranchInfo.MainName, DateTime.Now);
    }

    private async Task CreateBranchCoreAsync(string db, string name, string from, string kind, CancellationToken ct)
    {
        Names.Validate(name, "Tên nhánh");
        if (name == BranchInfo.MainName) throw new TestDbException("Nhánh main đã tồn tại");
        var source = RequireBranchFile(db, from);
        var target = BranchFile(db, name);
        if (File.Exists(target)) throw new TestDbException($"Nhánh {name} đã tồn tại");
        try
        {
            await CopyDatabaseFileAsync(source, target, ct);
        }
        catch
        {
            File.Delete(target);
            throw;
        }
        var meta = ReadMeta(db);
        meta.RemoveAll(m => m.Name == name);
        meta.Add(new BranchMeta(name, kind, from, DateTime.Now));
        WriteMeta(db, meta);
    }

    private static async Task CopyDatabaseFileAsync(string source, string target, CancellationToken ct)
    {
        await using var src = Connection(source, SqliteOpenMode.ReadOnly);
        await using var dst = Connection(target, SqliteOpenMode.ReadWriteCreate);
        await src.OpenAsync(ct);
        await dst.OpenAsync(ct);
        src.BackupDatabase(dst);
    }

    public override Task DropBranchAsync(string db, string branch, CancellationToken ct = default)
    {
        if (branch == BranchInfo.MainName) throw new TestDbException("Không thể xoá nhánh main");
        File.Delete(RequireBranchFile(db, branch));
        var meta = ReadMeta(db);
        meta.RemoveAll(m => m.Name == branch);
        WriteMeta(db, meta);
        return Task.CompletedTask;
    }

    /// <summary>Main is replaced by the branch's content (also used to restore a snapshot).</summary>
    public override async Task<string> MergeBranchAsync(string db, string branch, CancellationToken ct = default)
    {
        if (branch == BranchInfo.MainName) throw new TestDbException("Hãy chọn một nhánh khác main");
        await CopyDatabaseFileAsync(RequireBranchFile(db, branch), BranchFile(db, BranchInfo.MainName), ct);
        return $"Đã ghi đè main của {db} bằng nội dung nhánh {branch}";
    }

    public override async Task<IReadOnlyList<TableInfo>> ListTablesAsync(string db, string branch, CancellationToken ct = default)
    {
        await using var session = await OpenSessionAsync(db, branch, ct);
        var r = await session.RunAsync(
            "SELECT name, type FROM sqlite_master WHERE type IN ('table', 'view') AND name NOT LIKE 'sqlite_%' ORDER BY name", int.MaxValue, ct);
        return r.Rows.Select(x => new TableInfo((string)x[0]!, (string?)x[1] == "view")).ToList();
    }

    public override async Task<IReadOnlyList<ColumnInfo>> DescribeTableAsync(string db, string branch, string table, CancellationToken ct = default)
    {
        Names.Validate(table, "Tên bảng");
        await using var session = await OpenSessionAsync(db, branch, ct);
        // cid, name, type, notnull, dflt_value, pk
        var r = await session.RunAsync($"PRAGMA table_info({Ident(table)})", int.MaxValue, ct);
        return r.Rows.Select(x => new ColumnInfo(
            Convert.ToString(x[1]) ?? "",
            Convert.ToString(x[2]) ?? "",
            Convert.ToInt64(x[3] ?? 0L) == 0 ? "YES" : "NO",
            Convert.ToInt64(x[5] ?? 0L) > 0 ? "PRI" : null,
            Convert.ToString(x[4]),
            null)).ToList();
    }

    public override async Task<string> GetTableDdlAsync(string db, string branch, string table, IReadOnlyList<PortableColumn> columns, CancellationToken ct = default)
    {
        await using var session = await OpenSessionAsync(db, branch, ct);
        var r = await session.RunAsync($"SELECT sql FROM sqlite_master WHERE type = 'table' AND name = {PortableSql.Quote(table, Dialect)}", 1, ct);
        return r.Rows.FirstOrDefault()?[0] as string ?? CreateTableSql(table, columns);
    }
}
