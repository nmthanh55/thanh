using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TestDbHub.Wpf.Views;

/// <summary>
/// Small modal form built in code: a column of labelled fields with OK/Cancel.
/// Used for every input dialog in the app (new database, branch, copy, settings...).
/// </summary>
public sealed class FormDialog : Window
{
    private readonly StackPanel _fields = new();
    private readonly TextBlock _error = new() { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8), Visibility = Visibility.Collapsed };
    private readonly List<Func<string?>> _validators = [];
    private Control? _firstInput;

    public FormDialog(string title, string okText = "OK")
    {
        Title = title;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Owner = Application.Current.MainWindow;

        var ok = new Button { Content = okText, IsDefault = true, MinWidth = 90, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) => Submit();
        var cancel = new Button { Content = "Huỷ", IsCancel = true, MinWidth = 90, Padding = new Thickness(10, 4, 10, 4) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(_fields);
        root.Children.Add(_error);
        root.Children.Add(buttons);
        Content = root;
        Loaded += (_, _) => _firstInput?.Focus();
    }

    private void AddField(string label, FrameworkElement input, string? help = null)
    {
        _fields.Children.Add(new TextBlock { Text = label, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 3) });
        input.Margin = new Thickness(0, 0, 0, help is null ? 10 : 2);
        _fields.Children.Add(input);
        if (help is not null)
            _fields.Children.Add(new TextBlock { Text = help, Foreground = Brushes.Gray, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        if (_firstInput is null && input is Control c) _firstInput = c;
    }

    public TextBox AddText(string label, string value = "", string? help = null)
    {
        var box = new TextBox { Text = value, Padding = new Thickness(4, 3, 4, 3) };
        AddField(label, box, help);
        return box;
    }

    public PasswordBox AddPassword(string label, string value = "")
    {
        var box = new PasswordBox { Password = value, Padding = new Thickness(4, 3, 4, 3) };
        AddField(label, box);
        return box;
    }

    public ComboBox AddCombo<T>(string label, IEnumerable<T> items, T? selected = default, Func<T, string>? display = null, string? help = null)
    {
        var combo = new ComboBox { Padding = new Thickness(4, 3, 4, 3) };
        SetComboItems(combo, items, selected, display);
        AddField(label, combo, help);
        return combo;
    }

    public static void SetComboItems<T>(ComboBox combo, IEnumerable<T> items, T? selected = default, Func<T, string>? display = null)
    {
        var list = items.Select(i => new ComboBoxItem { Content = display is null ? i?.ToString() : display(i), Tag = i }).ToList();
        combo.ItemsSource = list;
        combo.SelectedItem = list.FirstOrDefault(x => Equals(x.Tag, selected)) ?? list.FirstOrDefault();
    }

    public static T? Selected<T>(ComboBox combo) => combo.SelectedItem is ComboBoxItem { Tag: T value } ? value : default;

    public CheckBox AddCheck(string label, bool value)
    {
        var check = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 0, 0, 10) };
        _fields.Children.Add(check);
        return check;
    }

    public List<CheckBox> AddCheckList(string label, IEnumerable<string> items, bool isChecked = true)
    {
        var checks = items.Select(i => new CheckBox { Content = i, IsChecked = isChecked, Margin = new Thickness(2) }).ToList();
        var panel = new StackPanel();
        foreach (var c in checks) panel.Children.Add(c);
        var scroll = new ScrollViewer { Content = panel, MaxHeight = 180, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var border = new Border { Child = scroll, BorderBrush = Brushes.Gainsboro, BorderThickness = new Thickness(1), Padding = new Thickness(6) };
        AddField(label, border);
        return checks;
    }

    public void AddNote(string text)
    {
        _fields.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xF1, 0xF3, 0xF6)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 0, 0, 12),
            Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray },
        });
    }

    /// <summary>Add a check run on OK; return an error message to keep the dialog open.</summary>
    public void Validate(Func<string?> validator) => _validators.Add(validator);

    public bool ShowForm() => ShowDialog() == true;

    private void Submit()
    {
        foreach (var v in _validators)
        {
            var message = v();
            if (message is not null)
            {
                _error.Text = message;
                _error.Visibility = Visibility.Visible;
                return;
            }
        }
        DialogResult = true;
    }
}
