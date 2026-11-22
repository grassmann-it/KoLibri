using KoLibri.Core.Basis;

/// <summary>Shared by <c>login clear</c> in the CLI and the REPL: removes the stored secret, keeps the profile.</summary>
internal static class LoginClear
{
    /// <summary>Returns the roles whose credentials were removed (roles without a profile are skipped).</summary>
    public static async Task<IReadOnlyList<AccountRole>> ClearAsync(
        AccountSessionManager accounts, IEnumerable<AccountRole> roles, CancellationToken cancellationToken = default)
    {
        var cleared = new List<AccountRole>();
        foreach (var role in roles)
        {
            var profile = await accounts.GetProfileAsync(role, cancellationToken).ConfigureAwait(false);
            if (profile is null) continue;
            await accounts.ClearCredentialsAsync(role, cancellationToken).ConfigureAwait(false);
            cleared.Add(role);
        }
        return cleared;
    }
}
