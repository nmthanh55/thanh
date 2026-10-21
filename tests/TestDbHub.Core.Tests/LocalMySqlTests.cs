using System.IO.Compression;
using TestDbHub.Core;
using TestDbHub.Core.Engines;
using Xunit.Abstractions;

namespace TestDbHub.Core.Tests;

public sealed class LocalMySqlTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "testdbhub-mysql-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task InstallsFromZipWithOneTopFolder()
    {
        var zip = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                archive.CreateEntry($"mysql-8.4.6-winx64/bin/{(OperatingSystem.IsWindows() ? "mysqld.exe" : "mysqld")}");
                archive.CreateEntry("mysql-8.4.6-winx64/share/README");
            }
            var local = new LocalMySql(_root);
            Assert.False(local.IsInstalled);
            await local.InstallFromZipAsync(zip);
            Assert.True(local.IsInstalled);
            Assert.True(File.Exists(Path.Combine(local.ServerFolder, "share", "README")));
            Assert.Equal(["server"], Directory.GetDirectories(_root).Select(Path.GetFileName));
            await Assert.ThrowsAsync<TestDbException>(() => local.InstallFromZipAsync(zip));
        }
        finally
        {
            File.Delete(zip);
        }
    }

    [Fact]
    public async Task RejectsZipWithoutMysqld()
    {
        var zip = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create)) archive.CreateEntry("something/readme.txt");
            var local = new LocalMySql(_root);
            await Assert.ThrowsAsync<TestDbException>(() => local.InstallFromZipAsync(zip));
            Assert.False(local.IsInstalled);
            Assert.Empty(Directory.GetDirectories(_root));
        }
        finally
        {
            File.Delete(zip);
        }
    }

    /// <summary>Runs a real mysqld when TESTDB_MYSQLD_BIN points at a folder with mysqld and mysql (e.g. /usr/bin).</summary>
    [Fact]
    public async Task StartsImportsStopsAndKeepsData()
    {
        var bin = Environment.GetEnvironmentVariable("TESTDB_MYSQLD_BIN");
        if (bin is null)
        {
            output.WriteLine("TESTDB_MYSQLD_BIN chưa đặt — bỏ qua");
            return;
        }
        Directory.CreateDirectory(_root);
        const int port = 3399;
        var local = new LocalMySql(_root, port)
        {
            BinFolder = bin,
            ExtraServerArgs = OperatingSystem.IsWindows() ? "" : $"--user=root --socket=\"{Path.Combine(_root, "mysqld.sock")}\" --secure-file-priv=\"\"",
        };
        Assert.True(local.IsInstalled);
        Assert.Null(await local.GetRunningVersionAsync());
        try
        {
            var version = await local.StartAsync();
            output.WriteLine("MySQL " + version);
            Assert.True(local.IsInitialized);
            Assert.Equal(version, await local.StartAsync()); // already running

            var dump = Path.Combine(_root, "dump.sql");
            await File.WriteAllTextAsync(dump, """
                CREATE DATABASE portal_test CHARACTER SET utf8mb4;
                USE portal_test;
                CREATE TABLE users (id INT PRIMARY KEY, name VARCHAR(100));
                INSERT INTO users VALUES (1, 'Võ Kiều Vân'), (2, 'O''Brien');
                DELIMITER ;;
                CREATE PROCEDURE count_users() BEGIN SELECT COUNT(*) FROM users; END;;
                DELIMITER ;
                """);
            await local.ImportSqlAsync(dump);

            var engine = new MySqlEngine("127.0.0.1", port, "root", "");
            Assert.Contains(await engine.ListDatabasesAsync(), d => d.Name == "portal_test");
            var rows = await engine.QueryAsync("portal_test", "main", "CALL count_users(); SELECT name FROM users WHERE id = 1");
            Assert.All(rows, r => Assert.Null(r.Error));
            Assert.Equal("Võ Kiều Vân", rows[^1].Rows![0][0]);

            var snap = await engine.CreateSnapshotAsync("portal_test");
            await engine.QueryAsync("portal_test", "main", "DELETE FROM users");
            await engine.MergeBranchAsync("portal_test", snap.Name);
            Assert.Equal(2L, Convert.ToInt64((await engine.QueryAsync("portal_test", "main", "SELECT COUNT(*) FROM users"))[0].Rows![0][0]));

            var bad = Path.Combine(_root, "bad.sql");
            await File.WriteAllTextAsync(bad, "SELECT * FROM portal_test.missing_table;");
            var ex = await Assert.ThrowsAsync<TestDbException>(() => local.ImportSqlAsync(bad));
            Assert.Contains("missing_table", ex.Message);

            await local.StopAsync();
            Assert.Null(await local.GetRunningVersionAsync());

            await local.StartAsync();
            Assert.Contains(await engine.ListDatabasesAsync(), d => d.Name == "portal_test");
        }
        finally
        {
            await local.StopAsync();
        }
    }
}
