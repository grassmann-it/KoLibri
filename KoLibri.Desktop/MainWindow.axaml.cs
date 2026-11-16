using Avalonia.Controls;
using Avalonia.Interactivity;
using KolArbitrage.Core.Accounts;
using KolArbitrage.Core.Analytics;
using KolArbitrage.Core.Integration;

namespace KoLibri.Desktop;

public partial class MainWindow : Window
{
    private TextBlock? _statusText;
    private Border? _statusBar;
    private Border? _accountsPanel;
    private Border? _mallPanel;
    private TextBlock? _mallSummary;
    private TextBlock? _mallRows;
    private readonly AccountSessionManager _accounts = AccountSessionManager.CreateDefault();
    private readonly KoLLoginClient _loginClient = new();

    public MainWindow()
    {
        InitializeComponent();
        _statusText = this.FindControl<TextBlock>("StatusText");
        _statusBar = this.FindControl<Border>("StatusBar");
        _accountsPanel = this.FindControl<Border>("AccountsPanel");
        _mallPanel = this.FindControl<Border>("MallPanel");
        _mallSummary = this.FindControl<TextBlock>("MallSummary");
        _mallRows = this.FindControl<TextBlock>("MallRows");
        _ = LoadSavedAccountsAsync();
    }

    private void InitializeComponent() => Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);

    private void Navigate(string section)
    {
        if (_statusText is not null)
            _statusText.Text = $"Bereich „{section}“ ist vorbereitet. Datenquellen werden im nächsten Schritt verbunden.";
        if (_statusBar is not null)
            _statusBar.IsVisible = true;
    }

    private void OverviewClick(object? sender, RoutedEventArgs e) => Navigate("Übersicht");
    private void PricingClick(object? sender, RoutedEventArgs e) => Navigate("Pricing Engine");
    private async void MallClick(object? sender, RoutedEventArgs e)
    {
        if (_mallPanel is not null) _mallPanel.IsVisible = true;
        Navigate("Mall & Shop");
        await RefreshMallAsync();
    }
    private async void AccountsClick(object? sender, RoutedEventArgs e)
    {
        if (_accountsPanel is not null)
            _accountsPanel.IsVisible = true;
        Navigate("Konten & Transfer");
        await LoadSavedAccountsAsync();
    }
    private void AdventureClick(object? sender, RoutedEventArgs e) => Navigate("Adventure");
    private void SettingsClick(object? sender, RoutedEventArgs e) => Navigate("Einstellungen");
    private void RefreshClick(object? sender, RoutedEventArgs e) => Navigate("Aktualisierung gestartet");
    private async void RefreshMallClick(object? sender, RoutedEventArgs e) => await RefreshMallAsync();

    private async Task RefreshMallAsync()
    {
        if (_mallSummary is null || _mallRows is null) return;
        try
        {
            await _accounts.InitializeAsync();
            var session = await _accounts.GetSessionAsync(AccountRole.Mallbot)
                ?? await _accounts.GetSessionAsync(AccountRole.Main);
            if (session is null || string.IsNullOrWhiteSpace(session.Credentials.CookieHeader))
            {
                _mallSummary.Text = "Bitte zuerst Main oder Mallbot über Konten anmelden.";
                _mallRows.Text = string.Empty;
                return;
            }

            using var http = KoLLoginClient.CreateAuthenticatedClient(
                session.Credentials,
                new Uri("https://www.kingdomofloathing.com/"));
            var mall = new KoLMallReadClient(http);
            var listings = await mall.FetchOwnShopAsync();
            var categorizer = new InventoryCategorizer();
            var analyses = new List<InventoryCategorization>();
            foreach (var listing in listings.Take(20))
            {
                var market = await mall.SearchAsync(listing.DisplayName ?? listing.ItemId);
                var lowest = market.Listings.Count == 0 ? (decimal?)null : market.Listings.Min(x => (decimal)x.Price);
                analyses.Add(categorizer.Analyze(new InventoryItemSnapshot
                {
                    ItemId = listing.ItemId,
                    DisplayName = listing.DisplayName ?? $"Item {listing.ItemId}",
                    OwnedQuantity = listing.Quantity,
                    CurrentListingPrice = listing.Price,
                    LowestMarketPrice = lowest,
                    MarketReferencePrice = lowest,
                    ObservedQuantity = market.Listings.Sum(x => x.Quantity)
                }));
            }
            _mallSummary.Text = $"{listings.Count} eigene Listings · {analyses.Count(x => x.Has(InventoryCategory.Overpriced))} overpriced · {analyses.Count(x => x.Has(InventoryCategory.Underpriced))} unter Markt · Quelle: {session.Profile.Role}";
            _mallRows.Text = analyses.Count == 0
                ? "Keine Listings gefunden oder Shopseite konnte noch nicht geparst werden."
                : string.Join(Environment.NewLine, analyses.Select(x =>
                    $"{x.DisplayName}   {x.CurrentPrice:N0} Meat   {string.Join(", ", x.Categories)}   → {x.SuggestedAction}"));
            SetStatus("Mall- und Shopdaten aktualisiert. Preisänderungen bleiben deaktiviert.");
        }
        catch (Exception exception)
        {
            _mallSummary.Text = "Mall-Aktualisierung fehlgeschlagen.";
            _mallRows.Text = exception.Message;
            SetStatus("Mall konnte nicht aktualisiert werden: " + exception.Message);
        }
    }

    private async Task LoadSavedAccountsAsync()
    {
        try
        {
            await _accounts.InitializeAsync();
            await LoadSavedAccountAsync(AccountRole.Main);
            await LoadSavedAccountAsync(AccountRole.Mallbot);
        }
        catch (Exception exception)
        {
            SetStatus($"Konten konnten nicht geladen werden: {exception.Message}");
        }
    }

    private async Task LoadSavedAccountAsync(AccountRole role)
    {
        var session = await _accounts.GetSessionAsync(role, includeExpired: true);
        if (session is null)
            return;

        var username = session.Credentials.Username ?? session.Profile.DisplayName;
        var usernameBox = this.FindControl<TextBox>(role == AccountRole.Main ? "MainUsername" : "MallbotUsername");
        var passwordBox = this.FindControl<TextBox>(role == AccountRole.Main ? "MainPassword" : "MallbotPassword");
        var rememberBox = this.FindControl<CheckBox>(role == AccountRole.Main ? "MainRememberPassword" : "MallbotRememberPassword");
        var status = this.FindControl<TextBlock>(role == AccountRole.Main ? "MainAccountStatus" : "MallbotAccountStatus");
        if (usernameBox is null || passwordBox is null || rememberBox is null || status is null)
            return;
        usernameBox.Text = username;
        // Never repopulate a password from disk. Existing installations may
        // have one from an older version, but it must not be exposed in the UI
        // or used implicitly for a new login.
        passwordBox.Text = string.Empty;
        status.Text = "Gespeichert (Session noch nicht geprüft)";
        status.Foreground = Avalonia.Media.Brushes.DarkGoldenrod;
    }

    private async void SaveAccountClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || !Enum.TryParse<AccountRole>(button.Tag?.ToString(), out var role))
            return;

        button.IsEnabled = false;

        var usernameBox = this.FindControl<TextBox>(role == AccountRole.Main ? "MainUsername" : "MallbotUsername");
        var passwordBox = this.FindControl<TextBox>(role == AccountRole.Main ? "MainPassword" : "MallbotPassword");
        var rememberBox = this.FindControl<CheckBox>(role == AccountRole.Main ? "MainRememberPassword" : "MallbotRememberPassword");
        var status = this.FindControl<TextBlock>(role == AccountRole.Main ? "MainAccountStatus" : "MallbotAccountStatus");
        if (usernameBox is null || passwordBox is null || rememberBox is null || status is null)
            return;
        var username = usernameBox.Text?.Trim();
        var password = passwordBox.Text;

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            status.Text = "Benutzername und Passwort erforderlich";
            status.Foreground = Avalonia.Media.Brushes.IndianRed;
            return;
        }

        try
        {
            var id = role == AccountRole.Main ? "main" : "mallbot";
            await _accounts.SetProfileAsync(new AccountProfile
            {
                Id = id,
                DisplayName = username,
                Role = role,
                Enabled = true
            });
            // Remove any previous session before associating this role with a
            // new login. This prevents a failed attempt from silently using a
            // stale session for a different character.
            await _accounts.ClearCredentialsAsync(role);

            status.Text = "Login läuft …";
            status.Foreground = Avalonia.Media.Brushes.DarkGoldenrod;
            var login = await _loginClient.LoginAsync(username, password, rememberPassword: rememberBox.IsChecked == true);
            await _accounts.SetCredentialsAsync(role, login.Credentials);
            status.Text = "Verbunden und verschlüsselt gespeichert";
            status.Foreground = Avalonia.Media.Brushes.LightGreen;
            SetStatus($"{role} ist bei KoL angemeldet. Read-only-Daten können jetzt geladen werden.");
        }
        catch (KoLLoginException exception)
        {
            passwordBox.Text = string.Empty;
            status.Text = "Login fehlgeschlagen (Passwort nicht gespeichert)";
            status.Foreground = Avalonia.Media.Brushes.IndianRed;
            SetStatus($"{role}: KoL-Login fehlgeschlagen [{exception.Reason}] ({exception.Message}). Bitte erneut versuchen.");
        }
        catch (Exception exception)
        {
            passwordBox.Text = string.Empty;
            status.Text = "Speichern fehlgeschlagen";
            status.Foreground = Avalonia.Media.Brushes.IndianRed;
            SetStatus($"{role} konnte nicht gespeichert werden: {exception.Message}");
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private void SetStatus(string message)
    {
        if (_statusText is not null)
            _statusText.Text = message;
        if (_statusBar is not null)
            _statusBar.IsVisible = true;
    }
}
