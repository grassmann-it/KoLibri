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
        Assert.Equal(2, table.Version);
        Assert.True(table.TryGet(1060, out var e));
        Assert.True(e.IsLive);
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
    [InlineData("""{"version":3,"choices":[]}""")]
    [InlineData("""{"version":2,"choices":[{"id":1,"source":"x","confidence":"guess"}]}""")]
    [InlineData("""{"version":2,"choices":[{"id":1,"source":"x","options":{"1":{"items":"denim axe"}}}]}""")]
    [InlineData("""{"version":2,"choices":[{"id":1,"source":"x","options":{"1":{"startsFight":"yes"}}}]}""")]
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

    // --- Version 2: importierte KoLmafia-Spoiler ---

    private const string KolmafiaSample = """
        {"version":2,"choices":[
          {"id":4,"name":"Finger-Lickin'... Death.","location":"South of the Border","zone":"Beach","default":3,
           "options":{"1":{"label":"small meat boost","costsMeat":true,"safe":false},
                      "2":{"label":"try for poultrygeist","items":["poultrygeist"],"costsMeat":true,"safe":false},
                      "3":{"label":"skip adventure","skipsTurn":true,"safe":true}},
           "source":"kolmafia 95ebac8dce63","confidence":"kolmafia-default"},
          {"id":1427,"name":"The Hidden Junction","default":1,
           "options":{"1":{"label":"fight screambat","startsFight":true,"safe":false},"2":{"label":"gain 300-400 meat","safe":true}},
           "source":"kolmafia 95ebac8dce63","confidence":"kolmafia-default"},
          {"id":20,"default":1,"options":{"1":{"label":"no flag stated"}},"source":"kolmafia x","confidence":"kolmafia-default"},
          {"id":30,"default":1,"options":{"1":{"label":"fight it","startsFight":true,"safe":false}},"source":"live verifiziert","confidence":"live"},
          {"id":40,"name":"Turtle Thing","default":1,"options":{"1":{"label":"fight","startsFight":true,"safe":false},"2":{"label":"pet"}},
           "source":"kolmafia x","confidence":"kolmafia-default"}
        ]}
        """;

    [Fact]
    public void Version_2_import_format_reads_flags_items_location_and_confidence()
    {
        var table = Parse(KolmafiaSample);
        Assert.Equal(2, table.Version);
        Assert.True(table.TryGet(4, out var e));
        Assert.Equal(ChoiceDefault.KolmafiaConfidence, e.Confidence);
        Assert.False(e.IsLive);
        Assert.Equal("South of the Border", e.Location);
        Assert.Equal("Beach", e.Zone);
        Assert.Equal(3, e.Default);
        Assert.True(e.Options[1].CostsMeat);
        Assert.False(e.Options[1].StartsFight);
        Assert.Equal(["poultrygeist"], e.Options[2].Items);
        Assert.True(e.Options[3].SkipsTurn);
        Assert.True(e.Options[3].Safe);
        Assert.True(table.TryGet(1427, out var j));
        Assert.True(j.Options[1].StartsFight);
    }

    [Fact]
    public void Missing_confidence_means_live_so_version_1_files_keep_working()
    {
        var table = Parse("""{"version":1,"choices":[{"id":9,"default":1,"source":"s"}]}""");
        Assert.True(table.Entries.Single().IsLive);
        Assert.Equal(1, table.Resolver().Resolve(Ctx(9, null, 1, 2)).Option);
    }

    [Fact]
    public void Safe_kolmafia_default_is_taken()
    {
        var d = Parse(KolmafiaSample).Resolver().Resolve(Ctx(4, "Finger-Lickin'... Death.", 1, 2, 3));
        Assert.Equal(3, d.Option);
        Assert.Equal(ChoiceDecisionSource.BuiltIn, d.Source);
        Assert.Contains("KoLmafia", d.Reason);
        Assert.Contains("skip adventure", d.Reason);
    }

    [Fact]
    public void Unsafe_kolmafia_default_asks_with_spoilers_and_the_choice_action_hint()
    {
        var d = Parse(KolmafiaSample).Resolver().Resolve(Ctx(1427, "The Hidden Junction", 1, 2));
        Assert.Null(d.Option);
        Assert.Equal(ChoiceDecisionSource.None, d.Source);
        Assert.Contains("startet Kampf", d.Reason);
        Assert.Contains("1 = fight screambat [Kampf]", d.Reason);
        Assert.Contains("2 = gain 300-400 meat", d.Reason);
        Assert.Contains("--choice-action 1427:<N>", d.Reason);
    }

    [Fact]
    public void Kolmafia_default_without_safe_flag_asks()
    {
        var d = Parse(KolmafiaSample).Resolver().Resolve(Ctx(20, null, 1, 2));
        Assert.Null(d.Option);
        Assert.Contains("nicht als sicher markiert", d.Reason);
    }

    [Fact]
    public void Live_entry_beats_kolmafia_flags()
    {
        // Live verifizierte Defaults gelten auch dann, wenn die Option als Kampf markiert ist.
        var d = Parse(KolmafiaSample).Resolver().Resolve(Ctx(30, null, 1, 2));
        Assert.Equal(1, d.Option);
        Assert.Equal(ChoiceDecisionSource.BuiltIn, d.Source);
    }

    [Fact]
    public void Override_beats_an_unsafe_kolmafia_default()
    {
        var d = Parse(KolmafiaSample).Resolver(new Dictionary<int, int> { [1427] = 1 }).Resolve(Ctx(1427, null, 1, 2));
        Assert.Equal(1, d.Option);
        Assert.Equal(ChoiceDecisionSource.Configured, d.Source);
    }

    [Fact]
    public void Turtle_with_an_unsafe_kolmafia_default_still_clicks_through()
    {
        var table = Parse(KolmafiaSample);
        var d = table.Resolver().Resolve(Ctx(40, "Turtle Soup Kitchen", 1, 2));
        Assert.Equal(1, d.Option);
        Assert.Contains("turtle", d.Reason);
        Assert.Null(table.Resolver(stopOnTurtle: true).Resolve(Ctx(40, "Turtle Soup Kitchen", 1, 2)).Option);
    }

    [Fact]
    public void Embedded_table_holds_kolmafia_spoilers_and_keeps_1060_live()
    {
        var table = ChoiceDefaults.Embedded;
        var imported = table.Entries.Where(e => !e.IsLive).ToArray();
        Assert.True(imported.Length > 400, $"nur {imported.Length} KoLmafia-Einträge");
        Assert.All(imported, e =>
        {
            Assert.Equal(ChoiceDefault.KolmafiaConfidence, e.Confidence);
            Assert.StartsWith("kolmafia ", e.Source);
        });
        // Jede KoLmafia-Default-Option, die Kosten/Kampf/Schaden trägt, ist nicht sicher.
        Assert.All(imported.SelectMany(e => e.Options.Values), o =>
        {
            if (o.CostsMeat || o.CostsItem || o.StartsFight || o.Hurts) Assert.False(o.Safe);
        });
        Assert.Equal(new[] { 1060 }, table.Entries.Where(e => e.IsLive).Select(e => e.Id).ToArray());
    }

    [Theory]
    [InlineData(3, 3, new[] { 1, 2, 3 })]      // The Oracle: "make plus sign usable"
    [InlineData(5, 2, new[] { 1, 2 })]         // Gravy Barrow: Feenkönigin-Kampf → skip adventure
    public void Embedded_safe_kolmafia_defaults_resolve(int choice, int expected, int[] visible)
    {
        var d = ChoiceDefaults.Resolver().Resolve(Ctx(choice, null, visible));
        Assert.Equal(expected, d.Option);
        Assert.Equal(ChoiceDecisionSource.BuiltIn, d.Source);
    }

    [Theory]
    [InlineData(6, "Beaten Up")]               // Darker Than Dark: Default 1 = "get Beaten Up"
    [InlineData(1427, "fight screambat")]      // The Hidden Junction: Default 1 = Kampf
    public void Embedded_unsafe_kolmafia_defaults_ask(int choice, string spoiler)
    {
        var d = ChoiceDefaults.Resolver().Resolve(Ctx(choice, null, 1, 2));
        Assert.Null(d.Option);
        Assert.Contains(spoiler, d.Reason);
        Assert.Contains($"--choice-action {choice}:", d.Reason);
    }

    [Fact]
    public void Embedded_resource_stays_small()
    {
        using var stream = typeof(ChoiceDefaults).Assembly.GetManifestResourceStream("KoLibri.Data.choices.json");
        Assert.NotNull(stream);
        Assert.True(stream!.Length < 500 * 1024, $"choices.json ist {stream.Length / 1024} KB");
    }
}
