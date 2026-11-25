using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Logitail.App.Dialogs;

/// <summary>Minimal OK-only message box (Avalonia has none built in).</summary>
public static class MessageWindow
{
    public static Task Show(Window owner, string title, string message, bool monospace = false)
    {
        var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true, MinWidth = 80, Classes = { "primary" }, HorizontalAlignment = HorizontalAlignment.Right };
        var window = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            MaxWidth = 720,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(18),
                Spacing = 16,
                Children =
                {
                    new SelectableTextBlock
                    {
                        Text = message,
                        TextWrapping = TextWrapping.Wrap,
                        FontFamily = monospace ? new FontFamily("Cascadia Mono, Consolas, Courier New") : FontFamily.Default,
                    },
                    ok,
                },
            },
        };
        ok.Click += (_, _) => window.Close();
        return window.ShowDialog(owner);
    }
}
