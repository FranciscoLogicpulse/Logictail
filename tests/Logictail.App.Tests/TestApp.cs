using Avalonia;
using Avalonia.Headless;
using Logictail.App.Tests;

[assembly: AvaloniaTestApplication(typeof(TestApp))]

namespace Logictail.App.Tests;

/// <summary>Runs the real App styles headlessly, with Skia so frames can be captured.</summary>
public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
