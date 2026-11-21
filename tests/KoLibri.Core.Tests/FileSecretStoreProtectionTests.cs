using System.Security.Cryptography;
using KoLibri.Core.Basis;
using Xunit;

namespace KoLibri.Core.Tests;

public sealed class FileSecretStoreProtectionTests : IDisposable
{
    /// <summary>Reversible stand-in for DPAPI: XOR with a fixed byte.</summary>
    private sealed class XorProtector : IKeyProtector
    {
        public byte[] Protect(byte[] key) => key.Select(b => (byte)(b ^ 0x5A)).ToArray();
        public byte[] Unprotect(byte[] protectedKey) => protectedKey.Select(b => (byte)(b ^ 0x5A)).ToArray();
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kolibri-keyprot-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static AccountCredentials Credentials() => new() { CookieHeader = "PHPSESSID=abc", Password = "pw" };

    [Fact]
    public async Task A_protected_key_is_not_stored_raw_and_still_decrypts()
    {
        var keyPath = Path.Combine(_dir, ".k");
        var first = new FileSecretStore(_dir, keyPath, new XorProtector());
        await first.SetAsync("main", Credentials());

        var fileBytes = File.ReadAllBytes(keyPath);
        Assert.Equal("KLK1", System.Text.Encoding.ASCII.GetString(fileBytes, 0, 4));

        var second = new FileSecretStore(_dir, keyPath, new XorProtector());
        Assert.Equal("PHPSESSID=abc", (await second.GetAsync("main"))!.CookieHeader);
    }

    [Fact]
    public async Task A_legacy_raw_key_is_migrated_to_the_protected_form_without_losing_secrets()
    {
        var keyPath = Path.Combine(_dir, ".k");
        var legacy = new FileSecretStore(_dir, keyPath, keyProtector: NoProtection.Instance);
        await legacy.SetAsync("main", Credentials());
        Assert.Equal(32, File.ReadAllBytes(keyPath).Length);

        var upgraded = new FileSecretStore(_dir, keyPath, new XorProtector());
        Assert.Equal("PHPSESSID=abc", (await upgraded.GetAsync("main"))!.CookieHeader);
        Assert.Equal("KLK1", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(keyPath), 0, 4));

        var again = new FileSecretStore(_dir, keyPath, new XorProtector());
        Assert.Equal("PHPSESSID=abc", (await again.GetAsync("main"))!.CookieHeader);
    }

    [Fact]
    public async Task A_protected_key_cannot_be_read_by_a_store_without_the_protector()
    {
        var keyPath = Path.Combine(_dir, ".k");
        await new FileSecretStore(_dir, keyPath, new XorProtector()).SetAsync("main", Credentials());
        await Assert.ThrowsAsync<CryptographicException>(
            () => new FileSecretStore(_dir, keyPath, NoProtection.Instance).GetAsync("main"));
    }
}
