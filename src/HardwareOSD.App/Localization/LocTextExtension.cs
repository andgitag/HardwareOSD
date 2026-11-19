using System.Windows.Data;
using System.Windows.Markup;

namespace HardwareOSD.App.Localization;

/// <summary>
/// XAML helper: <c>Text="{l:LocText nav_overlay}"</c> binds to the central string table and updates
/// when the language changes.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class LocTextExtension : MarkupExtension
{
    public LocTextExtension()
    {
    }

    public LocTextExtension(string key)
    {
        Key = key;
    }

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = Loc.Instance,
            Mode = BindingMode.OneWay,
        };
        return binding.ProvideValue(serviceProvider);
    }
}
