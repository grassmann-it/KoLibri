using System.Collections.ObjectModel;
using System.Globalization;

namespace KoLibri.Desktop;

/// <summary>What a row of the location picker is: a section heading or a location.</summary>
public enum LocationPickerEntryKind
{
    Header,
    Location
}

/// <summary>
/// One row of the location picker. Headers open/close a zone; location rows carry the catalog row.
/// <see cref="Detail"/> is the secondary text after the name ("· 439", plus the zone in Favorites and Recent).
/// </summary>
public sealed record LocationPickerEntry(
    LocationPickerEntryKind Kind,
    string Section,
    string Label,
    string Detail,
    EncounterBrowserLocationRow? Location = null,
    bool IsFavorite = false,
    bool IsLocked = false,
    string? LockReason = null,
    bool IsExpanded = true,
    int Count = 0)
{
    public bool IsHeader => Kind == LocationPickerEntryKind.Header;
    public bool IsLocation => Kind == LocationPickerEntryKind.Location;
    public string Chevron => IsExpanded ? "▾" : "▸";
    public string Star => IsFavorite ? "★" : "☆";
    public string StarTip => IsFavorite ? "Remove from favorites" : "Add to favorites";
    public double RowOpacity => IsLocked ? 0.45 : 1.0;
    public string? Tip => IsLocked ? LockReason ?? "Not accessible right now" : null;
    public string CountText => Count.ToString(CultureInfo.InvariantCulture);

    /// <summary>"The Skeleton Store · 439": the name with the snarfblat as secondary id.</summary>
    public string AccessibleName => Location is { } l
        ? l.Snarfblat > 0 ? $"{l.DisplayName} · {l.Snarfblat}" : l.DisplayName
        : $"{Label} ({Count})";
}

/// <summary>
/// The searchable location picker of the Adventure tab, used by "Adventure once" and the auto-combat batch
/// ("Run turns"). It is a two-level list: Favorites and Recently used on top, then every zone of the
/// KoLmafia catalog with its locations. Typing filters incrementally (each word a case-insensitive part of
/// the name, the zone, or the snarfblat), arrow keys move between locations, Enter chooses.
/// The picker never sends a request: access is shown only when it is already known (<see cref="SetAccess"/>).
/// </summary>
public sealed class LocationPickerViewModel : DesktopViewModel
{
    public const string FavoritesSection = "Favorites";
    public const string RecentSection = "Recently used";
    public const int RecentLimit = 5;
    public const int FavoriteLimit = 50;

    private readonly IReadOnlyList<EncounterBrowserLocationRow> _all;
    private readonly Dictionary<string, EncounterBrowserLocationRow> _byId;
    private readonly List<string> _favorites;
    private readonly List<string> _recent;
    private readonly HashSet<string> _collapsed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (bool Accessible, string? Reason)> _access = new(StringComparer.OrdinalIgnoreCase);
    private string _searchText = string.Empty;
    private LocationPickerEntry? _selected;

    public LocationPickerViewModel(
        IEnumerable<EncounterBrowserLocationRow> locations,
        IEnumerable<string>? favorites = null,
        IEnumerable<string>? recent = null)
    {
        ArgumentNullException.ThrowIfNull(locations);
        _all = locations.Where(l => !string.IsNullOrWhiteSpace(l.LocationId))
            .GroupBy(l => l.LocationId, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToArray();
        _byId = _all.ToDictionary(l => l.LocationId, StringComparer.OrdinalIgnoreCase);
        _favorites = Known(favorites).Take(FavoriteLimit).ToList();
        _recent = Known(recent).Take(RecentLimit).ToList();
        // Zones start closed so the list opens short; a search opens every zone that matches.
        foreach (var zone in _all.Select(l => ZoneOf(l)).Distinct(StringComparer.OrdinalIgnoreCase))
            _collapsed.Add(zone);
        Rebuild();
    }

    /// <summary>The flat two-level list the view shows (headers and locations).</summary>
    public ObservableCollection<LocationPickerEntry> Entries { get; } = new();

    /// <summary>Favorite location ids in the order they were starred (stored in the desktop settings).</summary>
    public IReadOnlyList<string> Favorites => _favorites;

    /// <summary>The last used location ids, newest first, at most <see cref="RecentLimit"/>.</summary>
    public IReadOnlyList<string> Recent => _recent;

    /// <summary>Raised when the favorites or the recent list changed, so the shell can save them.</summary>
    public event EventHandler? ListsChanged;

    /// <summary>Raised when a location is chosen (Enter, double-click).</summary>
    public event EventHandler<EncounterBrowserLocationRow>? LocationChosen;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value ?? string.Empty)) return;
            Rebuild(keepSelection: false);
            // Typing highlights the best hit (name starts with the search first), so Enter right after typing chooses it.
            var words = Words(_searchText);
            Selected = Entries.Where(e => e.IsLocation)
                .Select((e, i) => (Entry: e, Index: i, Score: words.Sum(w => Score(e.Location!.DisplayName, w))))
                .OrderByDescending(x => x.Score).ThenBy(x => x.Index)
                .Select(x => x.Entry).FirstOrDefault();
        }
    }

    public bool IsSearching => !string.IsNullOrWhiteSpace(_searchText);

    /// <summary>The highlighted row; only location rows can be selected.</summary>
    public LocationPickerEntry? Selected
    {
        get => _selected;
        set
        {
            if (value is { IsHeader: true }) return;
            if (!SetProperty(ref _selected, value)) return;
            OnPropertyChanged(nameof(SelectedLocation));
        }
    }

    public EncounterBrowserLocationRow? SelectedLocation => _selected?.Location;

    /// <summary>How many locations match the current search (each location counted once).</summary>
    public int MatchCount => _all.Count(l => Matches(l, _searchText));

    /// <summary>"No location matches …" while a search finds nothing, otherwise null.</summary>
    public string? EmptyMessage => IsSearching && MatchCount == 0
        ? $"No location matches \"{_searchText.Trim()}\"."
        : null;

    public EncounterBrowserLocationRow? Find(string locationId)
        => _byId.TryGetValue(locationId ?? string.Empty, out var row) ? row : null;

    public bool IsFavorite(string locationId) => _favorites.Contains(locationId, StringComparer.OrdinalIgnoreCase);

    /// <summary>Stars or un-stars a location.</summary>
    public void ToggleFavorite(string locationId)
    {
        if (!_byId.ContainsKey(locationId ?? string.Empty)) return;
        var at = _favorites.FindIndex(f => string.Equals(f, locationId, StringComparison.OrdinalIgnoreCase));
        if (at >= 0) _favorites.RemoveAt(at);
        else if (_favorites.Count < FavoriteLimit) _favorites.Add(_byId[locationId!].LocationId);
        else return;
        Rebuild();
        ListsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Puts a location at the top of Recently used (it is called when a run starts there).</summary>
    public void MarkUsed(string locationId)
    {
        if (!_byId.TryGetValue(locationId ?? string.Empty, out var row)) return;
        _recent.RemoveAll(r => string.Equals(r, row.LocationId, StringComparison.OrdinalIgnoreCase));
        _recent.Insert(0, row.LocationId);
        if (_recent.Count > RecentLimit) _recent.RemoveRange(RecentLimit, _recent.Count - RecentLimit);
        Rebuild();
        ListsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Records an access result that is already known (from a run that checked it). Locked locations stay in
    /// the list, grayed out with the reason as tooltip. Unknown locations are shown normally.
    /// </summary>
    public void SetAccess(int snarfblat, bool accessible, string? reason = null)
    {
        var changed = false;
        foreach (var row in _all.Where(l => l.Snarfblat > 0 && l.Snarfblat == snarfblat))
        {
            _access[row.LocationId] = (accessible, reason);
            changed = true;
        }
        if (changed) Rebuild();
    }

    /// <summary>Forgets every known access result (another account or a new ascension).</summary>
    public void ClearAccess()
    {
        if (_access.Count == 0) return;
        _access.Clear();
        Rebuild();
    }

    /// <summary>Opens or closes a zone. While searching every matching zone stays open.</summary>
    public void ToggleZone(string section)
    {
        if (section is FavoritesSection or RecentSection || IsSearching) return;
        if (!_collapsed.Remove(section)) _collapsed.Add(section);
        Rebuild();
    }

    public bool IsZoneExpanded(string zone) => IsSearching || !_collapsed.Contains(zone);

    /// <summary>Moves the highlight to the next (+1) or previous (−1) location row, skipping headers.</summary>
    public void Move(int delta)
    {
        if (delta == 0) return;
        var rows = Entries.Where(e => e.IsLocation).ToList();
        if (rows.Count == 0) return;
        var at = _selected is null ? -1 : rows.IndexOf(_selected);
        var next = at < 0
            ? (delta > 0 ? 0 : rows.Count - 1)
            : Math.Clamp(at + Math.Sign(delta), 0, rows.Count - 1);
        Selected = rows[next];
    }

    /// <summary>Chooses the highlighted location: raises <see cref="LocationChosen"/>; null when nothing is highlighted.</summary>
    public EncounterBrowserLocationRow? Choose()
    {
        if (_selected?.Location is not { } row) return null;
        LocationChosen?.Invoke(this, row);
        return row;
    }

    /// <summary>Highlights a location by id (for "Adventure here" from a menu); opens its zone when needed.</summary>
    public bool Select(string locationId)
    {
        if (!_byId.TryGetValue(locationId ?? string.Empty, out var row)) return false;
        var entry = FindEntry(row.LocationId);
        if (entry is null)
        {
            if (IsSearching && !Matches(row, _searchText)) { _searchText = string.Empty; OnPropertyChanged(nameof(SearchText)); }
            _collapsed.Remove(ZoneOf(row));
            Rebuild(keepSelection: false);
            entry = FindEntry(row.LocationId);
        }
        Selected = entry;
        return entry is not null;
    }

    /// <summary>
    /// Every word of the search must be part of the name, the zone or the snarfblat (case-insensitive),
    /// so "skel" finds The Skeleton Store, "cyrpt nook" The Defiled Nook, "439" the Skeleton Store.
    /// </summary>
    public static bool Matches(EncounterBrowserLocationRow row, string? search)
    {
        return Words(search).All(w =>
        {
            return row.DisplayName.Contains(w, StringComparison.OrdinalIgnoreCase)
                || ZoneOf(row).Contains(w, StringComparison.OrdinalIgnoreCase)
                || (row.Snarfblat > 0 && row.Snarfblat.ToString(CultureInfo.InvariantCulture).StartsWith(w, StringComparison.Ordinal));
        });
    }

    private static string[] Words(string? search)
        => (search ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(w => w.TrimStart('#')).Where(w => w.Length > 0).ToArray();

    /// <summary>3: the name (without "The ") starts with the word; 2: a word of the name does; 1: the name contains it.</summary>
    private static int Score(string name, string word)
    {
        if (SortName(name).StartsWith(word, StringComparison.OrdinalIgnoreCase)) return 3;
        if (name.Split(' ', '-', '\'').Any(part => part.StartsWith(word, StringComparison.OrdinalIgnoreCase))) return 2;
        return name.Contains(word, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
    }

    private static string ZoneOf(EncounterBrowserLocationRow row)
        => string.IsNullOrWhiteSpace(row.Zone) ? "Other" : row.Zone;

    private IEnumerable<string> Known(IEnumerable<string>? ids)
        => (ids ?? Array.Empty<string>())
            .Where(id => id is not null && _byId.ContainsKey(id))
            .Select(id => _byId[id].LocationId)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private LocationPickerEntry? FindEntry(string locationId)
        => Entries.FirstOrDefault(e => e.IsLocation && string.Equals(e.Location!.LocationId, locationId, StringComparison.OrdinalIgnoreCase)
                                       && e.Section is not FavoritesSection and not RecentSection)
           ?? Entries.FirstOrDefault(e => e.IsLocation && string.Equals(e.Location!.LocationId, locationId, StringComparison.OrdinalIgnoreCase));

    private void Rebuild(bool keepSelection = true)
    {
        var previous = keepSelection ? _selected : null;
        var entries = new List<LocationPickerEntry>();

        void AddSection(string section, IEnumerable<EncounterBrowserLocationRow> rows, bool showZone, bool collapsible)
        {
            var list = rows.ToList();
            if (list.Count == 0) return;
            var expanded = !collapsible || IsZoneExpanded(section);
            entries.Add(new LocationPickerEntry(LocationPickerEntryKind.Header, section, section, string.Empty,
                IsExpanded: expanded, Count: list.Count));
            if (!expanded) return;
            foreach (var row in list)
                entries.Add(Entry(section, row, showZone));
        }

        var search = _searchText;
        AddSection(FavoritesSection, _favorites.Select(id => _byId[id]).Where(r => Matches(r, search)), showZone: true, collapsible: false);
        AddSection(RecentSection, _recent.Select(id => _byId[id]).Where(r => Matches(r, search)), showZone: true, collapsible: false);
        foreach (var zone in _all.Where(r => Matches(r, search))
                     .GroupBy(ZoneOf, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            AddSection(zone.Key, zone.OrderBy(r => SortName(r.DisplayName), StringComparer.OrdinalIgnoreCase), showZone: false, collapsible: true);

        Entries.Clear();
        foreach (var entry in entries) Entries.Add(entry);

        // Keep the highlight on the same location in the same section (records are rebuilt).
        _selected = null;
        if (previous?.Location is { } loc)
            _selected = entries.FirstOrDefault(e => e.IsLocation && e.Section == previous.Section && e.Location!.LocationId == loc.LocationId)
                        ?? entries.FirstOrDefault(e => e.IsLocation && e.Location!.LocationId == loc.LocationId);
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(SelectedLocation));
        OnPropertyChanged(nameof(IsSearching));
        OnPropertyChanged(nameof(MatchCount));
        OnPropertyChanged(nameof(EmptyMessage));
    }

    private LocationPickerEntry Entry(string section, EncounterBrowserLocationRow row, bool showZone)
    {
        var detail = row.Snarfblat > 0 ? $"· {row.Snarfblat.ToString(CultureInfo.InvariantCulture)}" : "· special route";
        if (showZone) detail += $" · {ZoneOf(row)}";
        var locked = _access.TryGetValue(row.LocationId, out var access) && !access.Accessible;
        return new LocationPickerEntry(LocationPickerEntryKind.Location, section, row.DisplayName, detail, row,
            IsFavorite: IsFavorite(row.LocationId), IsLocked: locked, LockReason: locked ? access.Reason : null);
    }

    /// <summary>"The Skeleton Store" sorts under S.</summary>
    private static string SortName(string name)
        => name.StartsWith("The ", StringComparison.OrdinalIgnoreCase) ? name[4..] : name;
}
