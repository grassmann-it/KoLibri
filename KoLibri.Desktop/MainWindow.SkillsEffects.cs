using KoLibri.Core.Spiel;
using LibKolibri;

namespace KoLibri.Desktop;

/// <summary>Wires the skills page: its model, the confirmed-cast request and the charpane refresh after a cast.</summary>
public partial class MainWindow
{
    private SkillsEffectsViewModel? _skillsEffectsModel;

    private void AttachSkillsEffects()
    {
        if (_skillsEffectsView is null) return;
        _skillsEffectsModel = new SkillsEffectsViewModel(
            _catalog is null ? [] : SkillsEffectsViewModel.FromCatalog(_catalog.Skills));
        _skillsEffectsView.AttachModel(_skillsEffectsModel, CastSkillAsync,
            () => RefreshSidebarAsync(forceRefresh: true));
    }

    /// <summary>One explicit, user-confirmed cast through the existing character action client.</summary>
    private async Task<CharacterCastOutcome> CastSkillAsync(int skillId, int times, CancellationToken cancellationToken)
    {
        using var session = await CharacterSession.OpenAsync(_selectedRole, _accounts, cancellationToken);
        return await session.CastAsync(skillId, times, cancellationToken);
    }
}
