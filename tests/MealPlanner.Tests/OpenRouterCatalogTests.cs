using System.Net;
using Microsoft.Extensions.Logging.Abstractions;

namespace MealPlanner.Tests;

/// <summary>
/// The OpenRouter model-id check, run end to end through <see cref="FakeHttp"/>.
/// No network, and nothing here ever reaches a provider.
/// </summary>
public class OpenRouterCatalogTests
{
    /// <summary>
    /// Shaped like the real answer, cut to the fields the catalogue reads. Four
    /// models chosen to cover each verdict: one fully capable, one with no
    /// structured output, one text-only, and one <c>:free</c> variant.
    /// </summary>
    private const string Catalogue =
        """
        {"data":[
          {"id":"anthropic/claude-opus-5","name":"Anthropic: Claude Opus 5",
           "architecture":{"input_modalities":["text","image","file"]},
           "supported_parameters":["reasoning","response_format","structured_outputs"]},
          {"id":"anthropic/claude-sonnet-5","name":"Anthropic: Claude Sonnet 5",
           "architecture":{"input_modalities":["text","image","file"]},
           "supported_parameters":["reasoning","response_format","structured_outputs"]},
          {"id":"z-ai/glm-5.3-flashx","name":"Z.ai: GLM 5.3 FlashX",
           "architecture":{"input_modalities":["text","image"]},
           "supported_parameters":["reasoning","response_format"]},
          {"id":"deepseek/deepseek-chat-v3.1","name":"DeepSeek: DeepSeek V3.1",
           "architecture":{"input_modalities":["text"]},
           "supported_parameters":["structured_outputs"]},
          {"id":"inclusionai/ling-3.0-flash-vl:free","name":"inclusionAI: Ling 3.0 Flash VL (free)",
           "architecture":{"input_modalities":["text","image"]},
           "supported_parameters":["structured_outputs"]}
        ]}
        """;

    private static OpenRouterCatalog Catalog(FakeHttp http) =>
        new(http, NullLogger<OpenRouterCatalog>.Instance);

    [Fact]
    public async Task A_listed_id_comes_back_with_its_name_and_what_it_can_do()
    {
        var check = await Catalog(new FakeHttp(Catalogue)).CheckAsync("anthropic/claude-opus-5");

        Assert.Equal(ModelIdVerdict.Listed, check.Verdict);
        Assert.Equal("Anthropic: Claude Opus 5", check.Name);
        Assert.True(check.SupportsStructuredOutput);
        Assert.True(check.AcceptsImages);
        Assert.True(check.AcceptsFiles);
        Assert.True(check.SupportsEffort);
        Assert.Empty(check.Suggestions);
    }

    [Fact]
    public async Task A_model_that_cannot_do_structured_output_is_listed_and_says_so()
    {
        // The failure this whole feature exists for: the id is real, and every call
        // would still come back as prose.
        var check = await Catalog(new FakeHttp(Catalogue)).CheckAsync("z-ai/glm-5.3-flashx");

        Assert.Equal(ModelIdVerdict.Listed, check.Verdict);
        Assert.False(check.SupportsStructuredOutput);
        Assert.True(check.AcceptsImages);
    }

    [Fact]
    public async Task A_text_only_model_reports_no_image_or_file_input()
    {
        var check = await Catalog(new FakeHttp(Catalogue)).CheckAsync("deepseek/deepseek-chat-v3.1");

        Assert.Equal(ModelIdVerdict.Listed, check.Verdict);
        Assert.False(check.AcceptsImages);
        Assert.False(check.AcceptsFiles);
        Assert.False(check.SupportsEffort);
    }

    [Fact]
    public async Task A_free_variant_suffix_is_part_of_the_id()
    {
        var check = await Catalog(new FakeHttp(Catalogue))
            .CheckAsync("inclusionai/ling-3.0-flash-vl:free");

        Assert.Equal(ModelIdVerdict.Listed, check.Verdict);
    }

    [Fact]
    public async Task Case_and_surrounding_space_are_not_typos()
    {
        var check = await Catalog(new FakeHttp(Catalogue)).CheckAsync("  Anthropic/Claude-Opus-5 ");

        Assert.Equal(ModelIdVerdict.Listed, check.Verdict);
        // The id echoed back is OpenRouter's spelling, not the typed one.
        Assert.Equal("anthropic/claude-opus-5", check.Id);
    }

    [Fact]
    public async Task An_unlisted_id_suggests_the_one_it_was_probably_meant_to_be()
    {
        var check = await Catalog(new FakeHttp(Catalogue)).CheckAsync("anthropic/claude-opus-6");

        Assert.Equal(ModelIdVerdict.NotListed, check.Verdict);
        Assert.Equal("anthropic/claude-opus-5", check.Suggestions[0]);
    }

    [Fact]
    public async Task An_id_with_nothing_close_suggests_nothing()
    {
        // A wrong suggestion invites a wrong adopt, so silence beats a guess.
        var check = await Catalog(new FakeHttp(Catalogue)).CheckAsync("acme/frobnicator-9000");

        Assert.Equal(ModelIdVerdict.NotListed, check.Verdict);
        Assert.Empty(check.Suggestions);
    }

    [Fact]
    public async Task An_empty_box_asks_nothing_of_the_network()
    {
        var http = new FakeHttp(Catalogue);

        var check = await Catalog(http).CheckAsync("   ");

        Assert.Equal(ModelIdVerdict.Blank, check.Verdict);
        Assert.Null(http.Request);
    }

    [Fact]
    public async Task The_list_is_asked_for_without_a_key()
    {
        // The point of the feature is that it works before a key is stored, and a
        // key would make a failure ambiguous between a bad id and a bad key.
        var http = new FakeHttp(Catalogue);

        await Catalog(http).CheckAsync("anthropic/claude-opus-5");

        Assert.Equal("https://openrouter.ai/api/v1/models", http.Request!.RequestUri!.ToString());
        Assert.Null(http.Request.Headers.Authorization);
    }

    [Fact]
    public async Task A_refused_request_is_unchecked_rather_than_not_listed()
    {
        var http = new FakeHttp("nope", HttpStatusCode.InternalServerError);

        var check = await Catalog(http).CheckAsync("anthropic/claude-opus-5");

        Assert.Equal(ModelIdVerdict.Unchecked, check.Verdict);
    }

    [Fact]
    public async Task A_network_failure_is_unchecked_rather_than_thrown()
    {
        var http = new FakeHttp((_, _) => throw new HttpRequestException("no route to host"));

        var check = await Catalog(http).CheckAsync("anthropic/claude-opus-5");

        Assert.Equal(ModelIdVerdict.Unchecked, check.Verdict);
    }

    [Fact]
    public async Task A_failure_after_a_good_fetch_keeps_answering_from_the_good_one()
    {
        var fail = false;
        var http = new FakeHttp((_, _) => fail
            ? throw new HttpRequestException("gone")
            : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Catalogue),
            }));

        var catalog = Catalog(http);
        await catalog.CheckAsync("anthropic/claude-opus-5");

        // Nothing re-fetches inside the TTL, so force the question by asking after
        // the transport has broken: a stale answer beats no answer.
        fail = true;
        var check = await catalog.CheckAsync("z-ai/glm-5.3-flashx");

        Assert.Equal(ModelIdVerdict.Listed, check.Verdict);
    }

    [Fact]
    public async Task The_list_is_fetched_once_and_reused()
    {
        // FakeHttp keeps only the last request, so counting needs a closure.
        var calls = 0;
        var http = new FakeHttp((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Catalogue),
            });
        });

        var catalog = Catalog(http);
        await catalog.CheckAsync("anthropic/claude-opus-5");
        await catalog.CheckAsync("z-ai/glm-5.3-flashx");
        await catalog.CheckAsync("acme/nothing");

        Assert.Equal(1, calls);
    }

    [Fact]
    public void An_answer_that_is_not_the_shape_we_expect_parses_to_nothing()
    {
        // Rather than throwing out of the parser and reporting as a network fault.
        var snapshot = OpenRouterCatalog.ParseCatalog("""{"error":{"message":"nope"}}""");

        Assert.Empty(snapshot.Ids);
    }

    [Fact]
    public void A_suggestion_never_crosses_to_another_author_when_one_matches()
    {
        string[] ids = ["anthropic/claude-opus-5", "openai/claude-opus-5-lookalike"];

        var suggestions = OpenRouterCatalog.Suggest("anthropic/claude-opus-6", ids);

        Assert.Equal(["anthropic/claude-opus-5"], suggestions);
    }

    [Fact]
    public void A_bare_word_with_no_author_suggests_nothing()
    {
        string[] ids = ["anthropic/claude-opus-5"];

        Assert.Empty(OpenRouterCatalog.Suggest("claude-opus-5", ids));
    }
}
