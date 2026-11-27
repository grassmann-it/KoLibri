using Xunit;

namespace KoLibri.Core.Tests;

/// <summary>
/// Tests that run the CLI. The CLI writes to the process-wide <see cref="Console.Out"/>, and some of these tests swap it
/// with <see cref="Console.SetOut"/> to read the JSON it prints. Run in parallel, one test's CLI output lands in
/// another test's writer and its JSON no longer parses. <c>DisableParallelization</c> runs this collection on its own,
/// after the parallel ones, so no other test writes to the console while one is redirected.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleCollection
{
    public const string Name = "Console (CLI output)";
}
