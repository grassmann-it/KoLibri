using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KolArbitrage.Core.Markt;

/// <summary>Persistent local CRM record. The character name never belongs in analytics exports.</summary>
public sealed record BuyerCrmProfile
{
    public required string BuyerKey { get; init; }
    public required string CharacterName { get; init; }
    public int LoyaltyPoints { get; init; }
    public long TotalMeat { get; init; }
    public int TotalQuantity { get; init; }
    public int SaleCount { get; init; }
    public bool IsBlocked { get; init; }
    public string? Notes { get; init; }
    public DateTimeOffset? FirstPurchase { get; init; }
    public DateTimeOffset? LastPurchase { get; init; }
    public HashSet<string> ProcessedSaleIds { get; init; } = new(StringComparer.Ordinal);
}

public sealed record BuyerCrmDatabase
{
    public int Version { get; init; } = 1;
    public IReadOnlyList<BuyerCrmProfile> Buyers { get; init; } = Array.Empty<BuyerCrmProfile>();
}

public sealed record BuyerLoyaltyPolicy
{
    /// <summary>One point is granted per this many Meat, plus the per-sale bonus.</summary>
    public long MeatPerPoint { get; init; } = 1_000;
    public int PointsPerSale { get; init; } = 1;

    public void Validate()
    {
        if (MeatPerPoint <= 0) throw new ArgumentOutOfRangeException(nameof(MeatPerPoint));
        if (PointsPerSale < 0) throw new ArgumentOutOfRangeException(nameof(PointsPerSale));
    }
}

public sealed record BuyerCrmSyncResult(
    BuyerCrmDatabase Database,
    int NewSales,
    int DuplicateSales,
    int PointsGranted,
    IReadOnlyList<string> UpdatedBuyerKeys);

public enum BuyerContactAction
{
    Message,
    Gift
}

/// <summary>
/// A preview for a future message/gift adapter. Creating an intent never sends
/// anything and still requires a human approval step before a network request.
/// </summary>
public sealed record BuyerContactIntent(
    string BuyerKey,
    string CharacterName,
    BuyerContactAction Action,
    string Reason,
    string? Message,
    string? ItemId,
    int Quantity,
    bool RequiresApproval = true);

public static class BuyerCrmService
{
    public static BuyerCrmDatabase SetBlocked(
        BuyerCrmDatabase database,
        string buyerKey,
        bool blocked,
        string? note = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (string.IsNullOrWhiteSpace(buyerKey))
            throw new ArgumentException("A buyer key is required.", nameof(buyerKey));

        var found = false;
        var buyers = database.Buyers.Select(profile =>
        {
            if (!string.Equals(profile.BuyerKey, buyerKey, StringComparison.Ordinal))
                return profile;
            found = true;
            return profile with
            {
                IsBlocked = blocked,
                Notes = note ?? profile.Notes
            };
        }).ToArray();
        if (!found)
            throw new KeyNotFoundException($"Buyer key '{buyerKey}' is not in the CRM ledger.");
        return database with { Buyers = buyers };
    }

    public static BuyerCrmSyncResult ApplySales(
        BuyerCrmDatabase database,
        IEnumerable<ShopSale> sales,
        BuyerLoyaltyPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(sales);
        policy ??= new BuyerLoyaltyPolicy();
        policy.Validate();

        var profiles = database.Buyers
            .Where(x => !string.IsNullOrWhiteSpace(x.BuyerKey))
            .GroupBy(x => x.BuyerKey, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Last(), StringComparer.Ordinal);
        var updated = new HashSet<string>(StringComparer.Ordinal);
        var newSales = 0;
        var duplicates = 0;
        var pointsGranted = 0;

        foreach (var sale in sales)
        {
            if (string.IsNullOrWhiteSpace(sale.BuyerKey) || sale.Quantity <= 0 || sale.TotalPrice < 0)
                continue;

            var profile = profiles.TryGetValue(sale.BuyerKey, out var existing)
                ? existing
                : new BuyerCrmProfile
                {
                    BuyerKey = sale.BuyerKey,
                    CharacterName = sale.RawBuyerName ?? sale.BuyerKey
                };
            var processed = new HashSet<string>(profile.ProcessedSaleIds ?? [], StringComparer.Ordinal);
            var saleId = Fingerprint(sale);
            if (!processed.Add(saleId))
            {
                duplicates++;
                continue;
            }

            var salePoints = checked(policy.PointsPerSale + (int)Math.Min(int.MaxValue, sale.TotalPrice / policy.MeatPerPoint));
            var soldAt = sale.SoldAt ?? sale.ObservedAt;
            var characterName = string.IsNullOrWhiteSpace(profile.CharacterName)
                || string.Equals(profile.CharacterName, profile.BuyerKey, StringComparison.Ordinal)
                ? sale.RawBuyerName ?? profile.CharacterName
                : profile.CharacterName;
            profile = profile with
            {
                CharacterName = characterName,
                LoyaltyPoints = checked(profile.LoyaltyPoints + salePoints),
                TotalMeat = checked(profile.TotalMeat + sale.TotalPrice),
                TotalQuantity = checked(profile.TotalQuantity + sale.Quantity),
                SaleCount = checked(profile.SaleCount + 1),
                FirstPurchase = profile.FirstPurchase is null || soldAt < profile.FirstPurchase ? soldAt : profile.FirstPurchase,
                LastPurchase = profile.LastPurchase is null || soldAt > profile.LastPurchase ? soldAt : profile.LastPurchase,
                ProcessedSaleIds = processed
            };
            profiles[sale.BuyerKey] = profile;
            updated.Add(sale.BuyerKey);
            newSales++;
            pointsGranted = checked(pointsGranted + salePoints);
        }

        var result = new BuyerCrmDatabase
        {
            Version = Math.Max(1, database.Version),
            Buyers = profiles.Values.OrderByDescending(x => x.LoyaltyPoints).ThenBy(x => x.BuyerKey, StringComparer.Ordinal).ToArray()
        };
        return new BuyerCrmSyncResult(result, newSales, duplicates, pointsGranted, updated.ToArray());
    }

    public static BuyerContactIntent? BuildRewardIntent(
        BuyerCrmProfile profile,
        int minimumPoints,
        BuyerContactAction action,
        string reason,
        string? message = null,
        string? itemId = null,
        int quantity = 0)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (minimumPoints < 0) throw new ArgumentOutOfRangeException(nameof(minimumPoints));
        if (profile.IsBlocked || profile.LoyaltyPoints < minimumPoints)
            return null;
        if (action == BuyerContactAction.Gift && (string.IsNullOrWhiteSpace(itemId) || quantity <= 0))
            throw new ArgumentException("A gift intent requires an item id and positive quantity.");
        if (action == BuyerContactAction.Message && string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("A message intent requires message text.");

        return new BuyerContactIntent(
            profile.BuyerKey,
            profile.CharacterName,
            action,
            reason,
            message,
            itemId,
            quantity,
            RequiresApproval: true);
    }

    public static string Fingerprint(ShopSale sale)
    {
        var value = string.Join("|", sale.BuyerKey, sale.ItemName, sale.Quantity, sale.TotalPrice,
            sale.SoldAt?.ToUniversalTime().ToString("O") ?? string.Empty);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }
}

/// <summary>Encrypted local store for stable buyer keys, names, points and block flags.</summary>
public sealed class BuyerCrmStore
{
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly string _directory;
    private readonly string _keyPath;
    private readonly string _dataPath;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly byte[] _key;

    public BuyerCrmStore(string? directory = null)
    {
        _directory = directory ?? DefaultDirectory();
        _keyPath = Path.Combine(_directory, ".buyer-key");
        _dataPath = Path.Combine(_directory, "buyers.json.enc");
        _key = LoadOrCreateKey();
    }

    public BuyerIdentityHasher CreateHasher() => new(_key);

    public async Task<BuyerCrmDatabase> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_dataPath)) return new BuyerCrmDatabase();
        try
        {
            await using var stream = File.OpenRead(_dataPath);
            var envelope = await JsonSerializer.DeserializeAsync<Envelope>(stream, _json, cancellationToken).ConfigureAwait(false)
                ?? throw new CryptographicException("Buyer CRM envelope is empty.");
            var plaintext = new byte[Convert.FromBase64String(envelope.Ciphertext).Length];
            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(Convert.FromBase64String(envelope.Nonce), Convert.FromBase64String(envelope.Ciphertext),
                Convert.FromBase64String(envelope.Tag), plaintext, Encoding.UTF8.GetBytes("KoLibri-BuyerCRM-v1"));
            return JsonSerializer.Deserialize<BuyerCrmDatabase>(plaintext, _json) ?? new BuyerCrmDatabase();
        }
        catch (JsonException exception)
        {
            throw new CryptographicException("Buyer CRM data is malformed.", exception);
        }
    }

    public async Task SaveAsync(BuyerCrmDatabase database, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        Directory.CreateDirectory(_directory);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(database, _json);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using (var aes = new AesGcm(_key, TagSize))
            aes.Encrypt(nonce, plaintext, ciphertext, tag, Encoding.UTF8.GetBytes("KoLibri-BuyerCRM-v1"));
        CryptographicOperations.ZeroMemory(plaintext);
        var envelope = new Envelope
        {
            Version = 1,
            Nonce = Convert.ToBase64String(nonce),
            Ciphertext = Convert.ToBase64String(ciphertext),
            Tag = Convert.ToBase64String(tag)
        };
        var temp = $"{_dataPath}.{Guid.NewGuid():N}.tmp";
        await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, envelope, _json, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        RestrictFile(temp);
        File.Move(temp, _dataPath, overwrite: true);
    }

    public static string DefaultDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return string.IsNullOrWhiteSpace(appData)
            ? Path.Combine(Environment.CurrentDirectory, ".kolarbitrage", "crm")
            : Path.Combine(appData, "KolArbitrage", "crm");
    }

    private byte[] LoadOrCreateKey()
    {
        Directory.CreateDirectory(_directory);
        if (File.Exists(_keyPath))
        {
            var key = File.ReadAllBytes(_keyPath);
            if (key.Length != KeySize) throw new CryptographicException("Buyer CRM key has an invalid length.");
            return key;
        }
        var created = RandomNumberGenerator.GetBytes(KeySize);
        File.WriteAllBytes(_keyPath, created);
        RestrictFile(_keyPath);
        return created;
    }

    private static void RestrictFile(string path)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsFreeBSD()) return;
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch (PlatformNotSupportedException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record Envelope
    {
        public int Version { get; init; }
        public required string Nonce { get; init; }
        public required string Ciphertext { get; init; }
        public required string Tag { get; init; }
    }
}
