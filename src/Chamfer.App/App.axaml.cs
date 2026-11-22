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
            vm.LoadPlugins(
            [
                Path.Combine(AppContext.BaseDirectory, "plugins"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Chamfer", "plugins"),
            ]);
            desktop.MainWindow = new MainWindow { DataContext = vm };
        }

        base.OnFrameworkInitializationCompleted();
    }
}