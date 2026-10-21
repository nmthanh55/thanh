using TestDbHub.Core;
using TestDbHub.Core.Engines;
using Xunit.Abstractions;

namespace TestDbHub.Core.Tests;

/// <summary>
/// Runs against real servers when configured through environment variables:
/// TESTDB_MYSQL_HOST / _PORT / _USER / _PASSWORD and
/// TESTDB_HELIOS_HOST / _PORT / _USER / _PASSWORD / _DATABASE.
/// Without them these tests pass without doing anything.
/// </summary>
public class IntegrationTests(ITestOutputHelper output)
{
    private static readonly string Suffix = Guid.NewGuid().ToString("N")[..8];

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name);

    private MySqlEngine? MySql()
    {
        var host = Env("TESTDB_MYSQL_HOST");
        if (host is null)
        {
            output.WriteLine("TESTDB_MYSQL_HOST chưa đặt — bỏ qua");
            return null;
        }
        return new MySqlEngine(host, int.Parse(Env("TESTDB_MYSQL_PORT") ?? "3306"), Env("TESTDB_MYSQL_USER") ?? "root", Env("TESTDB_MYSQL_PASSWORD") ?? "");
    }

    private HeliosEngine? Helios()
    {
        var host = Env("TESTDB_HELIOS_HOST");
        if (host is null)
        {
            output.WriteLine("TESTDB_HELIOS_HOST chưa đặt — bỏ qua");
            return null;
        }
        return new HeliosEngine(host, int.Parse(Env("TESTDB_HELIOS_PORT") ?? "5432"), Env("TESTDB_HELIOS_USER") ?? "postgres",
            Env("TESTDB_HELIOS_PASSWORD") ?? "", Env("TESTDB_HELIOS_DATABASE") ?? "heliosdb");
    }

    private static async Task<object?> ScalarAsync(DbEngine engine, string db, string branch, string sql)
    {
        var results = await engine.QueryAsync(db, branch, sql);
        var last = results[^1];
        Assert.Null(last.Error);
        return last.Rows![0][0];
    }

    private static async Task<List<int>> IdsAsync(DbEngine engine, string db, string branch, string table)
    {
        var results = await engine.QueryAsync(db, branch, $"SELECT id FROM {table} ORDER BY id");
        AssertNoErrors(results);
        return results[0].Rows!.Select(r => Convert.ToInt32(r[0])).ToList();
    }

    private static void AssertNoErrors(IReadOnlyList<QueryResult> results)
    {
        foreach (var r in results) Assert.True(r.Error is null, $"{r.Sql}: {r.Error}");
    }

    [Fact]
    public async Task MySql_BranchSnapshotMergeExport()
    {
        var engine = MySql();
        if (engine is null) return;
        var db = "it_" + Suffix;
        await engine.CreateDatabaseAsync(db);
        try
        {
            AssertNoErrors(await engine.QueryAsync(db, "main", SampleData.Sql));
            Assert.Contains(await engine.ListDatabasesAsync(), d => d.Name == db);

            await engine.CreateBranchAsync(db, "dev");
            AssertNoErrors(await engine.QueryAsync(db, "dev", "UPDATE orders SET total = total * 2"));
            Assert.Equal(3730000.00m, Convert.ToDecimal(await ScalarAsync(engine, db, "dev", "SELECT SUM(total) FROM orders")));
            Assert.Equal(1865000.00m, Convert.ToDecimal(await ScalarAsync(engine, db, "main", "SELECT SUM(total) FROM orders")));

            var snap = await engine.CreateSnapshotAsync(db);
            var branches = await engine.ListBranchesAsync(db);
            Assert.Contains(branches, b => b.Name == "dev" && b.Kind == BranchKind.Branch);
            Assert.Contains(branches, b => b.Name == snap.Name && b.Kind == BranchKind.Snapshot);
            Assert.DoesNotContain(await engine.ListDatabasesAsync(), d => d.Name.StartsWith(db + "__", StringComparison.Ordinal));

            await engine.MergeBranchAsync(db, "dev");
            Assert.Equal(3730000.00m, Convert.ToDecimal(await ScalarAsync(engine, db, "main", "SELECT SUM(total) FROM orders")));

            // Restore the snapshot taken before the merge.
            await engine.MergeBranchAsync(db, snap.Name);
            Assert.Equal(1865000.00m, Convert.ToDecimal(await ScalarAsync(engine, db, "main", "SELECT SUM(total) FROM orders")));

            var columns = await engine.DescribeTableAsync(db, "main", "orders");
            Assert.Contains(columns, c => c.Name == "id" && c.Key == "PRI");

            var dump = await engine.ExportSqlAsync(db, "main");
            Assert.Contains("CREATE TABLE `customers`", dump);
            Assert.Contains("'Nguyễn Văn An'", dump);

            await engine.DropBranchAsync(db, "dev");
            Assert.DoesNotContain(await engine.ListBranchesAsync(db), b => b.Name == "dev");

            var errors = await engine.QueryAsync(db, "main", "SELECT 1; SELECT * FROM missing_table; SELECT 2");
            Assert.Equal(2, errors.Count);
            Assert.NotNull(errors[1].Error);
        }
        finally
        {
            await engine.DropDatabaseAsync(db);
        }
        Assert.DoesNotContain(await engine.ListDatabasesAsync(), d => d.Name == db);
    }

    [Fact]
    public async Task Helios_BranchTimeTravelMerge()
    {
        var engine = Helios();
        if (engine is null) return;
        var db = engine.DatabaseName;
        var table = "it_items_" + Suffix;
        var branch = "it_br_" + Suffix;
        Assert.True((await engine.GetStatusAsync()).Ok);
        AssertNoErrors(await engine.QueryAsync(db, "main", $"CREATE TABLE {table} (id INT PRIMARY KEY, name TEXT); INSERT INTO {table} VALUES (1, 'một'), (2, 'hai')"));
        try
        {
            await Task.Delay(1500);
            var before = DateTime.Now;
            await Task.Delay(1500);
            await engine.CreateBranchAsync(db, branch);
            Assert.Contains(await engine.ListBranchesAsync(db), b => b.Name == branch && b.Parent == "main");
            Assert.Contains(await engine.ListTablesAsync(db, branch), t => t.Name == table);

            AssertNoErrors(await engine.QueryAsync(db, branch, $"INSERT INTO {table} VALUES (3, 'ba')"));
            Assert.Equal([1, 2, 3], await IdsAsync(engine, db, branch, table));
            Assert.Equal([1, 2], await IdsAsync(engine, db, "main", table));
            Assert.Equal([1, 2, 3], await IdsAsync(engine, db, branch, table));

            // Time-travel: rows inserted later are hidden and updated rows show their old value.
            // (Nano 4.41 does not bring back DELETEd rows, so that is not exercised here.)
            AssertNoErrors(await engine.QueryAsync(db, "main", $"UPDATE {table} SET name = 'MỘT' WHERE id = 1; INSERT INTO {table} VALUES (4, 'bốn')"));
            Assert.Equal([1, 2, 4], await IdsAsync(engine, db, "main", table));
            var past = await engine.QueryAsync(db, "main", $"SELECT id, name FROM {table} AS OF TIMESTAMP '{before:yyyy-MM-dd HH:mm:ss}' ORDER BY id");
            AssertNoErrors(past);
            Assert.Equal([1, 2], past[0].Rows!.Select(r => Convert.ToInt32(r[0])));
            Assert.Equal("một", past[0].Rows![0][1]);

            var columns = await engine.DescribeTableAsync(db, "main", table);
            Assert.Equal(["id", "name"], columns.Select(c => c.Name));

            await engine.MergeBranchAsync(db, branch);
            var ids = await IdsAsync(engine, db, "main", table);
            output.WriteLine("Sau merge: " + string.Join(",", ids));
            Assert.Contains(3, ids);
        }
        finally
        {
            if ((await engine.ListBranchesAsync(db)).Any(b => b.Name == branch)) await engine.DropBranchAsync(db, branch);
            await engine.QueryAsync(db, "main", $"DROP TABLE IF EXISTS {table}");
        }
        Assert.DoesNotContain(await engine.ListBranchesAsync(db), b => b.Name == branch);
    }

    [Fact]
    public async Task CopiesTablesBetweenMySqlAndHelios()
    {
        var mysql = MySql();
        var helios = Helios();
        if (mysql is null || helios is null) return;
        var srcDb = "it_src_" + Suffix;
        var backDb = "it_back_" + Suffix;
        var branch = "it_copy_" + Suffix;
        await mysql.CreateDatabaseAsync(srcDb);
        await mysql.CreateDatabaseAsync(backDb);
        await helios.CreateBranchAsync(helios.DatabaseName, branch);
        try
        {
            AssertNoErrors(await mysql.QueryAsync(srcDb, "main", SampleData.Sql + """
                ;
                CREATE TABLE mixed (id BIGINT, ok BOOLEAN, ratio DOUBLE, born DATE, seen DATETIME(3), note TEXT, meta JSON);
                INSERT INTO mixed VALUES (1, TRUE, 0.25, '2000-02-29', '2026-10-21 08:09:10.123', 'O''Brien \\ path', '{"vip": true}'),
                                         (2, FALSE, NULL, NULL, NULL, NULL, NULL);
                """));

            var to = new Location(helios.Id, helios.DatabaseName, branch);
            var copied = await CopyService.CopyTablesAsync(mysql, new Location(mysql.Id, srcDb, "main"), helios, to);
            Assert.Equal(["customers", "mixed", "orders"], copied.Select(c => c.Table).Order());

            Assert.Equal(1865000.00m, Convert.ToDecimal(await ScalarAsync(helios, to.Database, branch, "SELECT SUM(total) FROM orders")));
            Assert.Equal("Lê Minh Châu", await ScalarAsync(helios, to.Database, branch, "SELECT name FROM customers WHERE id = 3"));
            Assert.Equal(@"O'Brien \ path", await ScalarAsync(helios, to.Database, branch, "SELECT note FROM mixed WHERE id = 1"));
            // Nano shares table definitions between branches, but the rows stay in the branch.
            var mainRows = await helios.QueryAsync(helios.DatabaseName, "main", "SELECT * FROM mixed");
            Assert.True(mainRows[0].IsError || mainRows[0].RowCount == 0);

            var back = await CopyService.CopyTablesAsync(helios, to, mysql, new Location(mysql.Id, backDb, "main"), ["orders", "mixed"]);
            Assert.Equal(2, back.Count);
            Assert.Equal(1865000.00m, Convert.ToDecimal(await ScalarAsync(mysql, backDb, "main", "SELECT SUM(total) FROM orders")));
            Assert.Equal("2026-01-05", Convert.ToDateTime(await ScalarAsync(mysql, backDb, "main", "SELECT created_at FROM orders WHERE id = 1")).ToString("yyyy-MM-dd"));
            Assert.Equal(@"O'Brien \ path", await ScalarAsync(mysql, backDb, "main", "SELECT note FROM mixed WHERE id = 1"));
            Assert.Equal(2L, Convert.ToInt64(await ScalarAsync(mysql, backDb, "main", "SELECT COUNT(*) FROM mixed")));

            var dump = await helios.ExportSqlAsync(helios.DatabaseName, branch);
            Assert.Contains("CREATE TABLE \"orders\"", dump);
        }
        finally
        {
            await helios.DropBranchAsync(helios.DatabaseName, branch);
            await mysql.DropDatabaseAsync(srcDb);
            await mysql.DropDatabaseAsync(backDb);
        }
    }
}
