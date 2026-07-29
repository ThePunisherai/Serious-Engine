using System.Windows;
using System.Windows.Threading;

namespace GameStudio.App;

public partial class App : Application
{
    public App()
    {
        // A background failure should surface as a dialog, not take the whole editor down with it.
        DispatcherUnhandledException += OnUnhandledException;
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "GameStudio Engine", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
