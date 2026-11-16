namespace KolArbitrage.Core.Pricing;

/// <summary>Guards that keep automated pricing from selling below cost or chasing outliers.</summary>
public sealed record PricePolicy
{
    public TimeSpan CacheTtl { get; init; } = TimeSpan.FromMinutes(20);
    public TimeSpan MaxStale { get; init; } = TimeSpan.FromHours(12);
    public decimal MinimumMarginPercent { get; init; } = 0.12m;
    public decimal UndercutPercent { get; init; } = 0.005m;
    public decimal UndercutTick { get; init; } = 1m;
    public decimal UnderpricedPercent { get; init; } = 0.20m;
    public decimal MinimumConfidence { get; init; } = 0.35m;
    public IReadOnlyDictionary<PriceSource, decimal> SourceWeights { get; init; } =
        new Dictionary<PriceSource, decimal>
        {
            [PriceSource.Shop] = 1.0m,
            [PriceSource.Pricegun] = 1.0m,
            [PriceSource.PirateEncryptionKey] = 0.85m,
            [PriceSource.ElfGuardPresent] = 0.85m,
            [PriceSource.CrimbucaneerBooty] = 0.85m,
            [PriceSource.Mallbot] = 0.90m,
            [PriceSource.Mall] = 0.95m,
            [PriceSource.Manual] = 0.70m
        };

    public void Validate()
    {
        if (CacheTtl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(CacheTtl));
        if (MaxStale < CacheTtl) throw new ArgumentException("MaxStale must be at least CacheTtl.", nameof(MaxStale));
        if (MinimumMarginPercent < 0) throw new ArgumentOutOfRangeException(nameof(MinimumMarginPercent));
        if (UndercutPercent < 0 || UndercutPercent >= 1) throw new ArgumentOutOfRangeException(nameof(UndercutPercent));
        if (UndercutTick < 0) throw new ArgumentOutOfRangeException(nameof(UndercutTick));
        if (UnderpricedPercent <= 0 || UnderpricedPercent >= 1) throw new ArgumentOutOfRangeException(nameof(UnderpricedPercent));
        if (MinimumConfidence < 0 || MinimumConfidence > 1) throw new ArgumentOutOfRangeException(nameof(MinimumConfidence));
    }

    public decimal WeightFor(PriceObservation observation)
    {
        var sourceWeight = SourceWeights.TryGetValue(observation.Source, out var value) ? value : 1m;
        return Math.Max(0m, sourceWeight * observation.Confidence);
    }
}
