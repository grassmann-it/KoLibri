using KolLibri.Mcp;

namespace KoLibri.Tests;

/// <summary>MCP test helpers: accounts on the fake KoL (no live request is possible).</summary>
internal static class McpTest
{
    public static McpAccounts Accounts(FakeKol? kol = null, IAccountStore? store = null, string? state = null)
        => new(TestClient.Create(kol ?? new FakeKol(), store ?? MemoryAccountStore.WithMain(), state: state));

    /// <summary>A store with "main" and "mallbot" logged in.</summary>
    public static MemoryAccountStore BothRoles()
    {
        var store = MemoryAccountStore.WithMain();
        store.SaveAsync(new AccountRecord(
            new Account(new AccountId("mallbot"), "Botter", 2002, null, Array.Empty<string>()), "PHPSESSID=def", null, null), default).GetAwaiter().GetResult();
        return store;
    }
}
