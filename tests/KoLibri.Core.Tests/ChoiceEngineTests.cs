using Xunit;
using KoLibri.Core.Spiel;

namespace KoLibri.Core.Tests;

public sealed class ChoiceEngineTests
{
    [Fact]
    public void UnknownChoiceDefaultsToManualReview()
    {
        var result = new ChoiceEngine().Decide(new ChoiceObservation(
            "9999",
            new[] { "1", "2" }));

        Assert.True(result.IsManual);
        Assert.Equal(ChoiceDecisionKind.Manual, result.Kind);
        Assert.Equal(ChoiceDecisionReason.UnknownChoice, result.ReasonCode);
        Assert.Null(result.OptionId);
    }

    [Fact]
    public void AllowlistedOptionIsSelectedDeterministically()
    {
        var policy = new ChoicePolicy
        {
            Choices = new[]
            {
                new ChoiceDefinition
                {
                    ChoiceId = "1234",
                    Options = new[]
                    {
                        new ChoiceOption { OptionId = "9", Priority = 3 },
                        new ChoiceOption { OptionId = "2", Priority = 1 },
                        new ChoiceOption { OptionId = "1", Priority = 1 }
                    }
                }
            }
        };

        var engine = new ChoiceEngine(policy);
        var first = engine.Decide(new ChoiceObservation("1234", new[] { "9", "1", "2" }));
        var second = engine.Decide(new ChoiceObservation("1234", new[] { "2", "9", "1" }));

        Assert.Equal(ChoiceDecisionKind.Select, first.Kind);
        Assert.Equal("1", first.OptionId);
        Assert.Equal(first.OptionId, second.OptionId);
    }

    [Fact]
    public void PreferredOptionUnavailableStopsForManualReview()
    {
        var policy = new ChoicePolicy
        {
            Choices = new[]
            {
                new ChoiceDefinition
                {
                    ChoiceId = "1234",
                    DefaultOptionId = "9",
                    Options = new[]
                    {
                        new ChoiceOption { OptionId = "9" },
                        new ChoiceOption { OptionId = "2" }
                    }
                }
            }
        };

        var result = new ChoiceEngine(policy).Decide(
            new ChoiceObservation("1234", new[] { "2" }));

        Assert.True(result.IsManual);
        Assert.Equal(ChoiceDecisionReason.PreferredOptionUnavailable, result.ReasonCode);
    }

    [Fact]
    public void OnlyTypedAllowlistedHiddenFieldsAreCarried()
    {
        var policy = new ChoicePolicy
        {
            Choices = new[]
            {
                new ChoiceDefinition
                {
                    ChoiceId = "1234",
                    Options = new[] { new ChoiceOption { OptionId = "2" } },
                    HiddenFields = new[] { new ChoiceHiddenField("pwd", required: true) }
                }
            }
        };
        var observed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["pwd"] = "page-token",
            ["csrf"] = "unconfigured",
            ["whichchoice"] = "attacker-choice",
            ["option"] = "9"
        };

        var result = new ChoiceEngine(policy).Decide(
            new ChoiceObservation("1234", new[] { "2" }, observed));

        Assert.True(result.IsSelected);
        Assert.Equal("page-token", result.HiddenFields["pwd"]);
        Assert.DoesNotContain("csrf", result.HiddenFields.Keys);
        Assert.DoesNotContain("whichchoice", result.HiddenFields.Keys);
        Assert.DoesNotContain("option", result.HiddenFields.Keys);
    }

    [Fact]
    public void RequiredHiddenFieldMissingStopsWithoutSelection()
    {
        var policy = new ChoicePolicy
        {
            Choices = new[]
            {
                new ChoiceDefinition
                {
                    ChoiceId = "1234",
                    Options = new[] { new ChoiceOption { OptionId = "2" } },
                    HiddenFields = new[] { new ChoiceHiddenField("pwd", required: true) }
                }
            }
        };

        var result = new ChoiceEngine(policy).Decide(
            new ChoiceObservation("1234", new[] { "2" }));

        Assert.True(result.IsManual);
        Assert.Equal(ChoiceDecisionReason.RequiredHiddenFieldMissing, result.ReasonCode);
        Assert.Empty(result.HiddenFields);
    }

    [Fact]
    public void InvalidHiddenFieldValueStopsWithoutEmittingFields()
    {
        var policy = new ChoicePolicy
        {
            Choices = new[]
            {
                new ChoiceDefinition
                {
                    ChoiceId = "1234",
                    Options = new[] { new ChoiceOption { OptionId = "2" } },
                    HiddenFields = new[] { new ChoiceHiddenField("mode", new[] { "safe" }) }
                }
            }
        };

        var result = new ChoiceEngine(policy).Decide(new ChoiceObservation(
            "1234",
            new[] { "2" },
            new Dictionary<string, string> { ["mode"] = "unsafe" }));

        Assert.True(result.IsManual);
        Assert.Equal(ChoiceDecisionReason.HiddenFieldValueNotAllowed, result.ReasonCode);
        Assert.Empty(result.HiddenFields);
    }

    [Fact]
    public void ReservedIdentityFieldsCannotBeConfiguredAsHidden()
    {
        var policy = new ChoicePolicy
        {
            Choices = new[]
            {
                new ChoiceDefinition
                {
                    ChoiceId = "1234",
                    Options = new[] { new ChoiceOption { OptionId = "2" } },
                    HiddenFields = new[] { new ChoiceHiddenField("whichchoice") }
                }
            }
        };

        Assert.Throws<ArgumentException>(() => new ChoiceEngine(policy));
    }
}

