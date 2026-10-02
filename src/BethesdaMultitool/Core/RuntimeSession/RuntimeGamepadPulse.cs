using System.Globalization;

namespace BethesdaMultitool.Core.RuntimeSession;

/// <summary>A bounded digital controller pulse. Completion requires both guest returns.</summary>
public sealed record RuntimeGamepadPulse(uint Buttons, int HoldMilliseconds = 100,
    int DeadlineMilliseconds = 1000, int Slot = 0)
{
    internal void Validate()
    {
        const uint allowed = 0x301f; // D-pad, START, A and B.
        if (Slot != 0 || Buttons == 0 || (Buttons & ~allowed) != 0 ||
            (Buttons & 3) == 3 || (Buttons & 12) == 12 ||
            HoldMilliseconds is < 20 or > 500 ||
            DeadlineMilliseconds <= HoldMilliseconds || DeadlineMilliseconds > 2000)
            throw new ArgumentException("Gamepad pulses require slot 0, supported non-opposed buttons, a 20–500 ms hold and a greater deadline of at most 2000 ms.");
    }

    internal string Payload(string? boundManifestSha256)
    {
        Validate();
        if (boundManifestSha256 is not { Length: 64 } ||
            !boundManifestSha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new InvalidOperationException("Gamepad input requires a bound guest-run manifest.");
        return string.Join('\n', "gamepad-pulse/1", Slot.ToString(CultureInfo.InvariantCulture),
            Buttons.ToString(CultureInfo.InvariantCulture), HoldMilliseconds.ToString(CultureInfo.InvariantCulture),
            DeadlineMilliseconds.ToString(CultureInfo.InvariantCulture), boundManifestSha256);
    }
}
