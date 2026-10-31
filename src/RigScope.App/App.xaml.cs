using System.Windows;
using System.Windows.Threading;
using RigScope.App.ViewModels;

namespace RigScope.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;

        var vm = new MainViewModel();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        _ = StartAsync(vm);
    }

    private static async Task StartAsync(MainViewModel vm)
    {
        await vm.LoadAsync();
        // After the first read so the probes have the PC to themselves. Off in Settings → no network call at all.
        await vm.Settings.CheckAtStartupAsync();
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // A read-only tool should never take the user's evening down with it: show the error and keep running.
        MessageBox.Show($"Something went wrong:\n\n{e.Exception.Message}\n\nRigScope keeps running. Please report this on GitHub.",
            "RigScope", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
