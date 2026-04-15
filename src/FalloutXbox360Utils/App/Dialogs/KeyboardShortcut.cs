namespace FalloutXbox360Utils;

/// <summary>
///     One row in the <see cref="KeyboardShortcutsDialog" />. Kept manually in sync with
///     the <c>&lt;KeyboardAccelerator&gt;</c> declarations scattered through the XAML:
///     each XAML accelerator should have a matching entry here so the F1 dialog lists it.
/// </summary>
internal sealed record KeyboardShortcut(string Group, string Keys, string Action);
