using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TestDbHub.Core;
using TestDbHub.Core.Engines;
using TestDbHub.Wpf.Services;
using TestDbHub.Wpf.Views;

namespace TestDbHub.Wpf.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private ConnectionSettings _settings = SettingsStore.Load();
    private IReadOnlyList<DbEngine> _engines = [];

    public ObservableCollection<EngineNode> Engines { get; } = [];
    public ObservableCollection<TableInfo> Tables { get; } = [];
    public ObservableCollection<ResultViewModel> Results { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasContext), nameof(NoContext), nameof(ContextEngine), nameof(ContextPath), nameof(ContextBrush),
        nameof(CanSnapshot), nameof(CanTimeTravel), nameof(CanMerge), nameof(MergeLabel), nameof(CanDropBranch), nameof(CanDropDatabase))]
    private BranchNode? _current;

    [ObservableProperty]
    private ResultViewModel? _selectedResult;

    [ObservableProperty]
    private string _sqlText = "";

    [ObservableProperty]
    private string _statusText = "Sẵn sàng";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _tablesHint = "";

    public bool HasContext => Current is not null;
    public bool NoContext => Current is null;
    public string ContextEngine => Current?.Engine.Label ?? "";
    public string ContextPath => Current is null ? "" : $"{Current.Database}  ›  {Current.Icon} {Current.Info.Name}";
    public Brush ContextBrush => (Brush)Application.Current.Resources[Current?.Engine.Id == "helios" ? "HeliosBrush" : "MySqlBrush"];
    public bool CanSnapshot => Current is { IsMain: true } c && c.Engine.Features.Snapshots;
    public bool CanTimeTravel => Current?.Engine.Features.TimeTravel == true;
    public bool CanMerge => Current is { IsMain: false };
    public bool CanDropBranch => Current is { IsMain: false };
    public bool CanDropDatabase => Current is { IsMain: true } c && c.Engine.Features.MultipleDatabases;

    public string MergeLabel => Current switch
    {
        { Info.Kind: BranchKind.Snapshot } => "↺ Khôi phục vào main",
        { } c when c.Engine.Features.NativeMerge => "⤴ Merge vào main",
        _ => "⤴ Ghi đè main",
    };

    // ---------- Loading ----------

    [RelayCommand]
    private Task RefreshAsync() => BusyAsync("Đang tải danh sách database...", ReloadTreeAsync);

    private async Task ReloadTreeAsync()
    {
        var keep = Current is null ? null : new Location(Current.Engine.Id, Current.Database, Current.Info.Name);
        var expanded = Engines.SelectMany(e => e.Databases).Where(d => d.IsExpanded).Select(d => (d.EngineNode.Engine.Id, d.Info.Name)).ToHashSet();
        bool firstLoad = Engines.Count == 0;

        foreach (var old in _engines) await old.DisposeAsync();
        _engines = EngineFactory.Create(_settings);
        var nodes = new List<EngineNode>();
        foreach (var engine in _engines)
        {
            var status = await engine.GetStatusAsync();
            var node = new EngineNode(engine, status) { IsExpanded = true };
            if (status.Ok)
            {
                try
                {
                    foreach (var db in await engine.ListDatabasesAsync())
                    {
                        var dbNode = new DatabaseNode(node, db) { IsExpanded = firstLoad || expanded.Contains((engine.Id, db.Name)) };
                        foreach (var b in await engine.ListBranchesAsync(db.Name)) dbNode.Children.Add(new BranchNode(dbNode, b));
                        node.Children.Add(dbNode);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Keep the other engines usable; show the problem on this one.
                    node = new EngineNode(engine, new EngineStatus(false, null, ex.Message)) { IsExpanded = true };
                }
            }
            nodes.Add(node);
        }

        Engines.Clear();
        foreach (var n in nodes) Engines.Add(n);

        BranchNode? next = null;
        if (keep is not null)
        {
            var db = Engines.Where(e => e.Engine.Id == keep.Engine).SelectMany(e => e.Databases).FirstOrDefault(d => d.Info.Name == keep.Database);
            next = db?.Branches.FirstOrDefault(b => b.Info.Name == keep.Branch) ?? db?.Branches.FirstOrDefault();
        }
        await SelectAsync(next);
        int ok = Engines.Count(e => e.Status.Ok);
        StatusText = Engines.Count == 0 ? "Chưa bật engine nào — mở ⚙ Kết nối" : $"Đã kết nối {ok}/{Engines.Count} engine";
    }

    /// <summary>Called by the view when the tree selection changes.</summary>
    public async Task OnTreeSelectionAsync(TreeNode? node)
    {
        var branch = node switch
        {
            BranchNode b => b,
            DatabaseNode d => d.Branches.FirstOrDefault(),
            _ => null,
        };
        if (branch is not null && branch != Current) await SelectAsync(branch);
    }

    private async Task SelectAsync(BranchNode? branch)
    {
        Current = branch;
        if (branch is not null)
        {
            branch.DatabaseNode.IsExpanded = true;
            branch.IsSelected = true;
        }
        await LoadTablesAsync();
    }

    private async Task LoadTablesAsync()
    {
        Tables.Clear();
        var c = Current;
        if (c is null) return;
        TablesHint = "Đang tải...";
        try
        {
            var tables = await c.Engine.ListTablesAsync(c.Database, c.Info.Name);
            if (Current != c) return;
            foreach (var t in tables) Tables.Add(t);
            TablesHint = tables.Count == 0 ? "Chưa có bảng nào — bấm \"Dữ liệu mẫu\" rồi Chạy" : "";
        }
        catch (Exception ex)
        {
            TablesHint = "Không tải được bảng: " + ex.Message;
        }
    }

    // ---------- SQL ----------

    [RelayCommand]
    private async Task RunAsync()
    {
        var c = Current;
        if (c is null || string.IsNullOrWhiteSpace(SqlText)) return;
        var sql = SqlText;
        await BusyAsync("Đang chạy SQL...", async () =>
        {
            var started = DateTime.Now;
            var results = await c.Engine.QueryAsync(c.Database, c.Info.Name, sql);
            ShowResults(results.Select((r, i) => ResultViewModel.From(r, i + 1)));
            var errors = results.Count(r => r.IsError);
            StatusText = $"Chạy {results.Count} câu lệnh trong {(DateTime.Now - started).TotalMilliseconds:0} ms" + (errors > 0 ? " — có lỗi" : "");
            if (results.Any(r => IsDdl(r.Sql))) await LoadTablesAsync();
        });
    }

    private static bool IsDdl(string sql)
    {
        var first = sql.TrimStart().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return first.ToUpperInvariant() is "CREATE" or "DROP" or "ALTER" or "RENAME" or "TRUNCATE";
    }

    private void ShowResults(IEnumerable<ResultViewModel> results)
    {
        Results.Clear();
        foreach (var r in results) Results.Add(r);
        SelectedResult = Results.FirstOrDefault(r => r.IsError) ?? Results.LastOrDefault(r => r.HasGrid) ?? Results.LastOrDefault();
    }

    [RelayCommand]
    private void LoadSample() => SqlText = SampleData.Sql;

    [RelayCommand]
    private void OpenSqlFile()
    {
        var dlg = new OpenFileDialog { Filter = "SQL (*.sql)|*.sql|Tất cả (*.*)|*.*", Title = "Mở file SQL" };
        if (dlg.ShowDialog() != true) return;
        SqlText = File.ReadAllText(dlg.FileName);
        StatusText = $"Đã mở {Path.GetFileName(dlg.FileName)} — bấm Chạy để nạp vào nhánh hiện tại";
    }

    /// <summary>Click: preview rows. Shift+click: show the table structure.</summary>
    [RelayCommand]
    private async Task PreviewTableAsync(TableInfo? table)
    {
        var c = Current;
        if (c is null || table is null) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            await BusyAsync($"Đang đọc cấu trúc {table.Name}...", async () =>
            {
                var cols = await c.Engine.DescribeTableAsync(c.Database, c.Info.Name, table.Name);
                ShowResults([ResultViewModel.FromTable($"Cấu trúc {table.Name}", ["cột", "kiểu", "null", "khoá", "mặc định", "extra"],
                    cols.Select(x => new object?[] { x.Name, x.Type, x.Nullable, x.Key, x.Default, x.Extra }))]);
                StatusText = $"Cấu trúc bảng {table.Name}";
            });
            return;
        }
        SqlText = c.Engine.PreviewSql(table.Name);
        await RunAsync();
    }

    // ---------- Databases & branches ----------

    [RelayCommand]
    private async Task NewDatabaseAsync()
    {
        var engineNode = Engines.FirstOrDefault(e => e.Status.Ok && e.Engine.Features.MultipleDatabases);
        if (engineNode is null)
        {
            ShowError("Chưa kết nối được MySQL — kiểm tra ⚙ Kết nối.");
            return;
        }
        var form = new FormDialog($"Tạo database trên {engineNode.Engine.Label}", "Tạo");
        var name = form.AddText("Tên database", "", "Chữ, số và dấu _ ; bắt đầu bằng chữ. Ví dụ: shop_test");
        form.Validate(() => Names.IsValid(name.Text) ? null : "Tên không hợp lệ");
        if (!form.ShowForm()) return;
        await BusyAsync($"Đang tạo database {name.Text}...", async () =>
        {
            await engineNode.Engine.CreateDatabaseAsync(name.Text);
            await ReloadAndSelectAsync(new Location(engineNode.Engine.Id, name.Text, BranchInfo.MainName));
            StatusText = $"Đã tạo database {name.Text}";
        });
    }

    [RelayCommand]
    private async Task DropDatabaseAsync()
    {
        var c = Current;
        if (c is null || !Confirm($"Xoá database {c.Database} cùng tất cả nhánh và snapshot?\n\nKhông thể hoàn tác.")) return;
        await BusyAsync($"Đang xoá database {c.Database}...", async () =>
        {
            await c.Engine.DropDatabaseAsync(c.Database);
            Current = null;
            await ReloadTreeAsync();
            StatusText = $"Đã xoá database {c.Database}";
        });
    }

    [RelayCommand]
    private async Task CreateBranchAsync()
    {
        var c = Current;
        if (c is null) return;
        var engine = c.Engine;
        var form = new FormDialog("Tạo nhánh", "Tạo nhánh");
        var name = form.AddText("Tên nhánh mới", "", "Ví dụ: thu_migration");
        var sources = c.DatabaseNode.Branches.Where(b => engine.Features.Snapshots || b.Info.Kind != BranchKind.Snapshot).Select(b => b.Info.Name);
        var from = form.AddCombo("Tạo từ nhánh", sources, c.Info.Name);
        TextBox? asOf = null;
        if (engine.Features.TimeTravel)
        {
            asOf = form.AddText("Tại thời điểm (tuỳ chọn)", "", "Để trống = trạng thái hiện tại. Dạng: yyyy-MM-dd HH:mm:ss (giờ máy chủ)");
            form.Validate(() => string.IsNullOrWhiteSpace(asOf.Text) || TryParseTime(asOf.Text, out _) ? null : "Thời điểm không hợp lệ");
        }
        else
        {
            form.AddNote("MySQL không có nhánh thật: app sao chép toàn bộ bảng sang một schema riêng. Khoá ngoại, view và trigger không được sao chép.");
        }
        form.Validate(() => Names.IsValid(name.Text) ? null : "Tên nhánh không hợp lệ");
        if (!form.ShowForm()) return;

        DateTime? when = asOf is not null && TryParseTime(asOf.Text, out var t) ? t : null;
        string source = FormDialog.Selected<string>(from) ?? BranchInfo.MainName;
        await BusyAsync($"Đang tạo nhánh {name.Text}...", async () =>
        {
            await engine.CreateBranchAsync(c.Database, name.Text, source, when);
            await ReloadAndSelectAsync(new Location(engine.Id, c.Database, name.Text));
            StatusText = $"Đã tạo nhánh {name.Text}";
        });
    }

    [RelayCommand]
    private async Task CreateSnapshotAsync()
    {
        var c = Current;
        if (c is null) return;
        await BusyAsync("Đang chụp snapshot...", async () =>
        {
            var snap = await c.Engine.CreateSnapshotAsync(c.Database);
            await ReloadAndSelectAsync(new Location(c.Engine.Id, c.Database, c.Info.Name));
            StatusText = $"Đã chụp snapshot {snap.Name}";
        });
    }

    [RelayCommand]
    private async Task MergeAsync()
    {
        var c = Current;
        if (c is null || c.IsMain) return;
        string question = c.Engine.Features.NativeMerge
            ? $"MERGE BRANCH {c.Info.Name} INTO main?\n\nHeliosDB merge theo kiểu last-writer-wins: nếu cùng một dòng bị sửa ở cả hai nhánh, một giá trị sẽ thắng mà không cảnh báo."
            : $"{(c.Info.Kind == BranchKind.Snapshot ? "Khôi phục" : "Ghi đè")} main của {c.Database} bằng nội dung \"{c.Info.Name}\"?\n\nToàn bộ bảng hiện có trong main sẽ bị thay thế. Mẹo: chụp snapshot main trước.";
        if (!Confirm(question)) return;
        await BusyAsync("Đang merge...", async () =>
        {
            var message = await c.Engine.MergeBranchAsync(c.Database, c.Info.Name);
            await ReloadAndSelectAsync(new Location(c.Engine.Id, c.Database, BranchInfo.MainName));
            StatusText = message;
        });
    }

    [RelayCommand]
    private async Task DropBranchAsync()
    {
        var c = Current;
        if (c is null || c.IsMain || !Confirm($"Xoá nhánh {c.Info.Name}? Dữ liệu trong nhánh sẽ mất.")) return;
        await BusyAsync($"Đang xoá nhánh {c.Info.Name}...", async () =>
        {
            await c.Engine.DropBranchAsync(c.Database, c.Info.Name);
            await ReloadAndSelectAsync(new Location(c.Engine.Id, c.Database, BranchInfo.MainName));
            StatusText = $"Đã xoá nhánh {c.Info.Name}";
        });
    }

    [RelayCommand]
    private async Task TimeTravelAsync()
    {
        var c = Current;
        if (c is null) return;
        if (Tables.Count == 0)
        {
            ShowError("Nhánh này chưa có bảng nào.");
            return;
        }
        var form = new FormDialog("Time-travel", "Xem");
        form.AddNote("Xem dữ liệu của bảng tại một thời điểm trong quá khứ (SELECT ... AS OF TIMESTAMP). Thời điểm tính theo giờ của máy chủ HeliosDB. "
            + "HeliosDB-Nano 4.41 hiện giá trị cũ của dòng bị UPDATE và ẩn dòng thêm sau đó, nhưng chưa hiện lại dòng đã DELETE.");
        var table = form.AddCombo("Bảng", Tables.Select(t => t.Name), Tables[0].Name);
        var when = form.AddText("Thời điểm", DateTime.Now.AddMinutes(-5).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        form.Validate(() => TryParseTime(when.Text, out _) ? null : "Thời điểm không hợp lệ (yyyy-MM-dd HH:mm:ss)");
        if (!form.ShowForm()) return;
        TryParseTime(when.Text, out var t);
        var name = FormDialog.Selected<string>(table)!;
        SqlText = $"SELECT * FROM {c.Engine.Ident(name)} AS OF TIMESTAMP '{t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}' LIMIT 100;";
        await RunAsync();
    }

    [RelayCommand]
    private async Task CopyTablesAsync()
    {
        var c = Current;
        if (c is null) return;
        var tables = Tables.Where(t => !t.IsView).Select(t => t.Name).ToList();
        if (tables.Count == 0)
        {
            ShowError("Nhánh này chưa có bảng nào.");
            return;
        }
        var targets = Engines.Where(e => e.Status.Ok).ToList();
        var form = new FormDialog("Sao chép bảng", "Sao chép");
        form.AddNote($"Sao chép từ {c.Engine.Label} › {c.Database} › {c.Info.Name}. Bảng trùng tên ở đích sẽ bị thay thế. "
            + "Sang engine khác thì kiểu dữ liệu được quy đổi tương đương, khoá chính và chỉ mục không được chép theo.");
        var engineBox = form.AddCombo("Engine đích", targets, targets.FirstOrDefault(e => e.Engine != c.Engine) ?? targets[0], e => e.Title);
        var dbBox = form.AddCombo<DatabaseNode>("Database đích", []);
        var branchBox = form.AddCombo<string>("Nhánh đích", []);
        var checks = form.AddCheckList("Bảng", tables);

        void FillBranches()
        {
            var db = FormDialog.Selected<DatabaseNode>(dbBox);
            FormDialog.SetComboItems(branchBox, db?.Branches.Where(b => b.Info.Kind != BranchKind.Snapshot).Select(b => b.Info.Name) ?? []);
        }
        void FillDatabases()
        {
            var engineNode = FormDialog.Selected<EngineNode>(engineBox);
            FormDialog.SetComboItems(dbBox, engineNode?.Databases ?? [], display: d => d.Title);
            FillBranches();
        }
        engineBox.SelectionChanged += (_, _) => FillDatabases();
        dbBox.SelectionChanged += (_, _) => FillBranches();
        FillDatabases();

        form.Validate(() => FormDialog.Selected<string>(branchBox) is null ? "Chưa chọn nơi đích" : null);
        form.Validate(() => checks.Any(x => x.IsChecked == true) ? null : "Chưa chọn bảng nào");
        if (!form.ShowForm()) return;

        var targetEngine = FormDialog.Selected<EngineNode>(engineBox)!.Engine;
        var to = new Location(targetEngine.Id, FormDialog.Selected<DatabaseNode>(dbBox)!.Info.Name, FormDialog.Selected<string>(branchBox)!);
        var from = new Location(c.Engine.Id, c.Database, c.Info.Name);
        var selected = checks.Where(x => x.IsChecked == true).Select(x => (string)x.Content).ToList();
        await BusyAsync("Đang sao chép...", async () =>
        {
            var progress = new Progress<string>(s => StatusText = s);
            var copied = await CopyService.CopyTablesAsync(c.Engine, from, targetEngine, to, selected, progress);
            StatusText = $"Đã sao chép {copied.Count} bảng ({copied.Sum(x => x.Rows)} dòng) sang {targetEngine.Label} › {to.Database} › {to.Branch}";
            if (to.Engine == from.Engine && to.Database == from.Database && to.Branch == from.Branch) await LoadTablesAsync();
        });
    }

    [RelayCommand]
    private async Task ExportSqlAsync()
    {
        var c = Current;
        if (c is null) return;
        var dlg = new SaveFileDialog
        {
            Filter = "SQL (*.sql)|*.sql",
            FileName = $"{c.Engine.Id}-{c.Database}-{c.Info.Name}.sql",
            Title = "Xuất file SQL",
        };
        if (dlg.ShowDialog() != true) return;
        await BusyAsync("Đang xuất dữ liệu...", async () =>
        {
            var sql = await c.Engine.ExportSqlAsync(c.Database, c.Info.Name);
            await File.WriteAllTextAsync(dlg.FileName, sql, new UTF8Encoding(false));
            StatusText = $"Đã xuất {Path.GetFileName(dlg.FileName)}";
        });
    }

    // ---------- Settings ----------

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        var s = _settings;
        var form = new FormDialog("Kết nối", "Lưu & kết nối lại");
        var myOn = form.AddCheck("Dùng MySQL", s.MySqlEnabled);
        var myHost = form.AddText("MySQL — máy chủ", s.MySqlHost);
        var myPort = form.AddText("MySQL — cổng", s.MySqlPort.ToString(CultureInfo.InvariantCulture));
        var myUser = form.AddText("MySQL — tài khoản", s.MySqlUser);
        var myPass = form.AddPassword("MySQL — mật khẩu", s.MySqlPassword);
        var heOn = form.AddCheck("Dùng HeliosDB-Nano (giao thức PostgreSQL)", s.HeliosEnabled);
        var heHost = form.AddText("HeliosDB — máy chủ", s.HeliosHost);
        var hePort = form.AddText("HeliosDB — cổng PostgreSQL", s.HeliosPort.ToString(CultureInfo.InvariantCulture));
        var heUser = form.AddText("HeliosDB — tài khoản", s.HeliosUser);
        var hePass = form.AddPassword("HeliosDB — mật khẩu (để trống nếu chạy --auth trust)", s.HeliosPassword);
        var heDb = form.AddText("HeliosDB — tên database", s.HeliosDatabase);
        form.Validate(() => IsPort(myPort.Text) && IsPort(hePort.Text) ? null : "Cổng phải là số từ 1 đến 65535");
        if (!form.ShowForm()) return;

        _settings = new ConnectionSettings
        {
            MySqlEnabled = myOn.IsChecked == true,
            MySqlHost = myHost.Text.Trim(),
            MySqlPort = int.Parse(myPort.Text, CultureInfo.InvariantCulture),
            MySqlUser = myUser.Text.Trim(),
            MySqlPassword = myPass.Password,
            HeliosEnabled = heOn.IsChecked == true,
            HeliosHost = heHost.Text.Trim(),
            HeliosPort = int.Parse(hePort.Text, CultureInfo.InvariantCulture),
            HeliosUser = heUser.Text.Trim(),
            HeliosPassword = hePass.Password,
            HeliosDatabase = heDb.Text.Trim(),
        };
        try
        {
            SettingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
            ShowError("Không lưu được cấu hình: " + ex.Message);
        }
        await RefreshAsync();
    }

    private static bool IsPort(string text) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) && p is > 0 and < 65536;

    // ---------- Helpers ----------

    private async Task ReloadAndSelectAsync(Location target)
    {
        Current = null;
        await ReloadTreeAsync();
        var db = Engines.Where(e => e.Engine.Id == target.Engine).SelectMany(e => e.Databases).FirstOrDefault(d => d.Info.Name == target.Database);
        var branch = db?.Branches.FirstOrDefault(b => b.Info.Name == target.Branch) ?? db?.Branches.FirstOrDefault();
        if (branch is not null) await SelectAsync(branch);
    }

    private async Task BusyAsync(string status, Func<Task> action)
    {
        IsBusy = true;
        StatusText = status;
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusText = "Lỗi: " + ex.Message;
            ShowError(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static bool TryParseTime(string text, out DateTime value) =>
        DateTime.TryParseExact(text.Trim(), ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out value);

    private static bool Confirm(string message) =>
        MessageBox.Show(Application.Current.MainWindow, message, "Xác nhận", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    private static void ShowError(string message) =>
        MessageBox.Show(Application.Current.MainWindow, message, "TestDB Hub", MessageBoxButton.OK, MessageBoxImage.Error);
}
