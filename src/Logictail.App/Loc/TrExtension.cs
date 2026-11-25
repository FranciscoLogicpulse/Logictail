using Avalonia.Markup.Xaml;
using Logictail.Core.Localization;

namespace Logictail.App.Loc;

/// <summary>XAML text in the current language: <c>Header="{l:Tr '_File'}"</c>. English is the source text.</summary>
public sealed class TrExtension(string text) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) => L._(text);
}
