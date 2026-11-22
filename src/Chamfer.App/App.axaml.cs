using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Chamfer.App.ViewModels;
using Chamfer.App.Views;

namespace Chamfer.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainViewModel();
            vm.LoadLanguage();
            vm.LoadLayout();
            vm.LoadPlugins(
            [
                Path.Combine(AppContext.BaseDirectory, "plugins"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Chamfer", "plugins"),
            ]);
            desktop.MainWindow = new MainWindow { DataContext = vm };

            // Auto-save unsaved work for recovery; on a crash, write it out at once and log the error.
            vm.StartAutoSave(TimeSpan.FromMinutes(2));
            vm.ShowRecoveryPanel();
            AppDomain.CurrentDomain.UnhandledException += (_, e) => OnCrash(vm, e.ExceptionObject as Exception);
            Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) => OnCrash(vm, e.Exception);
            desktop.ShutdownRequested += (_, _) => vm.DiscardRecovery();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void OnCrash(MainViewModel vm, Exception? ex)
    {
        try
        {
            vm.AutoSaveNow();
            Directory.CreateDirectory(vm.Recovery.Folder);
            File.WriteAllText(Path.Combine(vm.Recovery.Folder, "..", "crash.log"),
                $"{DateTime.Now:O}{Environment.NewLine}{ex}{Environment.NewLine}");
        }
        catch (Exception logError) when (logError is IOException or UnauthorizedAccessException)
        {
            // Nothing more we can do while crashing.
        }
    }
}