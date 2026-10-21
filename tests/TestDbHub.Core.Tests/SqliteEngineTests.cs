using TestDbHub.Core;
using TestDbHub.Core.Engines;
using TestDbHub.Core.Sessions;

namespace TestDbHub.Core.Tests;

/// <summary>The built-in engine needs no server, so these always run.</summary>
public sealed class SqliteEngineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "testdbhub-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly SqliteEngine _engine;

    public SqliteEngineTests() => _engine = new SqliteEngine(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private async Task<object?> ScalarAsync(string db, string branch, string sql)
    {
        var results = await _engine.QueryAsync(db, branch, sql);
        Assert.Null(results[^1].Error);
        return results[^1].Rows![0][0];
    }

    [Fact]
    public async Task BranchSnapshotMergeExport()
    {
        Assert.True((await _engine.GetStatusAsync()).Ok);
        await _engine.CreateDatabaseAsync("shop");
        await Assert.ThrowsAsync<TestDbException>(() => _engine.CreateDatabaseAsync("shop"));
        Assert.Equal(["shop"], (await _engine.ListDatabasesAsync()).Select(d => d.Name));

        var sample = await _engine.QueryAsync("shop", "main", SampleData.Sql);
        Assert.All(sample, r => Assert.Null(r.Error));
        Assert.Equal(4, sample[^1].RowCount);
        Assert.Equal(["customers", "orders"], (await _engine.ListTablesAsync("shop", "main")).Select(t => t.Name));

        await _engine.CreateBranchAsync("shop", "dev");
        Assert.All(await _engine.QueryAsync("shop", "dev", "UPDATE orders SET total = total * 2"), r => Assert.Null(r.Error));
        Assert.Equal(3730000m, Convert.ToDecimal(await ScalarAsync("shop", "dev", "SELECT SUM(total) FROM orders")));
        Assert.Equal(1865000m, Convert.ToDecimal(await ScalarAsync("shop", "main", "SELECT SUM(total) FROM orders")));

        var snap = await _engine.CreateSnapshotAsync("shop");
        var branches = await _engine.ListBranchesAsync("shop");
        Assert.Equal(["main", "dev", snap.Name], branches.Select(b => b.Name));
        Assert.Equal(BranchKind.Snapshot, branches[2].Kind);
        Assert.Equal(2, (await _engine.ListDatabasesAsync())[0].BranchCount);

        Assert.Contains("Đã ghi đè", await _engine.MergeBranchAsync("shop", "dev"));
        Assert.Equal(3730000m, Convert.ToDecimal(await ScalarAsync("shop", "main", "SELECT SUM(total) FROM orders")));
        await _engine.MergeBranchAsync("shop", snap.Name);
        Assert.Equal(1865000m, Convert.ToDecimal(await ScalarAsync("shop", "main", "SELECT SUM(total) FROM orders")));

        var columns = await _engine.DescribeTableAsync("shop", "main", "orders");
        Assert.Equal(["id", "customer_id", "total", "created_at"], columns.Select(c => c.Name));
        Assert.Equal("PRI", columns[0].Key);

        var dump = await _engine.ExportSqlAsync("shop", "main");
        Assert.Contains("CREATE TABLE customers", dump);
        Assert.Contains("'Nguyễn Văn An'", dump);

        // The dump replays into a fresh database.
        await _engine.CreateDatabaseAsync("restored");
        Assert.All(await _engine.QueryAsync("restored", "main", dump), r => Assert.Null(r.Error));
        Assert.Equal(1865000m, Convert.ToDecimal(await ScalarAsync("restored", "main", "SELECT SUM(total) FROM orders")));

        await _engine.DropBranchAsync("shop", "dev");
        Assert.DoesNotContain(await _engine.ListBranchesAsync("shop"), b => b.Name == "dev");

        var errors = await _engine.QueryAsync("shop", "main", "SELECT 1; SELECT * FROM missing_table; SELECT 2");
        Assert.Equal(2, errors.Count);
        Assert.Contains("missing_table", errors[1].Error);

        await _engine.DropDatabaseAsync("shop");
        Assert.Equal(["restored"], (await _engine.ListDatabasesAsync()).Select(d => d.Name));
    }

    [Fact]
    public async Task CopiesTablesBetweenDatabasesAndKeepsDefinition()
    {
        await _engine.CreateDatabaseAsync("a");
        await _engine.CreateDatabaseAsync("b");
        await _engine.QueryAsync("a", "main", SampleData.Sql);
        var copied = await CopyService.CopyTablesAsync(_engine, new("local", "a", "main"), _engine, new("local", "b", "main"));
        Assert.Equal(["customers", "orders"], copied.Select(c => c.Table));
        Assert.Equal("Lê Minh Châu", await ScalarAsync("b", "main", "SELECT name FROM customers WHERE id = 3"));
        Assert.Equal("PRI", (await _engine.DescribeTableAsync("b", "main", "customers"))[0].Key);
        await Assert.ThrowsAsync<TestDbException>(() =>
            CopyService.CopyTablesAsync(_engine, new("local", "a", "main"), _engine, new("local", "a", "main")));
    }

    [Fact]
    public async Task RejectsUnknownOrInvalidNames()
    {
        await Assert.ThrowsAsync<TestDbException>(() => _engine.ListBranchesAsync("missing"));
        await Assert.ThrowsAsync<TestDbException>(() => _engine.CreateDatabaseAsync("../evil"));
        await _engine.CreateDatabaseAsync("x");
        await Assert.ThrowsAsync<TestDbException>(() => _engine.QueryAsync("x", "nope", "SELECT 1"));
        await Assert.ThrowsAsync<TestDbException>(() => _engine.CreateBranchAsync("x", "main"));
        await Assert.ThrowsAsync<TestDbException>(() => _engine.DropBranchAsync("x", "main"));
    }

    [Theory]
    [InlineData("INT", PortableType.Int)]
    [InlineData("INTEGER", PortableType.BigInt)]
    [InlineData("VARCHAR(100)", PortableType.Text)]
    [InlineData("DATETIME", PortableType.DateTime)]
    [InlineData("DATE", PortableType.Date)]
    [InlineData("BLOB", PortableType.Binary)]
    [InlineData("BOOLEAN", PortableType.Boolean)]
    public void MapsDeclaredTypes(string declared, PortableType expected) =>
        Assert.Equal(expected, SqliteSession.MapDeclared("c", declared, null).Type);

    [Fact]
    public void ReadsDecimalPrecisionFromDeclaredType()
    {
        var col = SqliteSession.MapDeclared("c", "DECIMAL(10,2)", null);
        Assert.Equal((PortableType.Decimal, 10, 2), (col.Type, col.Precision, col.Scale));
    }
}
