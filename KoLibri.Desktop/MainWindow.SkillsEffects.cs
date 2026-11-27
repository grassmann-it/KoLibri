
namespace KoLibri.Desktop;

/// <summary>Wires the skills page: its model, the confirmed-cast request and the charpane refresh after a cast.</summary>
public partial class MainWindow
{
    private SkillsEffectsViewModel? _skillsEffectsModel;

    private void AttachSkillsEffects()
    {
        if (_skillsEffectsView is null) return;
        _skillsEffectsModel = new SkillsEffectsViewModel(
            _catalog is null ? [] : SkillsEffectsViewModel.FromCatalog(_catalog.Skills))
        { Role = _selectedRole.ToString() };
        _skillsEffectsView.AttachModel(_skillsEffectsModel, CastSkillAsync,
            () => RefreshSidebarAsync(forceRefresh: true));
    }

    /// <summary>One explicit, user-confirmed cast through the library session.</summary>
    private async Task<ActionResult> CastSkillAsync(int skillId, int times, CancellationToken cancellationToken)
    {
        // Re-check at execution: the confirmation was made for a specific role.
        if (_skillsEffectsModel is { } model && model.Role != _selectedRole.ToString())
            throw new InvalidOperationException("The selected account changed; the cast was not sent.");
        var session = await _live.RequireSessionAsync(_selectedRole, "cast a skill", cancellationToken);
        // One cast per request inside the library, so the confirmed count is real.
        return await session.Character.CastAsync(skillId, times, cancellationToken);
    }
}
