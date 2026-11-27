using KoLibri.Core.Spiel;
using Xunit;

namespace KoLibri.Core.Tests;

public sealed class OrganLimitDeriverTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static KoLStatusSnapshot Char(IReadOnlyList<string>? skills, string classId = "2", int? path = 0,
        Dictionary<string, string>? equipment = null, string? familiarId = null) =>
        new("Tester", 40, 1000, 13, Now)
        {
            ClassId = classId, PathId = path, Skills = skills, FamiliarId = familiarId,
            Fullness = 3, Inebriety = 0, Spleen = 7,
            Equipment = equipment ?? new Dictionary<string, string>()
        };

    [Fact]
    public void Plain_character_gets_the_base_15_14_15_as_derived()
    {
        var l = OrganLimitDeriver.Derive(Char([]));
        Assert.Equal(OrganLimitConfidence.Derived, l.Confidence);
        Assert.Equal((15, 14, 15), (l.Fullness, l.Inebriety, l.Spleen));
    }

    [Fact]
    public void Turtle_tamer_with_liver_and_stomach_and_spleen_of_steel_gets_raised_limits()
    {
        var s = Char(["Liver of Steel", "Stomach of Steel (P)", "Spleen of Steel", "Hollow Leg", "Toss"],
            equipment: new() { ["acc1"] = "12072", ["pants"] = "devilbone greaves" });
        var l = OrganLimitDeriver.Derive(s);
        Assert.Equal(OrganLimitConfidence.Derived, l.Confidence);
        Assert.Equal(15 + 5, l.Fullness);
        Assert.Equal(14 + 5 + 1 + 1, l.Inebriety);
        Assert.Equal(15 + 5 + 1, l.Spleen);
    }

    [Fact]
    public void Stooper_familiar_adds_one_liver()
        => Assert.Equal(15, OrganLimitDeriver.Derive(Char([], familiarId: "261")).Inebriety);

    [Fact]
    public void Unread_skills_are_unknown_never_a_default()
    {
        var l = OrganLimitDeriver.Derive(Char(null));
        Assert.Equal(OrganLimitConfidence.Unknown, l.Confidence);
        Assert.Null(l.Inebriety);
        var applied = OrganLimitDeriver.Apply(Char(null));
        Assert.Null(applied.InebrietyLimit);
    }

    [Theory]
    [InlineData("8", 0)]   // Avatar of Boris class
    [InlineData("2", 1)]   // Boozetafarian
    [InlineData("2", 35)]  // Dark Gyffte
    public void Special_classes_and_paths_are_unknown(string classId, int path)
        => Assert.Equal(OrganLimitConfidence.Unknown, OrganLimitDeriver.Derive(Char([], classId, path)).Confidence);

    [Fact]
    public void Ambiguous_hard_drinker_and_pantsgiving_are_unknown()
    {
        Assert.Equal(OrganLimitConfidence.Unknown, OrganLimitDeriver.Derive(Char(["Hard Drinker"])).Confidence);
        Assert.Equal(OrganLimitConfidence.Unknown,
            OrganLimitDeriver.Derive(Char([], equipment: new() { ["pants"] = "6860" })).Confidence);
    }

    [Fact]
    public void Limits_reported_by_kol_win()
    {
        var s = Char(null) with { FullnessLimit = 30, InebrietyLimit = 25, SpleenLimit = 20 };
        var l = OrganLimitDeriver.Derive(s);
        Assert.Equal(OrganLimitConfidence.Reported, l.Confidence);
        Assert.Equal(25, l.Inebriety);
    }

    [Fact]
    public void Charsheet_skill_links_are_read_and_a_login_page_is_not_mistaken_for_no_skills()
    {
        var html = "<b>Skills</b><a onclick='skill(1)'>Liver of Steel (P)</a> <a>Toss</a> <a>Spleen of Steel</a>";
        Assert.Equal(["Liver of Steel", "Spleen of Steel"], OrganLimitDeriver.ParseCharsheetSkills(html));
        Assert.Null(OrganLimitDeriver.ParseCharsheetSkills("<html>Please log in</html>"));
    }

    [Fact]
    public void Modifier_table_matches_bundled_modifiers_txt_when_present()
    {
        var dir = AppContext.BaseDirectory;
        string? file = null;
        for (var d = new DirectoryInfo(dir); d is not null && file is null; d = d.Parent)
        {
            var c = Path.Combine(d.FullName, "external", "kolmafia", "src", "main", "resources", "data", "modifiers.txt");
            if (File.Exists(c)) file = c;
        }
        if (file is null) return; // submodule not checked out: nothing to compare
        foreach (var line in File.ReadLines(file))
        {
            var parts = line.Split('\t');
            if (parts.Length < 3 || parts[0] != "Skill") continue;
            var name = System.Text.RegularExpressions.Regex.Replace(parts[1], @"^\[\d+\]", "");
            if (name == "Hard Drinker") continue;
            int Mod(string key) => System.Text.RegularExpressions.Regex.Match(parts[2], key + @": ([+-]\d+)") is { Success: true } m
                ? int.Parse(m.Groups[1].Value) : 0;
            var expected = (Mod("Stomach Capacity"), Mod("Liver Capacity"), Mod("Spleen Capacity"));
            if (expected == (0, 0, 0)) continue;
            Assert.True(OrganLimitDeriver.SkillModifiers.TryGetValue(name, out var actual), $"missing skill {name}");
            Assert.Equal(expected, actual);
        }
    }
}
