using System.Data;
using System.Windows;
using System.Windows.Controls;
using TestDbHub.Wpf.ViewModels;

namespace TestDbHub.Wpf.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        Loaded += async (_, _) => await _vm.RefreshCommand.ExecuteAsync(null);
    }

    private async void OnTreeSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e) =>
        await _vm.OnTreeSelectionAsync(e.NewValue as TreeNode);

    // Result columns are named c0, c1...; show the real column name stored in the caption.
    // A TextBlock header keeps underscores, which a plain string header treats as access keys.
    private void OnAutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        if (sender is DataGrid { ItemsSource: DataView view } && view.Table?.Columns[e.PropertyName] is { } column)
        {
            e.Column.Header = new TextBlock { Text = column.Caption };
        }
    }
}
