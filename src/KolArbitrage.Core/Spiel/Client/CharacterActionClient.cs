using System.Globalization;
using System.Net;
using KolArbitrage.Core.Basis;

namespace KolArbitrage.Core.Spiel;

public enum CharacterEquipmentSlot
{
    Hat, Weapon, Offhand, Container, Shirt, Pants, Accessory1, Accessory2, Accessory3
}

public abstract record CharacterAction;
public sealed record UseInventoryItem(int ItemId) : CharacterAction;
public sealed record EquipInventoryItem(int ItemId, CharacterEquipmentSlot Slot, bool DualWield = false) : CharacterAction;
public sealed record UnequipInventoryItem(CharacterEquipmentSlot Slot) : CharacterAction;
public sealed record WearCharacterOutfit(int OutfitId) : CharacterAction;
public sealed record SelectCharacterFamiliar(int FamiliarId) : CharacterAction;
public sealed record EquipCharacterFamiliar(int FamiliarId, int? ItemId) : CharacterAction;

public enum CharacterConsumableKind { Food, Drink, Spleen }

public sealed record ConsumeCharacterItem(
    int ItemId, CharacterConsumableKind Kind, int Quantity = 1) : CharacterAction;
public sealed record CastCharacterSkill(int SkillId, int Quantity = 1) : CharacterAction;
public sealed record RestAtCampground : CharacterAction;

public sealed record MoveClosetItem(int ItemId, int Quantity, bool IntoCloset) : CharacterAction;
public sealed record PullStorageItem(int ItemId, int Quantity) : CharacterAction;
public sealed record CombineCharacterItems(int OutputId, int FirstItemId, int SecondItemId) : CharacterAction;

public enum FriarBlessing { Food = 1, Familiar = 2, Booze = 3 }
public sealed record RequestFriarBlessing(FriarBlessing Blessing) : CharacterAction;
public sealed record PerformFriarRitual : CharacterAction;

public enum FriarsPageState { Unknown, CollectingItems, ReadyForRitual, RitualComplete }

public sealed record CharacterMoodRule(string EffectName, int SkillId, int ManaCost, int RenewAtTurns = 1);
public sealed record CharacterMoodResult(bool CastSubmitted, string Message, CharacterActionSnapshot Snapshot);

public sealed record CharacterStorageSnapshot(
    KoLStatusSnapshot Status, KoLInventorySnapshot Closet, KoLInventorySnapshot Storage);

public sealed record CharacterActionSnapshot(KoLStatusSnapshot Status, KoLInventorySnapshot Inventory)
{
    public string? RejectionReason { get; init; }
}

/// <summary>Submits one explicit character action and returns fresh account observations.</summary>
public sealed class CharacterActionClient
{
    private readonly HttpClient _http;
    private readonly KoLSecurityPolicy _security;
    private readonly KoLAccountReadClient _reader;
    private readonly string _passwordHash;

    public CharacterActionClient(HttpClient http, string passwordHash, KoLSecurityPolicy? security = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        _security = security ?? new KoLSecurityPolicy();
        _security.Validate();
        _security.EnsureAllowed(http.BaseAddress ?? throw new ArgumentException("A KoL base address is required.", nameof(http)));
        _http = http;
        _passwordHash = passwordHash;
        _reader = new KoLAccountReadClient(http, http.BaseAddress, securityPolicy: _security);
    }

    /// <summary>
    /// The response snapshot allows callers to verify the observed result.
    /// A failure after submission is indeterminate and must not be retried blindly.
    /// </summary>
    public async Task<CharacterActionSnapshot> ExecuteAsync(
        CharacterAction action, CancellationToken cancellationToken = default)
    {
        var (route, fields) = BuildRequest(action);
        var before = action is CombineCharacterItems
            ? await _reader.FetchInventoryAsync(forceRefresh: true, cancellationToken).ConfigureAwait(false)
            : null;
        if (action is CombineCharacterItems combine && before is not null &&
            (before.IsStale || new[] { combine.FirstItemId, combine.SecondItemId }.GroupBy(id => id)
                .Any(group => CountItem(before, group.Key) < group.Count())))
            throw new InvalidOperationException("Fresh inventory does not contain the recipe ingredients.");
        fields["pwd"] = _passwordHash;
        var uri = new Uri(_http.BaseAddress!, route);
        _security.EnsureAllowed(uri, "Character action");
        using var body = new FormUrlEncodedContent(fields);
        using var response = await _http.PostAsync(uri, body, cancellationToken).ConfigureAwait(false);
        _security.EnsureResponseUri(uri, response.RequestMessage?.RequestUri);
        if (response.StatusCode is >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest)
            throw new InvalidOperationException("The character action returned a redirect. Refresh the account before trying again.");
        response.EnsureSuccessStatusCode();
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (KoLHtmlResponseClassifier.Classify(responseText) == KoLHtmlResponseKind.LoginPage)
            throw new KoLHtmlResponseException(KoLHtmlResponseKind.LoginPage, "KoL returned a login page.");
        var snapshot = await ReadAsync(cancellationToken).ConfigureAwait(false);
        var rejection = ObserveRejection(action, responseText);
        if (action is CombineCharacterItems craft && before is not null &&
            (snapshot.Inventory.IsStale || CountItem(snapshot.Inventory, craft.OutputId) <= CountItem(before, craft.OutputId)))
            rejection = "KoL did not confirm a crafted item in inventory.";
        return snapshot with { RejectionReason = rejection };
    }

    private static int CountItem(KoLInventorySnapshot inventory, int id)
        => inventory.Items.Where(item => item.ItemId == id.ToString(CultureInfo.InvariantCulture))
            .Sum(item => item.Quantity);

    private static string? ObserveRejection(CharacterAction action, string response)
        => action switch
        {
            ConsumeCharacterItem { Kind: CharacterConsumableKind.Food }
                when response.Contains("too full", StringComparison.OrdinalIgnoreCase)
                => "KoL refused the food because you are too full.",
            ConsumeCharacterItem { Kind: CharacterConsumableKind.Drink }
                when response.Contains("too drunk", StringComparison.OrdinalIgnoreCase)
                => "KoL refused the drink because you are too drunk.",
            ConsumeCharacterItem { Kind: CharacterConsumableKind.Spleen }
                when response.Contains("rupture", StringComparison.OrdinalIgnoreCase)
                => "KoL refused the item because it would exceed your spleen limit.",
            RequestFriarBlessing when response.Contains("one of those per day", StringComparison.OrdinalIgnoreCase)
                => "KoL reports that today's Friar blessing has already been used.",
            _ => null
        };

    public async Task<CharacterActionSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        var status = await _reader.FetchStatusAsync(forceRefresh: true, cancellationToken).ConfigureAwait(false);
        var inventory = await _reader.FetchInventoryAsync(forceRefresh: true, cancellationToken).ConfigureAwait(false);
        return new CharacterActionSnapshot(status, inventory);
    }

    public async Task<CharacterMoodResult> EnsureEffectAsync(
        CharacterMoodRule rule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentException.ThrowIfNullOrWhiteSpace(rule.EffectName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rule.SkillId);
        ArgumentOutOfRangeException.ThrowIfNegative(rule.ManaCost);
        ArgumentOutOfRangeException.ThrowIfNegative(rule.RenewAtTurns);
        var before = await ReadAsync(cancellationToken).ConfigureAwait(false);
        var duration = EffectTurns(before.Status, rule.EffectName);
        if (before.Status.IsStale || before.Inventory.IsStale)
            return new(false, "Refresh character state before renewing this effect.", before);
        if (duration is null && before.Status.Effects.Contains(rule.EffectName, StringComparer.OrdinalIgnoreCase))
            return new(false, "The effect is present but its duration is unknown.", before);
        if (duration > rule.RenewAtTurns)
            return new(false, $"The effect still has {duration} turns remaining.", before);
        if (before.Status.CurrentMp < rule.ManaCost)
            return new(false, $"Need {rule.ManaCost} MP to renew {rule.EffectName}.", before);

        var after = await ExecuteAsync(new CastCharacterSkill(rule.SkillId), cancellationToken).ConfigureAwait(false);
        var refreshedDuration = EffectTurns(after.Status, rule.EffectName);
        var confirmed = !after.Status.IsStale && refreshedDuration > (duration ?? 0);
        return new(true, confirmed ? $"{rule.EffectName} renewed for {refreshedDuration} turns."
            : $"KoL did not confirm that {rule.EffectName} was renewed.", after);
    }

    private static int? EffectTurns(KoLStatusSnapshot status, string effectName)
        => status.TimedEffects.FirstOrDefault(effect =>
            string.Equals(effect.Name, effectName, StringComparison.OrdinalIgnoreCase))?.TurnsRemaining;

    public async Task<FriarsPageState> ReadFriarsAsync(CancellationToken cancellationToken = default)
    {
        var html = await _reader.FetchFriarsPageAsync(cancellationToken).ConfigureAwait(false);
        if (KoLHtmlResponseClassifier.Classify(html) == KoLHtmlResponseKind.LoginPage)
            throw new KoLHtmlResponseException(KoLHtmlResponseKind.LoginPage, "KoL returned a login page.");
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

    public async Task<CharacterStorageSnapshot> ReadStorageAsync(CancellationToken cancellationToken = default)
    {
        var status = await _reader.FetchStatusAsync(forceRefresh: true, cancellationToken).ConfigureAwait(false);
        var closet = await _reader.FetchClosetAsync(cancellationToken).ConfigureAwait(false);
        var storage = await _reader.FetchStorageAsync(cancellationToken).ConfigureAwait(false);
        return new CharacterStorageSnapshot(status, closet, storage);
    }

    private static (string Route, Dictionary<string, string> Fields) BuildRequest(CharacterAction action)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal) { ["ajax"] = "1" };
        switch (action)
        {
            case UseInventoryItem use:
                fields["which"] = "3";
                fields["whichitem"] = PositiveId(use.ItemId);
                return ("inv_use.php", fields);
            case PerformFriarRitual:
                fields.Remove("ajax");
                fields["action"] = "ritual";
                return ("friars.php", fields);
            case RequestFriarBlessing blessing:
                if (!Enum.IsDefined(blessing.Blessing)) throw new ArgumentOutOfRangeException(nameof(action));
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
            case ConsumeCharacterItem consume:
                fields["whichitem"] = PositiveId(consume.ItemId);
                fields["quantity"] = PositiveId(consume.Quantity);
                return (consume.Kind switch
                {
                    CharacterConsumableKind.Food => "inv_eat.php",
                    CharacterConsumableKind.Drink => "inv_booze.php",
                    CharacterConsumableKind.Spleen => "inv_spleen.php",
                    _ => throw new ArgumentOutOfRangeException(nameof(action))
                }, fields);
            case CastCharacterSkill cast:
                fields["action"] = "Skillz";
                fields["whichskill"] = PositiveId(cast.SkillId);
                fields["quantity"] = PositiveId(cast.Quantity);
                return ("runskillz.php", fields);
            case RestAtCampground:
                fields.Remove("ajax");
                fields["action"] = "rest";
                return ("campground.php", fields);
            case EquipInventoryItem equip:
                fields["which"] = "2";
                var slot = SlotName(equip.Slot);
                if (equip.DualWield && equip.Slot != CharacterEquipmentSlot.Offhand)
                    throw new ArgumentException("Dual wield requires the offhand slot.", nameof(action));
                fields["action"] = equip.DualWield ? "dualwield" : "equip";
                fields["whichitem"] = PositiveId(equip.ItemId);
                if (slot.StartsWith("acc", StringComparison.Ordinal)) fields["slot"] = slot[3..];
                return ("inv_equip.php", fields);
            case UnequipInventoryItem unequip:
                fields["which"] = "2";
                fields["action"] = "unequip";
                fields["type"] = SlotName(unequip.Slot);
                return ("inv_equip.php", fields);
            case WearCharacterOutfit outfit:
                ArgumentOutOfRangeException.ThrowIfZero(outfit.OutfitId);
                fields["which"] = "2";
                fields["action"] = "outfit";
                fields["whichoutfit"] = outfit.OutfitId.ToString(CultureInfo.InvariantCulture);
                return ("inv_equip.php", fields);
            case SelectCharacterFamiliar familiar:
                fields["action"] = "newfam";
                fields["newfam"] = PositiveId(familiar.FamiliarId);
                return ("familiar.php", fields);
            case EquipCharacterFamiliar familiarEquipment:
                fields["action"] = familiarEquipment.ItemId is null ? "unequip" : "equip";
                fields[familiarEquipment.ItemId is null ? "famid" : "whichfam"] = PositiveId(familiarEquipment.FamiliarId);
                if (familiarEquipment.ItemId is { } item) fields["whichitem"] = PositiveId(item);
                return ("familiar.php", fields);
            default:
                throw new ArgumentException("Unknown character action.", nameof(action));
        }
    }

    private static string PositiveId(int id)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        return id.ToString(CultureInfo.InvariantCulture);
    }

    public static string SlotName(CharacterEquipmentSlot slot) => slot switch
    {
        CharacterEquipmentSlot.Hat => "hat",
        CharacterEquipmentSlot.Weapon => "weapon",
        CharacterEquipmentSlot.Offhand => "offhand",
        CharacterEquipmentSlot.Container => "container",
        CharacterEquipmentSlot.Shirt => "shirt",
        CharacterEquipmentSlot.Pants => "pants",
        CharacterEquipmentSlot.Accessory1 => "acc1",
        CharacterEquipmentSlot.Accessory2 => "acc2",
        CharacterEquipmentSlot.Accessory3 => "acc3",
        _ => throw new ArgumentOutOfRangeException(nameof(slot))
    };
}
