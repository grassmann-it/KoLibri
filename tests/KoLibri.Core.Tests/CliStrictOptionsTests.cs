using System.Net;
using Xunit;
using KoLibri.Core.Basis;

namespace KoLibri.Core.Tests;

public sealed class CliStrictOptionsTests
{
    private static async Task<(int Code, string Out)> Run(Func<string[], Task<int>> command, params string[] args)
    {
        var original = Console.Out;
        var writer = new StringWriter();
        Console.SetOut(writer);
        try { return (await command(args), writer.ToString()); }
        finally { Console.SetOut(original); }
    }

    public static IEnumerable<object[]> BadWriteInvocations() => new[]
    {
        new object[] { "rest", new[] { "--times", "0", "--execute" } },
        new object[] { "rest", new[] { "--times", "typo", "--execute" } },
        new object[] { "rest", new[] { "--times", "101", "--execute" } },
        new object[] { "rest", new[] { "--execute", "--role" } },
        new object[] { "rest", new[] { "--role", "--execute" } },
        new object[] { "rest", new[] { "--role", "mainn", "--execute" } },
        new object[] { "rest", new[] { "--timez", "2", "--execute" } },
        new object[] { "consume", new[] { "--kind", "food", "--item", "1", "--qty", "typo", "--execute" } },
        new object[] { "consume", new[] { "--kind", "food", "--item", "1", "--qty", "0", "--execute" } },
        new object[] { "consume", new[] { "--kind", "food", "--item", "x", "--execute" } },
        new object[] { "consume", new[] { "--kind", "food", "--item", "1", "--execute", "--role" } },
        new object[] { "consume", new[] { "--kind", "food", "--item", "1", "--role", "mallbo", "--execute" } },
        new object[] { "cast", new[] { "--skill", "7185", "--times", "0", "--execute" } },
        new object[] { "cast", new[] { "--skill", "7185", "--times", "many", "--execute" } },
        new object[] { "cast", new[] { "--skill", "--execute" } },
        new object[] { "cast", new[] { "--skill", "7185", "--execute", "--role" } },
    };

    [Theory]
    [MemberData(nameof(BadWriteInvocations))]
    public async Task Write_commands_reject_bad_input_before_any_game_action(string command, string[] args)
    {
        Func<string[], Task<int>> run = command switch
        {
            "rest" => KolibriCli.RestAsync,
            "consume" => KolibriCli.ConsumeAsync,
            _ => KolibriCli.CastAsync
        };
        // Parsing throws before a session is opened; nothing is printed as an executed action.
        var original = Console.Out;
        var writer = new StringWriter();
        Console.SetOut(writer);
        try { await Assert.ThrowsAsync<ArgumentException>(() => run(args)); }
        finally { Console.SetOut(original); }
        Assert.DoesNotContain("\"executed\": true", writer.ToString());
    }

    [Fact]
    public async Task Valid_previews_keep_working_and_defaults_apply_only_when_omitted()
    {
        var (code, output) = await Run(KolibriCli.RestAsync);
        Assert.Equal(0, code);
        Assert.Contains("\"role\":\"Main\"", output.Replace(" ", ""));
        Assert.Contains("\"times\":1", output.Replace(" ", ""));
        (code, output) = await Run(KolibriCli.RestAsync, "--role", "mallbot", "--times", "3");
        Assert.Equal(0, code);
        Assert.Contains("\"times\":3", output.Replace(" ", ""));
        (code, output) = await Run(KolibriCli.ConsumeAsync, "--kind", "food", "--item", "5", "--qty", "2");
        Assert.Equal(0, code);
        Assert.Contains("\"quantity\":2", output.Replace(" ", ""));
        (code, _) = await Run(KolibriCli.CastAsync, "--skill", "7185", "--times", "4");
        Assert.Equal(0, code);
    }

    private sealed class Stub(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var r = respond();
            return Task.FromResult(r);
        }
    }

    [Fact]
    public async Task Session_probe_separates_rejection_from_unreachable()
    {
        var creds = new AccountCredentials();
        Assert.Equal(KolibriCli.SessionProbe.Works, await KolibriCli.ProbeSessionAsync(creds,
            new Stub(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"playerid\":\"1\"}") })));
        Assert.Equal(KolibriCli.SessionProbe.Unreachable, await KolibriCli.ProbeSessionAsync(creds,
            new Stub(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))));
        Assert.Equal(KolibriCli.SessionProbe.Unreachable, await KolibriCli.ProbeSessionAsync(creds,
            new Stub(() => throw new TaskCanceledException())));
        Assert.Equal(KolibriCli.SessionProbe.Rejected, await KolibriCli.ProbeSessionAsync(creds,
            new Stub(() => new HttpResponseMessage(HttpStatusCode.Forbidden))));
        var redirect = new HttpResponseMessage(HttpStatusCode.Found);
        redirect.Headers.Location = new Uri("https://www.kingdomofloathing.com/login.php");
        Assert.Equal(KolibriCli.SessionProbe.Rejected, await KolibriCli.ProbeSessionAsync(creds, new Stub(() => redirect)));
    }

    [Fact]
    public void Status_row_does_not_call_unreachable_a_rejection_and_has_no_loggedInAt()
    {
        var profile = new AccountProfile { Id = "main", DisplayName = "x", Role = AccountRole.Main, Enabled = true };
        var session = new AccountSession(profile, new AccountCredentials { CookieHeader = "c=1", ExpiresAt = DateTimeOffset.UtcNow.AddDays(1) });
        var json = System.Text.Json.JsonSerializer.Serialize(KolibriCli.LoginStatusRow(AccountRole.Main, profile, session, KolibriCli.SessionProbe.Unreachable));
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.False(root.GetProperty("rejectedByServer").GetBoolean());
        Assert.True(root.GetProperty("serverUnreachable").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, root.GetProperty("usable").ValueKind);
        Assert.False(root.TryGetProperty("loggedInAt", out _));
        json = System.Text.Json.JsonSerializer.Serialize(KolibriCli.LoginStatusRow(AccountRole.Main, profile, session, KolibriCli.SessionProbe.Rejected));
        using var doc2 = System.Text.Json.JsonDocument.Parse(json);
        Assert.True(doc2.RootElement.GetProperty("rejectedByServer").GetBoolean());
        Assert.False(doc2.RootElement.GetProperty("usable").GetBoolean());
    }
}
