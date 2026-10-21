using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using TestDbHub.Core;

namespace TestDbHub.Wpf.Views;

/// <summary>
/// Turns the official MySQL ZIP into a local test server without installing
/// anything: unpack, start/stop, load a .sql backup, copy the connection string.
/// </summary>
public sealed class LocalMySqlWindow : Window
{
    private const string DownloadUrl = "https://dev.mysql.com/downloads/mysql/";

    private readonly LocalMySql _mysql;
    private readonly Func<Task> _onStarted;
    private readonly TextBlock _status = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _detail = new() { Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10) };
    private readonly TextBox _connection = new() { IsReadOnly = true, Padding = new Thickness(4, 3, 4, 3), FontFamily = new FontFamily("Consolas") };
    private readonly Button _install = Btn("Chọn file ZIP MySQL...");
    private readonly Button _start = Btn("▶ Bật");
    private readonly Button _stop = Btn("■ Tắt");
    private readonly Button _import = Btn("Nạp file .sql...");
    private readonly Button _folder = Btn("Mở thư mục");
    private bool _busy;

    public LocalMySqlWindow(LocalMySql mysql, Func<Task> onStarted)
    {
        _mysql = mysql;
        _onStarted = onStarted;
        Title = "MySQL nội bộ (không cần cài đặt)";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Owner = Application.Current.MainWindow;

        _install.Click += async (_, _) => await InstallAsync();
        _start.Click += async (_, _) => await RunAsync("Đang bật MySQL...", async () =>
        {
            await _mysql.StartAsync();
            await _onStarted();
        });
        _stop.Click += async (_, _) => await RunAsync("Đang tắt MySQL...", () => _mysql.StopAsync());
        _import.Click += async (_, _) => await ImportAsync();
        _folder.Click += (_, _) =>
        {
            Directory.CreateDirectory(_mysql.RootFolder);
            Process.Start(new ProcessStartInfo(_mysql.RootFolder) { UseShellExecute = true });
        };
        var copy = new Button { Content = "Chép", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0) };
        copy.Click += (_, _) => Clipboard.SetText(_connection.Text);

        var connectionRow = new DockPanel();
        DockPanel.SetDock(copy, Dock.Right);
        connectionRow.Children.Add(copy);
        connectionRow.Children.Add(_connection);

        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        foreach (var b in new[] { _install, _start, _stop, _import, _folder }) buttons.Children.Add(b);
        var close = new Button { Content = "Đóng", IsCancel = true, MinWidth = 90, Padding = new Thickness(10, 4, 10, 4), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(_status);
        root.Children.Add(_detail);
        root.Children.Add(new TextBlock { Text = "Chuỗi kết nối cho web (appsettings.json):", Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 3) });
        root.Children.Add(connectionRow);
        root.Children.Add(buttons);
        root.Children.Add(Note(
            "Đây là MySQL thật chạy từ bản ZIP: không cài đặt, không cần quyền admin, chỉ nhận kết nối từ chính máy này (127.0.0.1), "
            + "tài khoản root không mật khẩu — chỉ dùng để test. Nên chọn cùng bản MySQL với server để code chạy giống hệt khi đưa lên server.\n"
            + $"Tải bản ZIP: {DownloadUrl} → Microsoft Windows → \"Windows (x86, 64-bit), ZIP Archive\". "
            + "MySQL vẫn chạy khi đóng TestDB Hub; bấm Tắt để dừng."));
        root.Children.Add(close);
        Content = root;
        Loaded += async (_, _) => await RefreshAsync();
    }

    private static Button Btn(string text) => new() { Content = text, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 6) };

    private static Border Note(string text) => new()
    {
        Background = new SolidColorBrush(Color.FromRgb(0xF1, 0xF3, 0xF6)),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(8),
        Margin = new Thickness(0, 8, 0, 0),
        Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, FontSize = 12 },
    };

    private async Task RefreshAsync()
    {
        var version = await _mysql.GetRunningVersionAsync();
        _status.Text = version is not null ? $"● Đang chạy — MySQL {version}, cổng {_mysql.Port}"
            : _mysql.IsInstalled ? "○ Đang tắt"
            : "Chưa có MySQL nội bộ — chọn file ZIP MySQL đã tải về";
        _status.Foreground = version is not null ? Brushes.ForestGreen : Brushes.Black;
        _detail.Text = $"Thư mục: {_mysql.RootFolder}" + (_mysql.IsInitialized ? "" : _mysql.IsInstalled ? " · dữ liệu sẽ được khởi tạo khi bật lần đầu" : "");
        _connection.Text = _mysql.ConnectionString("ten_database");
        _install.IsEnabled = !_busy && !_mysql.IsInstalled;
        _start.IsEnabled = !_busy && _mysql.IsInstalled && version is null;
        _stop.IsEnabled = !_busy && version is not null;
        _import.IsEnabled = !_busy && version is not null;
    }

    private async Task RunAsync(string status, Func<Task> action)
    {
        _busy = true;
        _status.Text = status;
        _status.Foreground = Brushes.Black;
        foreach (var b in new[] { _install, _start, _stop, _import }) b.IsEnabled = false;
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            MessageBox.Show(this, ex.Message, "MySQL nội bộ", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _busy = false;
            await RefreshAsync();
        }
    }

    private async Task InstallAsync()
    {
        var dlg = new OpenFileDialog { Filter = "MySQL ZIP (*.zip)|*.zip", Title = "Chọn file MySQL ZIP (Windows x86, 64-bit)" };
        if (dlg.ShowDialog(this) != true) return;
        await RunAsync("Đang giải nén MySQL (khoảng 1 phút)...", () => _mysql.InstallFromZipAsync(dlg.FileName));
    }

    private async Task ImportAsync()
    {
        var dlg = new OpenFileDialog { Filter = "SQL (*.sql)|*.sql|Tất cả (*.*)|*.*", Title = "Chọn file .sql (vd bản backup mysqldump)" };
        if (dlg.ShowDialog(this) != true) return;
        var name = Path.GetFileName(dlg.FileName);
        await RunAsync($"Đang nạp {name}... (file lớn có thể mất vài phút)", async () =>
        {
            await _mysql.ImportSqlAsync(dlg.FileName);
            await _onStarted();
            MessageBox.Show(this, $"Đã nạp xong {name}.", "MySQL nội bộ", MessageBoxButton.OK, MessageBoxImage.Information);
        });
    }
}
