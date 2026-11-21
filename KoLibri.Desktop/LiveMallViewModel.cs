using System.Collections.ObjectModel;
using KoLibri.Core.Basis;

namespace KoLibri.Desktop;

/// <summary>One own-store listing with the numbers the operator acts on.</summary>
public sealed record MallStoreRow
{
    public required string ItemId { get; init; }
    public required string ItemName { get; init; }
    public required long Price { get; init; }
    public required int Quantity { get; init; }
    public required int? PurchaseLimit { get; init; }
    public required string PriceText { get; init; }
    public required string QuantityText { get; init; }
    public required string LimitText { get; init; }

}

/// <summary>
/// Read-only view of the selected account’s own listings.
/// </summary>
public sealed class LiveMallViewModel : DesktopViewModel
{
    private readonly DesktopLiveDataProvider _provider;
    private readonly AccountRole _role;
    public LiveMallViewModel(AccountRole role = AccountRole.Mallbot, DesktopLiveDataProvider? provider = null)
    {
        _role = role;
        _provider = provider ?? new DesktopLiveDataProvider();
    }

    public ObservableCollection<MallStoreRow> Rows { get; } = new();

    public string Summary { get; private set; } = "—";

    public int ListingCount => Rows.Count;

    public long TotalOffered => Rows.Sum(r => r.Price * r.Quantity);

    /// <summary>Loads the selected account’s store.</summary>
    public async Task LoadAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        LoadState = DesktopLoadState.Loading;
        try
        {
            var listings = await _provider.LoadOwnShopAsync(_role, cancellationToken, forceRefresh);
            Rows.Clear();
            if (listings is null)
            {
                Summary = $"Sign in as {_role} under Accounts to see store listings.";
                LoadState = DesktopLoadState.Empty;
                return;
            }

            foreach (var listing in listings)
            {
                var name = string.IsNullOrWhiteSpace(listing.DisplayName)
                    ? listing.ItemId
                    : listing.DisplayName!;
                Rows.Add(new MallStoreRow
                {
                    ItemId = listing.ItemId,
                    ItemName = name,
                    Price = listing.Price,
                    Quantity = listing.Quantity,
                    PurchaseLimit = listing.PurchaseLimit,
                    PriceText = $"{listing.Price:N0} Meat",
                    QuantityText = listing.Quantity == 1 ? "1 pc" : $"{listing.Quantity:N0} pcs",
                    LimitText = listing.PurchaseLimit is > 0 ? $"{listing.PurchaseLimit} / day" : "—"
                });
            }

            Summary = Rows.Count == 0
                ? "No own listings."
                : $"{Rows.Count} listings · {TotalOffered:N0} Meat offered";
            LoadState = Rows.Count == 0 ? DesktopLoadState.Empty : DesktopLoadState.Ready;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LoadState = DesktopLoadState.Empty;
            throw;
        }
        catch (Exception exception)
        {
            Summary = $"Store unavailable: {exception.Message}";
            ErrorMessage = exception.Message;
            LoadState = DesktopLoadState.Error;
        }
        finally
        {
            OnPropertyChanged(nameof(ListingCount));
            OnPropertyChanged(nameof(TotalOffered));
        }
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
        => LoadAsync(forceRefresh: true, cancellationToken);
}

public sealed record MallMarketRow(string Store, string Price, string Stock, string Limit);
