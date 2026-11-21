using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace KolArbitrage.Core.Tests;

/// <summary>
/// Pins the three-layer split of KoLibri.Core: Basis (accounts, security, safety, audit, requests)
/// is used by Spiel (adventure, daily, client, data), which is used by Markt (mall, pricing,
/// analytics). A lower layer must never name a higher one.
/// </summary>
public sealed class CoreLayeringTests
{
    private static readonly string CoreRoot = Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(ThisFile())!, "..", "..", "src", "KolArbitrage.Core"));

    private static string ThisFile([CallerFilePath] string path = "") => path;

    private static IEnumerable<(string Layer, string File, string Text)> Sources()
    {
        foreach (var layer in new[] { "Basis", "Spiel", "Markt" })
            foreach (var file in Directory.EnumerateFiles(Path.Combine(CoreRoot, layer), "*.cs", SearchOption.AllDirectories)
                         .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
                yield return (layer, file, File.ReadAllText(file));
    }

    [Fact]
    public void Every_file_declares_the_namespace_of_its_layer()
    {
        var wrong = Sources()
            .Where(s => !Regex.IsMatch(s.Text, $@"^namespace KolArbitrage\.Core\.{s.Layer}\b", RegexOptions.Multiline))
            .Select(s => Path.GetRelativePath(CoreRoot, s.File)).ToArray();
        Assert.Empty(wrong);
    }

    [Theory]
    [InlineData("Basis", "Spiel")]
    [InlineData("Basis", "Markt")]
    [InlineData("Spiel", "Markt")]
    public void A_lower_layer_never_names_a_higher_one(string lower, string higher)
    {
        var pattern = new Regex($@"KolArbitrage\.Core\.{higher}\b");
        var offenders = Sources()
            .Where(s => s.Layer == lower && pattern.IsMatch(s.Text))
            .Select(s => Path.GetRelativePath(CoreRoot, s.File)).ToArray();
        Assert.Empty(offenders);
    }
}
