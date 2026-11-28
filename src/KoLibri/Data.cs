using System.Globalization;
using KoLibri.Internal;
using CoreSpiel = KoLibri.Core.Spiel;

namespace KoLibri
{
    public readonly record struct ItemRef(int Id)
    {
        public static implicit operator ItemRef(int id) => new(id);
    }
    public readonly record struct SkillRef(int Id)
    {
        public static implicit operator SkillRef(int id) => new(id);
    }
    public readonly record struct Location(int Snarfblat)
    {
        public static implicit operator Location(int snarfblat) => new(snarfblat);
    }

    public sealed record Status(string PlayerName, int Level, long Meat, int Adventures, DateTimeOffset FetchedAt)
    {
        public int Hp { get; init; }
        public int MaxHp { get; init; }
        public int Mp { get; init; }
        public int MaxMp { get; init; }
        public OrganState Fullness { get; init; }
        public OrganState Inebriety { get; init; }
        public OrganState Spleen { get; init; }
        public IReadOnlyList<ActiveEffect> Effects { get; init; } = Array.Empty<ActiveEffect>();
        public int? FamiliarId { get; init; }
        public string? PathName { get; init; }
        public int? RoninTurnsRemaining { get; init; }
        public int? StoragePullsRemaining { get; init; }
        /// <summary>KoL-Spieler-Id aus api.php. Ergänzt in Schritt 3 (Desktop).</summary>
        public int? PlayerId { get; init; }
        /// <summary>Name und Gewicht des aktiven Familiars. Ergänzt in Schritt 3 (Desktop-Charpane).</summary>
        public string? FamiliarName { get; init; }
        public int? FamiliarWeight { get; init; }
        /// <summary>Pfad-Id (0 = kein Pfad), Aszensionen, Hardcore. Ergänzt in Schritt 3 (Desktop).</summary>
        public int? PathId { get; init; }
        public int? Ascensions { get; init; }
        public bool? Hardcore { get; init; }
    }

    public readonly record struct OrganState(int Current, int? Limit);
    public sealed record ActiveEffect(int Id, string Name, int Turns);

    public sealed record InventoryItem(ItemRef Item, string Name, int Quantity);

    public sealed record Inventory(IReadOnlyList<InventoryItem> Items, DateTimeOffset FetchedAt)
    {
        public int Count(ItemRef item) => Items.Where(i => i.Item == item).Sum(i => i.Quantity);
    }

    public sealed record Equipment(IReadOnlyDictionary<EquipSlot, ItemRef?> Slots, DateTimeOffset FetchedAt);

    public enum EquipSlot { Hat, Back, Shirt, Weapon, OffHand, Pants, Accessory1, Accessory2, Accessory3, Familiar }

    public sealed class InventoryReader
    {
        private readonly KolibriSession _session;

        internal InventoryReader(KolibriSession session) => _session = session;

        public async Task<Inventory> GetAsync(bool refresh = false, CancellationToken cancellationToken = default)
        {
            var snapshot = await _session.ReadAsync(r => r.FetchInventoryAsync(refresh, cancellationToken), cancellationToken).ConfigureAwait(false);
            return Mapping.ToInventory(snapshot);
        }

        public async Task<Inventory> GetClosetAsync(CancellationToken cancellationToken = default)
            => Mapping.ToInventory(await _session.ReadAsync(r => r.FetchClosetAsync(cancellationToken), cancellationToken).ConfigureAwait(false));

        public async Task<Inventory> GetStorageAsync(CancellationToken cancellationToken = default)
            => Mapping.ToInventory(await _session.ReadAsync(r => r.FetchStorageAsync(cancellationToken), cancellationToken).ConfigureAwait(false));

        public async Task<Equipment> GetEquipmentAsync(bool refresh = false, CancellationToken cancellationToken = default)
        {
            var status = await _session.ReadAsync(r => r.FetchStatusAsync(refresh, cancellationToken), cancellationToken).ConfigureAwait(false);
            return Mapping.ToEquipment(status);
        }
    }

    /// <summary>KoLmafia-Daten: Items, Skills, Monster, Orte. Aus DataDirectory oder dem mitgelieferten Ordner.</summary>
    public sealed class GameData
    {
        private readonly Lazy<Catalog> _catalog;

        internal GameData(string? dataDirectory)
            => _catalog = new Lazy<Catalog>(() => Catalog.Load(dataDirectory), LazyThreadSafetyMode.ExecutionAndPublication);

        public ItemInfo? Item(ItemRef item) => _catalog.Value.ItemsById.TryGetValue(item.Id, out var info) ? info : null;
        public ItemInfo? FindItem(string name)
            => string.IsNullOrWhiteSpace(name) ? null : _catalog.Value.ItemsByName.TryGetValue(name.Trim(), out var info) ? info : null;
        public SkillInfo? Skill(SkillRef skill) => _catalog.Value.SkillsById.TryGetValue(skill.Id, out var info) ? info : null;
        public SkillInfo? FindSkill(string name)
            => string.IsNullOrWhiteSpace(name) ? null : _catalog.Value.SkillsByName.TryGetValue(name.Trim(), out var info) ? info : null;
        public LocationInfo? Location(Location location) => _catalog.Value.LocationsById.TryGetValue(location.Snarfblat, out var info) ? info : null;
        public LocationInfo? FindLocation(string name)
            => string.IsNullOrWhiteSpace(name) ? null : _catalog.Value.LocationsByName.TryGetValue(name.Trim(), out var info) ? info : null;
        public MonsterInfo? FindMonster(string name)
            => string.IsNullOrWhiteSpace(name) ? null : _catalog.Value.MonstersByName.TryGetValue(name.Trim(), out var info) ? info : null;

        internal Func<string, string?> ItemNameResolver => id
            => int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && Item(n) is { } info ? info.Name : null;

        internal int EffectId(string name) => _catalog.Value.EffectIds.TryGetValue(name.Trim(), out var id) ? id : 0;

        private sealed class Catalog
        {
            public Dictionary<int, ItemInfo> ItemsById { get; } = new();
            public Dictionary<string, ItemInfo> ItemsByName { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<int, SkillInfo> SkillsById { get; } = new();
            public Dictionary<string, SkillInfo> SkillsByName { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<int, LocationInfo> LocationsById { get; } = new();
            public Dictionary<string, LocationInfo> LocationsByName { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, MonsterInfo> MonstersByName { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, int> EffectIds { get; } = new(StringComparer.OrdinalIgnoreCase);

            public static Catalog Load(string? directory)
            {
                var result = new Catalog();
                var path = directory ?? new[]
                {
                    Path.Combine(AppContext.BaseDirectory, "external", "kolmafia-data"),
                    Path.Combine(AppContext.BaseDirectory, "kolmafia-data"),
                    Path.Combine(Directory.GetCurrentDirectory(), "external", "kolmafia-data"),
                }.FirstOrDefault(Directory.Exists);
                if (path is null || !Directory.Exists(path)) return result;
                var data = new CoreSpiel.MafiaDataLoader().Load(path);

                var organs = data.Organs
                    .Where(o => o.Size > 0)
                    .GroupBy(o => o.ItemId ?? o.Name, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First().Size, StringComparer.OrdinalIgnoreCase);
                foreach (var item in data.Items)
                {
                    if (!int.TryParse(item.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || string.IsNullOrWhiteSpace(item.Name)) continue;
                    var (use, slot) = Classify(item);
                    var info = new ItemInfo(id, item.Name, use, item.Tradeable || item.AccessFlags.Contains("t"), item.Autosell)
                    {
                        OrganSize = organs.TryGetValue(item.Id, out var size) || organs.TryGetValue(item.Name, out size) ? size : 0,
                        Slot = slot
                    };
                    result.ItemsById.TryAdd(id, info);
                    result.ItemsByName.TryAdd(item.Name, info);
                }
                foreach (var skill in data.Skills.Where(s => !string.IsNullOrWhiteSpace(s.Name)))
                {
                    var info = new SkillInfo(skill.Id, skill.Name, skill.MpCost,
                        skill.Tags.Any(t => string.Equals(t, "combat", StringComparison.OrdinalIgnoreCase)));
                    result.SkillsById.TryAdd(skill.Id, info);
                    result.SkillsByName.TryAdd(skill.Name, info);
                }
                foreach (var adventure in data.Adventures.Where(a => a.Snarfblat > 0 && !string.IsNullOrWhiteSpace(a.Name)))
                {
                    var info = new LocationInfo(adventure.Snarfblat, adventure.Name, adventure.Zone);
                    result.LocationsById.TryAdd(adventure.Snarfblat, info);
                    result.LocationsByName.TryAdd(adventure.Name, info);
                }
                foreach (var monster in data.Monsters.Where(m => !string.IsNullOrWhiteSpace(m.Name)))
                {
                    result.MonstersByName.TryAdd(monster.Name, new MonsterInfo(
                        int.TryParse(monster.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var mid) ? mid : 0,
                        monster.Name, Positive(monster.Hp), Positive(monster.Attack), Positive(monster.Defense)));
                }
                foreach (var effect in data.Effects.Where(e => !string.IsNullOrWhiteSpace(e.Name)))
                    result.EffectIds.TryAdd(effect.Name, effect.Id);
                return result;
            }

            private static int? Positive(int value) => value > 0 ? value : null;

            private static (ItemUse, EquipSlot?) Classify(CoreSpiel.MafiaItemRecord item)
            {
                var primary = item.PrimaryUse.Select(u => u.ToLowerInvariant()).ToArray();
                var secondary = item.SecondaryUse.Select(u => u.ToLowerInvariant()).ToArray();
                if (primary.Contains("food")) return (ItemUse.Food, null);
                if (primary.Contains("drink")) return (ItemUse.Drink, null);
                if (primary.Contains("spleen")) return (ItemUse.Spleen, null);
                if (primary.Contains("hat")) return (ItemUse.Equipment, EquipSlot.Hat);
                if (primary.Contains("container")) return (ItemUse.Equipment, EquipSlot.Back);
                if (primary.Contains("shirt")) return (ItemUse.Equipment, EquipSlot.Shirt);
                if (primary.Contains("weapon")) return (ItemUse.Equipment, EquipSlot.Weapon);
                if (primary.Contains("offhand")) return (ItemUse.Equipment, EquipSlot.OffHand);
                if (primary.Contains("pants")) return (ItemUse.Equipment, EquipSlot.Pants);
                if (primary.Contains("accessory")) return (ItemUse.Equipment, EquipSlot.Accessory1);
                if (primary.Contains("familiar")) return (ItemUse.Familiar, null);
                if (secondary.Any(s => s.StartsWith("multiple", StringComparison.Ordinal))) return (ItemUse.MultiUsable, null);
                if (item.Usable || primary.Contains("potion") || secondary.Any(s => s.StartsWith("usable", StringComparison.Ordinal)))
                    return (ItemUse.Usable, null);
                if (secondary.Any(s => s.StartsWith("combat", StringComparison.Ordinal))) return (ItemUse.Combat, null);
                return (ItemUse.None, null);
            }
        }
    }

    public enum ItemUse { None, Food, Drink, Spleen, Usable, MultiUsable, Equipment, Familiar, Combat }

    public sealed record ItemInfo(ItemRef Item, string Name, ItemUse Use, bool Tradeable, int AutosellPrice)
    {
        public int OrganSize { get; init; }
        public EquipSlot? Slot { get; init; }
    }
    public sealed record SkillInfo(SkillRef Skill, string Name, int MpCost, bool UsableInCombat);
    public sealed record LocationInfo(Location Location, string Name, string Zone);
    public sealed record MonsterInfo(int Id, string Name, int? Hp, int? Attack, int? Defense);

    /// <summary>Einmal-pro-KoL-Tag-Marker (KoL-Rollover) je Konto. Baustein für eigene Daily-Routinen.</summary>
    public sealed class DailyMarkers
    {
        private readonly CoreSpiel.DailyDoneStore _store;
        private readonly Func<string> _accountKey;

        internal DailyMarkers(string? stateDirectory, Func<string> accountKey, TimeProvider? clock = null)
        {
            _store = new CoreSpiel.DailyDoneStore(stateDirectory is null ? null : Path.Combine(stateDirectory, "daily-done.json"), clock);
            _accountKey = accountKey;
        }

        public DateOnly GameDay => DateOnly.ParseExact(CoreSpiel.DailyDoneStore.KolDay(_store.Now), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        public bool IsDone(string key) => _store.IsDone(_accountKey(), Key(key));
        public void MarkDone(string key) => _store.MarkDone(_accountKey(), Key(key));
        public IReadOnlyList<string> DoneToday() => _store.DoneToday(_accountKey());

        internal Task<FileStream> LockActionAsync(string action, CancellationToken cancellationToken)
            => _store.LockActionAsync(action, cancellationToken);

        private static string Key(string key)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            return key.Trim();
        }
    }
}

namespace KoLibri.Internal
{
    using CoreBasis = KoLibri.Core.Basis;

    /// <summary>Maps core snapshots to the public records.</summary>
    internal static class Mapping
    {
        public static Status ToStatus(CoreSpiel.KoLStatusSnapshot s, GameData data)
        {
            var known = s.OrganLimitConfidence != CoreSpiel.OrganLimitConfidence.Unknown;
            return new Status(s.PlayerName ?? string.Empty, s.Level, s.Meat, s.Adventures, s.FetchedAt)
            {
                Hp = s.CurrentHp,
                MaxHp = s.MaximumHp,
                Mp = s.CurrentMp,
                MaxMp = s.MaximumMp,
                Fullness = new OrganState(s.Fullness ?? 0, known ? s.FullnessLimit : null),
                Inebriety = new OrganState(s.Inebriety ?? 0, known ? s.InebrietyLimit : null),
                Spleen = new OrganState(s.Spleen ?? 0, known ? s.SpleenLimit : null),
                Effects = s.TimedEffects.Select(e => new ActiveEffect(data.EffectId(e.Name), e.Name, e.TurnsRemaining)).ToArray(),
                FamiliarId = int.TryParse(s.FamiliarId, NumberStyles.None, CultureInfo.InvariantCulture, out var fam) ? fam : null,
                PathName = s.PathName,
                RoninTurnsRemaining = s.RoninTurnsRemaining,
                StoragePullsRemaining = s.StoragePullsRemaining,
                PlayerId = int.TryParse(s.PlayerId, NumberStyles.None, CultureInfo.InvariantCulture, out var player) ? player : null,
                FamiliarName = string.IsNullOrWhiteSpace(s.FamiliarName) ? null : s.FamiliarName.Trim(),
                FamiliarWeight = s.FamiliarWeight,
                PathId = s.PathId,
                Ascensions = s.Ascensions,
                Hardcore = s.Hardcore
            };
        }

        public static Inventory ToInventory(CoreSpiel.KoLInventorySnapshot s)
            => new(s.Items
                .Select(i => int.TryParse(i.ItemId, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                    ? new InventoryItem(id, i.Name, i.Quantity) : null)
                .OfType<InventoryItem>()
                .ToArray(), s.FetchedAt);

        private static readonly (string Key, EquipSlot Slot)[] SlotKeys =
        {
            ("hat", EquipSlot.Hat), ("container", EquipSlot.Back), ("shirt", EquipSlot.Shirt), ("weapon", EquipSlot.Weapon),
            ("offhand", EquipSlot.OffHand), ("pants", EquipSlot.Pants), ("acc1", EquipSlot.Accessory1),
            ("acc2", EquipSlot.Accessory2), ("acc3", EquipSlot.Accessory3), ("familiarequip", EquipSlot.Familiar),
        };

        public static Equipment ToEquipment(CoreSpiel.KoLStatusSnapshot s)
        {
            var slots = new Dictionary<EquipSlot, ItemRef?>();
            foreach (var (key, slot) in SlotKeys)
            {
                ItemRef? item = null;
                if ((s.Equipment.TryGetValue(key, out var raw) || (slot == EquipSlot.Familiar && s.Equipment.TryGetValue("familiar", out raw)))
                    && int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0)
                    item = id;
                slots[slot] = item;
            }
            return new Equipment(slots, s.FetchedAt);
        }

        public static CoreSpiel.CharacterEquipmentSlot ToCoreSlot(EquipSlot slot) => slot switch
        {
            EquipSlot.Hat => Parse("Hat"),
            EquipSlot.Back => Parse("Back", "Container"),
            EquipSlot.Shirt => Parse("Shirt"),
            EquipSlot.Weapon => Parse("Weapon"),
            EquipSlot.OffHand => Parse("OffHand", "Offhand"),
            EquipSlot.Pants => Parse("Pants"),
            EquipSlot.Accessory1 => Parse("Accessory1", "Acc1"),
            EquipSlot.Accessory2 => Parse("Accessory2", "Acc2"),
            EquipSlot.Accessory3 => Parse("Accessory3", "Acc3"),
            EquipSlot.Familiar => Parse("Familiar", "FamiliarEquipment"),
            _ => throw new ArgumentOutOfRangeException(nameof(slot))
        };

        private static CoreSpiel.CharacterEquipmentSlot Parse(params string[] names)
        {
            foreach (var name in names)
                if (Enum.TryParse<CoreSpiel.CharacterEquipmentSlot>(name, ignoreCase: true, out var slot)) return slot;
            throw new NotSupportedException($"Equipment slot {names[0]} is not supported yet.");
        }

        public static IReadOnlyList<InventoryItem> Gained(Inventory before, Inventory after)
            => Diff(before, after, positive: true);

        public static IReadOnlyList<InventoryItem> Lost(Inventory before, Inventory after)
            => Diff(before, after, positive: false);

        private static IReadOnlyList<InventoryItem> Diff(Inventory before, Inventory after, bool positive)
        {
            var ids = before.Items.Select(i => i.Item).Concat(after.Items.Select(i => i.Item)).Distinct();
            var list = new List<InventoryItem>();
            foreach (var id in ids)
            {
                var delta = after.Count(id) - before.Count(id);
                if (positive ? delta > 0 : delta < 0)
                {
                    var name = after.Items.Concat(before.Items).First(i => i.Item == id).Name;
                    list.Add(new InventoryItem(id, name, Math.Abs(delta)));
                }
            }
            return list;
        }

        public static bool IsLoginPage(Exception exception)
            => exception is CoreBasis.KoLHtmlResponseException { Kind: CoreBasis.KoLHtmlResponseKind.LoginPage };
    }
}
