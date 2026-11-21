using System.Collections.ObjectModel;
using LibKolibri;
using LibSnapshotFreshness = LibKolibri.SnapshotFreshness;
using KoLibri.Core.Basis;
using KoLibri.Core.Spiel;

namespace KoLibri.Desktop;

/// <summary>Optional read-side source for an approval inbox.</summary>
public interface IApprovalInboxQuery
{
    Task<IReadOnlyList<ApprovalPreview>> GetPendingAsync(CancellationToken cancellationToken = default);
}

/// <summary>Account and inventory facts projected for a cockpit card.</summary>
public sealed record AccountCardProjection
{
    public string AccountId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? PlayerName { get; init; }
    public AccountRole? Role { get; init; }
    public long Meat { get; init; }
    public int Adventures { get; init; }
    public int Level { get; init; }
    public int InventoryItemCount { get; init; }
    public int InventoryQuantity { get; init; }
    public DateTimeOffset ObservedAt { get; init; }
    public bool IsStale { get; init; }
    public bool IsAuthenticated { get; init; }
    public DesktopSnapshotInfo Snapshot { get; init; } = new(LibSnapshotFreshness.Unknown, null, null, null, null, null);

    public string MeatText => $"{Meat:N0} Meat";
    public string AdventuresText => $"{Adventures:N0} Adventures";
    public string SessionText => IsAuthenticated ? (IsStale ? "Session · stale" : "Session · ready") : "Session · unavailable";

    public static AccountCardProjection From(
        string accountId,
        string displayName,
        AccountRole? role,
        KoLStatusSnapshot status,
        KoLInventorySnapshot? inventory)
    {
        ArgumentNullException.ThrowIfNull(status);
        var inventoryStale = inventory?.IsStale == true;
        var stale = status.IsStale || inventoryStale;
        var observedAt = inventory is { FetchedAt: var inventoryAt } && inventoryAt > status.FetchedAt
            ? inventoryAt
            : status.FetchedAt;
        var freshness = stale ? LibSnapshotFreshness.Stale : LibSnapshotFreshness.Fresh;
        return new AccountCardProjection
        {
            AccountId = accountId,
            DisplayName = displayName,
            PlayerName = status.PlayerName,
            Role = role,
            Meat = status.Meat,
            Adventures = status.Adventures,
            Level = status.Level,
            InventoryItemCount = inventory?.Items.Count ?? 0,
            InventoryQuantity = inventory?.Items.Sum(item => Math.Max(0, item.Quantity)) ?? 0,
            ObservedAt = observedAt,
            IsStale = stale,
            IsAuthenticated = !string.IsNullOrWhiteSpace(status.PlayerName),
            Snapshot = new DesktopSnapshotInfo(freshness, observedAt, "account", null, null, null)
        };
    }
}

public sealed record CockpitSnapshot(
    AccountCardProjection? Main,
    AccountCardProjection? Mallbot,
    IReadOnlyList<ApprovalPreview> PendingApprovals,
    DateTimeOffset ObservedAt)
{
    public bool IsStale => (Main?.IsStale ?? false) || (Mallbot?.IsStale ?? false);
    public bool IsEmpty => Main is null && Mallbot is null && PendingApprovals.Count == 0;
}

/// <summary>
/// Read-side cockpit projection. It consumes LibKolibri account contracts and
/// leaves transport/session details in the registered gateway.
/// </summary>
public class CockpitViewModel : DesktopViewModel
{
    private readonly IAccountGateway? _mainGateway;
    private readonly IAccountGateway? _mallbotGateway;
    private readonly IApprovalInboxQuery? _approvalInbox;
    private AccountCardProjection? _main;
    private AccountCardProjection? _mallbot;
    private int _openApprovalCount;
    private string _dailyGoal = "Tagesziel wird nach dem ersten Snapshot angezeigt.";

    public CockpitViewModel(
        IAccountGateway? mainGateway = null,
        IAccountGateway? mallbotGateway = null,
        IApprovalInboxQuery? approvalInbox = null)
    {
        _mainGateway = mainGateway;
        _mallbotGateway = mallbotGateway;
        _approvalInbox = approvalInbox;
        PendingApprovals = new ObservableCollection<ApprovalPreview>();
        StaleSources = new ObservableCollection<string>();
    }

    public AccountCardProjection? Main
    {
        get => _main;
        private set => SetProperty(ref _main, value);
    }

    public AccountCardProjection? Mallbot
    {
        get => _mallbot;
        private set => SetProperty(ref _mallbot, value);
    }

    public AccountCardProjection? MainAccount => Main;
    public AccountCardProjection? MallbotAccount => Mallbot;
    public ObservableCollection<ApprovalPreview> PendingApprovals { get; }
    public ObservableCollection<string> StaleSources { get; }

    public int OpenApprovalCount
    {
        get => _openApprovalCount;
        private set => SetProperty(ref _openApprovalCount, value);
    }

    public string DailyGoal
    {
        get => _dailyGoal;
        set => SetProperty(ref _dailyGoal, value ?? string.Empty);
    }

    public string DailyTarget
    {
        get => DailyGoal;
        set => DailyGoal = value;
    }

    public long TotalMeat => (Main?.Meat ?? 0) + (Mallbot?.Meat ?? 0);
    public int TotalAdventures => (Main?.Adventures ?? 0) + (Mallbot?.Adventures ?? 0);

    /// <summary>Reloads all configured cards. Null gateways produce an empty card.</summary>
    public Task<CockpitSnapshot?> LoadAsync(CancellationToken cancellationToken = default)
        => LoadCoreAsync(
            LoadSnapshotAsync,
            ApplySnapshot,
            snapshot => snapshot.IsEmpty ? DesktopLoadState.Empty : snapshot.IsStale ? DesktopLoadState.Stale : DesktopLoadState.Ready,
            snapshot => snapshot.IsStale,
            snapshot => snapshot.ObservedAt,
            cancellationToken);

    public Task<CockpitSnapshot?> RefreshAsync(CancellationToken cancellationToken = default)
        => LoadAsync(cancellationToken);

    private async Task<CockpitSnapshot> LoadSnapshotAsync(CancellationToken cancellationToken)
    {
        var mainTask = ReadAccountAsync(_mainGateway, "main", "Main", AccountRole.Main, cancellationToken);
        var mallbotTask = ReadAccountAsync(_mallbotGateway, "mallbot", "Mallbot", AccountRole.Mallbot, cancellationToken);
        var approvalTask = _approvalInbox is null
            ? Task.FromResult<IReadOnlyList<ApprovalPreview>>(Array.Empty<ApprovalPreview>())
            : _approvalInbox.GetPendingAsync(cancellationToken);

        await Task.WhenAll(mainTask, mallbotTask, approvalTask).ConfigureAwait(false);
        return new CockpitSnapshot(
            await mainTask.ConfigureAwait(false),
            await mallbotTask.ConfigureAwait(false),
            (await approvalTask.ConfigureAwait(false)) ?? Array.Empty<ApprovalPreview>(),
            DateTimeOffset.UtcNow);
    }

    private void ApplySnapshot(CockpitSnapshot snapshot)
    {
        Main = snapshot.Main;
        Mallbot = snapshot.Mallbot;
        PendingApprovals.Clear();
        foreach (var approval in snapshot.PendingApprovals)
            PendingApprovals.Add(approval);
        OpenApprovalCount = snapshot.PendingApprovals.Count(approval => approval.State == LibKolibri.ApprovalState.Pending);
        StaleSources.Clear();
        if (Main?.IsStale == true) StaleSources.Add("Main account");
        if (Mallbot?.IsStale == true) StaleSources.Add("Mallbot account");
        OnPropertyChanged(nameof(MainAccount));
        OnPropertyChanged(nameof(MallbotAccount));
        OnPropertyChanged(nameof(TotalMeat));
        OnPropertyChanged(nameof(TotalAdventures));
    }

    private static async Task<AccountCardProjection?> ReadAccountAsync(
        IAccountGateway? gateway,
        string accountId,
        string displayName,
        AccountRole role,
        CancellationToken cancellationToken)
    {
        if (gateway is null) return null;
        var statusTask = gateway.GetStatusAsync(cancellationToken);
        var inventoryTask = gateway.GetInventoryAsync(cancellationToken);
        await Task.WhenAll(statusTask, inventoryTask).ConfigureAwait(false);
        return AccountCardProjection.From(
            accountId,
            displayName,
            role,
            await statusTask.ConfigureAwait(false),
            await inventoryTask.ConfigureAwait(false));
    }
}

/// <summary>Short state-name aliases for hosts that prefer state over VM terminology.</summary>
public sealed class CockpitState : CockpitViewModel
{
    public CockpitState(IAccountGateway? mainGateway = null, IAccountGateway? mallbotGateway = null,
        IApprovalInboxQuery? approvalInbox = null)
        : base(mainGateway, mallbotGateway, approvalInbox) { }
}
