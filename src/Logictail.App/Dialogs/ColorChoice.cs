using Logictail.Core.Localization;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Logictail.App.Dialogs;

/// <summary>Named color list plus a hex box for custom colors. Colors are 0xAARRGGBB.</summary>
public sealed class ColorChoice : StackPanel
{
    public static readonly (string Name, uint Argb)[] Named =
    [
        ("Black", 0xFF000000), ("White", 0xFFFFFFFF), ("Red", 0xFFE53935), ("Green", 0xFF43A047),
        ("Blue", 0xFF1E88E5), ("Yellow", 0xFFFFEB3B), ("Cyan", 0xFF00ACC1), ("Magenta", 0xFFD81B60),
        ("Dark Red", 0xFFC62828), ("Dark Green", 0xFF2E7D32), ("Dark Blue", 0xFF1565C0), ("Dark Yellow", 0xFFFFE082),
        ("Dark Cyan", 0xFF00838F), ("Dark Magenta", 0xFF8E24AA),
        ("Gray 1", 0xFF212121), ("Gray 2", 0xFF424242), ("Gray 3", 0xFF616161), ("Gray 4", 0xFF9E9E9E),
        ("Gray 5", 0xFFBDBDBD), ("Gray 6", 0xFFE0E0E0), ("Gray 7", 0xFFF5F5F5),
        // Soft tints used by the default rules: readable backgrounds with matching dark text.
        ("Soft red", 0xFFFBE1DC), ("Soft orange", 0xFFFDEBDD), ("Brick", 0xFF8A2A1B), ("Brown", 0xFF7A4A12),
    ];

    private readonly ComboBox _combo = new() { Width = 150 };
    private readonly TextBox _hex = new() { Width = 90, PlaceholderText = L._("#RRGGBB") };
    private readonly Border _swatch = new() { Width = 22, Height = 22, BorderBrush = Brushes.Gray, BorderThickness = new(1) };
    private bool _updating;
    private uint _value;

    public ColorChoice()
    {
        Orientation = Orientation.Horizontal;
        Spacing = 6;
        _combo.ItemsSource = Named.Select(n => L._(n.Name)).Append(L._("Custom")).ToList();
        _combo.SelectionChanged += (_, _) =>
        {
            if (_updating || _combo.SelectedIndex < 0 || _combo.SelectedIndex >= Named.Length) return;
            SetValue(Named[_combo.SelectedIndex].Argb);
        };
        _hex.LostFocus += (_, _) => ApplyHex();
        _hex.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) ApplyHex(); };
        Children.Add(_swatch);
        Children.Add(_combo);
        Children.Add(_hex);
    }

    public event EventHandler? ValueChanged;

    public uint Value
    {
        get => _value;
        set => SetValue(value);
    }

    private void ApplyHex()
    {
        var text = (_hex.Text ?? "").Trim().TrimStart('#');
        if (text.Length == 6 && uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            SetValue(0xFF000000 | rgb);
        else
            SetValue(_value); // put back the last valid value
    }

    private void SetValue(uint argb)
    {
        _updating = true;
        var changed = argb != _value;
        _value = argb;
        _swatch.Background = new SolidColorBrush(Color.FromUInt32(argb));
        _hex.Text = $"#{argb & 0xFFFFFF:X6}";
        var named = Array.FindIndex(Named, n => n.Argb == argb);
        _combo.SelectedIndex = named >= 0 ? named : Named.Length; // "Custom"
        _updating = false;
        if (changed) ValueChanged?.Invoke(this, EventArgs.Empty);
    }
}
