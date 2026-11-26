using KoLibri;

/// <summary>Shared by <c>login clear</c>: removes the stored cookie and password, keeps the account.</summary>
internal static class LoginClear
{
    /// <summary>Returns the roles whose credentials were removed (roles without an account are skipped).</summary>
    public static async Task<IReadOnlyList<AccountRole>> ClearAsync(
        AccountRegistry accounts, IEnumerable<AccountRole> roles, CancellationToken cancellationToken = default)
    {
        var cleared = new List<AccountRole>();
        var known = await accounts.ListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var role in roles)
        {
            var account = known.FirstOrDefault(a => a.Id.Value == KolibriCli.AccountIdOf(role));
            if (account is null) continue;
            await accounts.LogoutAsync(account.Id, cancellationToken).ConfigureAwait(false);
            cleared.Add(role);
        }
        return cleared;
    }
}
