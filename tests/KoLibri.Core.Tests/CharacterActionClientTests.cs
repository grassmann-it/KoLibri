using System.Net;
using Xunit;
using KoLibri.Core.Basis;
using KoLibri.Core.Spiel;

namespace KoLibri.Core.Tests;

public sealed class CharacterActionClientTests
{
    [Theory]
    [InlineData("equip", "/inv_equip.php", "slot=2")]
    [InlineData("unequip", "/inv_equip.php", "type=acc2")]
    [InlineData("outfit", "/inv_equip.php", "whichoutfit=-7")]
    [InlineData("familiar", "/familiar.php", "newfam=7")]
    [InlineData("familiar gear", "/familiar.php", "whichfam=7")]
    [InlineData("use", "/inv_use.php", "whichitem=123")]
    [InlineData("food", "/inv_eat.php", "quantity=1")]
    [InlineData("drink", "/inv_booze.php", "quantity=1")]
    [InlineData("spleen", "/inv_spleen.php", "quantity=1")]
    [InlineData("cast", "/runskillz.php", "whichskill=2009")]
    [InlineData("rest", "/campground.php", "action=rest")]
    [InlineData("closet put", "/inventory.php", "action=closetpush")]
    [InlineData("closet take", "/inventory.php", "action=closetpull")]
    [InlineData("storage pull", "/storage.php", "howmany1=1")]
    public async Task Sends_the_character_form_then_reads_fresh_account_state(
        string kind, string route, string expectedField)
    {
        CharacterAction action = kind switch
        {
            "equip" => new EquipInventoryItem(123, CharacterEquipmentSlot.Accessory2),
            "unequip" => new UnequipInventoryItem(CharacterEquipmentSlot.Accessory2),
            "outfit" => new WearCharacterOutfit(-7),
            "familiar" => new SelectCharacterFamiliar(7),
            "familiar gear" => new EquipCharacterFamiliar(7, 123),
            "food" => new ConsumeCharacterItem(123, CharacterConsumableKind.Food),
            "drink" => new ConsumeCharacterItem(123, CharacterConsumableKind.Drink),
            "spleen" => new ConsumeCharacterItem(123, CharacterConsumableKind.Spleen),
            "cast" => new CastCharacterSkill(2009),
            "rest" => new RestAtCampground(),
            "closet put" => new MoveClosetItem(123, 1, IntoCloset: true),
            "closet take" => new MoveClosetItem(123, 1, IntoCloset: false),
            "storage pull" => new PullStorageItem(123, 1),
            _ => new UseInventoryItem(123)
        };
        using var handler = new RecordingHandler();
        using var http = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("https://fixture.test/") };
        var client = new CharacterActionClient(http, "fixture-pwd",
            new KoLSecurityPolicy { AllowedHosts = new[] { "fixture.test" } });

        var observation = await client.ExecuteAsync(action);

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal((HttpMethod.Post, route), (handler.Requests[0].Method, handler.Requests[0].Path));
        Assert.Contains(expectedField, handler.Requests[0].Body);
        Assert.Contains("pwd=fixture-pwd", handler.Requests[0].Body);
        Assert.All(handler.Requests.Skip(1), request => Assert.Equal(HttpMethod.Get, request.Method));
        Assert.Equal("7", observation.Status.FamiliarId);
        Assert.Equal(15, observation.Status.Fullness);
        Assert.Equal(2, observation.Status.Inebriety);
        Assert.Equal(3, observation.Status.Spleen);
        Assert.Equal(new KoLTimedEffect("Empathy", 29), Assert.Single(observation.Status.TimedEffects));
        Assert.Equal("123", observation.Status.Equipment["acc2"]);
        Assert.Equal(2, Assert.Single(observation.Inventory.Items).Quantity);
    }

    [Fact]
    public async Task Character_preparation_reports_each_change_from_a_fresh_server_snapshot()
    {
        using var handler = new RecordingHandler(new[]
        {
            """{"familiar":"6","equipment":{"hat":"123"}}""", """{"55":2,"77":1}""",
            """{"familiar":"7","equipment":{"hat":"123","familiarequip":"0"}}""", """{"55":2,"77":1}""",
            """{"familiar":"7","equipment":{"hat":"123","familiarequip":"77"}}""", """{"55":2}""",
            """{"familiar":"7","equipment":{"hat":"0","familiarequip":"77"}}""", """{"55":2,"123":1}""",
            """{"familiar":"7","equipment":{"hat":"123","pants":"98","familiarequip":"77"}}""", """{"55":2}""",
            """{"familiar":"7","equipment":{"hat":"123","pants":"98","familiarequip":"77"}}""", """{"55":1}"""
        });
        using var http = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("https://fixture.test/") };
        var client = new CharacterActionClient(http, "fixture-pwd",
            new KoLSecurityPolicy { AllowedHosts = new[] { "fixture.test" } });

        var equipped = await client.ExecuteAsync(new EquipInventoryItem(123, CharacterEquipmentSlot.Hat));
        Assert.Equal("123", equipped.Status.Equipment["hat"]);
        Assert.DoesNotContain(equipped.Inventory.Items, item => item.ItemId == "123");

        var familiar = await client.ExecuteAsync(new SelectCharacterFamiliar(7));
        Assert.Equal("7", familiar.Status.FamiliarId);
        Assert.Equal("123", familiar.Status.Equipment["hat"]);

        var familiarGear = await client.ExecuteAsync(new EquipCharacterFamiliar(7, 77));
        Assert.Equal("77", familiarGear.Status.Equipment["familiarequip"]);
        Assert.DoesNotContain(familiarGear.Inventory.Items, item => item.ItemId == "77");

        var unequipped = await client.ExecuteAsync(new UnequipInventoryItem(CharacterEquipmentSlot.Hat));
        Assert.Equal("0", unequipped.Status.Equipment["hat"]);
        Assert.Equal(1, Assert.Single(unequipped.Inventory.Items, item => item.ItemId == "123").Quantity);

        var outfit = await client.ExecuteAsync(new WearCharacterOutfit(-7));
        Assert.Equal("123", outfit.Status.Equipment["hat"]);
        Assert.Equal("98", outfit.Status.Equipment["pants"]);

        var used = await client.ExecuteAsync(new UseInventoryItem(55));
        Assert.Equal("7", used.Status.FamiliarId);
        Assert.Equal(1, Assert.Single(used.Inventory.Items, item => item.ItemId == "55").Quantity);
        Assert.Equal(6, handler.Requests.Count(request => request.Method == HttpMethod.Post));
        Assert.Equal(12, handler.Requests.Count(request => request.Method == HttpMethod.Get));
    }

    [Fact]
    public async Task Preparation_updates_consumption_effects_and_resources_and_keeps_refused_state()
    {
        const string full = """{"full":"15","drunk":2,"spleen":"3","hp":90,"mp":50,"adventures":19,"effects":{"hash":["Empathy","5"]}}""";
        using var handler = new RecordingHandler(new[]
        {
            """{"full":15,"drunk":0,"spleen":0,"hp":40,"mp":20}""", """{"55":2,"56":1,"57":1}""",
            """{"full":15,"drunk":2,"spleen":0,"hp":40,"mp":20}""", """{"55":2,"57":1}""",
            """{"full":15,"drunk":2,"spleen":3,"hp":40,"mp":20}""", """{"55":2}""",
            """{"full":15,"drunk":2,"spleen":3,"hp":40,"mp":10,"effects":{"hash":["Empathy","5"]}}""", """{"55":2}""",
            full, """{"55":2}""",
            full, """{"55":2}"""
        }, new[] { "Ate", "Drank", "Used", "Cast", "Rested", "<p>You are too full to eat that.</p>" });
        using var http = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("https://fixture.test/") };
        var client = new CharacterActionClient(http, "fixture-pwd",
            new KoLSecurityPolicy { AllowedHosts = new[] { "fixture.test" } });

        Assert.Equal(15, (await client.ExecuteAsync(new ConsumeCharacterItem(55, CharacterConsumableKind.Food))).Status.Fullness);
        Assert.Equal(2, (await client.ExecuteAsync(new ConsumeCharacterItem(56, CharacterConsumableKind.Drink))).Status.Inebriety);
        Assert.Equal(3, (await client.ExecuteAsync(new ConsumeCharacterItem(57, CharacterConsumableKind.Spleen))).Status.Spleen);
        var cast = await client.ExecuteAsync(new CastCharacterSkill(2009));
        Assert.Equal(10, cast.Status.CurrentMp);
        Assert.Equal(new KoLTimedEffect("Empathy", 5), Assert.Single(cast.Status.TimedEffects));
        var rested = await client.ExecuteAsync(new RestAtCampground());
        Assert.Equal(90, rested.Status.CurrentHp);
        Assert.Equal(50, rested.Status.CurrentMp);
        Assert.Equal(19, rested.Status.Adventures);
        var refused = await client.ExecuteAsync(new ConsumeCharacterItem(55, CharacterConsumableKind.Food));
        Assert.Contains("too full", refused.RejectionReason);
        Assert.Equal(rested.Status.Fullness, refused.Status.Fullness);
        Assert.Equal(2, Assert.Single(refused.Inventory.Items).Quantity);
        Assert.Equal(6, handler.Requests.Count(request => request.Method == HttpMethod.Post));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Bone_rattle_recipe_checks_ingredients_and_verifies_the_crafted_result(bool hasIngredients)
    {
        var catalog = new MafiaDataLoader().Load(Path.Combine(AppContext.BaseDirectory, "external", "kolmafia-data"));
        var recipe = Assert.Single(CharacterRecipeCatalog.Find(catalog, "bone rattle"), row => row.CombineAction is not null);
        Assert.Equal(new[] { "skeleton bone", "broken skull" }, recipe.IngredientNames);
        var action = Assert.IsType<CombineCharacterItems>(recipe.CombineAction);
        Assert.Equal(new CombineCharacterItems(168, 163, 741), action);

        using var handler = new RecordingHandler(new[]
        {
            hasIngredients ? """{"163":1,"741":1,"25":1}""" : """{"163":1,"25":1}""",
            """{"pullsleft":"3"}""",
            """{"168":1}"""
        });
        using var http = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("https://fixture.test/") };
        var client = new CharacterActionClient(http, "fixture-pwd",
            new KoLSecurityPolicy { AllowedHosts = new[] { "fixture.test" } });

        if (!hasIngredients)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.ExecuteAsync(action));
            Assert.Equal(HttpMethod.Get, Assert.Single(handler.Requests).Method);
            return;
        }

        var result = await client.ExecuteAsync(action);
        Assert.Null(result.RejectionReason);
        Assert.Equal("168", Assert.Single(result.Inventory.Items).ItemId);
        Assert.Equal(3, result.Status.StoragePullsRemaining);
        var craft = Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post);
        Assert.Equal("/craft.php", craft.Path);
        Assert.Contains("mode=combine", craft.Body);
        Assert.Contains("a=163", craft.Body);
        Assert.Contains("b=741", craft.Body);
    }

    [Fact]
    public async Task Storage_pull_is_reflected_in_inventory_storage_and_remaining_pulls()
    {
        using var handler = new RecordingHandler(new[]
        {
            """{"pullsleft":3}""", """{"163":"1"}""", """{"741":2}""",
            """{"pullsleft":2}""", """{"741":1}""",
            """{"pullsleft":2}""", """{"163":"1"}""", """{"741":1}"""
        });
        using var http = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("https://fixture.test/") };
        var client = new CharacterActionClient(http, "fixture-pwd",
            new KoLSecurityPolicy { AllowedHosts = new[] { "fixture.test" } });

        var before = await client.ReadStorageAsync();
        Assert.Equal(3, before.Status.StoragePullsRemaining);
        Assert.Equal("163", Assert.Single(before.Closet.Items).ItemId);
        Assert.Equal(2, Assert.Single(before.Storage.Items).Quantity);

        var pulled = await client.ExecuteAsync(new PullStorageItem(741, 1));
        Assert.Equal("741", Assert.Single(pulled.Inventory.Items).ItemId);
        var after = await client.ReadStorageAsync();
        Assert.Equal(2, after.Status.StoragePullsRemaining);
        Assert.Equal(1, Assert.Single(after.Storage.Items).Quantity);
        Assert.Equal("/storage.php", Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post).Path);
        Assert.Contains("what=closet", handler.ReadQueries[1]);
        Assert.Contains("what=storage", handler.ReadQueries[2]);
    }

    [Fact]
    public async Task Friars_page_action_refreshes_inventory_and_effects_on_the_same_client()
    {
        using var handler = new RecordingHandler(new[]
        {
            "<p>You've got all three of the ritual items, Adventurer!</p>",
            """{"adventures":20,"effects":{}}""", "{}",
            "<p>We're good at blessings.</p>",
            """{"adventures":20,"effects":{"hash":["Brother Smothers's Blessing","20"]}}""", "{}"
        });
        using var http = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("https://fixture.test/") };
        var client = new CharacterActionClient(http, "fixture-pwd",
            new KoLSecurityPolicy { AllowedHosts = new[] { "fixture.test" } });

        Assert.Equal(FriarsPageState.ReadyForRitual, await client.ReadFriarsAsync());
        await client.ExecuteAsync(new PerformFriarRitual());
        Assert.Equal(FriarsPageState.RitualComplete, await client.ReadFriarsAsync());
        var blessing = await client.ExecuteAsync(new RequestFriarBlessing(FriarBlessing.Food));
        Assert.Equal(new KoLTimedEffect("Brother Smothers's Blessing", 20), Assert.Single(blessing.Status.TimedEffects));
        var posts = handler.Requests.Where(request => request.Method == HttpMethod.Post).ToArray();
        Assert.Equal(2, posts.Length);
        Assert.All(posts, request => Assert.Equal("/friars.php", request.Path));
        Assert.Contains("action=ritual", posts[0].Body);
        Assert.Contains("action=buffs", posts[1].Body);
        Assert.Contains("bro=1", posts[1].Body);
    }

    [Theory]
    [InlineData(14, 1, false)]
    [InlineData(30, 4, false)]
    [InlineData(30, 1, true)]
    public async Task Mood_renews_only_when_the_effect_is_expiring_and_mana_is_available(
        int mana, int turns, bool shouldCast)
    {
        using var handler = new RecordingHandler(new[]
        {
            $$$"""{"mp":{{{mana}}},"effects":{"hash":["Empathy",{{{turns}}}]}}""", "{}",
            """{"mp":15,"effects":{"hash":["Empathy",6]}}""", "{}"
        });
        using var http = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("https://fixture.test/") };
        var client = new CharacterActionClient(http, "fixture-pwd",
            new KoLSecurityPolicy { AllowedHosts = new[] { "fixture.test" } });
        var mood = await client.EnsureEffectAsync(new CharacterMoodRule("Empathy", 2009, 15));

        Assert.Equal(shouldCast, mood.CastSubmitted);
        Assert.Equal(shouldCast ? 1 : 0, handler.Requests.Count(request => request.Method == HttpMethod.Post));
        if (shouldCast)
        {
            Assert.Equal(6, Assert.Single(mood.Snapshot.Status.TimedEffects).TurnsRemaining);
            Assert.Equal(15, mood.Snapshot.Status.CurrentMp);
        }
    }

    private static CharacterActionClient Client(RecordingHandler handler, out HttpClient http)
    {
        http = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("https://fixture.test/") };
        return new CharacterActionClient(http, "fixture-pwd", new KoLSecurityPolicy { AllowedHosts = new[] { "fixture.test" } });
    }

    private static string St(int hp, int mp, int adv) => $$"""{"hp":{{hp}},"mp":{{mp}},"adventures":{{adv}}}""";

    [Fact]
    public async Task Cast_with_unchanged_mp_is_unconfirmed_and_carries_the_page_excerpt()
    {
        using var handler = new RecordingHandler(new[] { St(40, 20, 10), "{}", St(40, 20, 10), "{}" }, new[] { "<p>Nothing special happens.</p>" });
        var outcome = await Client(handler, out var http).CastAsync(7185, 1);
        http.Dispose();
        Assert.False(outcome.Confirmed);
        Assert.Contains("did not confirm", outcome.Problem);
        Assert.Contains("Nothing special happens.", outcome.Problem);
    }

    [Fact]
    public async Task Cast_refused_for_missing_soulsauce_is_reported()
    {
        using var handler = new RecordingHandler(new[] { St(40, 20, 10), "{}", St(40, 20, 10), "{}" },
            new[] { "<p>You don't have enough soulsauce to cast that.</p>" });
        var outcome = await Client(handler, out var http).CastAsync(7185, 1);
        http.Dispose();
        Assert.False(outcome.Confirmed);
        Assert.Contains("not enough mana or soulsauce", outcome.Problem);
        Assert.Contains("soulsauce", outcome.Problem);
    }

    [Fact]
    public async Task Cast_confirmed_by_changed_mp_or_by_kols_gain_message()
    {
        using var handler = new RecordingHandler(new[] { St(40, 20, 10), "{}", St(40, 35, 10), "{}" }, new[] { "You gain 15 Mana Points" });
        var ok = await Client(handler, out var http).CastAsync(7185, 1);
        http.Dispose();
        Assert.True(ok.Confirmed);
        Assert.Null(ok.Problem);
        Assert.Equal(35, ok.After.Status.CurrentMp);

        using var capped = new RecordingHandler(new[] { St(40, 50, 10), "{}", St(40, 50, 10), "{}" }, new[] { "You gain 0 Mana Points" });
        var gained = await Client(capped, out var http2).CastAsync(7185, 1);
        http2.Dispose();
        Assert.True(gained.Confirmed);
    }

    [Fact]
    public async Task Free_rest_series_counts_each_confirmed_rest_and_spends_no_adventures()
    {
        using var handler = new RecordingHandler(new[]
        {
            St(10, 5, 20), "{}", St(60, 30, 20), "{}", St(90, 50, 20), "{}"
        }, new[] { "You rest.", "You rest." });
        var outcome = await Client(handler, out var http).RestAsync(2);
        http.Dispose();
        Assert.Equal(2, outcome.Rests.Count);
        Assert.Equal(0, outcome.AdventuresSpent);
        Assert.Null(outcome.Problem);
    }

    [Fact]
    public async Task Costly_rest_books_the_adventure_and_an_unchanged_second_rest_stops_the_series()
    {
        using var handler = new RecordingHandler(new[]
        {
            St(10, 5, 20), "{}", St(60, 30, 19), "{}", St(60, 30, 19), "{}"
        }, new[] { "You rest.", "Nothing." });
        var outcome = await Client(handler, out var http).RestAsync(3);
        http.Dispose();
        Assert.Single(outcome.Rests);
        Assert.Equal(1, outcome.AdventuresSpent);
        Assert.Contains("did not confirm the rest", outcome.Problem);
        Assert.Equal(2, handler.Requests.Count(r => r.Method == HttpMethod.Post));   // stopped after the unclear one
    }

    [Fact]
    public async Task Refused_rest_is_not_counted()
    {
        using var handler = new RecordingHandler(new[] { St(10, 5, 0), "{}", St(10, 5, 0), "{}" },
            new[] { "<p>You don't have enough adventures to rest.</p>" });
        var outcome = await Client(handler, out var http).RestAsync(2);
        http.Dispose();
        Assert.Empty(outcome.Rests);
        Assert.Contains("refused the rest", outcome.Problem);
        Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Cast_series_confirms_each_cast_and_reports_how_many_really_took_effect()
    {
        // Three casts requested; only the first one changes MP, the second does nothing visible.
        using var handler = new RecordingHandler(new[]
        {
            St(40, 50, 10), "{}", St(40, 40, 10), "{}", St(40, 40, 10), "{}"
        }, new[] { "<p>ok</p>", "<p>Nothing special happens.</p>" });
        var outcome = await Client(handler, out var http).CastAsync(7185, 3);
        http.Dispose();
        Assert.False(outcome.Confirmed);
        Assert.Equal(1, outcome.ConfirmedCount);
        Assert.Equal(3, outcome.Requested);
        Assert.Contains("Confirmed 1 of 3", outcome.Problem);
        Assert.Equal(2, handler.Requests.Count(r => r.Method == HttpMethod.Post));
        Assert.All(handler.Requests.Where(r => r.Method == HttpMethod.Post), r => Assert.Contains("quantity=1", r.Body));
    }

    [Fact]
    public async Task Cast_series_with_every_cast_confirmed_returns_the_full_count()
    {
        using var handler = new RecordingHandler(new[]
        {
            St(40, 50, 10), "{}", St(40, 40, 10), "{}", St(40, 30, 10), "{}"
        }, new[] { "<p>ok</p>", "<p>ok</p>" });
        var outcome = await Client(handler, out var http).CastAsync(7185, 2);
        http.Dispose();
        Assert.True(outcome.Confirmed);
        Assert.Equal(2, outcome.ConfirmedCount);
    }

    [Fact]
    public async Task Free_rest_at_full_hp_and_mp_is_confirmed_by_the_rest_answer_not_counted_as_zero()
    {
        using var handler = new RecordingHandler(new[] { St(50, 50, 20), "{}", St(50, 50, 20), "{}" },
            new[] { "<p>You rest in your tent. You have 4 free rests remaining.</p>" });
        var outcome = await Client(handler, out var http).RestAsync(1);
        http.Dispose();
        Assert.Single(outcome.Rests);
        Assert.Null(outcome.Problem);
    }

    [Fact]
    public async Task A_use_form_rejected_with_a_normal_page_is_a_rejection()
    {
        using var handler = new RecordingHandler(new[] { St(10, 10, 10), "{}" },
            new[] { "<p>You can't use that right now.</p>" });
        var result = await Client(handler, out var http).ExecuteAsync(new UseInventoryItem(123));
        http.Dispose();
        Assert.NotNull(result.RejectionReason);
    }

    [Fact]
    public async Task Failure_of_the_follow_up_read_after_the_post_is_reported_as_outcome_unknown()
    {
        using var handler = new PostThenFailHandler();
        using var http = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("https://fixture.test/") };
        var client = new CharacterActionClient(http, "fixture-pwd", new KoLSecurityPolicy { AllowedHosts = new[] { "fixture.test" } });
        var exception = await Assert.ThrowsAsync<CharacterActionOutcomeUnknownException>(
            () => client.ExecuteAsync(new ConsumeCharacterItem(123, CharacterConsumableKind.Food)));
        Assert.Contains("may have happened", exception.Message);
        Assert.Equal(1, handler.Posts);
    }

    private sealed class PostThenFailHandler : HttpMessageHandler
    {
        public int Posts { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method != HttpMethod.Post) throw new HttpRequestException("connection reset");
            Posts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent("You eat it.") });
        }
    }

    private sealed class RecordingHandler(
        IReadOnlyList<string>? observations = null, IReadOnlyList<string>? posts = null) : HttpMessageHandler
    {
        private int _readIndex;
        private int _postIndex;
        public List<string> ReadQueries { get; } = new();
        public List<(HttpMethod Method, string Path, string Body)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add((request.Method, request.RequestUri!.AbsolutePath,
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(token)));
            if (request.Method == HttpMethod.Get) ReadQueries.Add(request.RequestUri!.Query);
            var content = request.Method == HttpMethod.Post ? posts?[_postIndex++] ?? "Submitted"
                : observations is not null ? observations[_readIndex++]
                : Requests.Count == 2 ? """{"familiar":"7","equipment":{"acc2":"123"},"full":"15","drunk":2,"spleen":"3","effects":{"hash":["Empathy","29","empathy","skill:2009","50"]}}"""
                : """{"123":2}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(content)
            };
        }
    }
}
