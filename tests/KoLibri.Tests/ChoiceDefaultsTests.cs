using System.Text;
using KoLibri;
using Xunit;

namespace KoLibri.Tests;

public sealed class ChoiceDefaultsTests
{
    private static ChoiceContext Ctx(int id, string? title, params int[] options)
        => new(null, id, title, options.Select(o => new ChoiceOption(o, $"Option {o}")).ToArray());

    private static ChoiceDefaultTable Parse(string json)
        => ChoiceDefaults.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void Embedded_resource_loads_and_contains_the_verified_farm_choice()
    {
        var table = ChoiceDefaults.Embedded;
        Assert.Equal(1, table.Version);
        Assert.True(table.TryGet(1060, out var e));
        Assert.Equal(2, e.Default);
        Assert.Equal(439, e.Snarfblat);
        Assert.False(string.IsNullOrWhiteSpace(e.Source));
    }

    [Fact]
    public void Choice_1060_resolves_to_option_2_without_a_hand_table()
    {
        var d = ChoiceDefaults.Resolver().Resolve(Ctx(1060, "Whatever", 1, 2));
        Assert.Equal(2, d.Option);
        Assert.Equal(ChoiceDecisionSource.BuiltIn, d.Source);
        Assert.Contains("1060", d.Reason);
    }

    [Fact]
    public void Unknown_choice_asks()
    {
        var d = ChoiceDefaults.Resolver().Resolve(Ctx(9999, "Something", 1, 2));
        Assert.Null(d.Option);
        Assert.Equal(ChoiceDecisionSource.None, d.Source);
    }

    [Fact]
    public void Override_beats_default()
    {
        var resolver = ChoiceDefaults.Resolver(new Dictionary<int, int> { [1060] = 1 });
        var d = resolver.Resolve(Ctx(1060, null, 1, 2));
        Assert.Equal(1, d.Option);
        Assert.Equal(ChoiceDecisionSource.Configured, d.Source);
    }

    [Fact]
    public void Override_for_an_unlisted_choice_applies()
    {
        var d = ChoiceDefaults.Resolver(new Dictionary<int, int> { [4242] = 3 }).Resolve(Ctx(4242, null, 1, 3));
        Assert.Equal(3, d.Option);
    }

    [Fact]
    public void Override_not_visible_asks_and_does_not_fall_back_to_default()
    {
        var d = ChoiceDefaults.Resolver(new Dictionary<int, int> { [1060] = 7 }).Resolve(Ctx(1060, null, 1, 2));
        Assert.Null(d.Option);
        Assert.Contains("7", d.Reason);
    }

    [Fact]
    public void Default_not_visible_asks()
    {
        var d = ChoiceDefaults.Resolver().Resolve(Ctx(1060, null, 1, 3));
        Assert.Null(d.Option);
        Assert.Contains("nicht sichtbar", d.Reason);
    }

    [Fact]
    public void Turtle_title_without_entry_clicks_through_with_the_first_visible_option()
    {
        var d = ChoiceDefaults.Resolver().Resolve(Ctx(777, "Turtle Soup Kitchen", 3, 2, 4));
        Assert.Equal(2, d.Option);
        Assert.Equal(ChoiceDecisionSource.BuiltIn, d.Source);
        Assert.Contains("turtle: standardmäßig durchklicken", d.Reason);
        Assert.Contains("unbelegt", d.Reason);
    }

    [Fact]
    public void Turtle_with_stop_opt_in_asks_with_the_choice_id()
    {
        var d = ChoiceDefaults.Resolver(stopOnTurtle: true).Resolve(Ctx(777, "Turtle Soup Kitchen", 1, 2));
        Assert.Null(d.Option);
        Assert.Contains("777", d.Reason);
        Assert.Contains("Turtle Tamer", d.Reason);
        Assert.Contains("--choice-action 777", d.Reason);
    }

    [Fact]
    public void Turtle_flag_entry_clicks_through_or_stops_on_opt_in()
    {
        var table = Parse("""{"version":1,"choices":[{"id":555,"default":null,"turtle":true,"source":"test"}]}""");
        Assert.Equal(1, table.Resolver().Resolve(Ctx(555, "Opaque Title", 1, 2)).Option);
        var d = table.Resolver(stopOnTurtle: true).Resolve(Ctx(555, "Opaque Title", 1, 2));
        Assert.Null(d.Option);
        Assert.Contains("555", d.Reason);
    }

    [Fact]
    public void Verified_turtle_default_beats_the_first_option()
    {
        var table = Parse("""{"version":1,"choices":[{"id":555,"default":2,"turtle":false,"source":"test"}]}""");
        Assert.Equal(2, table.Resolver().Resolve(Ctx(555, "Turtle Thing", 1, 2)).Option);
    }

    [Fact]
    public void Turtle_choice_can_still_be_answered_by_an_override()
    {
        var d = ChoiceDefaults.Resolver(new Dictionary<int, int> { [555] = 2 }).Resolve(Ctx(555, "Turtle Thing", 1, 2));
        Assert.Equal(2, d.Option);
        Assert.Equal(ChoiceDecisionSource.Configured, d.Source);
    }

    [Fact]
    public void Turtle_choice_without_visible_options_asks()
    {
        Assert.Null(ChoiceDefaults.Resolver().Resolve(Ctx(777, "Turtle Soup Kitchen")).Option);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"choices":[]}""")]
    [InlineData("""{"version":2,"choices":[]}""")]
    [InlineData("""{"version":1}""")]
    [InlineData("""{"version":1,"choices":[{"source":"x"}]}""")]
    [InlineData("""{"version":1,"choices":[{"id":1}]}""")]
    [InlineData("""{"version":1,"choices":[{"id":1,"source":"x"},{"id":1,"source":"y"}]}""")]
    [InlineData("""{"version":1,"choices":[{"id":1,"source":"x","default":3,"options":{"1":{},"2":{}}}]}""")]
    [InlineData("""{"version":1,"choices":[{"id":1,"source":"x","default":2,"turtle":true}]}""")]
    [InlineData("""{"version":1,"choices":[{"id":1,"source":"x","options":{"a":{}}}]}""")]
    [InlineData("""{"version":1,"choices":[{"id":"1","source":"x"}]}""")]
    public void Broken_file_throws_a_clear_exception(string json)
    {
        var e = Assert.Throws<InvalidDataException>(() => Parse(json));
        Assert.StartsWith("choices.json:", e.Message);
    }

    [Fact]
    public void Options_keep_only_what_the_file_states()
    {
        var table = Parse("""{"version":1,"choices":[{"id":9,"name":"N","default":1,"source":"s","options":{"1":{"label":"L","costsTurn":true},"2":{"safe":true}}}]}""");
        var e = table.Entries.Single();
        Assert.Equal("L", e.Options[1].Label);
        Assert.True(e.Options[1].CostsTurn);
        Assert.Null(e.Options[1].Safe);
        Assert.True(e.Options[2].Safe);
    }

    [Fact]
    public void Encounters_detect_turtles()
    {
        var page = new Page(new Uri("https://www.kingdomofloathing.com/main.php"), 200, "", PageKind.Normal, DateTimeOffset.UnixEpoch);
        Assert.True(Encounters.IsTurtleMonster("giant sea turtle"));
        Assert.False(Encounters.IsTurtleMonster("skeleton"));
        Assert.False(Encounters.IsTurtleMonster(null));
        Assert.True(Encounters.IsTurtleEncounter(new ChoiceEncounter(page, null, "Turtle Soup Kitchen", 777, [])));
        Assert.False(Encounters.IsTurtleEncounter(new ChoiceEncounter(page, null, "Temporarily Out of Skeletons", 1060, [])));
        Assert.True(Encounters.IsTurtleEncounter(new FightEncounter(page, null, null, "snapping turtle", null, 1)));
        Assert.False(Encounters.IsTurtleEncounter(new FightEncounter(page, null, null, "skeleton", null, 1)));
    }
}
