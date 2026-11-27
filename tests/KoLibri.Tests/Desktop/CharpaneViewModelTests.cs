using KoLibri.Desktop;
using KoLibri.Extras;
using Xunit;

namespace KoLibri.Tests.Desktop;

public sealed class CharpaneViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static Status Status(int mp = 100, int? fullLimit = null, int? drunkLimit = null, int? spleenLimit = null) =>
        new("Tester", 42, 1234567, 13, Now)
        {
            Hp = 50, MaxHp = 200, Mp = mp, MaxMp = 300,
            Fullness = new global::KoLibri.OrganState(3, fullLimit), Inebriety = new global::KoLibri.OrganState(0, drunkLimit),
            Spleen = new global::KoLibri.OrganState(7, spleenLimit), FamiliarId = 9, FamiliarWeight = 20,
            Effects = [new ActiveEffect(1, "Ode to Booze", 12), new ActiveEffect(2, "Ur-Kel's", 1)]
        };

    [Fact]
    public void Status_shows_limits_familiar_weight_and_effect_turns()
    {
        var s = CharpaneState.FromStatus(Status(), "Main", Now);
        Assert.Equal("HP 50 / 200", s.Hp);
        Assert.Equal(0.25, s.HpFraction);
        Assert.Equal("Fullness 3 / ?", s.Fullness);
        Assert.Equal("Drunkenness 0 / ?", s.Drunk);
        Assert.Equal("Spleen 7 / ?", s.Spleen);
        Assert.Equal("Familiar #9 · 20 lb", s.Familiar);
        Assert.Equal("Ode to Booze · 12 turns", s.Effects[0].Display);
        Assert.Equal("Ur-Kel's · 1 turn", s.Effects[1].Display);
        Assert.Null(s.Message);
    }

    [Fact]
    public void Limits_from_the_charsheet_are_shown_and_unknown_ones_are_question_marks()
    {
        var s = CharpaneState.FromStatus(Status(fullLimit: 15, drunkLimit: 19, spleenLimit: 15), "Main", Now);
        Assert.Equal("Fullness 3 / 15", s.Fullness);
        Assert.Equal("Drunkenness 0 / 19", s.Drunk);
        Assert.Equal("Fullness 3/15 · Inebriety 0/19 · Spleen 7/15",
            KoLibri.Desktop.OrganState.From(Status(fullLimit: 15, drunkLimit: 19, spleenLimit: 15)).Summary);
        Assert.Equal("Drunkenness 0 / ?", CharpaneState.FromStatus(Status(), "Main", Now).Drunk);
        Assert.Contains("?", KoLibri.Desktop.OrganState.From(Status()).Summary);
    }

    [Fact]
    public void Empty_name_falls_back_to_the_role_and_no_familiar_says_none()
    {
        var bare = new Status("", 0, 0, 1, Now);
        var b = CharpaneState.FromStatus(bare, "Main", Now);
        Assert.Equal("Fullness 0 / ?", b.Fullness);
        Assert.Equal("HP —", b.Hp);
        Assert.Equal("None", b.Familiar);
        Assert.Equal("None", b.EffectsText);
        Assert.Equal("Main", b.Name);
        Assert.Null(b.Message);
        Assert.Equal("Main", b.Role);
    }

    [Fact]
    public void Named_familiar_shows_name_and_weight()
        => Assert.Equal("Gogo · 20 lb", CharpaneState.DescribeFamiliar(Status() with { FamiliarName = " Gogo " }));

    [Fact]
    public void Signed_out_and_expired_session_have_clear_text()
    {
        Assert.Contains("sign in", CharpaneState.SignedOut("Main").Message, StringComparison.OrdinalIgnoreCase);
        var expired = CharpaneState.Failed("Main", new SessionExpiredException(new AccountId("main")));
        Assert.True(expired.IsError);
        Assert.Contains("expired", expired.Message);
        var page = new Page(new Uri("https://www.kingdomofloathing.com/main.php"), 200, "", PageKind.Maintenance, Now);
        Assert.Contains("unexpected page", CharpaneState.DescribeError(new UnexpectedPageException(page, "maintenance")));
        Assert.StartsWith("Could not reach KoL", CharpaneState.DescribeError(new HttpRequestException("down")));
    }

    private static ActionResult Outcome(int hp0, int mp0, int hp1, int mp1, bool ok, string? problem = null, int completed = -1)
        => new(ok ? ActionStatus.Done : ActionStatus.Rejected, completed >= 0 ? completed : ok ? 1 : 0, problem, null)
        {
            Before = Status(mp0) with { Hp = hp0 },
            After = Status(mp1) with { Hp = hp1 }
        };

    private static SkillsEffectsViewModel Model() => new([
        new SkillRow(3012, "Cannelloni Cocoon", 20, "noncombat"),
        new SkillRow(7185, "Soul Food", 0, "noncombat"),
        new SkillRow(3009, "Lasagna Bandages", 5, "combat")]);

    [Fact]
    public async Task Cast_needs_confirmation_and_sends_nothing_before()
    {
        var m = Model();
        var calls = 0;
        m.SelectedSkill = m.Skills.First(s => s.Name == "Cannelloni Cocoon");
        m.Quantity = 3;
        Assert.True(m.RequestCast());
        Assert.Equal("Cast Cannelloni Cocoon ×3 for 60 MP?", m.PendingText);
        Assert.Equal(0, calls);
        m.CancelCast();
        Assert.False(m.HasPending);
        Assert.Null(await m.ConfirmCastAsync((_, _, _) => { calls++; return Task.FromResult(Outcome(1, 1, 1, 1, true)); }));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Role_switch_discards_pending_cast_and_text_names_the_role()
    {
        var m = Model();
        m.Role = "Main";
        m.SelectedSkill = m.Skills.First(s => s.Name == "Cannelloni Cocoon");
        Assert.True(m.RequestCast());
        Assert.Equal("Cast Cannelloni Cocoon ×1 as Main for 20 MP?", m.PendingText);
        m.Role = "Mallbot";
        Assert.False(m.HasPending);
        var calls = 0;
        Assert.Null(await m.ConfirmCastAsync((_, _, _) => { calls++; return Task.FromResult(Outcome(1, 1, 1, 1, true)); }));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Confirmed_cast_reports_hp_mp_change_and_updates_effects()
    {
        var m = Model();
        m.ApplyStatus(Status(100));
        m.SelectedSkill = m.Skills.First(s => s.Name == "Cannelloni Cocoon");
        m.Quantity = 2;
        m.RequestCast();
        int? gotId = null, gotTimes = null;
        var report = await m.ConfirmCastAsync((id, times, _) =>
        {
            gotId = id; gotTimes = times;
            return Task.FromResult(Outcome(50, 100, 150, 60, true, completed: 2));
        });
        Assert.Equal((3012, 2), (gotId, gotTimes));
        Assert.True(report!.Confirmed);
        Assert.Equal(100, report.HpChange);
        Assert.Equal(-40, report.MpChange);
        Assert.Equal("Cast Cannelloni Cocoon ×2 (each confirmed). HP +100, MP -40.", m.ResultText);
        Assert.Equal(2, m.Effects.Count);
        Assert.False(m.HasPending);
        Assert.False(m.IsBusy);
    }

    [Fact]
    public async Task Rejection_and_exceptions_become_visible_text()
    {
        var m = Model();
        m.SelectedSkill = m.Skills.First(s => s.Name == "Soul Food");
        m.Quantity = 5;
        m.RequestCast();
        Assert.Contains("no MP cost", m.PendingText);
        var r = await m.ConfirmCastAsync((_, _, _) => Task.FromResult(
            Outcome(10, 10, 10, 10, false, "KoL refused the cast: not enough mana or soulsauce.")));
        Assert.False(r!.Confirmed);
        Assert.Contains("soulsauce", m.ResultText);

        m.RequestCast();
        await m.ConfirmCastAsync((_, _, _) => throw new SessionExpiredException(new AccountId("main")));
        Assert.Contains("expired", m.ResultText);
        Assert.False(m.IsBusy);
    }

    [Fact]
    public void Not_enough_mp_blocks_the_confirmation()
    {
        var m = Model();
        m.ApplyStatus(Status(30));
        m.SelectedSkill = m.Skills.First(s => s.Name == "Cannelloni Cocoon");
        m.Quantity = 2;
        Assert.False(m.RequestCast());
        Assert.Contains("needs 40", m.ResultText);
        Assert.False(m.HasPending);
    }

    [Fact]
    public void Filter_and_catalog_projection_leave_out_combat_and_passive()
    {
        var rows = SkillsEffectsViewModel.FromCatalog([
            new MafiaSkillRecord { Id = 1, Name = "Salve", Type = "noncombat", MpCost = 5 },
            new MafiaSkillRecord { Id = 2, Name = "Toss", Type = "combat" },
            new MafiaSkillRecord { Id = 3, Name = "Gnomish", Type = "passive" }]).ToList();
        Assert.Equal(["Salve"], rows.Select(r => r.Name));
        var m = Model();
        m.Filter = "soul";
        Assert.Equal(["Soul Food"], m.Skills.Select(s => s.Name));
    }
}
