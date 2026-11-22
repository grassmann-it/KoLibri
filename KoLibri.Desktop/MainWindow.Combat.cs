using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;


namespace KoLibri.Desktop;

public partial class MainWindow
{
    private async Task LoadCombatCatalogAsync(CancellationToken cancellationToken)
    {
        await _autoCombat.LoadAsync(cancellationToken);
        if (_combatTargetList is not null) _combatTargetList.ItemsSource = _autoCombat.Targets;
        if (_combatSkillPicker is not null)
        {
            _combatSkillPicker.ItemsSource = _autoCombat.Skills;
            _combatSkillPicker.ItemTemplate = BuildOptionTemplate();
        }
        if (_combatItemPicker is not null)
        {
            _combatItemPicker.ItemsSource = _autoCombat.Items;
            _combatItemPicker.ItemTemplate = BuildOptionTemplate();
        }
        if (_combatSkillList is not null) _combatSkillList.ItemsSource = _autoCombat.SelectedSkills;
        if (_combatItemList is not null) _combatItemList.ItemsSource = _autoCombat.SelectedItems;
        if (_combatAbortHp is not null) _combatAbortHp.Value = _autoCombat.AbortHpPercent;
        if (_combatMaxRounds is not null) _combatMaxRounds.Value = _autoCombat.MaxRounds;
        if (_combatPreviewText is not null) _combatPreviewText.Text = _autoCombat.PreviewText;
    }

    private static Avalonia.Controls.Templates.IDataTemplate BuildOptionTemplate()
        => new Avalonia.Controls.Templates.FuncDataTemplate<CombatOption>((option, _) =>
            new TextBlock { Text = option?.Name ?? string.Empty, Margin = new Avalonia.Thickness(4, 2) });

    private void CombatTargetChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_combatTargetList?.SelectedItem is CombatTarget target)
            _autoCombat.SelectedTarget = target;
        if (_combatPreviewText is not null) _combatPreviewText.Text = _autoCombat.PreviewText;
    }

    private void CombatSkillPicked(object? sender, SelectionChangedEventArgs e)
    {
        if (_combatSkillPicker?.SelectedItem is CombatOption skill)
            _autoCombat.SelectedSkill = skill;
        if (_combatSkillList is not null) _combatSkillList.ItemsSource = _autoCombat.SelectedSkills;
        if (_combatPreviewText is not null) _combatPreviewText.Text = _autoCombat.PreviewText;
    }

    private void CombatItemPicked(object? sender, SelectionChangedEventArgs e)
    {
        if (_combatItemPicker?.SelectedItem is CombatOption item)
            _autoCombat.SelectedItem = item;
        if (_combatItemList is not null) _combatItemList.ItemsSource = _autoCombat.SelectedItems;
        if (_combatPreviewText is not null) _combatPreviewText.Text = _autoCombat.PreviewText;
    }

    private void CombatAbortChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (e.NewValue is { } value) _autoCombat.AbortHpPercent = (int)value;
        if (_combatPreviewText is not null) _combatPreviewText.Text = _autoCombat.PreviewText;
    }

    private void CombatRoundsChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (e.NewValue is { } value) _autoCombat.MaxRounds = (int)value;
        if (_combatPreviewText is not null) _combatPreviewText.Text = _autoCombat.PreviewText;
    }

    public async void CombatRunClick(object? sender, RoutedEventArgs e)
    {
        if (_encounterBrowser is not { Session: { } session } pane || _autoCombat.SelectedTarget is not { } target
            || !_autoCombat.HasConfiguration)
        {
            if (_combatRunStatusText is not null) _combatRunStatusText.Text = "Select a target and a skill first.";
            return;
        }

        await pane.FightCurrentAsync(target, _autoCombat.BuildPolicy(), _autoCombat.MaxRounds, _autoCombat.AbortHpPercent);
        if (_combatRunStatusText is not null && ReferenceEquals(pane.Session, session))
            _combatRunStatusText.Text = session.LastMessage;
        RefreshEncounterStepLog();
    }
}
