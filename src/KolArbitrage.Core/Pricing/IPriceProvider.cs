namespace KolArbitrage.Core.Pricing;

/// <summary>Adapter for a game/shop integration. Implementations should return all quotes known for the item.</summary>
public interface IPriceProvider
{
    Task<IReadOnlyCollection<PriceObservation>> FetchAsync(
        string itemId,
        CancellationToken cancellationToken = default);
}

/// <summary>Useful for the UI and tests when no remote connector is configured yet.</summary>
public sealed class InMemoryPriceProvider : IPriceProvider
{
    private readonly IReadOnlyCollection<PriceObservation> _observations;

    public InMemoryPriceProvider(IEnumerable<PriceObservation> observations)
    {
        _observations = observations.ToArray();
    }

    public Task<IReadOnlyCollection<PriceObservation>> FetchAsync(
        string itemId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = _observations
            .Where(x => string.Equals(x.ItemId, itemId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return Task.FromResult<IReadOnlyCollection<PriceObservation>>(result);
    }
}
