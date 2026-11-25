using Avalonia;
using System.Runtime.CompilerServices;
using Avalonia.Headless;
using Logictail.Core.Localization;
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

internal static class EnglishForTests
{
    // Assertions use the English text; UiReviewTests renders Portuguese too.
    [ModuleInitializer]
    internal static void Init() => L.Default = Language.English;
}
