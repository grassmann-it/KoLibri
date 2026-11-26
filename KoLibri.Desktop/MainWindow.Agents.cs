using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace KoLibri.Desktop;

/// <summary>The "Agents" page: approved AI clients, their limits, today's usage and a revoke button.</summary>
public partial class MainWindow
{
    private void LoadAgents()
    {
        var list = this.FindControl<StackPanel>("AgentsList");
        if (list is null) return;
        list.Children.Clear();
        var grants = new GrantStore().List();
        var ledger = new UsageLedger();
        if (grants.Count == 0)
        {
            list.Children.Add(new TextBlock
            {
                Text = "No AI client has been approved yet. Start kolibri-mcp and connect a client to see it here.",
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }
        foreach (var grant in grants.OrderByDescending(g => g.CreatedAt))
            list.Children.Add(AgentCard(grant, ledger));
    }

    private Control AgentCard(Grant grant, UsageLedger ledger)
    {
        var now = DateTimeOffset.UtcNow;
        var state = grant.RevokedAt is not null ? "revoked" : grant.ExpiresAt <= now ? "expired" : "active";
        var lines = new StackPanel { Spacing = 2 };
        lines.Children.Add(new TextBlock
        {
            Text = $"{grant.ClientName}  ({state})",
            FontWeight = FontWeight.SemiBold,
        });
        lines.Children.Add(new TextBlock
        {
            Text = $"Accounts: {string.Join(", ", grant.Accounts)} · until {grant.ExpiresAt.ToLocalTime():g} · id {grant.Id}",
            FontSize = 12,
            Opacity = .75,
        });
        foreach (var (right, limit) in grant.Rights)
        {
            var parts = new List<string>();
            if (limit.DailyBudget is { } budget) parts.Add($"{ledger.UsedToday(grant.Id, right)} / {budget} today");
            else parts.Add("read only");
            if (limit.MaxUnitPrice is { } price) parts.Add($"max price {price}");
            if (limit.MeatReserve is { } reserve) parts.Add($"reserve {reserve}");
            if (limit.MaxChangePercent is { } pct) parts.Add($"max change {pct}%");
            lines.Children.Add(new TextBlock { Text = $"• {right}: {string.Join(", ", parts)}", FontSize = 12 });
        }

        var revoke = new Button { Content = "Revoke", VerticalAlignment = VerticalAlignment.Top, IsEnabled = state == "active", Tag = grant.Id };
        revoke.Click += RevokeAgentClick;
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        row.Children.Add(lines);
        Grid.SetColumn(revoke, 1);
        row.Children.Add(revoke);
        return new Border { Classes = { "card" }, Padding = new Thickness(12), Child = row };
    }

    private void RevokeAgentClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id })
        {
            new GrantStore().Revoke(id);
            LoadAgents();
        }
    }
}
