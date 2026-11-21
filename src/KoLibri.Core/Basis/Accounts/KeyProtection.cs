using System.Security.Cryptography;

namespace KoLibri.Core.Basis;

/// <summary>Wraps the local secret key with something the operating system guards.</summary>
public interface IKeyProtector
{
    byte[] Protect(byte[] key);
    byte[] Unprotect(byte[] protectedKey);
}

/// <summary>Marker: store the key file raw (owner-only), without any OS wrapper.</summary>
public sealed class NoProtection : IKeyProtector
{
    public static NoProtection Instance { get; } = new();
    public byte[] Protect(byte[] key) => key;
    public byte[] Unprotect(byte[] protectedKey) => protectedKey;
}

/// <summary>
/// Windows: DPAPI bound to the current user, so the key file is useless to another user or machine.
/// Linux and macOS: no wrapper; the key file is owner-only (mode 600). An OS keychain is future work.
/// </summary>
public static class OsKeyProtector
{
    public static IKeyProtector? ForCurrentOs()
        => OperatingSystem.IsWindows() ? new DpapiKeyProtector() : null;

    private sealed class DpapiKeyProtector : IKeyProtector
    {
        public byte[] Protect(byte[] key)
            => Dpapi.Protect(key);

        public byte[] Unprotect(byte[] protectedKey)
            => Dpapi.Unprotect(protectedKey);
    }

    private static class Dpapi
    {
        public static byte[] Protect(byte[] data)
            => ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);

        public static byte[] Unprotect(byte[] data)
            => ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
    }
}
