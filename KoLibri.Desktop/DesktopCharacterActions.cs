using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace KoLibri.Desktop;

/// <summary>The Deep Fat Friars page as last read.</summary>
public enum FriarsPageState { Unknown, CollectingItems, ReadyForRitual, RitualComplete }

/// <summary>The three Friar blessings; the value is KoL's "bro" field.</summary>
public enum FriarBlessing { Food = 1, Familiar = 2, Booze = 3 }

/// <summary>
/// One explicit character change from the Gear, Inventory, Skills and Adventure pages. The library actions
/// (cast, rest, unequip, outfit, familiar) go through <see cref="CharacterActions"/>; the ones the 0.5 API has no
/// call for (closet, storage, combine, Friars, familiar equipment for a given familiar) are desktop-owned POSTs
/// through <see cref="KolibriSession.PostAsync"/>, so allowlist, limiter and guard still apply.
/// </summary>
public abstract record CharacterCommand;
public sealed record CastCharacterSkill(int SkillId, int Quantity = 1) : CharacterCommand;
public sealed record RestAtCampground : CharacterCommand;
/// <summary>Eat, drink or chew (daily steps); sent through <see cref="CharacterActions.ConsumeAsync"/>.</summary>
public sealed record ConsumeCharacterItem(int ItemId, ConsumeKind Kind, int Quantity = 1) : CharacterCommand;
public sealed record UnequipInventoryItem(EquipSlot Slot) : CharacterCommand;
public sealed record WearCharacterOutfit(int OutfitId) : CharacterCommand;
public sealed record SelectCharacterFamiliar(int FamiliarId) : CharacterCommand;
public sealed record EquipCharacterFamiliar(int FamiliarId, int? ItemId) : CharacterCommand;
/// <summary>A one-handed weapon into the offhand (KoL's dual wield action; the library's EquipAsync has no dual wield).</summary>
public sealed record DualWieldItem(int ItemId) : CharacterCommand;
public sealed record MoveClosetItem(int ItemId, int Quantity, bool IntoCloset) : CharacterCommand;
public sealed record PullStorageItem(int ItemId, int Quantity) : CharacterCommand;
public sealed record CombineCharacterItems(int OutputId, int FirstItemId, int SecondItemId) : CharacterCommand;
public sealed record PerformFriarRitual : CharacterCommand;
public sealed record RequestFriarBlessing(FriarBlessing Blessing) : CharacterCommand;

/// <summary>Fresh status, inventory and worn equipment of one account, read right after each other.</summary>
public sealed record CharacterSnapshot(Status Status, Inventory Inventory, Equipment Equipment)
{
    /// <summary>Set after an action KoL refused or did not confirm.</summary>
    public string? RejectionReason { get; init; }

    /// <summary>First part of the page KoL answered with, tags stripped.</summary>
    public string? ResponseExcerpt { get; init; }

    public int Count(int itemId) => Inventory.Items.Where(i => i.Item.Id == itemId).Sum(i => i.Quantity);
}

/// <summary>Closet and storage of one account, plus the status (storage pulls remaining).</summary>
public sealed record CharacterStorageSnapshot(Status Status, Inventory Closet, Inventory Storage);

/// <summary>"Keep this effect up": renew it by casting the skill when fewer than <see cref="RenewAtTurns"/> turns remain.</summary>
public sealed record CharacterMoodRule(string EffectName, int SkillId, int ManaCost, int RenewAtTurns = 1);

public sealed record CharacterMoodResult(bool CastSubmitted, string Message, CharacterSnapshot Snapshot);

/// <summary>
/// Desktop-owned character requests on the public session API. Request shapes, refusal texts and the Friars page
/// parser are copied from KoLibri.Core (0.5 step 3) CharacterActionClient.
/// </summary>
public static class DesktopCharacterActions
{
    /// <summary>Fresh status, inventory and equipment (three reads).</summary>
    public static async Task<CharacterSnapshot> ReadAsync(KolibriSession session, CancellationToken cancellationToken = default)
    {
        var status = await session.Character.GetStatusAsync(refresh: true, cancellationToken).ConfigureAwait(false);
        var inventory = await session.Inventory.GetAsync(refresh: true, cancellationToken).ConfigureAwait(false);
        var equipment = await session.Inventory.GetEquipmentAsync(refresh: true, cancellationToken).ConfigureAwait(false);
        return new CharacterSnapshot(status, inventory, equipment);
    }

    public static async Task<CharacterStorageSnapshot> ReadStorageAsync(KolibriSession session, CancellationToken cancellationToken = default)
    {
        var status = await session.Character.GetStatusAsync(refresh: true, cancellationToken).ConfigureAwait(false);
        var closet = await session.Inventory.GetClosetAsync(cancellationToken).ConfigureAwait(false);
        var storage = await session.Inventory.GetStorageAsync(cancellationToken).ConfigureAwait(false);
        return new CharacterStorageSnapshot(status, closet, storage);
    }

    public static async Task<FriarsPageState> ReadFriarsAsync(KolibriSession session, CancellationToken cancellationToken = default)
    {
        var page = await session.GetPageAsync("friars.php", cancellationToken: cancellationToken).ConfigureAwait(false);
        return ParseFriars(page.Html);
    }

    /// <summary>Copied from KoLibri.Core (0.5 step 3) CharacterActionClient.ReadFriarsAsync.</summary>
    public static FriarsPageState ParseFriars(string html)
    {
        if (html.Contains("You've got all three of the ritual items", StringComparison.OrdinalIgnoreCase))
            return FriarsPageState.ReadyForRitual;
        if (html.Contains("action=buffs", StringComparison.OrdinalIgnoreCase)
            || html.Contains("good at blessings", StringComparison.OrdinalIgnoreCase))
            return FriarsPageState.RitualComplete;
        if (html.Contains("ritual items yet", StringComparison.OrdinalIgnoreCase)
            || html.Contains("We were performing a ritual", StringComparison.OrdinalIgnoreCase))
            return FriarsPageState.CollectingItems;
        return FriarsPageState.Unknown;
    }

    /// <summary>
    /// Sends one command and reads the account back. Cast and rest are not handled here (the caller uses
    /// <see cref="CharacterActions.CastAsync"/> and <see cref="CharacterActions.RestAsync"/> for their verified texts).
    /// </summary>
    public static async Task<CharacterSnapshot> ExecuteAsync(KolibriSession session, CharacterCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        string? rejection;
        string? excerpt = null;
        switch (command)
        {
            case UnequipInventoryItem unequip:
                rejection = Rejection(await session.Character.UnequipAsync(unequip.Slot, cancellationToken).ConfigureAwait(false));
                break;
            case WearCharacterOutfit outfit:
                rejection = Rejection(await session.Character.WearOutfitAsync(outfit.OutfitId, cancellationToken).ConfigureAwait(false));
                break;
            case SelectCharacterFamiliar familiar:
                rejection = Rejection(await session.Character.SetFamiliarAsync(familiar.FamiliarId, cancellationToken).ConfigureAwait(false));
                break;
            case CastCharacterSkill:
            case RestAtCampground:
            case ConsumeCharacterItem:
                throw new ArgumentException("Cast, rest and consume go through the library's verified actions.", nameof(command));
            default:
                var combine = command as CombineCharacterItems;
                var before = combine is null ? null : await session.Inventory.GetAsync(refresh: true, cancellationToken).ConfigureAwait(false);
                if (combine is not null && before is not null
                    && new[] { combine.FirstItemId, combine.SecondItemId }.GroupBy(id => id).Any(g => before.Count(g.Key) < g.Count()))
                    throw new InvalidOperationException("Fresh inventory does not contain the recipe ingredients.");
                var (path, form) = BuildRequest(command);
                var page = await session.PostAsync(path, form, cancellationToken: cancellationToken).ConfigureAwait(false);
                var plain = Plain(page.Html);
                excerpt = plain.Length > 300 ? plain[..300] : plain;
                rejection = ObserveRejection(command, page.Html)
                    ?? (LooksLikeKoLError(plain) ? $"KoL rejected the action: \"{excerpt}\"" : null);
                if (combine is not null && before is not null)
                {
                    var after = await session.Inventory.GetAsync(refresh: true, cancellationToken).ConfigureAwait(false);
                    if (after.Count(combine.OutputId) <= before.Count(combine.OutputId))
                        rejection ??= "KoL did not confirm a crafted item in inventory.";
                }
                break;
        }
        var snapshot = await ReadAsync(session, cancellationToken).ConfigureAwait(false);
        return snapshot with { RejectionReason = rejection, ResponseExcerpt = excerpt };
    }

    /// <summary>Renews an effect by one cast when it is about to run out (copied from KoLibri.Core (0.5 step 3) EnsureEffectAsync).</summary>
    public static async Task<CharacterMoodResult> EnsureEffectAsync(KolibriSession session, CharacterMoodRule rule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentException.ThrowIfNullOrWhiteSpace(rule.EffectName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rule.SkillId);
        var before = await ReadAsync(session, cancellationToken).ConfigureAwait(false);
        var duration = EffectTurns(before.Status, rule.EffectName);
        if (duration is 0)
            return new(false, "The effect is present but its duration is unknown.", before);
        if (duration > rule.RenewAtTurns)
            return new(false, $"The effect still has {duration} turns remaining.", before);
        if (before.Status.Mp < rule.ManaCost)
            return new(false, $"Need {rule.ManaCost} MP to renew {rule.EffectName}.", before);

        var result = await session.Character.CastAsync(rule.SkillId, 1, cancellationToken).ConfigureAwait(false);
        var after = await ReadAsync(session, cancellationToken).ConfigureAwait(false);
        var refreshed = EffectTurns(after.Status, rule.EffectName);
        var confirmed = refreshed > (duration ?? 0);
        return new(true, confirmed ? $"{rule.EffectName} renewed for {refreshed} turns."
            : $"KoL did not confirm that {rule.EffectName} was renewed.", after with { RejectionReason = Rejection(result) });
    }

    /// <summary>Null when the effect is not active; 0 when it is active without a turn count (intrinsic).</summary>
    private static int? EffectTurns(Status status, string effectName)
        => status.Effects.FirstOrDefault(e => string.Equals(e.Name, effectName, StringComparison.OrdinalIgnoreCase))?.Turns;

    /// <summary>The refusal of a library action, null when it was done.</summary>
    public static string? Rejection(ActionResult result)
        => result.Status == ActionStatus.Done ? null : result.Rejection ?? "KoL did not confirm it.";

    /// <summary>The worn item of a slot, 0 when the slot is empty, null when the slot was not reported.</summary>
    public static int? Worn(Equipment equipment, EquipSlot slot)
        => equipment.Slots.TryGetValue(slot, out var item) ? item?.Id ?? 0 : null;

    private static (string Path, Dictionary<string, string> Form) BuildRequest(CharacterCommand command)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal) { ["ajax"] = "1" };
        switch (command)
        {
            case PerformFriarRitual:
                fields.Remove("ajax");
                fields["action"] = "ritual";
                return ("friars.php", fields);
            case RequestFriarBlessing blessing:
                if (!Enum.IsDefined(blessing.Blessing)) throw new ArgumentOutOfRangeException(nameof(command));
                fields.Remove("ajax");
                fields["action"] = "buffs";
                fields["bro"] = PositiveId((int)blessing.Blessing);
                return ("friars.php", fields);
            case CombineCharacterItems combine:
                PositiveId(combine.OutputId);
                fields["action"] = "craft";
                fields["mode"] = "combine";
                fields["a"] = PositiveId(combine.FirstItemId);
                fields["b"] = PositiveId(combine.SecondItemId);
                fields["qty"] = "1";
                return ("craft.php", fields);
            case MoveClosetItem move:
                fields["action"] = move.IntoCloset ? "closetpush" : "closetpull";
                fields["whichitem"] = PositiveId(move.ItemId);
                fields["qty"] = PositiveId(move.Quantity);
                return ("inventory.php", fields);
            case PullStorageItem pull:
                fields["action"] = "pull";
                fields["whichitem1"] = PositiveId(pull.ItemId);
                fields["howmany1"] = PositiveId(pull.Quantity);
                return ("storage.php", fields);
            case DualWieldItem dual:
                fields["which"] = "2";
                fields["action"] = "dualwield";
                fields["whichitem"] = PositiveId(dual.ItemId);
                return ("inv_equip.php", fields);
            case EquipCharacterFamiliar familiarEquipment:
                fields["action"] = familiarEquipment.ItemId is null ? "unequip" : "equip";
                fields[familiarEquipment.ItemId is null ? "famid" : "whichfam"] = PositiveId(familiarEquipment.FamiliarId);
                if (familiarEquipment.ItemId is { } item) fields["whichitem"] = PositiveId(item);
                return ("familiar.php", fields);
            default:
                throw new ArgumentException("Unknown character command.", nameof(command));
        }
    }

    private static string PositiveId(int id)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        return id.ToString(CultureInfo.InvariantCulture);
    }

    private static string? ObserveRejection(CharacterCommand command, string response)
        => command switch
        {
            RequestFriarBlessing when response.Contains("one of those per day", StringComparison.OrdinalIgnoreCase)
                => "KoL reports that today's Friar blessing has already been used.",
            _ => null
        };

    private static string Plain(string html)
    {
        var plain = Regex.Replace(Regex.Replace(html, @"(?is)<(script|style).*?</\1>", " "), "<[^>]+>", " ");
        return Regex.Replace(WebUtility.HtmlDecode(plain), @"\s+", " ").Trim();
    }

    private static bool LooksLikeKoLError(string plain)
        => plain.Length is > 0 and < 400 && Regex.IsMatch(plain,
            @"(?i)\b(invalid|bad|wrong|missing)\b.{0,30}\b(pwd|password|hash|session)\b|\b(you|i) (can't|cannot|don't have|do not have|aren't|are not)\b|\bnot (a )?valid\b|\bno such\b|\bnot allowed\b|\byou need\b|\bsomething went wrong\b");
}
