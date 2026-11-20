using System.Collections.ObjectModel;
using KolArbitrage.Core.Accounts;
using KolArbitrage.Core.Integration;

namespace KoLibri.Desktop;

/// <summary>One own-store listing with its live numbers.</summary>
public sealed record LiveShopRow
{
    public required string ItemId { get; init; }
    public required string ItemName { get; init; }
    public required long Price { get; init; }
    public required int Quantity { get; init; }
    public required string PriceText { get; init; }
    public required string QuantityText { get; init; }
}

/// <summary>
/// The "Guts" screen: what the account owns and what it sells, both live.
///
/// KoLmafia separates these into the guts and the store; here they share
/// one provider because both come from the same session. The account
/// cards come from the live status, the store rows from the live store.
/// </summary>
public sealed class LiveGutsViewModel : DesktopViewModel
{
    private readonly DesktopLiveDataProvider _provider;
    private readonly AccountRole _role;

    public LiveGutsViewModel(
        AccountRole role = AccountRole.Main,
        DesktopLiveDataProvider? provider = null)
    {
        _role = role;
        _provider = provider ?? new DesktopLiveDataProvider();
    }

    public AccountRole Role => _role;

    /// <summary>The account cards — one per configured role.</summary>
    public ObservableCollection<GutsAccountCard> Accounts { get; } = new();

    /// <summary>The own-store listings.</summary>
    public ObservableCollection<LiveShopRow> StoreRows { get; } = new();

    public string StoreSummary { get; private set; } = "—";

    public long TotalMeat => Accounts.Sum(a => a.Meat);

    public int TotalItems => Accounts.Sum(a => a.InventoryItemCount);

    public string GutsSummary => Accounts.Count == 0
        ? "No session."
        : $"{Accounts.Count} account(s) · {TotalMeat:N0} Meat · {TotalItems:N0} items";

    /// <summary>Loads the live account cards and the own store.</summary>
    public async Task LoadAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        LoadState = DesktopLoadState.Loading;
        try
        {
            Accounts.Clear();
            foreach (var role in Enum.GetValues<AccountRole>())
            {
                var status = await _provider
                    .LoadStatusAsync(role, forceRefresh, cancellationToken).ConfigureAwait(false);
                if (status is null) continue;
                var inventory = await _provider
                    .LoadInventoryAsync(role, forceRefresh, null, cancellationToken).ConfigureAwait(false);
                Accounts.Add(new GutsAccountCard
                {
                    Role = role,
                    PlayerName = status.PlayerName ?? role.ToString(),
                    Meat = status.Meat,
                    Adventures = status.Adventures,
                    Level = status.Level,
                    InventoryItemCount = inventory.Items.Count,
                    InventoryQuantity = inventory.Items.Sum(i => i.Quantity),
                    IsStale = status.IsStale || inventory.IsStale
                });
            }

            StoreRows.Clear();
            var listings = await _provider
                .LoadOwnShopAsync(_role, cancellationToken).ConfigureAwait(false);
            foreach (var listing in listings)
            {
                StoreRows.Add(new LiveShopRow
                {
                    ItemId = listing.ItemId,
                    ItemName = string.IsNullOrWhiteSpace(listing.DisplayName)
                        ? listing.ItemId
                        : listing.DisplayName!,
                    Price = listing.Price,
                    Quantity = listing.Quantity,
                    PriceText = $"{listing.Price:N0} Meat",
                    QuantityText = listing.Quantity == 1 ? "1 pc" : $"{listing.Quantity:N0} pcs"
                });
            }
            StoreSummary = StoreRows.Count == 0
                ? "No own listings."
                : $"{StoreRows.Count} listings · {StoreRows.Sum(r => r.Price * r.Quantity):N0} Meat offered";

            LoadState = Accounts.Count == 0 && StoreRows.Count == 0
                ? DesktopLoadState.Empty
                : DesktopLoadState.Ready;
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
            LoadState = DesktopLoadState.Error;
        }
        finally
        {
            OnPropertyChanged(nameof(TotalMeat));
            OnPropertyChanged(nameof(TotalItems));
            OnPropertyChanged(nameof(GutsSummary));
            OnPropertyChanged(nameof(StoreSummary));
        }
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
        => LoadAsync(forceRefresh: true, cancellationToken);
}

/// <summary>One account in the guts overview.</summary>
public sealed record GutsAccountCard
{
    public required AccountRole Role { get; init; }
    public required string PlayerName { get; init; }
    public required long Meat { get; init; }
    public required int Adventures { get; init; }
    public required int Level { get; init; }
    public required int InventoryItemCount { get; init; }
    public required int InventoryQuantity { get; init; }
    public required bool IsStale { get; init; }

    public string MeatText => $"{Meat:N0} Meat";
    public string AdventuresText => $"{Adventures:N0} Adventures";
    public string InventoryText => $"{InventoryItemCount:N0} items · {InventoryQuantity:N0} pcs";
    public string FreshnessText => IsStale ? "stale" : "fresh";
}
