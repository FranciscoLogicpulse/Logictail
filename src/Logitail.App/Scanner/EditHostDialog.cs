using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Logitail.Scanner;

namespace Logitail.App.Scanner;

/// <summary>Edits the user's name, comment and MAC for one device. Returns the note, or null on Cancel.</summary>
public sealed class EditHostDialog : Window
{
    public EditHostDialog(HostResult host, HostNote? note)
    {
        Title = $"Edit {host.Address}";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var name = new TextBox { Text = note?.Name, PlaceholderText = host.Name ?? "Name to show instead of the scanned one" };
        var comment = new TextBox { Text = note?.Comment, AcceptsReturn = true, Height = 70, TextWrapping = TextWrapping.Wrap };
        var mac = new TextBox { Text = note?.Mac ?? (host.Mac is null ? null : ""), PlaceholderText = host.Mac ?? "00-11-22-33-44-55 (for Wake-on-LAN)", IsEnabled = host.Mac is null };
        var error = new TextBlock { Foreground = Brushes.Firebrick };

        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80, Classes = { "primary" } };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        ok.Click += (_, _) =>
        {
            var macText = string.IsNullOrWhiteSpace(mac.Text) ? null : mac.Text.Trim();
            if (macText is not null && new string(macText.Where(Uri.IsHexDigit).ToArray()).Length != 12)
            {
                error.Text = "MAC address needs 12 hex digits, e.g. 00-11-22-33-44-55.";
                return;
            }
            Close(new HostNote
            {
                Name = string.IsNullOrWhiteSpace(name.Text) ? null : name.Text.Trim(),
                Comment = string.IsNullOrWhiteSpace(comment.Text) ? null : comment.Text.Trim(),
                Mac = macText?.ToUpperInvariant().Replace(':', '-'),
                Favorite = note?.Favorite ?? false,
            });
        };
        cancel.Click += (_, _) => Close(null);

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = "Name:" }, name,
                new TextBlock { Text = "Comment:" }, comment,
                new TextBlock { Text = host.Mac is null ? "MAC address:" : "MAC address (found by the scan):" }, mac,
                error,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0), Children = { ok, cancel } },
            },
        };
    }
}
