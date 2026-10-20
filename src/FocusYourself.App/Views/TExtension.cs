using System.Windows.Data;
using System.Windows.Markup;
using FocusYourself.Core;

namespace FocusYourself.Views;

/// <summary>XAML 中的本地化文字：Text="{l:T PomoTitle}"，切换语言时自动更新。</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }

    public TExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}
