using Avalonia.Controls;
using Avalonia.Interactivity;
using KoLibri.Desktop.ContextMenus;

namespace KoLibri.Desktop;

/// <summary>View for the DailyView tab area; its handlers forward to <see cref="IDailyViewHandlers"/> until the tab worker moves them here.</summary>
public partial class DailyView : UserControl
{
    private IShellHost? _host;
    private IDailyViewHandlers? _handlers;

    public DailyView() => InitializeComponent();

    /// <summary>The shell this view reports to (null until the shell attaches).</summary>
    public IShellHost? Host => _host;

    private IDailyStepHandlers? _stepHandlers;
    private DailyBriefingViewModel? _model;

    /// <summary>Right-click and keyboard menu of the step list.</summary>
    public RowContextMenuController<DailyStepRow>? StepMenu { get; private set; }

    public void Attach(IShellHost host, IDailyViewHandlers handlers)
    {
        _host = host;
        _handlers = handlers;
        _stepHandlers = handlers as IDailyStepHandlers;
        StepMenu = RowContextMenuController<DailyStepRow>.Attach(this.FindControl<ListBox>("DailyStepList")!, BuildStepMenu);
    }

    /// <summary>Shows the steps and the stale-session banner of the account view model.</summary>
    public void Bind(DailyBriefingViewModel model)
    {
        if (_model is not null) _model.PropertyChanged -= OnModelChanged;
        _model = model;
        this.FindControl<ListBox>("DailyStepList")!.ItemsSource = model.Steps;
        model.PropertyChanged += OnModelChanged;
        RenderBanner();
    }

    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DailyBriefingViewModel.SessionStale) or nameof(DailyBriefingViewModel.SessionBannerText))
            Avalonia.Threading.Dispatcher.UIThread.Post(RenderBanner);
    }

    private void RenderBanner()
    {
        this.FindControl<Border>("DailyStaleBanner")!.IsVisible = _model?.SessionStale == true;
        this.FindControl<TextBlock>("DailyStaleBannerText")!.Text = _model?.SessionBannerText ?? "";
    }

    /// <summary>The entries for a step row. Building runs nothing.</summary>
    public IReadOnlyList<ContextMenuItem> BuildStepMenu(DailyStepRow row)
    {
        var run = ContextMenuItem.Action("Run now", () => _stepHandlers?.DailyStepRun(row)).When(row.CanRun, row.DisabledReason);
        var skip = row.IsSkipped
            ? ContextMenuItem.Action("Undo skip", () => _stepHandlers?.DailyStepSkip(row, skip: false))
            : ContextMenuItem.Action("Skip today", () => _stepHandlers?.DailyStepSkip(row, skip: true))
                .When(!row.IsDone, "This step is already done.");
        return
        [
            run, skip, ContextMenuItem.Separator,
            ContextMenuItem.Action("Copy", () => _host?.CopyTextAsync(row.CopyText))
        ];
    }

    private void DailyStepRunClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DailyStepRow row }) _stepHandlers?.DailyStepRun(row);
    }

    private void DailyLogInClick(object? sender, RoutedEventArgs e) => _ = _host?.NavigateToAsync("Settings");

    private void InitializeComponent() => Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);

    private void DailySelectionChanged(object? sender, SelectionChangedEventArgs e) => _handlers?.DailySelectionChanged(sender, e);

    private void DailyRunClick(object? sender, RoutedEventArgs e) => _handlers?.DailyRunClick(sender, e);
}

/// <summary>The step-level handlers the shell offers the Daily view (kept apart from the shared <see cref="IDailyViewHandlers"/>).</summary>
public interface IDailyStepHandlers
{
    void DailyStepRun(DailyStepRow row);
    void DailyStepSkip(DailyStepRow row, bool skip);
}
