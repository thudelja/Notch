using System;
using System.Windows.Markup;
using SDBackup.Core;

namespace SDBackup.App;

/// <summary>
/// <c>{l:T 'Some text'}</c> in XAML: the text in Notch's language (see <see cref="Loc"/>). The language is set at startup,
/// before any window is made, so the text is looked up once.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public TExtension()
    {
    }

    public TExtension(string text) => Text = text;

    [ConstructorArgument("text")]
    public string Text { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Text);
}
