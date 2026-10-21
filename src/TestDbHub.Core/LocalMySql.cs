using System.Diagnostics;
using System.IO.Compression;
using MySqlConnector;

namespace TestDbHub.Core;

/// <summary>
/// A real MySQL server run from the official ZIP archive without installing it
/// (no Windows service, no admin rights): binaries in <c>server\</c>, data in
/// <c>data\</c> under <see cref="RootFolder"/>. Using real MySQL, ideally the
/// same version as production, keeps local tests behaving like the server.
/// It listens on 127.0.0.1 only, with user root and no password.
/// </summary>
public sealed class LocalMySql(string rootFolder, int port = LocalMySql.DefaultPort)
{
    public const int DefaultPort = 3306;

    // Windows "STATUS_DLL_NOT_FOUND": the Visual C++ runtime MySQL needs is missing.
    private const int DllNotFoundExitCode = unchecked((int)0xC0000135);

    public static string DefaultFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TestDbHub", "mysql");

    public string RootFolder { get; } = string.IsNullOrWhiteSpace(rootFolder) ? DefaultFolder : rootFolder;
    public int Port => port;
    public string ServerFolder => Path.Combine(RootFolder, "server");
    public string DataFolder => Path.Combine(RootFolder, "data");
    public string LogFile => Path.Combine(RootFolder, "mysqld.log");

    /// <summary>Use MySQL binaries from another folder (tests use the system packages).</summary>
    public string? BinFolder { get; init; }

    /// <summary>Extra mysqld arguments (on Linux tests: --user, --socket).</summary>
    public string ExtraServerArgs { get; init; } = "";

    private string Bin => BinFolder ?? Path.Combine(ServerFolder, "bin");

    private string Tool(string name)
    {
        string exe = OperatingSystem.IsWindows() ? name + ".exe" : name;
        string path = Path.Combine(Bin, exe);
        // Linux packages keep mysqld in sbin next to bin.
        string sbin = Path.Combine(Path.GetDirectoryName(Bin) ?? Bin, "sbin", exe);
        return !File.Exists(path) && File.Exists(sbin) ? sbin : path;
    }

    public bool IsInstalled => File.Exists(Tool("mysqld"));
    public bool IsInitialized => Directory.Exists(Path.Combine(DataFolder, "mysql"));

    /// <summary>Connection string for apps (e.g. a web portal's appsettings.json).</summary>
    public string ConnectionString(string? database = null) => new MySqlConnectionStringBuilder
    {
        Server = "127.0.0.1",
        Port = (uint)port,
        UserID = "root",
        Password = "",
        Database = database ?? "",
    }.ConnectionString;

    private string AdminConnectionString => new MySqlConnectionStringBuilder(ConnectionString()) { Pooling = false, ConnectionTimeout = 2 }.ConnectionString;

    /// <summary>Server version if a MySQL server answers on the port, otherwise null.</summary>
    public async Task<string?> GetRunningVersionAsync(CancellationToken ct = default)
    {
        try
        {
            await using var conn = new MySqlConnection(AdminConnectionString);
            await conn.OpenAsync(ct);
            return conn.ServerVersion;
        }
        catch (MySqlException)
        {
            return null;
        }
    }

    /// <summary>Unpack the MySQL "Windows (x86, 64-bit), ZIP Archive" into <see cref="ServerFolder"/>.</summary>
    public async Task InstallFromZipAsync(string zipPath, CancellationToken ct = default)
    {
        if (IsInstalled) throw new TestDbException($"MySQL nội bộ đã có sẵn ở {ServerFolder}");
        Directory.CreateDirectory(RootFolder);
        var temp = Path.Combine(RootFolder, "giai-nen-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, temp), ct);
            // The archive holds one top folder, e.g. mysql-8.4.6-winx64.
            var top = Directory.GetFiles(temp).Length == 0 && Directory.GetDirectories(temp) is [var only] ? only : temp;
            if (!File.Exists(Path.Combine(top, "bin", OperatingSystem.IsWindows() ? "mysqld.exe" : "mysqld")))
                throw new TestDbException("File ZIP này không phải bản MySQL cho Windows (không thấy bin\\mysqld.exe)");
            Directory.Move(top, ServerFolder);
        }
        catch (InvalidDataException)
        {
            throw new TestDbException("Không đọc được file ZIP — file tải về có thể bị lỗi, hãy tải lại");
        }
        finally
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }
    }

    private string BaseArgs() =>
        $"--no-defaults --basedir=\"{Path.GetDirectoryName(Bin)}\" --datadir=\"{DataFolder}\" --log-error=\"{LogFile}\" {ExtraServerArgs}".TrimEnd();

    /// <summary>Create the data folder (root user, empty password) the first time.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (IsInitialized) return;
        RequireInstalled();
        // A data folder without the mysql schema is a failed earlier attempt: it holds no user data.
        if (Directory.Exists(DataFolder)) Directory.Delete(DataFolder, recursive: true);
        using var proc = StartProcess(Tool("mysqld"), $"{BaseArgs()} --initialize-insecure");
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0 || !IsInitialized) throw new TestDbException(StartFailure("Khởi tạo dữ liệu MySQL lỗi", proc.ExitCode));
    }

    /// <summary>Start the server in the background and wait until it accepts connections. Returns its version.</summary>
    public async Task<string> StartAsync(CancellationToken ct = default)
    {
        if (await GetRunningVersionAsync(ct) is { } running) return running;
        RequireInstalled();
        await InitializeAsync(ct);
        // The server keeps running after TestDB Hub closes, so a web app can go on using it.
        using var proc = StartProcess(Tool("mysqld"), $"{BaseArgs()} --port={port} --bind-address=127.0.0.1 --mysqlx=OFF");
        for (int i = 0; i < 120; i++)
        {
            if (proc.HasExited) throw new TestDbException(StartFailure("MySQL tắt ngay khi khởi động", proc.ExitCode));
            if (await GetRunningVersionAsync(ct) is { } version) return version;
            await Task.Delay(500, ct);
        }
        throw new TestDbException($"MySQL chưa sẵn sàng sau 60 giây. Xem nhật ký: {LogFile}");
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        try
        {
            await using var conn = new MySqlConnection(AdminConnectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new MySqlCommand("SHUTDOWN", conn);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (MySqlException)
        {
            return; // not running
        }
        for (int i = 0; i < 60 && await GetRunningVersionAsync(ct) is not null; i++) await Task.Delay(500, ct);
    }

    /// <summary>Load a .sql file (e.g. a mysqldump backup) with the mysql client, so DELIMITER blocks work too.</summary>
    public async Task ImportSqlAsync(string sqlFile, CancellationToken ct = default)
    {
        RequireInstalled();
        var psi = new ProcessStartInfo(Tool("mysql"),
            $"--no-defaults --protocol=TCP --host=127.0.0.1 --port={port} --user=root --default-character-set=utf8mb4")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
        };
        using var proc = Process.Start(psi) ?? throw new TestDbException("Không chạy được mysql");
        var errors = proc.StandardError.ReadToEndAsync(ct);
        await using (var input = File.OpenRead(sqlFile))
        {
            await input.CopyToAsync(proc.StandardInput.BaseStream, ct);
        }
        proc.StandardInput.Close();
        await proc.WaitForExitAsync(ct);
        var message = (await errors).Trim();
        if (proc.ExitCode != 0) throw new TestDbException("Nạp file SQL lỗi: " + (message.Length > 0 ? message : $"mã {proc.ExitCode}"));
    }

    private void RequireInstalled()
    {
        if (!IsInstalled) throw new TestDbException("Chưa có MySQL nội bộ — chọn file ZIP MySQL để giải nén trước");
    }

    private static Process StartProcess(string exe, string args) =>
        Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true })
        ?? throw new TestDbException($"Không chạy được {exe}");

    private string StartFailure(string what, int exitCode) => exitCode == DllNotFoundExitCode
        ? $"{what}: máy thiếu Microsoft Visual C++ Redistributable (VCRUNTIME140.dll). Chép vcruntime140.dll, vcruntime140_1.dll, msvcp140.dll "
          + $"từ C:\\Windows\\System32 của một máy khác vào {Bin}, hoặc nhờ IT cài 'Visual C++ Redistributable x64'."
        : $"{what} (mã {exitCode}). Xem nhật ký: {LogFile}";
}
