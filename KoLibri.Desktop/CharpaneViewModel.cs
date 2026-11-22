using System.Globalization;
using KoLibri.Core.Basis;
using KoLibri.Core.Spiel;

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

    public static CharpaneState FromStatus(KoLStatusSnapshot status, string role, DateTimeOffset? now = null)
    {
        var effects = status.TimedEffects.Count > 0
            ? status.TimedEffects.Select(e => new EffectRow(e.Name, e.TurnsRemaining)).ToList()
            : status.Effects.Select(e => new EffectRow(e, 0)).ToList();
        var age = (now ?? DateTimeOffset.UtcNow) - status.FetchedAt;
        return new CharpaneState
        {
            Name = status.PlayerName ?? role,
            Role = status.IsStale ? $"{role} · stale" : role,
            Level = $"Level {status.Level}",
            Hp = status.MaximumHp > 0 ? $"HP {status.CurrentHp:N0} / {status.MaximumHp:N0}" : "HP —",
            Mp = status.MaximumMp > 0 ? $"MP {status.CurrentMp:N0} / {status.MaximumMp:N0}" : "MP —",
            HpFraction = Fraction(status.CurrentHp, status.MaximumHp),
            MpFraction = Fraction(status.CurrentMp, status.MaximumMp),
            Adventures = $"Adventures {status.Adventures:N0}",
            Meat = $"Meat {status.Meat:N0}",
            Fullness = Organ("Fullness", status.Fullness, status.FullnessLimit),
            Drunk = Organ("Drunkenness", status.Inebriety, status.InebrietyLimit),
            Spleen = Organ("Spleen", status.Spleen, status.SpleenLimit),
            Familiar = DescribeFamiliar(status),
            Effects = effects,
            EffectsText = effects.Count == 0 ? "None" : string.Join(Environment.NewLine, effects.Select(e => e.Display)),
            Message = status.IsStale
                ? $"Showing old data from {status.FetchedAt.ToLocalTime():HH:mm:ss} ({FormatAge(age)} ago): KoL did not answer with a fresh status."
                : null,
            IsError = status.IsStale
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
        KoLHtmlResponseException { Kind: KoLHtmlResponseKind.LoginPage }
            => "The KoL session has expired. Sign in again in Settings.",
        KoLHtmlResponseException => "KoL answered with an unexpected page (maintenance or an interstitial). Try again in a minute.",
        OperationCanceledException => "The request was cancelled.",
        HttpRequestException h => $"Could not reach KoL: {h.Message}",
        _ => $"The request failed: {exception.Message}"
    };

    public static string DescribeFamiliar(KoLStatusSnapshot status)
    {
        if (string.IsNullOrWhiteSpace(status.FamiliarId) || status.FamiliarId == "0") return "None";
        var weight = status.FamiliarWeight is { } w ? $" · {w} lb" : "";
        var name = string.IsNullOrWhiteSpace(status.FamiliarName) ? $"Familiar #{status.FamiliarId}" : status.FamiliarName.Trim();
        return $"{name}{weight}";
    }

    /// <summary>"Fullness 3 / 15". A limit the status did not report is shown as unknown: "3 / ?".</summary>
    private static string Organ(string label, int? value, int? reported)
        => value is not { } v ? $"{label} —"
            : reported is > 0 ? $"{label} {v.ToString("N0", CultureInfo.InvariantCulture)} / {reported}"
            : $"{label} {v.ToString("N0", CultureInfo.InvariantCulture)} / ?";

    private static double Fraction(int current, int maximum)
        => maximum > 0 ? Math.Clamp((double)current / maximum, 0, 1) : 0;

    private static string FormatAge(TimeSpan age)
        => age.TotalMinutes < 1 ? "under a minute" : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} min" : $"{(int)age.TotalHours} h";
}
