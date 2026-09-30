using System.Text.Json.Nodes;

namespace MealPlanner.Tests;

/// <summary>
/// The provider-neutral pieces under every API call: effort spelling, the
/// strict-schema adapter, and the one record that carries a key.
/// </summary>
public class ModelCallTests
{
    [Fact]
    public void A_resolved_model_never_prints_its_key()
    {
        // A record's generated ToString prints every property, and a resolved
        // model is exactly the thing somebody will drop into a log line.
        var model = new ResolvedModel(AiProvider.OpenAi, "gpt-5.6-luna", AiEffort.Low, "sk-proj-SECRETSECRET1234");

        var printed = model.ToString();

        Assert.DoesNotContain("SECRET", printed, StringComparison.Ordinal);
        Assert.DoesNotContain("1234", printed, StringComparison.Ordinal);
        Assert.Contains("gpt-5.6-luna", printed, StringComparison.Ordinal);
        Assert.Contains("ApiKey = set", printed, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AiEffort.Minimal, "minimal")]
    [InlineData(AiEffort.Low, "low")]
    [InlineData(AiEffort.Medium, "medium")]
    [InlineData(AiEffort.High, "high")]
    [InlineData(AiEffort.XHigh, "xhigh")]
    [InlineData(AiEffort.Max, "max")]
    public void Every_effort_has_a_spelling_for_every_provider(AiEffort effort, string expected)
    {
        foreach (var provider in Enum.GetValues<AiProvider>())
        {
            Assert.Equal(expected, AiEffortSpelling.For(provider, effort));
        }
    }

    [Fact]
    public void No_effort_spells_as_nothing_so_the_parameter_is_omitted()
    {
        foreach (var provider in Enum.GetValues<AiProvider>())
        {
            Assert.Null(AiEffortSpelling.For(provider, null));
        }
    }

    [Theory]
    [InlineData("low", AiEffort.Low)]
    [InlineData("MEDIUM", AiEffort.Medium)]
    [InlineData(" xhigh ", AiEffort.XHigh)]
    [InlineData("max", AiEffort.Max)]
    [InlineData("loww", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("3", null)]
    public void Parsing_an_appsettings_effort_tolerates_typos_as_none(string? word, AiEffort? expected)
    {
        Assert.Equal(expected, AiEffortSpelling.Parse(word));
    }

    [Fact]
    public void The_strict_schema_drops_array_bounds_at_any_depth()
    {
        var adapted = StrictSchema.Adapt(
            """
            {"type":"object","properties":{"recipes":{"type":"array","minItems":2,"maxItems":3,
             "items":{"type":"object","properties":{"steps":{"type":"array","maxItems":9,"items":{"type":"string"}}}}}}}
            """);

        var text = adapted.ToJsonString();
        Assert.DoesNotContain("minItems", text, StringComparison.Ordinal);
        Assert.DoesNotContain("maxItems", text, StringComparison.Ordinal);
        Assert.Equal("array", adapted["properties"]!["recipes"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void The_strict_schema_leaves_everything_else_alone()
    {
        const string schema =
            """{"type":"object","properties":{"a":{"type":"string","enum":["x","y"]}},"required":["a"],"additionalProperties":false}""";

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(schema), StrictSchema.Adapt(schema)));
    }
}
