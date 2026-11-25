using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Logitail.Core.Settings;

namespace Logitail.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var store = SettingsStore.ForCurrentUser();
            var settings = store.Load();
            var commandLine = CommandLine.Parse(desktop.Args ?? []);
            desktop.MainWindow = new MainWindow(store, settings, commandLine);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
