namespace KoLibri.Core.Basis;

/// <summary>
/// Verifies against KoL itself which player a stored role session belongs to, so a transfer
/// is addressed to the real target account and never to a stale stored display name.
/// </summary>
public static class AccountIdentityVerifier
{
    /// <summary>
    /// The verified identity of <paramref name="role"/>. Throws <see cref="AccountIdentityMismatchException"/>
    /// when KoL reports a different player than the stored profile, and <see cref="InvalidOperationException"/>
    /// when the role has no usable session or KoL does not report an identity. Nothing is guessed.
    /// </summary>
    public static async Task<KoLPlayerIdentity> VerifyRoleAsync(
        AccountSessionManager accounts, AccountRole role, CancellationToken cancellationToken = default,
        HttpMessageHandler? innerHandler = null)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        var session = await accounts.GetSessionAsync(role, includeExpired: true, cancellationToken: cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"The {role} account is not stored; nothing was sent.");
        if (string.IsNullOrWhiteSpace(session.Credentials.CookieHeader) || session.IsExpired())
            throw new InvalidOperationException($"The {role} session is not signed in; cannot verify the target player. Nothing was sent.");
        using var http = KoLLoginClient.CreateAuthenticatedClient(session.Credentials,
            session.Profile.Endpoint ?? new Uri("https://www.kingdomofloathing.com/"), innerHandler: innerHandler);
        var identity = await KoLPwdHashResolver.TryReadIdentityAsync(http, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"KoL did not report which player the {role} session is; nothing was sent.");
        var matches = session.Profile.PlayerId is { Length: > 0 } stored
            ? string.Equals(stored, identity.PlayerId, StringComparison.Ordinal)
            : string.Equals(session.Profile.DisplayName, identity.Name, StringComparison.OrdinalIgnoreCase);
        if (!matches)
            throw new AccountIdentityMismatchException(
                $"The {role} role is stored as '{session.Profile.DisplayName}' but its session is '{identity.Name}'. " +
                "Run login refresh --switch-account to confirm the change. Nothing was sent.");
        return identity;
    }
}
