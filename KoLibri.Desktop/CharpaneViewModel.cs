using System.Globalization;

namespace KoLibri.Desktop;

/// <summary>One active effect in the charpane or the effects list.</summary>
public sealed record EffectRow(string Name, int TurnsRemaining)
{
    public string Display => TurnsRemaining > 0 ? $"{Name} · {TurnsRemaining} turn{(TurnsRemaining == 1 ? "" : "s")}" : Name;
    public string TurnsText => TurnsRemaining > 0 ? $"{TurnsRemaining} turns remaining" : "no turn count (intrinsic or unknown)";
}

/// <summary>
/// Everything the character pane shows, computed from one status snapshot (or from the reason there is none).
/// Pure data: no controls, no requests. <see cref="Message"/> is a visible explanation when the pane has no live data.
/// </summary>
public sealed record CharpaneState
{
    public string Name { get; init; } = "Not signed in";
    public string Role { get; init; } = "";
    public string Level { get; init; } = "Level —";
    public string Hp { get; init; } = "HP —";
    public string Mp { get; init; } = "MP —";
    public double HpFraction { get; init; }
    public double MpFraction { get; init; }
    public string Adventures { get; init; } = "Adventures —";
    public string Meat { get; init; } = "Meat —";
    public string Fullness { get; init; } = "Fullness —";
    public string Drunk { get; init; } = "Drunkenness —";
    public string Spleen { get; init; } = "Spleen —";
    public string Familiar { get; init; } = "None";
    public IReadOnlyList<EffectRow> Effects { get; init; } = [];
    public string EffectsText { get; init; } = "None";
    /// <summary>Why the pane has no (fresh) data: signed out, session expired, rate-limited, stale; null when fine.</summary>
    public string? Message { get; init; }
    public bool IsError { get; init; }

    public static CharpaneState FromStatus(Status status, string role, DateTimeOffset? now = null)
    {
        // 0.5: the library throws instead of returning old data, so there is no stale state to show any more.
        var effects = status.Effects.Select(e => new EffectRow(e.Name, e.Turns)).ToList();
        return new CharpaneState
        {
            Name = string.IsNullOrWhiteSpace(status.PlayerName) ? role : status.PlayerName,
            Role = role,
            Level = $"Level {status.Level}",
            Hp = status.MaxHp > 0 ? $"HP {status.Hp:N0} / {status.MaxHp:N0}" : "HP —",
            Mp = status.MaxMp > 0 ? $"MP {status.Mp:N0} / {status.MaxMp:N0}" : "MP —",
            HpFraction = Fraction(status.Hp, status.MaxHp),
            MpFraction = Fraction(status.Mp, status.MaxMp),
            Adventures = $"Adventures {status.Adventures:N0}",
            Meat = $"Meat {status.Meat:N0}",
            Fullness = Organ("Fullness", status.Fullness.Current, status.Fullness.Limit),
            Drunk = Organ("Drunkenness", status.Inebriety.Current, status.Inebriety.Limit),
            Spleen = Organ("Spleen", status.Spleen.Current, status.Spleen.Limit),
            Familiar = DescribeFamiliar(status),
            Effects = effects,
            EffectsText = effects.Count == 0 ? "None" : string.Join(Environment.NewLine, effects.Select(e => e.Display)),
            Message = null,
            IsError = false
        };
    }

    public static CharpaneState SignedOut(string role) => new()
    {
        Role = role,
        Message = $"Not signed in as {role}. Open Settings and sign in to load the character pane.",
        IsError = false
    };

    /// <summary>A read that threw: expired sessions, unreachable KoL and plain failures each get their own sentence.</summary>
    public static CharpaneState Failed(string role, Exception exception) => new()
    {
        Role = role,
        Message = DescribeError(exception),
        IsError = true
    };

    public static string DescribeError(Exception exception) => exception switch
    {
        SessionExpiredException => "The KoL session has expired. Sign in again in Settings.",
        UnexpectedPageException { Page.Kind: PageKind.LoginRequired }
            => "The KoL session has expired. Sign in again in Settings.",
        UnexpectedPageException => "KoL answered with an unexpected page (maintenance or an interstitial). Try again in a minute.",
        OperationCanceledException => "The request was cancelled.",
        HttpRequestException h => $"Could not reach KoL: {h.Message}",
        _ => $"The request failed: {exception.Message}"
    };

    public static string DescribeFamiliar(Status status)
    {
        if (status.FamiliarId is not { } familiarId || familiarId == 0) return "None";
        var weight = status.FamiliarWeight is { } w ? $" · {w} lb" : "";
        var name = string.IsNullOrWhiteSpace(status.FamiliarName) ? $"Familiar #{familiarId}" : status.FamiliarName.Trim();
        return $"{name}{weight}";
    }

    /// <summary>"Fullness 3 / 15". A limit the status did not report is shown as unknown: "3 / ?".</summary>
    private static string Organ(string label, int? value, int? reported)
        => value is not { } v ? $"{label} —"
            : reported is > 0 ? $"{label} {v.ToString("N0", CultureInfo.InvariantCulture)} / {reported}"
            : $"{label} {v.ToString("N0", CultureInfo.InvariantCulture)} / ?";

    private static double Fraction(int current, int maximum)
        => maximum > 0 ? Math.Clamp((double)current / maximum, 0, 1) : 0;
}
