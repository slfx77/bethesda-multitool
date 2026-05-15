using System.ComponentModel;

namespace FalloutXbox360Utils;

/// <summary>
///     One row in the DMP→ESP converter's secondary data folders list. Holds the folder
///     path plus an auto-detected (user-editable) flag for whether the folder contains
///     Xbox 360 format assets that need on-the-fly conversion when packed into the BSA.
/// </summary>
public sealed class SecondaryFolderEntry : INotifyPropertyChanged
{
    private bool _isXbox360Format;

    public required string Path { get; init; }

    public bool IsXbox360Format
    {
        get => _isXbox360Format;
        set
        {
            if (_isXbox360Format != value)
            {
                _isXbox360Format = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsXbox360Format)));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
