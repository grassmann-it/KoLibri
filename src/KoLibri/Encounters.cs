using CoreSpiel = KoLibri.Core.Spiel;

namespace KoLibri;

/// <summary>Helfer für Begegnungen, die ein Lauf nie automatisch beantworten soll (Turtle Tamer).</summary>
public static class Encounters
{
    /// <summary>
    /// True, wenn die Begegnung eine Turtle-Begegnung ist: Titel einer Choice/eines Nichtkampfs oder
    /// Monster eines Kampfs passt auf das Turtle-Muster. Rein, ohne Requests.
    /// </summary>
    public static bool IsTurtleEncounter(Encounter encounter)
    {
        ArgumentNullException.ThrowIfNull(encounter);
        return encounter switch
        {
            FightEncounter fight => IsTurtleMonster(fight.MonsterName) || IsTurtleMonster(fight.Title),
            _ => CoreSpiel.TurtleTamerEncounters.IsTurtleEncounter(encounter.Title)
        };
    }

    /// <summary>True, wenn der Titel einer Choice/eines Nichtkampfs auf eine Turtle-Begegnung passt.</summary>
    internal static bool IsTurtleEncounterTitle(string? title)
        => CoreSpiel.TurtleTamerEncounters.IsTurtleEncounter(title);

    /// <summary>True, wenn der Monstername ein Turtle-Monster ist ("... turtle", "tortoise", "terrapin").</summary>
    public static bool IsTurtleMonster(string? monsterName)
        => CoreSpiel.TurtleTamerEncounters.IsTurtleMonster(monsterName);
}
