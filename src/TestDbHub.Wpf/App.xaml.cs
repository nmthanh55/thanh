using System.Windows;
using System.Windows.Threading;

namespace TestDbHub.Wpf;

public partial class App : Application
{
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "Lỗi không mong đợi", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
