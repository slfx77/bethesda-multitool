using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Slfx77.Multitool.Core.Localization;

namespace BethesdaMultitool.Localization;

/// <summary>Preserves authored BMT resource prefixes while forwarding all ownership and refresh behavior to the shared controller.</summary>
[WinRT.GeneratedWinRTExposedType]
public sealed partial class RuntimeLocalization : DependencyObject
{
    /// <summary>The former MRT resource prefix attached to an existing object.</summary>
    public static readonly DependencyProperty UidProperty = DependencyProperty.RegisterAttached(
        "Uid", typeof(string), typeof(RuntimeLocalization), new PropertyMetadata(null, OnUidChanged));
    /// <summary>Reads the authored prefix.</summary>
    /// <param name="target">The existing target.</param>
    /// <returns>The attached prefix.</returns>
    public static string? GetUid(DependencyObject target) => (string?)target.GetValue(UidProperty);
    /// <summary>Attaches a BMT resource prefix.</summary>
    /// <param name="target">The existing target.</param>
    /// <param name="value">The resource prefix.</param>
    public static void SetUid(DependencyObject target, string value) => target.SetValue(UidProperty, value);
    /// <summary>Forwards a keyed display value to the window-owned controller.</summary>
    /// <param name="target">The target.</param><param name="property">The display property.</param><param name="key">The resource key.</param><param name="arguments">Captured display arguments.</param>
    internal static void Set(DependencyObject target, DependencyProperty property, string key, params object?[] arguments)
        => Owner(target).Bind(target, property, key, arguments);
    /// <summary>Forwards a text message to the shared binding implementation.</summary>
    /// <param name="target">The target.</param><param name="key">The resource key.</param><param name="arguments">Captured arguments.</param>
    internal static void SetText(TextBlock target, string key, params object?[] arguments) => Set(target, TextBlock.TextProperty, key, arguments);
    /// <summary>Forwards a pure presentation formatter to the shared controller.</summary>
    /// <param name="target">The target.</param><param name="property">The display property.</param><param name="render">The retained formatter.</param>
    internal static void Bind(DependencyObject target, DependencyProperty property, Func<IStringCatalog, string> render)
        => Owner(target).Bind(target, property, render);
    /// <summary>Returns a property to its source-data producer.</summary>
    /// <param name="target">The target.</param><param name="property">The display property.</param><param name="value">The untranslated value.</param>
    internal static void SetRaw(DependencyObject target, DependencyProperty property, object? value)
        => Owner(target).SetRaw(target, property, value);
    /// <summary>Resolves the prefix through the shared property's typed vocabulary.</summary>
    /// <param name="sender">The XAML object.</param><param name="args">The authored prefix change.</param>
    private static void OnUidChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    { if (args.NewValue is string uid) Owner(sender).BindUid(sender, uid); }
    /// <summary>Uses the target's existing window owner, falling back only during initial XAML construction.</summary>
    /// <param name="target">The existing resource target.</param>
    /// <returns>The controller associated with this target's retained window.</returns>
    private static Slfx77.Multitool.WinUI.Localization.LocalizationController Owner(DependencyObject target)
        => Slfx77.Multitool.WinUI.Localization.Localization.GetContext(target) ?? MainWindow.Instance!.Localization;

}
