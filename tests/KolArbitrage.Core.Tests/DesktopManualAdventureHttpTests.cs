using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System.Net;
using KoLibri.Desktop;
using LibKolibri;
using Xunit;
using KolArbitrage.Core.Basis;
using KolArbitrage.Core.Spiel;

namespace KolArbitrage.Core.Tests;

public sealed class DesktopManualAdventureHttpTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Desktop_completes_fight_and_choice_through_the_account_bound_http_transport(bool fightTimesOut)
    {
        var root = Directory.CreateTempSubdirectory("kolibri-http-");
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures");
            var responses = new List<object>
            {
                File.ReadAllText(Path.Combine(fixture, "Adventure", "combat-round-1.html")),
                File.ReadAllText(Path.Combine(fixture, "Adventure", "choice-success.html")),
                File.ReadAllText(Path.Combine(fixture, "Html", "SkeletonOfCrimbosPast", "reward.html")),
                """{"name":"Fixture","adventures":"9","hp":"80","maxhp":"100","mp":"40","maxmp":"50"}"""
            };
            if (fightTimesOut) responses.Insert(1, new TimeoutException("Fixture timeout"));
            using var recording = new Recording(responses);
            using var tokenStore = new ApprovalTokenStore(root.FullName);
            using var audit = new AuditEventJournal(Path.Combine(root.FullName, "audit.jsonl"));
            var checkpoints = new JsonFileAdventureRunCheckpointStore(Path.Combine(root.FullName, "runs"));
            var endpoint = new Uri("https://adventure.test/");
            var session = new EncounterBrowserSessionViewModel((role, approval, verification, _) =>
            {
                Assert.Equal(AccountRole.Mallbot, role);
                var account = new AccountSession(new AccountProfile { Id = "fixture", DisplayName = "Fixture", Role = role }, new AccountCredentials());
                var http = new HttpClient(recording, disposeHandler: false) { BaseAddress = endpoint };
                return Task.FromResult(new ManualAdventureSession(KoLAdventureSessionFactory.Create(
                    account, http, approval, verification, checkpoints, audit,
                    new AdventureHttpTransportOptions { BaseUri = endpoint, AllowedHosts = new[] { endpoint.Host } })));
            }, tokenStore, AccountRole.Mallbot)
            {
                ApprovalToken = "fixture-approval"
            };

            var browser = new EncounterBrowserViewModel(new MafiaDataLoader().Load(
                Path.Combine(AppContext.BaseDirectory, "external", "kolmafia-data")));
            var location = new KoLibri.Desktop.AdventureLocation("fixture-zone", "Fixture zone", "Fixture", 543, 1,
                Array.Empty<AdventureEncounterMetadata>());
            var start = await session.StartAdventureAsync(location, browser);
            Assert.Equal(AdventureEncounterKind.Fight, start?.Encounter?.Kind);
            await session.StartAdventureAsync(location, browser);
            Assert.Single(recording.Requests);

            Assert.Equal(new[] { "attack", "skill", "item" }, browser.Actions.Select(action => action.Kind));
            Assert.Equal("2002", Assert.Single(browser.Actions, action => action.Kind == "item").Id);
            var skill = Assert.Single(browser.Actions, action => action.Kind == "skill");
            ManualStepOutcome? fight;
            if (fightTimesOut)
            {
                fight = await session.SubmitStepAsync(browser.BuildStepSpec(skill), browser);
            }
            else
            {
                var policy = new AutoCombatPolicy
                {
                    DefaultAction = new AutoCombatAction { Kind = AutoCombatActionKind.Skill, Id = "1001" }
                };
                var target = new CombatTarget { Id = "123", Name = "Skeleton", Zone = "", Detail = "" };
                await session.FightCurrentAsync(browser, target with { Id = "999" }, policy, 2, 30);
                Assert.Single(recording.Requests);
                fight = await session.FightCurrentAsync(browser, target, policy, 2, 30);
            }
            if (fightTimesOut)
            {
                Assert.True(fight?.RequiresRefresh);
                fight = await session.RecheckAsync(browser);
                Assert.Equal(HttpMethod.Get, recording.Requests[2].Method);
                Assert.Empty(recording.Requests[2].Body);
            }
            Assert.Equal(AdventureEncounterKind.Choice, fight?.Encounter?.Kind);
            Assert.Equal(new[] { "1234:1", "1234:2" }, browser.Actions.Select(action => action.Id));
            var option = Assert.Single(browser.Actions, action => action.Id == "1234:2");
            var finish = await session.SubmitStepAsync(browser.BuildStepSpec(option), browser);

            Assert.True(finish?.IsComplete, finish?.Message ?? finish?.StopReason);
            Assert.False(session.IsSessionActive);
            Assert.Contains("knucklebone", session.LastEncounterSummary);
            Assert.Contains("9 adventures left", session.LastEncounterSummary);
            var writes = recording.Requests.Where(request => request.Method == HttpMethod.Post).ToArray();
            Assert.Equal(new[] { "/adventure.php", "/fight.php", "/choice.php" },
                writes.Select(request => request.Path));
            Assert.Contains("snarfblat=543", writes[0].Body);
            Assert.Contains("whichskill=1001", writes[1].Body);
            Assert.Contains("option=2", writes[2].Body);
            Assert.Equal("/api.php", recording.Requests[^1].Path);
            Assert.Equal(HttpMethod.Get, recording.Requests[^1].Method);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("<p>Your session has expired. Please log in.</p>", "login", false)]
    [InlineData("<p>Unexpected page</p>", "did not match", true)]
    public async Task Switching_roles_discards_the_open_fight_and_surfaces_the_new_accounts_stop(
        string response, string expectedReason, bool ascended)
    {
        var root = Directory.CreateTempSubdirectory("kolibri-roles-");
        try
        {
            var endpoint = new Uri("https://adventure.test/");
            var roles = new List<AccountRole>();
            var messages = new List<string>();
            using var audit = new AuditEventJournal(Path.Combine(root.FullName, "audit.jsonl"));
            var checkpoints = new JsonFileAdventureRunCheckpointStore(Path.Combine(root.FullName, "runs"));
            var fight = File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
                "Fixtures", "Adventure", "combat-round-1.html"));
            using var recording = new Recording(new[] { fight, response });
            using var pane = new EncounterBrowserPane((role, approval, verification, _) =>
            {
                roles.Add(role);
                var account = new AccountSession(
                    new AccountProfile { Id = role.ToString(), DisplayName = role.ToString(), Role = role },
                    new AccountCredentials());
                var client = new HttpClient(recording, disposeHandler: false) { BaseAddress = endpoint };
                return Task.FromResult(new ManualAdventureSession(KoLAdventureSessionFactory.Create(
                    account, client, approval, verification, checkpoints, audit,
                    new AdventureHttpTransportOptions { BaseUri = endpoint, AllowedHosts = new[] { endpoint.Host } })));
            }, messages.Add);
            pane.Initialise(Path.Combine(AppContext.BaseDirectory, "external", "kolmafia-data"), root.FullName);

            pane.SelectAccount(AccountRole.Main);
            pane.ApplyApprovalToken("main-approval");
            Assert.False(pane.ObserveAscension(AccountRole.Main, "Main", 4));
            var location = Assert.Single(pane.Browser!.Locations, row => row.Snarfblat == 543);
            await pane.StartAdventureAsync(location);
            Assert.True(pane.Session!.IsSessionActive);
            var mainAction = Assert.Single(pane.Browser.Actions, action => action.Kind == "skill");
            Assert.False(pane.ObserveAscension(AccountRole.Mallbot, "Mallbot", 5));
            Assert.True(pane.Session.IsSessionActive);
            if (ascended)
            {
                Assert.True(pane.ObserveAscension(AccountRole.Main, "Main", 5));
                Assert.False(pane.Session!.IsSessionActive);
            }

            pane.SelectAccount(AccountRole.Mallbot);
            Assert.False(pane.Session!.IsSessionActive);
            Assert.Empty(pane.Session.ApprovalToken);
            pane.ApplyApprovalToken("mallbot-approval");
            await pane.SubmitStepAsync(mainAction);
            Assert.Single(recording.Requests);

            await pane.StartAdventureAsync(location);
            Assert.False(pane.Session.IsSessionActive);
            Assert.Contains(expectedReason, messages[^1], StringComparison.OrdinalIgnoreCase);
            Assert.Equal(new[] { AccountRole.Main, AccountRole.Mallbot }, roles);
            Assert.All(recording.Requests, request => Assert.Equal("/adventure.php", request.Path));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Turn_batch_preserves_the_budget_across_manual_choice_and_stops_at_item_goal()
    {
        var root = Directory.CreateTempSubdirectory("kolibri-turn-batch-");
        try
        {
            const string status = """{"name":"Fixture","adventures":"9"}""";
            var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures");
            var choice = File.ReadAllText(Path.Combine(fixture, "Adventure", "choice-success.html"));
            var reward = File.ReadAllText(Path.Combine(fixture, "Html", "SkeletonOfCrimbosPast", "reward.html"));
            using var recording = new Recording(new[]
            {
                status, "{}", choice,
                reward, status,
                status, "{}", reward, status, """{"123":1}""", status
            });
            using var tokens = new ApprovalTokenStore(root.FullName);
            using var audit = new AuditEventJournal(Path.Combine(root.FullName, "audit.jsonl"));
            var store = new InMemoryAdventureRunCheckpointStore();
            var endpoint = new Uri("https://adventure.test/");
            var viewModel = new EncounterBrowserSessionViewModel((role, approval, verification, _) =>
            {
                var account = new AccountSession(
                    new AccountProfile { Id = "fixture", DisplayName = "Fixture", Role = role },
                    new AccountCredentials());
                var http = new HttpClient(recording, disposeHandler: false) { BaseAddress = endpoint };
                return Task.FromResult(new ManualAdventureSession(KoLAdventureSessionFactory.Create(
                    account, http, approval, verification, store, audit,
                    new AdventureHttpTransportOptions { BaseUri = endpoint, AllowedHosts = new[] { endpoint.Host } })));
            }, tokens, AccountRole.Main) { ApprovalToken = "fixture" };
            var browser = new EncounterBrowserViewModel(new MafiaDataLoader().Load(
                Path.Combine(AppContext.BaseDirectory, "external", "kolmafia-data")));
            var location = new KoLibri.Desktop.AdventureLocation("fixture-zone", "Fixture", "Fixture", 543, 1,
                Array.Empty<AdventureEncounterMetadata>());

            await viewModel.RunTurnsAsync(browser, location, 10, "123");
            Assert.True(viewModel.IsSessionActive, viewModel.LastMessage);
            Assert.Contains("9 turns left", viewModel.LastMessage);
            Assert.Equal("/adventure.php", Assert.Single(recording.Requests, r => r.Method == HttpMethod.Post).Path);
            var selected = Assert.Single(browser.Actions, row => row.Id == "1234:2");
            var manual = await viewModel.SubmitStepAsync(browser.BuildStepSpec(selected), browser);
            Assert.True(manual?.IsComplete);
            await viewModel.RunTurnsAsync(browser, null, 0, null);

            Assert.False(viewModel.IsSessionActive);
            Assert.Contains("0 turns left", viewModel.LastMessage);
            Assert.Equal(new[] { "/adventure.php", "/choice.php", "/adventure.php" },
                recording.Requests.Where(r => r.Method == HttpMethod.Post).Select(r => r.Path));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(257, "The Goblin Who Wouldn't Be King", "Find your way inside Cobb's Knob.",
        "Find some way to infiltrate the Throne Room.", 0, 0)]
    [InlineData(539, "Trial By Friar", null, "Recover the ritual items.", 0, 0)]
    [InlineData(438, null, null, null, 0, 23)]
    public async Task Desktop_observes_quest_and_path_unlocks_before_starting(
        int area, string? title, string? before, string? after, int firstPath, int secondPath)
    {
        var root = Directory.CreateTempSubdirectory("kolibri-access-");
        try
        {
            var replies = new List<object>();
            AddState(firstPath, before);
            AddState(secondPath, after);
            replies.Add(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
                "Fixtures", "Adventure", "combat-round-1.html")));
            using var recording = new Recording(replies);
            using var tokens = new ApprovalTokenStore(root.FullName);
            using var audit = new AuditEventJournal(Path.Combine(root.FullName, "audit.jsonl"));
            var store = new InMemoryAdventureRunCheckpointStore();
            var endpoint = new Uri("https://adventure.test/");
            var viewModel = new EncounterBrowserSessionViewModel((role, approval, verification, _) =>
            {
                var account = new AccountSession(
                    new AccountProfile { Id = "fixture", DisplayName = "Fixture", Role = role },
                    new AccountCredentials());
                var http = new HttpClient(recording, disposeHandler: false) { BaseAddress = endpoint };
                return Task.FromResult(new ManualAdventureSession(KoLAdventureSessionFactory.Create(
                    account, http, approval, verification, store, audit,
                    new AdventureHttpTransportOptions { BaseUri = endpoint, AllowedHosts = new[] { endpoint.Host } })));
            }, tokens, AccountRole.Main) { ApprovalToken = "fixture" };
            var browser = new EncounterBrowserViewModel(new MafiaDataLoader().Load(
                Path.Combine(AppContext.BaseDirectory, "external", "kolmafia-data")));
            var location = new KoLibri.Desktop.AdventureLocation("access-zone", "Fixture", "Fixture", area, 1,
                Array.Empty<AdventureEncounterMetadata>());

            Assert.Null(await viewModel.StartAdventureAsync(location, browser));
            Assert.False(viewModel.IsSessionActive);
            Assert.All(recording.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
            var started = await viewModel.StartAdventureAsync(location, browser);
            Assert.Equal(AdventureEncounterKind.Fight, started?.Encounter?.Kind);
            var start = Assert.Single(recording.Requests, request => request.Method == HttpMethod.Post);
            Assert.Contains($"snarfblat={area}", start.Body);

            void AddState(int path, string? description)
            {
                replies.Add($$"""{"path":{{path}},"ascensions":2,"adventures":20}""");
                if (title is null) return;
                var entry = description is null ? "" : $"<b>{title}</b><br>{description}";
                replies.Add($"<b>Your Quest Log</b><a href='questlog.php?which=2'>Completed</a><blockquote>{entry}</blockquote>");
                replies.Add("<b>Your Quest Log</b><a href='questlog.php?which=1'>Current</a><blockquote></blockquote>");
            }
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [X11ReplayFact]
    public async Task Avalonia_buttons_read_mail_and_play_a_recorded_adventure()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var uiThread = new Thread(() =>
        {
            try
            {
                AppBuilder.Configure<App>().UsePlatformDetect().SetupWithoutStarting();
                using var stop = new CancellationTokenSource();
                Dispatcher.UIThread.Post(async () =>
                {
                    try
                    {
                        await PlayThroughControlsAsync();
                        completion.SetResult();
                    }
                    catch (Exception exception)
                    {
                        completion.SetException(exception);
                    }
                    finally
                    {
                        stop.Cancel();
                    }
                });
                Dispatcher.UIThread.MainLoop(stop.Token);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }) { IsBackground = true };
        uiThread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromMinutes(3));
    }

    private static async Task PlayThroughControlsAsync()
    {
        var root = Directory.CreateTempSubdirectory("kolibri-avalonia-replay-");
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures");
            using var recording = new Recording(new[]
            {
                File.ReadAllText(Path.Combine(fixture, "Adventure", "combat-round-1.html")),
                File.ReadAllText(Path.Combine(fixture, "Adventure", "choice-success.html")),
                File.ReadAllText(Path.Combine(fixture, "Html", "SkeletonOfCrimbosPast", "reward.html")),
                """{"adventures":"9","hp":"80","maxhp":"100","mp":"40","maxmp":"50"}"""
            });
            using var audit = new AuditEventJournal(Path.Combine(root.FullName, "audit.jsonl"));
            var checkpoints = new JsonFileAdventureRunCheckpointStore(Path.Combine(root.FullName, "runs"));
            var endpoint = new Uri("https://adventure.test/");
            var accounts = new AccountSessionManager(new InMemoryAccountStore(), new InMemorySecretStore());
            foreach (var role in new[] { AccountRole.Main, AccountRole.Mallbot })
            {
                await accounts.SetProfileAsync(new AccountProfile { Id = role.ToString(), DisplayName = role.ToString(), Role = role });
                await accounts.SetCredentialsAsync(role, new AccountCredentials { CookieHeader = "fixture=1", AccessToken = $"fixture-{role}" });
            }
            var inboxRequests = new List<(AccountRole Role, string Query)>();
            var sendRequests = new List<(AccountRole Role, string Form)>();
            var window = new MainWindow(status => new EncounterBrowserPane((role, approval, verification, _) =>
            {
                var account = new AccountSession(
                    new AccountProfile { Id = "fixture", DisplayName = "Fixture", Role = role },
                    new AccountCredentials());
                var client = new HttpClient(recording, disposeHandler: false) { BaseAddress = endpoint };
                return Task.FromResult(new ManualAdventureSession(KoLAdventureSessionFactory.Create(
                    account, client, approval, verification, checkpoints, audit,
                    new AdventureHttpTransportOptions { BaseUri = endpoint, AllowedHosts = new[] { endpoint.Host } })));
            }, status), root.FullName, accounts,
                session => new HttpClient(new MailReplayHandler(session.Profile.Role, inboxRequests, sendRequests)));
            window.Show();
            try
            {
                await CheckMailAsync();
                Click("NavAdventure");
                await UntilAsync(() => window.FindControl<Button>("EncounterStartButton")!.IsEffectivelyVisible);
                var locations = window.FindControl<ListBox>("EncounterLocationsGrid")!;
                var actions = window.FindControl<ListBox>("EncounterActionsGrid")!;
                var log = window.FindControl<TextBlock>("EncounterStepLogText")!;
                locations.SelectedItem = locations.Items.OfType<EncounterBrowserLocationRow>().Single(row => row.Snarfblat == 543);
                Click("EncounterStartButton");
                await UntilAsync(() => log.Text?.Contains("Round 1", StringComparison.Ordinal) == true);
                actions.SelectedItem = actions.Items.OfType<EncounterBrowserActionRow>().Single(row => row.Kind == "skill");
                Click("EncounterSubmitButton");
                await UntilAsync(() => log.Text?.Contains("Choice", StringComparison.Ordinal) == true);
                actions.SelectedItem = actions.Items.OfType<EncounterBrowserActionRow>().Single(row => row.Id == "1234:2");
                Click("EncounterSubmitButton");
                await UntilAsync(() => log.Text?.Contains("9 adventures left", StringComparison.Ordinal) == true);
                Assert.Contains("knucklebone", log.Text);
                Assert.Equal(new[] { "/adventure.php", "/fight.php", "/choice.php", "/api.php" },
                    recording.Requests.Select(request => request.Path));
            }
            finally
            {
                window.Close();
            }

            async Task CheckMailAsync()
            {
                var status = window.FindControl<TextBlock>("KoLmailInboxStatus")!;
                var rows = window.FindControl<ListBox>("KoLmailInboxRows")!;
                var previous = window.FindControl<Button>("KoLmailPrevious")!;
                var next = window.FindControl<Button>("KoLmailNext")!;
                var selector = window.FindControl<ComboBox>("AccountRoleSelector")!;
                var refresh = window.FindControl<Button>("GlobalRefreshButton")!;

                await UntilAsync(() => refresh.IsEnabled);
                window.FindControl<Expander>("KoLChatPanel")!.IsExpanded = true;
                var chatRows = window.FindControl<ItemsControl>("KoLChatRows")!;
                var chatChannel = window.FindControl<ComboBox>("KoLChatChannel")!;
                var contacts = window.FindControl<ComboBox>("KoLChatContacts")!;
                var chatStatus = window.FindControl<TextBlock>("KoLChatStatus")!;
                Click("ReadKoLChatContacts");
                await UntilAsync(() => chatStatus.Text == "No KoL contacts." && refresh.IsEnabled);
                Assert.Empty(contacts.Items);
                Click("ReadKoLChat");
                await UntilAsync(() => chatRows.Items.Count == 2 && refresh.IsEnabled);
                Assert.Contains(chatRows.Items.OfType<KoLChatMessage>(), message => message.Text == "Hello & welcome");
                Click("ReadKoLChat");
                await UntilAsync(() => chatRows.Items.Count == 3 && refresh.IsEnabled);
                chatChannel.SelectedItem = "/clan";
                Assert.Equal("Second clan message", Assert.Single(chatRows.Items.OfType<KoLChatMessage>()).Text);

                var clanStatus = window.FindControl<TextBlock>("ClanProfileStatus")!;
                Click("ReadClanProfile");
                await UntilAsync(() => clanStatus.Text == "No clan membership." && refresh.IsEnabled);
                window.FindControl<Expander>("ClanLounge")!.IsExpanded = true;
                var loungeStatus = window.FindControl<TextBlock>("ClanLoungeStatus")!;
                var loungeRows = window.FindControl<ItemsControl>("ClanLoungeFacilities")!;
                Click("ReadClanLounge");
                await UntilAsync(() => loungeStatus.Text == "Clan VIP lounge unavailable." && refresh.IsEnabled);
                Assert.Empty(loungeRows.Items);
                Assert.False(window.FindControl<Button>("UseClanLookingGlass")!.IsVisible);
                Click("UseClanLookingGlass");
                Click("KoLmailRead");
                await UntilAsync(() => status.Text == "Page 1 · 1 messages." && refresh.IsEnabled);
                Assert.False(previous.IsEnabled);
                Assert.True(next.IsEnabled);

                Click("KoLmailNext");
                await UntilAsync(() => status.Text == "Page 2 · 1 messages." && refresh.IsEnabled);
                Assert.True(previous.IsEnabled);
                Assert.False(next.IsEnabled);
                Assert.Equal("Mallbot page 2", Assert.Single(rows.Items.OfType<KoLMailEntry>()).Text);

                Click("KoLmailPrevious");
                await UntilAsync(() => status.Text == "Page 1 · 1 messages." && refresh.IsEnabled);
                selector.SelectedIndex = 0;
                await UntilAsync(() => string.IsNullOrEmpty(status.Text) && refresh.IsEnabled);
                Assert.Empty(rows.Items);
                Assert.False(previous.IsEnabled);
                Assert.False(next.IsEnabled);
                Assert.Empty(chatRows.Items);
                Click("ReadKoLChat");
                await UntilAsync(() => chatRows.Items.Count == 1 && refresh.IsEnabled);
                Assert.Equal("Main account chat", Assert.Single(chatRows.Items.OfType<KoLChatMessage>()).Text);
                var draft = window.FindControl<TextBox>("KoLChatDraft")!;
                var chatRecipient = window.FindControl<TextBox>("KoLChatRecipient")!;
                chatChannel.SelectedItem = "/clan";
                draft.Text = "Grüße & hallo";
                Click("SendKoLChat");
                Click("SendKoLChat");
                await UntilAsync(() => chatStatus.Text == "Chat message sent." && refresh.IsEnabled);
                Assert.Empty(draft.Text!);
                Click("ReadKoLChatContacts");
                await UntilAsync(() => contacts.Items.Count == 1 && refresh.IsEnabled);
                Assert.Equal(new KoLChatContact("3495347", "TortureBot"), contacts.Items[0]);
                contacts.SelectedIndex = 0;
                Assert.Equal("3495347", chatRecipient.Text);
                draft.Text = "Private hello";
                Click("SendKoLChat");
                await UntilAsync(() => chatStatus.Text == "Player unavailable." && refresh.IsEnabled);
                Assert.Equal("Private hello", draft.Text);
                Assert.Empty(clanStatus.Text ?? "");
                Click("ReadClanProfile");
                await UntilAsync(() => clanStatus.Text == "Fixture & Clan · Test title" && refresh.IsEnabled);
                var clanMembers = window.FindControl<ItemsControl>("ClanMembers")!;
                Assert.Collection(clanMembers.Items.OfType<KoLClanMember>(),
                    first => Assert.Equal(new KoLClanMember("1001", "Main", "Test title", 17) { Rank = "Officer" }, first),
                    second => Assert.Equal(new KoLClanMember("1003", "Another & Player", "", 9), second));

                Assert.Empty(loungeStatus.Text ?? "");
                Click("ReadClanLounge");
                await UntilAsync(() => loungeStatus.Text == "9 lounge facilities." && refresh.IsEnabled);
                Assert.Collection(loungeRows.Items.OfType<KoLClanLoungeFacility>(),
                    ground => Assert.Equal(new KoLClanLoungeFacility("hotdogstand", "A Hot Dog Stand", 1), ground),
                    klaw => Assert.Equal(new KoLClanLoungeFacility("klaw", "Deluxe Mr. Klaw", 1), klaw),
                    tub => Assert.Equal(new KoLClanLoungeFacility("hottub", "Hot Tub", 1), tub),
                    attic => Assert.Equal(new KoLClanLoungeFacility("pooltable", "A Pool Table", 2), attic),
                    glass => Assert.Equal(new KoLClanLoungeFacility("lookingglass", "A Looking Glass", 2), glass),
                    tree => Assert.Equal(new KoLClanLoungeFacility("crimbotree", "A Crimbo Tree", 2), tree),
                    shower => Assert.Equal(new KoLClanLoungeFacility("shower", "April Shower", 2), shower),
                    swim => Assert.Equal(new KoLClanLoungeFacility("swimmingpool", "Swimming Pool", 2), swim),
                    fax => Assert.Equal(new KoLClanLoungeFacility("faxmachine", "Fax Machine", 2), fax));

                window.FindControl<Expander>("ClanStash")!.IsExpanded = true;
                var stashRows = window.FindControl<ListBox>("ClanStashItems")!;
                var stashStatus = window.FindControl<TextBlock>("ClanStashStatus")!;
                var stashQuantity = window.FindControl<TextBox>("ClanStashQuantity")!;
                Click("ReadClanStash");
                await UntilAsync(() => stashRows.Items.Count == 1 && refresh.IsEnabled);
                Assert.Equal(1234, Assert.IsType<KoLClanStashItem>(stashRows.Items[0]).Quantity);
                stashRows.SelectedIndex = 0;
                stashQuantity.Text = "1235";
                Click("TakeClanStash");
                Assert.Equal("Enter a quantity between 1 and 1234.", stashStatus.Text);
                stashQuantity.Text = "2";
                Click("TakeClanStash");
                Click("TakeClanStash");
                await UntilAsync(() => stashStatus.Text == "KoL reported acquiring Seal & tooth." && refresh.IsEnabled);
                Assert.Equal(1232, Assert.IsType<KoLClanStashItem>(stashRows.Items[0]).Quantity);

                var depositItems = window.FindControl<ComboBox>("ClanStashDepositItems")!;
                var depositQuantity = window.FindControl<TextBox>("ClanStashDepositQuantity")!;
                Click("LoadClanStashDepositItems");
                await UntilAsync(() => depositItems.Items.Count == 1 && refresh.IsEnabled);
                Assert.Equal(5, Assert.IsType<DesktopInventoryItem>(depositItems.Items[0]).Quantity);
                depositItems.SelectedIndex = 0;
                depositQuantity.Text = "3";
                Click("DepositClanStash");
                Click("DepositClanStash");
                await UntilAsync(() => stashStatus.Text?.StartsWith("KoL reported depositing", StringComparison.Ordinal) == true
                    && refresh.IsEnabled);
                Assert.Equal(1235, Assert.IsType<KoLClanStashItem>(stashRows.Items[0]).Quantity);
                Assert.Equal(2, Assert.IsType<DesktopInventoryItem>(depositItems.Items[0]).Quantity);

                Click("ReceiveClanFax");
                Click("ReceiveClanFax");
                await UntilAsync(() => loungeStatus.Text == "Photocopy received: handsome mariachi." && refresh.IsEnabled);
                Click("InspectClanFax");
                await UntilAsync(() => loungeStatus.Text == "Photocopied monster: handsome mariachi." && refresh.IsEnabled);
                Click("SendClanFax");
                await UntilAsync(() => loungeStatus.Text == "Photocopy sent." && refresh.IsEnabled);
                Click("SendClanFax");
                await UntilAsync(() => loungeStatus.Text == "KoL did not confirm the fax transfer." && refresh.IsEnabled);

                Click("ClanCannonball");
                Click("ClanCannonball");
                await UntilAsync(() => loungeStatus.Text == "In the clan pool." && refresh.IsEnabled);
                Click("ClanPoolHandstand");
                await UntilAsync(() => refresh.IsEnabled);
                Click("ClanPoolDive");
                await UntilAsync(() => loungeStatus.Text == "Found rubber duck in the pool." && refresh.IsEnabled);
                Click("LeaveClanPool");
                await UntilAsync(() => loungeStatus.Text == "No active clan pool choice." && refresh.IsEnabled);

                Click("SwimClanLaps");
                Click("SwimClanLaps");
                await UntilAsync(() => loungeStatus.Text == "Swam 32 laps." && refresh.IsEnabled);
                Click("SwimClanSprints");
                await UntilAsync(() => loungeStatus.Text == "Today's swimming workout is already used." && refresh.IsEnabled);

                Click("TakeClanShower");
                Assert.Equal("Choose a shower temperature.", loungeStatus.Text);
                var showerTemperature = window.FindControl<ComboBox>("ClanShowerTemperature")!;
                Assert.Equal(5, showerTemperature.Items.Count);
                showerTemperature.SelectedIndex = 3;
                Click("TakeClanShower");
                Click("TakeClanShower");
                await UntilAsync(() => loungeStatus.Text == "April Shower taken." && refresh.IsEnabled);
                showerTemperature.SelectedIndex = 0;
                Click("TakeClanShower");
                await UntilAsync(() => loungeStatus.Text == "Today's April Shower is already used." && refresh.IsEnabled);

                Click("PlayClanPool");
                Assert.Equal("Choose a pool stance.", loungeStatus.Text);
                var poolStance = window.FindControl<ComboBox>("ClanPoolStance")!;
                var poolResults = new[]
                {
                    "Won the pool game.", "Lost the pool game.",
                    "Played a game of pool against yourself.", "The pool daily limit is reached."
                };
                for (var attempt = 0; attempt < poolResults.Length; attempt++)
                {
                    poolStance.SelectedIndex = attempt % 3;
                    Click("PlayClanPool");
                    Click("PlayClanPool");
                    await UntilAsync(() => loungeStatus.Text == poolResults[attempt] && refresh.IsEnabled);
                }

                Click("UseClanHotTub");
                await UntilAsync(() => loungeStatus.Text?.StartsWith("The Hot Tub would remove", StringComparison.Ordinal) == true && refresh.IsEnabled);
                window.FindControl<CheckBox>("AllowHotTubCurseRemoval")!.IsChecked = true;
                Click("UseClanHotTub");
                await UntilAsync(() => loungeStatus.Text == "Hot Tub soak used. 0 left today." && refresh.IsEnabled);
                Click("UseClanHotTub");
                await UntilAsync(() => loungeStatus.Text == "No Hot Tub soaks left today." && refresh.IsEnabled);

                Click("PlayClanKlaw");
                Click("PlayClanKlaw");
                await UntilAsync(() => loungeStatus.Text == "Won a Deluxe Mr. Klaw prize." && refresh.IsEnabled);
                Click("PlayClanKlaw");
                await UntilAsync(() => loungeStatus.Text == "Played Deluxe Mr. Klaw. No prize received." && refresh.IsEnabled);
                Click("PlayClanKlaw");
                await UntilAsync(() => loungeStatus.Text == "The Deluxe Mr. Klaw daily limit is reached." && refresh.IsEnabled);

                Click("UseClanCrimboTree");
                Click("UseClanCrimboTree");
                await UntilAsync(() => loungeStatus.Text == "Received a Crimbo gift." && refresh.IsEnabled);
                Click("UseClanCrimboTree");
                await UntilAsync(() => loungeStatus.Text == "Check the Crimbo Tree again in 7 days." && refresh.IsEnabled);

                Click("UseClanLookingGlass");
                Click("UseClanLookingGlass");
                await UntilAsync(() => loungeStatus.Text == "Received a DRINK ME potion." && refresh.IsEnabled);
                Click("UseClanLookingGlass");
                await UntilAsync(() => loungeStatus.Text?.StartsWith("No DRINK ME potion received", StringComparison.Ordinal) == true
                    && refresh.IsEnabled);


                Click("KoLmailRead");
                await UntilAsync(() => status.Text == "Page 1 · 1 messages." && refresh.IsEnabled);
                Assert.Equal("Main page 1", Assert.Single(rows.Items.OfType<KoLMailEntry>()).Text);
                Assert.Equal(new[]
                {
                    (AccountRole.Mallbot, "?box=Inbox&begin=1"),
                    (AccountRole.Mallbot, "?box=Inbox&begin=2"),
                    (AccountRole.Mallbot, "?box=Inbox&begin=1"),
                    (AccountRole.Main, "?box=Inbox&begin=1")
                }, inboxRequests);

                rows.SelectedIndex = 0;
                Click("KoLmailSave");
                await UntilAsync(() => status.Text == "Saved · Page 1 · 1 messages." && refresh.IsEnabled);
                Assert.Equal("101", Assert.Single(rows.Items.OfType<KoLMailEntry>()).Id);
                Assert.Equal("Main saved message", Assert.Single(rows.Items.OfType<KoLMailEntry>()).Text);

                rows.SelectedIndex = 0;
                var confirmDelete = window.FindControl<Button>("KoLmailConfirmDelete")!;
                Assert.False(confirmDelete.IsVisible);
                Click("KoLmailDelete");
                Assert.True(confirmDelete.IsVisible);
                Assert.Single(rows.Items);
                rows.SelectedIndex = -1;
                Assert.False(confirmDelete.IsVisible);
                rows.SelectedIndex = 0;
                Click("KoLmailDelete");
                Click("KoLmailConfirmDelete");
                await UntilAsync(() => status.Text == "Saved is empty." && refresh.IsEnabled);
                Assert.Empty(rows.Items);

                window.FindControl<Expander>("KoLmailComposer")!.IsExpanded = true;
                var recipient = window.FindControl<TextBox>("KoLmailRecipient")!;
                var message = window.FindControl<TextBox>("KoLmailMessage")!;
                var sendStatus = window.FindControl<TextBlock>("KoLmailSendStatus")!;
                var attachments = window.FindControl<TextBox>("KoLmailAttachments")!;
                var meat = window.FindControl<TextBox>("KoLmailMeat")!;
                recipient.Text = "Player & Co";
                message.Text = "First line\nSecond line";
                attachments.Text = "123 2\n123 3";
                Click("KoLmailSend");
                Assert.Contains("distinct item IDs", sendStatus.Text);
                Assert.Empty(sendRequests);

                attachments.Text = "123 2\n456 3";
                meat.Text = "42";
                Click("KoLmailSend");
                Click("KoLmailSend");
                await UntilAsync(() => sendStatus.Text == "Message sent as Main." && refresh.IsEnabled);
                var sent = Assert.Single(sendRequests);
                Assert.Equal(AccountRole.Main, sent.Role);
                var form = System.Web.HttpUtility.ParseQueryString(sent.Form);
                Assert.Equal("send", form["action"]);
                Assert.Equal("Player & Co", form["towho"]);
                Assert.Equal("First line\nSecond line", form["message"]);
                Assert.Equal("on", form["savecopy"]);
                Assert.Equal("fixture-Main", form["pwd"]);
                Assert.Equal("123", form["whichitem1"]);
                Assert.Equal("2", form["howmany1"]);
                Assert.Equal("456", form["whichitem2"]);
                Assert.Equal("3", form["howmany2"]);
                Assert.Equal("42", form["sendmeat"]);
                Assert.Empty(attachments.Text!);
                Assert.Empty(meat.Text!);
                Assert.Empty(recipient.Text!);
                Assert.Empty(message.Text!);

                Click("KoLmailOutbox");
                await UntilAsync(() => status.Text == "Outbox · Page 1 · 1 messages." && refresh.IsEnabled);
                var copy = Assert.Single(rows.Items.OfType<KoLMailEntry>());
                Assert.Equal("Player & Co", copy.Recipient);
                Assert.Equal("To Player & Co", copy.Correspondent);
                Assert.Equal("First line\nSecond line", copy.Text);

                recipient.Text = "Unknown player";
                message.Text = "Keep this draft";
                Click("KoLmailSend");
                await UntilAsync(() => sendStatus.Text?.StartsWith("Delivery was unconfirmed", StringComparison.Ordinal) == true);
                Assert.Equal(2, sendRequests.Count);
                Assert.Equal("Keep this draft", message.Text);
                selector.SelectedIndex = 1;
                await UntilAsync(() => refresh.IsEnabled);
                Assert.Empty(contacts.Items);
                Assert.Empty(chatRecipient.Text!);
                Assert.Empty(clanMembers.Items);
                Assert.Empty(stashRows.Items);
                Assert.Empty(loungeRows.Items);
                Assert.Empty(depositItems.Items);
            }

            void Click(string name) => window.FindControl<Button>(name)!
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!condition())
            await Task.Delay(20, timeout.Token);
    }

    private sealed class X11ReplayFactAttribute : FactAttribute
    {
        public X11ReplayFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("KOLIBRI_RUN_X11_REPLAY") != "1")
                Skip = "Run under Xvfb with KOLIBRI_RUN_X11_REPLAY=1.";
        }
    }

    private sealed class MailReplayHandler(
        AccountRole role, List<(AccountRole Role, string Query)> requests, List<(AccountRole Role, string Form)> sends)
        : HttpMessageHandler
    {
        private int _chatSendCount;
        private bool _saved;
        private int _stashCount = 1234;
        private int _inventoryCount = 3;
        private int _lookingGlassAttempts;
        private int _crimboTreeAttempts;
        private int _klawAttempts;
        private int _soaks = 1;
        private int _poolAttempts;
        private int _showerAttempts;
        private int _swimAttempts;
        private int _faxAttempts;
        private bool _inPool;
        private const string PoolChoice = """<input name="whichchoice" value="585"><input name="option" value="1">""";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath == "/account_contactlist.php")
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Empty(uri.Query);
                var fixture = role == AccountRole.Main ? "contacts.html" : "contacts-empty.html";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Html", "Chat", fixture)))
                };
            }
            if (uri.AbsolutePath == "/submitnewchat.php")
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal(AccountRole.Main, role);
                var query = System.Web.HttpUtility.ParseQueryString(uri.Query, System.Text.Encoding.Latin1);
                Assert.Equal("1", query["j"]);
                Assert.Equal("fixture-Main", query["pwd"]);
                Assert.Equal("1001", query["playerid"]);
                Assert.InRange(++_chatSendCount, 1, 2);
                Assert.Equal(_chatSendCount == 1 ? "/clan Grüße & hallo" : "/msg 3495347 Private hello", query["graf"]);
                var result = _chatSendCount == 1
                    ? """{"msgs":[{"mid":6,"type":"public","channel":"clan","who":{"id":"1001","name":"Main"},"msg":"Grüße &amp; hallo"}]}"""
                    : """{"msgs":[],"output":"<b>Player unavailable.</b>"}""";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(result) };
            }
            if (uri.AbsolutePath == "/newchatmessages.php")
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                var feed = (role, uri.Query) switch
                {
                    (AccountRole.Mallbot, "?j=1&lasttime=0") => """
                        {"last":"20","msgs":[
                          {"mid":"19","type":"public","channel":"newbie","who":{"name":"Player"},"msg":"<b>Hello</b> &amp; welcome"},
                          {"mid":20,"type":"private","who":{"name":"Friend"},"msg":"Private message"}
                        ]}
                        """,
                    (AccountRole.Mallbot, "?j=1&lasttime=20") => """
                        {"last":21,"msgs":[
                          {"mid":20,"type":"private","who":{"name":"Friend"},"msg":"Private message"},
                          {"mid":21,"type":"public","channel":"clan","who":{"name":"Member"},"msg":"Second clan message"}
                        ]}
                        """,
                    (AccountRole.Main, "?j=1&lasttime=0") => """
                        {"last":5,"msgs":[{"mid":5,"type":"public","channel":"clan","who":{"name":"Member"},"msg":"Main account chat"}]}
                        """,
                    _ => throw new InvalidOperationException($"Unexpected chat request: {role} {uri.Query}")
                };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(feed) };
            }
            if (uri.AbsolutePath == "/desc_item.php")
            {
                Assert.Equal(AccountRole.Main, role);
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("?whichitem=835898159", uri.Query);
                Assert.Equal(1, _faxAttempts);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
                        "Fixtures", "Html", "ClanLounge", "photocopy-mariachi.html")))
                };
            }

            if (uri.AbsolutePath == "/choice.php")
            {
                Assert.Equal(AccountRole.Main, role);
                var choiceHtml = _inPool ? PoolChoice : "<p>No choice.</p>";
                if (request.Method == HttpMethod.Post)
                {
                    Assert.True(_inPool);
                    var form = System.Web.HttpUtility.ParseQueryString(
                        await request.Content!.ReadAsStringAsync(cancellationToken));
                    Assert.Equal("585", form["whichchoice"]);
                    Assert.Equal("1", form["option"]);
                    Assert.Equal("fixture-Main", form["pwd"]);
                    switch (form["action"])
                    {
                        case "flip": break;
                        case "treasure":
                            choiceHtml += """<script>var state = "You found a rubber duck!";</script>""";
                            break;
                        case "leave":
                            _inPool = false;
                            choiceHtml = "<p>Left pool.</p>";
                            break;
                        default: throw new InvalidOperationException("Unexpected pool choice action.");
                    }
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(choiceHtml) };
            }
            if (uri.AbsolutePath == "/clan_viplounge.php")
            {
                if (request.Method == HttpMethod.Post)
                {
                    Assert.Equal(AccountRole.Main, role);
                    var form = System.Web.HttpUtility.ParseQueryString(
                        await request.Content!.ReadAsStringAsync(cancellationToken));
                    if (form["preaction"] is "receivefax" or "sendfax")
                    {
                        Assert.Null(form["action"]);
                        Assert.Equal("fixture-Main", form["pwd"]);
                        Assert.Equal("2", form["whichfloor"]);
                        Assert.Equal(++_faxAttempts == 1 ? "receivefax" : "sendfax", form["preaction"]);
                        var fax = _faxAttempts switch
                        {
                            1 => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Html", "ClanLounge", "receive-fax.html")),
                            2 => "Your photocopy slowly slides into the machine.",
                            3 => "There is no photocopy in your inventory.",
                            _ => throw new InvalidOperationException("Unexpected repeated fax request.")
                        };
                        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(fax) };
                    }
                    if (form["preaction"] == "goswimming")
                    {
                        Assert.Null(form["action"]);
                        Assert.Equal("fixture-Main", form["pwd"]);
                        Assert.Equal("2", form["whichfloor"]);
                        if (form["subaction"] == "screwaround")
                        {
                            Assert.False(_inPool);
                            _inPool = true;
                            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                            redirect.Headers.Location = new Uri("/choice.php", UriKind.Relative);
                            return redirect;
                        }
                        Assert.InRange(++_swimAttempts, 1, 2);
                        Assert.Equal(_swimAttempts == 1 ? "laps" : "submarine", form["subaction"]);
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(_swimAttempts == 1
                                ? "You manage to swim 32 before you get tired." : "You've already worked out in the pool today.")
                        };
                    }
                    if (form["preaction"] == "takeshower")
                    {
                        Assert.Null(form["action"]);
                        Assert.Equal("fixture-Main", form["pwd"]);
                        Assert.Equal("2", form["whichfloor"]);
                        Assert.InRange(++_showerAttempts, 1, 2);
                        Assert.Equal(_showerAttempts == 1 ? "4" : "1", form["temperature"]);
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(_showerAttempts == 1
                                ? "The warm water relaxes your muscles." : "You've already had a shower today.")
                        };
                    }
                    if (form["preaction"] == "poolgame")
                    {
                        Assert.Null(form["action"]);
                        Assert.Equal("fixture-Main", form["pwd"]);
                        Assert.Equal("2", form["whichfloor"]);
                        Assert.Equal((_poolAttempts % 3 + 1).ToString(), form["stance"]);
                        var poolResult = ++_poolAttempts switch
                        {
                            1 => "You skillfully defeat the player and take control of the table.",
                            2 => "Try as you might, you are unable to defeat the player.",
                            3 => "You play a game of pool against yourself.",
                            4 => "You're kind of pooled out for today.",
                            _ => throw new InvalidOperationException("Unexpected repeat pool request.")
                        };
                        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(poolResult) };
                    }
                    if (form["action"] == "hottub")
                    {
                        Assert.Equal("fixture-Main", form["pwd"]);
                        Assert.Null(form["whichfloor"]);
                        Assert.Equal(1, _soaks--);
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent("<img src='hottub0.gif'>")
                        };
                    }
                    if (form["action"] == "klaw")
                    {
                        Assert.Equal("fixture-Main", form["pwd"]);
                        Assert.Null(form["whichfloor"]);
                        var result = ++_klawAttempts switch
                        {
                            1 => "The claw slowly descends. You acquire an item.",
                            2 => "The claw slowly descends. It comes back empty.",
                            3 => "You should stop; you wouldn&#39;t want to look greedy.",
                            _ => throw new InvalidOperationException("Unexpected repeat Klaw request.")
                        };
                        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(result) };
                    }

                    if (form["action"] == "crimbotree")
                    {
                        Assert.Equal("fixture-Main", form["pwd"]);
                        Assert.Null(form["whichfloor"]);
                        Assert.InRange(++_crimboTreeAttempts, 1, 2);
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(_crimboTreeAttempts == 1
                                ? "You look under the Crimbo Tree and find a present with your name on it!"
                                : "There's nothing under the Crimbo Tree. Check back in 7 days.")
                        };
                    }
                    Assert.Equal("lookingglass", form["action"]);
                    Assert.Equal("2", form["whichfloor"]);
                    Assert.Equal("fixture-Main", form["pwd"]);
                    Assert.InRange(++_lookingGlassAttempts, 1, 2);
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(_lookingGlassAttempts == 1
                            ? "You acquire an item: DRINK ME potion." : "You have already used the looking glass today.")
                    };
                }

                Assert.Equal(HttpMethod.Get, request.Method);
                if (role == AccountRole.Mallbot)
                {
                    Assert.Empty(uri.Query);
                    var response = new HttpResponseMessage(HttpStatusCode.Redirect);
                    response.Headers.Location = new Uri("/clan_signup.php", UriKind.Relative);
                    return response;
                }

                var lounge = uri.Query switch
                {
                    "" => $"""
                        <b style="color: white">Clan VIP Lounge (Ground Floor)</b>
                        <a href=clan_viplounge.php?action=hotdogstand><img alt="A Hot Dog Stand"></a>
                        <a href=clan_viplounge.php?action=klaw><img alt="Deluxe Mr. Klaw"></a>
                        <a href=clan_viplounge.php?action=hottub><img src="hottub{_soaks}.gif" alt="Hot Tub"></a>
                        """,
                    "?whichfloor=2" => """
                        <b style="color: white">Clan VIP Lounge (Attic)</b>
                        <a href="clan_viplounge.php?action=pooltable&amp;whichfloor=2"><img alt="A Pool Table"></a>
                        <a href="clan_viplounge.php?action=lookingglass&amp;whichfloor=2"><img alt="A Looking Glass"></a>
                        <a href="clan_viplounge.php?action=crimbotree&amp;whichfloor=2"><img alt="A Crimbo Tree"></a>
                        <a href="clan_viplounge.php?action=shower&amp;whichfloor=2"><img alt="April Shower"></a>
                        <a href="clan_viplounge.php?action=swimmingpool&amp;whichfloor=2"><img alt="Swimming Pool"></a>
                        <a href="clan_viplounge.php?action=faxmachine&amp;whichfloor=2"><img alt="Fax Machine"></a>
                        """,
                    _ => throw new InvalidOperationException($"Unexpected clan lounge request: {uri.Query}")
                };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(lounge) };
            }

            if (uri.AbsolutePath == "/clan_stash.php")
            {
                Assert.Equal(AccountRole.Main, role);
                if (request.Method == HttpMethod.Post)
                {
                    var form = System.Web.HttpUtility.ParseQueryString(
                        await request.Content!.ReadAsStringAsync(cancellationToken));
                    if (form["action"] == "addgoodies")
                    {
                        Assert.Equal("2", form["item1"]);
                        Assert.Equal("3", form["qty1"]);
                        Assert.Equal("1", form["ajax"]);
                        Assert.Equal("fixture-Main", form["pwd"]);
                        Assert.Equal(1232, _stashCount);
                        _stashCount += 3;
                        _inventoryCount -= 3;
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent("You add 3 seal teeth to the Goodies Hoard.")
                        };
                    }
                    Assert.Equal("takegoodies", form["action"]);
                    Assert.Equal("2", form["whichitem"]);
                    Assert.Equal("2", form["quantity"]);
                    Assert.Equal("1", form["ajax"]);
                    Assert.Equal("fixture-Main", form["pwd"]);
                    Assert.Equal(1234, _stashCount);
                    _stashCount -= 2;
                    _inventoryCount += 2;
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("You acquire two items.") };
                }

                Assert.Equal(HttpMethod.Get, request.Method);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(FormattableString.Invariant($"""
                        <form name=takegoodies><select name=whichitem>
                        <option value=2 descid=123>Seal &amp; tooth ({_stashCount:N0}) (-3)</option>
                        </select></form>
                        <form name=addgoodies><select><option value=17 descid=555>Inventory item</option></select></form>
                        """))
                };
            }

            if (uri.AbsolutePath == "/sendmessage.php")
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                sends.Add((role, await request.Content!.ReadAsStringAsync(cancellationToken)));
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(sends.Count == 1
                        ? "<center>Message sent.</center>" : "<center>Message could not be sent.</center>"),
                    RequestMessage = request
                };
            }

            if (uri.AbsolutePath == "/messages.php" && request.Method == HttpMethod.Post)
            {
                Assert.Equal(AccountRole.Main, role);
                var form = System.Web.HttpUtility.ParseQueryString(
                    await request.Content!.ReadAsStringAsync(cancellationToken));
                if (form["the_action"] == "delete")
                {
                    Assert.Equal("Saved", form["box"]);
                    Assert.Equal("on", form["101"]);
                    Assert.Equal("fixture-Main", form["pwd"]);
                    Assert.True(_saved);
                    _saved = false;
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<p>Deleted.</p>") };
                }

                Assert.Equal("Inbox", form["box"]);
                Assert.Equal("save", form["the_action"]);
                Assert.Equal("on", form["101"]);
                Assert.Equal("fixture-Main", form["pwd"]);
                Assert.False(_saved);
                _saved = true;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<p>Saved.</p>") };
            }

            if (uri.Query == "?box=Outbox&begin=1")
            {
                Assert.Equal(AccountRole.Main, role);
                Assert.Single(sends);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        <b>Messages: Outbox, page 1 (1 message)</b>
                        <td valign=top><input name="201"><b>To:</b> Player &amp; Co<br>
                        <b>Date:</b> Friday<br>First line<br>Second line</td>
                        """)
                };
            }

            if (uri.Query == "?box=Saved&begin=1")
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_saved ? """
                        <b>Messages: Saved, page 1 (1 message)</b>
                        <td valign=top><input name="101"><b>From:</b> Fixture<br>
                        <b>Date:</b> Friday<br>Main saved message</td>
                        """ : "<p>There are no messages in this mailbox.</p>")
                };
            }

            Assert.Equal(HttpMethod.Get, request.Method);
            var playerId = role == AccountRole.Main ? 1001 : 1002;
            var body = uri.Query.Contains("what=status", StringComparison.Ordinal)
                ? $$"""{"playerid":{{playerId}},"effects":{"fixture-curse": ["Once-Cursed", 5] } }""" : "{}";
            if (role == AccountRole.Main && uri.Query.Contains("what=inventory", StringComparison.Ordinal))
                body = $$"""{"2":{{_inventoryCount}},"4508":{{(_lookingGlassAttempts > 0 ? 1 : 0)}}}""";
            if (uri.AbsolutePath == "/clan_detailedroster.php")
            {
                Assert.Equal(AccountRole.Main, role);
                body = """
                    <table><tr><td><a href="showplayer.php?who=1001">Main</a></td><td>Seal Clubber</td>
                    <td>30</td><td>4</td><td>8</td><td>42</td><td>2</td><td>1</td><td>0</td>
                    <td>Officer</td><td>100</td></tr></table>
                    """;
            }
            if (uri.AbsolutePath == "/showclan.php")
            {
                Assert.Equal(AccountRole.Main, role);
                body = uri.Query switch
                {
                    "?whichclan=85635&page=0" => """
                        <tr><td><b><a class=nounder href="showplayer.php?who=1001">Main</a></b>&nbsp;</td><td class=small>Test title&nbsp;</td><td class=small>17</td></tr>
                        <a href="showclan.php?whichclan=85635&amp;page=1">next page &gt;&gt;</a>
                        """,
                    "?whichclan=85635&page=1" => """
                        <tr><td><b><a class=nounder href="showplayer.php?who=1003">Another &amp; Player</a></b>&nbsp;</td><td class=small>&nbsp;</td><td class=small>9</td></tr>
                        """,
                    _ => throw new InvalidOperationException($"Unexpected clan roster request: {uri.Query}")
                };
            }
            if (uri.AbsolutePath == "/showplayer.php")
            {
                Assert.Equal($"?who={playerId}", uri.Query);
                var membership = role == AccountRole.Main
                    ? """<td>Clan: <b><a class=nounder href="showclan.php?whichclan=85635">Fixture &amp; Clan</a></b><br>Title: <b>Test title</b></td>"""
                    : "";
                body = $"<b>{role}</b> (#{playerId})<br>{membership}";
            }
            if (uri.AbsolutePath == "/messages.php")
            {
                requests.Add((role, uri.Query));
                var page = uri.Query.EndsWith("=2", StringComparison.Ordinal) ? 2 : 1;
                body = $"""
                    <b>Messages: Inbox, page {page} ({page} - {page} of 2)</b>
                    <td valign=top><input name="10{page}"><b>From:</b> Fixture<br>
                    <b>Date:</b> Friday<br>{role} page {page}</td>
                    """;
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body),
                RequestMessage = request
            };
        }
    }

    private sealed class Recording(IReadOnlyList<object> responses) : HttpMessageHandler
    {
        public List<(string Path, HttpMethod Method, string Body)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var index = Requests.Count;
            Requests.Add((request.RequestUri!.AbsolutePath, request.Method,
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            if (responses[index] is Exception failure) throw failure;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent((string)responses[index]),
                RequestMessage = request
            };
        }
    }
}
