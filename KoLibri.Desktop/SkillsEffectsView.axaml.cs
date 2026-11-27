using Avalonia.Controls;
using Avalonia.Interactivity;
using KoLibri.Desktop.ContextMenus;

namespace KoLibri.Desktop;

/// <summary>Skills and effects page: filterable skill list with a gated Cast, active effects with a context menu.</summary>
public partial class SkillsEffectsView : UserControl
{
    private IShellHost? _host;
    private ISkillsEffectsViewHandlers? _handlers;
    private SkillsEffectsViewModel? _model;
    private Func<int, int, CancellationToken, Task<ActionResult>>? _cast;
    private Func<Task>? _afterCast;

    public SkillsEffectsView() => InitializeComponent();

    /// <summary>The shell this view reports to (null until the shell attaches).</summary>
    public IShellHost? Host => _host;

    public RowContextMenuController<SkillRow>? SkillMenu { get; private set; }
    public RowContextMenuController<EffectRow>? EffectMenu { get; private set; }

    public void Attach(IShellHost host, ISkillsEffectsViewHandlers handlers)
    {
        _host = host;
        _handlers = handlers;
    }

    /// <summary>Binds the page to its model. <paramref name="cast"/> sends one confirmed cast; <paramref name="afterCast"/> refreshes the charpane.</summary>
    public void AttachModel(SkillsEffectsViewModel model,
        Func<int, int, CancellationToken, Task<ActionResult>> cast, Func<Task> afterCast)
    {
        _model = model;
        _cast = cast;
        _afterCast = afterCast;
        var skills = this.FindControl<ListBox>("SkillList")!;
        var effects = this.FindControl<ListBox>("EffectList")!;
        skills.ItemsSource = model.Skills;
        effects.ItemsSource = model.Effects;
        skills.SelectionChanged += (_, _) => model.SelectedSkill = skills.SelectedItem as SkillRow;
        skills.DoubleTapped += (_, _) => RequestCast();
        this.FindControl<TextBox>("SkillFilterBox")!.TextChanged += (_, e) =>
            model.Filter = ((TextBox)e.Source!).Text ?? "";
        this.FindControl<NumericUpDown>("SkillQuantity")!.ValueChanged += (_, e) =>
            model.Quantity = (int)(e.NewValue ?? 1);
        model.PropertyChanged += (_, _) => Sync();
        if (_host is { } host)
        {
            var skillMenu = new SkillContextMenu(host, target =>
            {
                if (model.Skills.FirstOrDefault(s => s.Id.ToString() == target.SkillId) is { } row)
                {
                    skills.SelectedItem = row;
                    RequestCast();
                }
            });
            SkillMenu = RowContextMenuController<SkillRow>.Attach(skills,
                row => skillMenu.Build(new SkillMenuTarget(row.Id.ToString(), row.Name, row.MpCost)));
            var effectMenu = new EffectContextMenu(host);
            EffectMenu = RowContextMenuController<EffectRow>.Attach(effects,
                row => effectMenu.Build(new EffectMenuTarget(row.Name, row.Name, false, row.TurnsRemaining)));
        }
        Sync();
    }

    private void Sync()
    {
        if (_model is not { } m) return;
        this.FindControl<Border>("SkillConfirmPanel")!.IsVisible = m.HasPending;
        this.FindControl<TextBlock>("SkillConfirmText")!.Text = m.PendingText ?? "";
        this.FindControl<TextBlock>("SkillResultText")!.Text = m.ResultText;
        this.FindControl<Button>("SkillCastButton")!.IsEnabled = m.CanRequestCast;
        this.FindControl<Button>("SkillConfirmButton")!.IsEnabled = !m.IsBusy;
    }

    private void RequestCast() => _model?.RequestCast();

    private void SkillCastClick(object? sender, RoutedEventArgs e) => RequestCast();

    private void SkillCancelClick(object? sender, RoutedEventArgs e) => _model?.CancelCast();

    private async void SkillConfirmClick(object? sender, RoutedEventArgs e)
    {
        if (_model is null || _cast is null) return;
        var report = await _model.ConfirmCastAsync(_cast);
        if (report is not null)
        {
            _host?.SessionLog.Add(report.Confirmed ? SessionLogKind.Info : SessionLogKind.Error, report.Message);
            _host?.ShowMessage(report.Message);
        }
        if (_afterCast is not null) await _afterCast();
    }

    private void InitializeComponent() => Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);

    private void CastCharacterSkillClick(object? sender, RoutedEventArgs e) => _handlers?.CastCharacterSkillClick(sender, e);

    private void RestCharacterClick(object? sender, RoutedEventArgs e) => _handlers?.RestCharacterClick(sender, e);

    private void ApplyCharacterMoodClick(object? sender, RoutedEventArgs e) => _handlers?.ApplyCharacterMoodClick(sender, e);
}
