namespace MealPlanner.Tests;

/// <summary>
/// <see cref="AiCatalog"/> is pure and static, so none of this needs a harness.
/// </summary>
public class AiCatalogTests
{
    [Theory]
    [InlineData("sk-ant-api03-abcdef", AiProvider.AnthropicApi)]
    [InlineData("sk-or-v1-abcdef", AiProvider.OpenRouter)]
    [InlineData("sk-proj-abcdef", AiProvider.OpenAi)]
    // Longest match wins, and this pair is the whole reason: "sk-proj-" starts
    // with "sk-", so an if-ladder would answer by insertion order instead.
    [InlineData("sk-abcdef", AiProvider.OpenAi)]
    public void Prefixes_map_to_the_providers_that_issue_them(string key, AiProvider expected) =>
        Assert.Equal(expected, AiCatalog.InferProvider(key));

    [Theory]
    [InlineData("gsk_something_else")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_key_we_cannot_place_infers_nothing(string? key) =>
        // Null, not an exception and not a guess: the page asks which provider
        // it is. Refusing would break the day a provider changes its prefix.
        Assert.Null(AiCatalog.InferProvider(key));

    [Fact]
    public void Surrounding_whitespace_does_not_defeat_inference() =>
        Assert.Equal(AiProvider.AnthropicApi, AiCatalog.InferProvider("  sk-ant-api03-abcdef\n"));

    [Fact]
    public void Only_the_claude_subscription_needs_no_key()
    {
        Assert.False(AiCatalog.For(AiProvider.ClaudeCli).NeedsApiKey);
        Assert.All(
            AiCatalog.Providers.Where(p => p.Provider != AiProvider.ClaudeCli),
            p => Assert.True(p.NeedsApiKey));
    }

    [Fact]
    public void Every_provider_offers_at_least_one_model_and_one_effort() =>
        Assert.All(AiCatalog.Providers, p =>
        {
            Assert.NotEmpty(p.Models);
            Assert.NotEmpty(p.Efforts);
        });

    [Fact]
    public void No_model_id_carries_a_date_suffix() =>
        // The Claude 5 family takes bare ids; a remembered date suffix is a
        // silent 404 at call time rather than a validation error here.
        Assert.All(
            AiCatalog.For(AiProvider.AnthropicApi).Models,
            m => Assert.DoesNotMatch(@"-\d{8}$", m.Id));

    [Fact]
    public void A_model_that_rejects_effort_says_so() =>
        // Haiku 4.5 does not merely ignore effort — it errors on it.
        Assert.False(AiCatalog.SupportsEffort(AiProvider.AnthropicApi, "claude-haiku-4-5"));

    [Fact]
    public void A_model_we_have_never_heard_of_borrows_the_providers_answer() =>
        // Somebody typed a raw id into "Other…". They are the one who knows
        // what it accepts, so the provider's own capability is the best guess.
        Assert.True(AiCatalog.SupportsEffort(AiProvider.OpenRouter, "some/model-we-do-not-list"));

    [Fact]
    public void KnowsModel_separates_the_curated_list_from_free_text()
    {
        Assert.True(AiCatalog.KnowsModel(AiProvider.AnthropicApi, "claude-opus-5"));
        Assert.False(AiCatalog.KnowsModel(AiProvider.AnthropicApi, "claude-opus-5-20260101"));
    }

    [Fact]
    public void Every_provider_in_the_enum_has_an_entry() =>
        // For() throws on a missing one, so this is the guard that a new enum
        // member cannot ship without its table row.
        Assert.All(Enum.GetValues<AiProvider>(), p => Assert.NotNull(AiCatalog.For(p)));
}
