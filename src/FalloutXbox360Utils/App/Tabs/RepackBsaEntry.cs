using System.ComponentModel;

namespace FalloutXbox360Utils;

/// <summary>
///     Data model for individual BSA file entries in the repacker.
/// </summary>
public sealed class RepackBsaEntry : INotifyPropertyChanged
{
    private bool _isSelected = true;

    public required string FileName { get; init; }
    public required string FullPath { get; init; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
