using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Logictail.Core.Localization;
using Logictail.Core.Settings;

namespace Logictail.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            SettingsStore.MigrateFromOldName(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
            var store = SettingsStore.ForCurrentUser();
            var settings = store.Load();
            L.Default = settings.Language; // before any window builds its text
            var commandLine = CommandLine.Parse(desktop.Args ?? []);
            desktop.MainWindow = new MainWindow(store, settings, commandLine);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
