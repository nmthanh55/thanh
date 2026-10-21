namespace TestDbHub.Core;

/// <summary>What an engine supports, so the UI can show the right actions.</summary>
public sealed record EngineFeatures(bool MultipleDatabases, bool Snapshots, bool TimeTravel, bool NativeMerge);

public sealed record EngineStatus(bool Ok, string? Version, string? Error);

public sealed record DatabaseInfo(string Name, int BranchCount);

public enum BranchKind { Main, Branch, Snapshot }

public sealed record BranchInfo(string Name, BranchKind Kind, string? Parent = null, DateTime? CreatedAt = null)
{
    public const string MainName = "main";
}

public sealed record TableInfo(string Name, bool IsView = false, long? ApproxRows = null);

public sealed record ColumnInfo(string Name, string Type, string? Nullable, string? Key, string? Default, string? Extra);

/// <summary>Where a table lives: engine id, database and branch.</summary>
public sealed record Location(string Engine, string Database, string Branch);

/// <summary>Outcome of one SQL statement.</summary>
public sealed class QueryResult
{
    public required string Sql { get; init; }
    public TimeSpan Elapsed { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<string>? Columns { get; init; }
    public IReadOnlyList<object?[]>? Rows { get; init; }
    public long RowCount { get; init; }
    public bool Truncated { get; init; }
    public long AffectedRows { get; init; }

    public bool IsError => Error is not null;
    public bool HasRows => Columns is not null;
}

/// <summary>Engine-neutral column type, used to recreate a table on another engine.</summary>
public enum PortableType { Text, Boolean, Int, BigInt, Decimal, Double, Date, DateTime, Time, Json, Binary, Uuid }

/// <summary>Column metadata used when recreating a table on another engine.</summary>
public sealed record PortableColumn(string Name, PortableType Type, int? Precision = null, int? Scale = null);

public sealed record TableData(IReadOnlyList<PortableColumn> Columns, IReadOnlyList<object?[]> Rows);

public sealed record CopiedTable(string Table, int Rows);

public sealed class ConnectionSettings
{
    // Built-in SQLite storage: nothing to install. Empty folder = default location.
    public bool LocalEnabled { get; set; } = true;
    public string LocalFolder { get; set; } = "";

    public bool MySqlEnabled { get; set; }
    public string MySqlHost { get; set; } = "127.0.0.1";
    public int MySqlPort { get; set; } = 3306;
    public string MySqlUser { get; set; } = "root";
    public string MySqlPassword { get; set; } = "";

    // HeliosDB-Nano is reached over its PostgreSQL wire protocol.
    public bool HeliosEnabled { get; set; }
    public string HeliosHost { get; set; } = "127.0.0.1";
    public int HeliosPort { get; set; } = 5432;
    public string HeliosUser { get; set; } = "postgres";
    public string HeliosPassword { get; set; } = "";
    public string HeliosDatabase { get; set; } = "heliosdb";
}

/// <summary>An error whose message is meant to be shown to the user as-is.</summary>
public class TestDbException(string message) : Exception(message);

/// <summary>The server rejected a SQL statement.</summary>
public sealed class SqlExecutionException(string message) : TestDbException(message);
