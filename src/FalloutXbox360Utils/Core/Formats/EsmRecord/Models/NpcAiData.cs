namespace FalloutXbox360Utils.Core.Formats.EsmRecord.Models;

/// <summary>
///     AI behavior data from TESAIForm, empirically at dump offset +160.
///     Layout (starting at +164 after vtable):
///     +164: Aggression (byte)
///     +165: Confidence (byte)
///     +166: Energy Level (byte)
///     +167: Responsibility (byte)
///     +168: Mood (byte)
///     +169-171: padding (3 bytes)
///     +172: BuySellAndServices / AI Flags (uint32 BE)
///     +178: Assistance (byte)
/// </summary>
public record NpcAiData(
    byte Aggression, // 0=Unaggressive, 1=Aggressive, 2=Very Aggressive, 3=Frenzied
    byte Confidence, // 0=Cowardly, 1=Cautious, 2=Average, 3=Brave, 4=Foolhardy
    byte EnergyLevel,
    byte Responsibility, // 0-100 scale: 0=Any Crime, 50=Property Crime, 100=No Crime
    byte Mood, // 0=Neutral, 1=Afraid, 2=Annoyed, 3=Cocky, 4=Drugged, 5=Pleasant, 6=Angry, 7=Sad
    uint Flags,
    byte Assistance) // 0=Nobody, 1=Allies, 2=Friends and Allies
{
    public string AggressionName => Aggression switch
    {
        0 => "Unaggressive",
        1 => "Aggressive",
        2 => "Very Aggressive",
        3 => "Frenzied",
        _ => $"Unknown ({Aggression})"
    };

    public string ConfidenceName => Confidence switch
    {
        0 => "Cowardly",
        1 => "Cautious",
        2 => "Average",
        3 => "Brave",
        4 => "Foolhardy",
        _ => $"Unknown ({Confidence})"
    };

    public string ResponsibilityName => Responsibility switch
    {
        0 => "Any Crime",
        <= 29 => "Violence Against Enemies",
        <= 49 => "Property Crime Only",
        <= 69 => "No Assault",
        <= 99 => "Avoids Crime",
        100 => "No Crime",
        _ => $"{Responsibility}"
    };

    public string MoodName => Mood switch
    {
        0 => "Neutral",
        1 => "Afraid",
        2 => "Annoyed",
        3 => "Cocky",
        4 => "Drugged",
        5 => "Pleasant",
        6 => "Angry",
        7 => "Sad",
        _ => $"Unknown ({Mood})"
    };

    public string AssistanceName => Assistance switch
    {
        0 => "Helps Nobody",
        1 => "Helps Allies",
        2 => "Helps Friends and Allies",
        _ => $"Unknown ({Assistance})"
    };
}
