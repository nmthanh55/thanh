namespace TestDbHub.Core.Sessions;

/// <summary>Result of one statement as returned by a session.</summary>
internal sealed record RawResult(IReadOnlyList<PortableColumn>? Columns, List<object?[]> Rows, long RowCount, long AffectedRows);

/// <summary>
/// One server connection, already pointed at a database/branch. Runs a single
/// statement at a time; SQL errors surface as <see cref="SqlExecutionException"/>.
/// </summary>
internal interface ISqlSession : IAsyncDisposable
{
    /// <summary>Run one statement, keeping at most <paramref name="maxRows"/> rows (all rows are still counted).</summary>
    Task<RawResult> RunAsync(string sql, int maxRows = int.MaxValue, CancellationToken ct = default);
}
