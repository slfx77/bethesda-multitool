namespace BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;

internal enum QuestConditionScopeKind { TopLevel, Stage, Objective, Target }

/// <summary>Canonical physical QUST condition routing; malformed markers retain parser routing but forbid source joins.</summary>
internal sealed class QuestConditionScope
{
    internal QuestConditionScopeKind Current { get; private set; } = QuestConditionScopeKind.TopLevel;
    internal bool Valid { get; private set; } = true;

    internal void Advance(string signature, int length)
    {
        switch (signature)
        {
            case "INDX":
                if (length < 2) Valid = false;
                else Current = QuestConditionScopeKind.Stage;
                break;
            case "QOBJ":
                if (length < 4) Valid = false;
                else Current = QuestConditionScopeKind.Objective;
                break;
            case "QSTA":
                if (length < 5) Valid = false;
                else Current = QuestConditionScopeKind.Target;
                break;
        }
    }
}
