using KoLibri.Core.Basis;
using KoLibri.Core.Spiel;

namespace LibKolibri;

/// <summary>Account-bound character actions and fresh status and inventory reads.</summary>
public sealed class CharacterSession : IDisposable
{
    private readonly HttpClient _http;
    private readonly CharacterActionClient _actions;

    private CharacterSession(AccountSession account, HttpClient http)
    {
        _http = http;
        Role = account.Profile.Role;
        AccountId = account.Profile.Id;
        _actions = new CharacterActionClient(http, account.Credentials.AccessToken!);
    }

    public AccountRole Role { get; }
    public string AccountId { get; }

    public static async Task<CharacterSession> OpenAsync(
        AccountRole role, AccountSessionManager? accounts = null, CancellationToken cancellationToken = default)
    {
        var manager = accounts ?? AccountSessionManager.CreateDefault();
        var account = await manager.GetRequiredSessionAsync(role, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(account.Credentials.AccessToken))
            throw new InvalidOperationException("Sign in again to obtain the character action token.");
        var http = KoLLoginClient.CreateAuthenticatedClient(account.Credentials,
            new Uri("https://www.kingdomofloathing.com/"));
        try
        {
            return new CharacterSession(account, http);
        }
        catch
        {
            http.Dispose();
            throw;
        }
    }

    public Task<CharacterActionSnapshot> ReadAsync(CancellationToken cancellationToken = default)
        => _actions.ReadAsync(cancellationToken);

    public Task<FriarsPageState> ReadFriarsAsync(CancellationToken cancellationToken = default)
        => _actions.ReadFriarsAsync(cancellationToken);

    public Task<CharacterStorageSnapshot> ReadStorageAsync(CancellationToken cancellationToken = default)
        => _actions.ReadStorageAsync(cancellationToken);

    public Task<CharacterMoodResult> EnsureEffectAsync(
        CharacterMoodRule rule, CancellationToken cancellationToken = default)
        => _actions.EnsureEffectAsync(rule, cancellationToken);

    public Task<CharacterActionSnapshot> ExecuteAsync(
        CharacterAction action, CancellationToken cancellationToken = default)
        => _actions.ExecuteAsync(action, cancellationToken);

    public Task<CharacterCastOutcome> CastAsync(int skillId, int times, CancellationToken cancellationToken = default)
        => _actions.CastAsync(skillId, times, cancellationToken);

    public void Dispose() => _http.Dispose();
}
