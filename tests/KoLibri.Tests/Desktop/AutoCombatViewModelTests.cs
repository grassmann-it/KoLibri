using KoLibri.Desktop;
using KoLibri.Extras;
using Xunit;

namespace KoLibri.Tests.Desktop;

using CombatOption = KoLibri.Desktop.CombatOption;

/// <summary>
/// The auto-combat configurator is the screen that replaces KoLmafia's dozen clicks with one panel. The tests pin the
/// configuration contract (a target, at least one skill, the abort rules) and the plan it hands to KoLibri.
/// </summary>
public sealed class AutoCombatViewModelTests
{
    private static AutoCombatViewModel Build() => new(new MafiaDataLoader().Load(AdventureKit.CatalogDirectory));

    private static readonly FightEncounter Skeleton = AdventureKit.FightEncounter();

    private static CombatContext Round(int round, IReadOnlyList<FightRound> history)
        => new(Skeleton, round, 80, 100, 40, 50, history);

    private static FightRound Sent(CombatAction action, int round)
        => new(round, action, FightOutcome.Ongoing, 80, 40, Skeleton.Page);

    [Fact]
    public async Task Loading_fills_the_targets_skills_and_items()
    {
        var viewModel = Build();
        await viewModel.LoadAsync();
        Assert.NotEmpty(viewModel.Targets);
        Assert.NotEmpty(viewModel.Skills);
        Assert.NotEmpty(viewModel.Items);
    }

    [Fact]
    public void A_target_shows_its_stats()
    {
        var viewModel = Build();
        viewModel.SelectedTarget = new CombatTarget { Id = "1", Name = "skeleton", Zone = "undead", Detail = "100 HP · 10 atk · 5 def" };
        Assert.Contains("skeleton", viewModel.PreviewText);
        Assert.Contains("100 HP", viewModel.PreviewText);
    }

    [Fact]
    public void Picking_a_skill_adds_it_to_the_plan_once()
    {
        var viewModel = Build();
        var skill = new CombatOption { Id = "1", Name = "Sword Dance", Kind = "skill", Detail = "" };
        viewModel.SelectedSkill = skill;
        viewModel.SelectedSkill = skill;
        Assert.Single(viewModel.SelectedSkills);
        Assert.Contains("Sword Dance", viewModel.PreviewText);
    }

    [Fact]
    public void Picking_an_item_adds_it_to_the_plan_once()
    {
        var viewModel = Build();
        var item = new CombatOption { Id = "1", Name = "seal-clubbing Club", Kind = "item", Detail = "" };
        viewModel.SelectedItem = item;
        viewModel.SelectedItem = item;
        Assert.Single(viewModel.SelectedItems);
        Assert.Contains("seal-clubbing Club", viewModel.PreviewText);
    }

    [Fact]
    public void The_abort_rules_show_up_in_the_preview()
    {
        var viewModel = Build();
        viewModel.SelectedTarget = new CombatTarget { Id = "1", Name = "skeleton", Zone = "", Detail = "" };
        viewModel.AbortHpPercent = 45;
        viewModel.MaxRounds = 12;
        Assert.Contains("45 % HP", viewModel.PreviewText);
        Assert.Contains("12 rounds", viewModel.PreviewText);
    }

    [Fact]
    public void Removing_a_skill_takes_it_out_of_the_plan()
    {
        var viewModel = Build();
        var skill = new CombatOption { Id = "1", Name = "Trance", Kind = "skill", Detail = "" };
        viewModel.SelectedSkill = skill;
        viewModel.RemoveSkill(skill);
        Assert.Empty(viewModel.SelectedSkills);
        Assert.DoesNotContain("Trance", viewModel.PreviewText);
    }

    [Fact]
    public void Without_a_target_the_preview_asks_for_one()
    {
        var viewModel = Build();
        Assert.Contains("Pick a target", viewModel.PreviewText);
        Assert.False(viewModel.HasConfiguration);
    }

    [Fact]
    public void The_plan_uses_each_skill_and_item_once_in_order_then_attacks()
    {
        var viewModel = Build();
        viewModel.SelectedSkill = new CombatOption { Id = "1001", Name = "a", Kind = "skill", Detail = "" };
        viewModel.SelectedSkill = new CombatOption { Id = "1005", Name = "b", Kind = "skill", Detail = "" };
        viewModel.SelectedItem = new CombatOption { Id = "2002", Name = "c", Kind = "item", Detail = "" };
        var strategy = CombatStrategies.FromRules(viewModel.BuildPolicy());

        var history = new List<FightRound>();
        var expected = new[] { CombatAction.Skill(1001), CombatAction.Skill(1005), CombatAction.Item(2002), CombatAction.Attack, CombatAction.Attack };
        foreach (var want in expected)
        {
            var next = strategy.Next(Round(history.Count + 1, history));
            Assert.Equal(want, next);
            history.Add(Sent(next, history.Count + 1));
        }
    }

    [Fact]
    public void Without_skills_and_items_the_batch_keeps_attacking()
    {
        var viewModel = Build();
        Assert.Null(viewModel.BuildBatchPolicy());
        Assert.Equal(CombatAction.Attack, CombatStrategies.FromRules(viewModel.BuildPolicy()).Next(Round(1, Array.Empty<FightRound>())));
    }
}

/// <summary>
/// The mall screen pairs the account's own listings with the cheapest market offer for the same item — the number
/// that says whether the asking price still wins.
/// </summary>
public sealed class LiveMallViewModelTests
{
    [Fact]
    public async Task Missing_session_yields_no_rows()
    {
        var kol = new FakeKol();
        await using var accounts = DesktopKit.Accounts(kol);
        var viewModel = new LiveMallViewModel(AccountRole.Mallbot, new DesktopLiveDataProvider(accounts));
        await viewModel.LoadAsync();
        Assert.Empty(viewModel.Rows);
        Assert.Equal("Sign in as Mallbot under Accounts to see store listings.", viewModel.Summary);
        Assert.Empty(kol.Requests);
    }
}
