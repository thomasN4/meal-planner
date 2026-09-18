using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MealPlanner.Tests;

/// <summary>
/// The three HTTP providers, run end to end through <see cref="FakeHttp"/>:
/// what goes on the wire, and what comes back out of a response. No network.
/// </summary>
public class ApiModelClientTests
{
    private const string Schema =
        """{"type":"object","properties":{"products":{"type":"array","minItems":2,"items":{"type":"string"}}},"required":["products"],"additionalProperties":false}""";

    private static readonly ModelAttachment Photo = new([1, 2, 3], "image/jpeg");
    private static readonly ModelAttachment Pdf = new([4, 5, 6], "application/pdf");

    private static ModelCall Call(ModelAttachment? attachment = null) =>
        new("You read receipts.", Schema, "Read this.", attachment, 4000, TimeSpan.FromSeconds(30));

    private static ResolvedModel Model(AiProvider provider, string model, AiEffort? effort) =>
        new(provider, model, effort, "sk-test-key-000000000000");

    private static JsonNode Body(FakeHttp http) => JsonNode.Parse(http.RequestBody!)!;

    // ---- OpenAI / OpenRouter ----

    private static string ChatAnswer(string content, string finish = "stop") =>
        JsonSerializer.Serialize(new
        {
            choices = new[] { new { finish_reason = finish, message = new { role = "assistant", content } } },
        });

    [Fact]
    public async Task OpenAi_gets_a_strict_schema_a_bearer_key_and_top_level_reasoning_effort()
    {
        var http = new FakeHttp(ChatAnswer("""{"products":[]}"""));
        var client = new OpenAiCompatibleClient(AiProvider.OpenAi, http);

        var answer = await client.CompleteJsonAsync(Call(), Model(AiProvider.OpenAi, "gpt-5.6-luna", AiEffort.XHigh));

        Assert.Equal("""{"products":[]}""", answer);
        Assert.Equal("https://api.openai.com/v1/chat/completions", http.Request!.RequestUri!.ToString());
        Assert.Equal("Bearer", http.Request.Headers.Authorization!.Scheme);
        Assert.Equal("sk-test-key-000000000000", http.Request.Headers.Authorization.Parameter);

        var body = Body(http);
        Assert.Equal("gpt-5.6-luna", body["model"]!.GetValue<string>());
        Assert.Equal("xhigh", body["reasoning_effort"]!.GetValue<string>());
        Assert.Null(body["reasoning"]);
        Assert.Equal(4000, body["max_completion_tokens"]!.GetValue<int>());
        Assert.Null(body["max_tokens"]);
        Assert.Null(body["provider"]);

        var format = body["response_format"]!;
        Assert.Equal("json_schema", format["type"]!.GetValue<string>());
        Assert.True(format["json_schema"]!["strict"]!.GetValue<bool>());
        Assert.DoesNotContain("minItems", format.ToJsonString(), StringComparison.Ordinal);

        Assert.Equal("system", body["messages"]![0]!["role"]!.GetValue<string>());
        Assert.Equal("You read receipts.", body["messages"]![0]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task OpenRouter_nests_effort_and_insists_on_a_backend_that_honours_the_schema()
    {
        var http = new FakeHttp(ChatAnswer("{}"));
        var client = new OpenAiCompatibleClient(AiProvider.OpenRouter, http);

        await client.CompleteJsonAsync(Call(), Model(AiProvider.OpenRouter, "google/gemini-3.6-flash", AiEffort.Low));

        Assert.Equal("https://openrouter.ai/api/v1/chat/completions", http.Request!.RequestUri!.ToString());
        var body = Body(http);
        Assert.Equal("low", body["reasoning"]!["effort"]!.GetValue<string>());
        Assert.Null(body["reasoning_effort"]);
        Assert.Equal(4000, body["max_tokens"]!.GetValue<int>());
        Assert.True(body["provider"]!["require_parameters"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData(AiProvider.OpenAi)]
    [InlineData(AiProvider.OpenRouter)]
    public async Task No_effort_sends_no_effort_field_at_all(AiProvider provider)
    {
        var http = new FakeHttp(ChatAnswer("{}"));
        await new OpenAiCompatibleClient(provider, http)
            .CompleteJsonAsync(Call(), Model(provider, "some-model", null));

        var body = Body(http);
        Assert.Null(body["reasoning_effort"]);
        Assert.Null(body["reasoning"]);
    }

    [Fact]
    public async Task A_photo_travels_as_an_image_data_url_ahead_of_the_text()
    {
        var http = new FakeHttp(ChatAnswer("{}"));
        await new OpenAiCompatibleClient(AiProvider.OpenAi, http)
            .CompleteJsonAsync(Call(Photo), Model(AiProvider.OpenAi, "gpt-5.6-luna", null));

        var content = Body(http)["messages"]![1]!["content"]!.AsArray();
        Assert.Equal("image_url", content[0]!["type"]!.GetValue<string>());
        Assert.Equal("data:image/jpeg;base64,AQID", content[0]!["image_url"]!["url"]!.GetValue<string>());
        Assert.Equal("text", content[1]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_pdf_travels_as_a_file_part_not_an_image()
    {
        // Sent as an image_url, a PDF is rejected — which would surface as
        // "the scanner is broken", not as anything about file types.
        var http = new FakeHttp(ChatAnswer("{}"));
        await new OpenAiCompatibleClient(AiProvider.OpenRouter, http)
            .CompleteJsonAsync(Call(Pdf), Model(AiProvider.OpenRouter, "openai/gpt-5.6-sol", null));

        var part = Body(http)["messages"]![1]!["content"]![0]!;
        Assert.Equal("file", part["type"]!.GetValue<string>());
        Assert.Equal("data:application/pdf;base64,BAUG", part["file"]!["file_data"]!.GetValue<string>());
        Assert.EndsWith(".pdf", part["file"]!["filename"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("length")]
    [InlineData("content_filter")]
    public void A_cut_off_or_filtered_answer_throws_instead_of_reaching_a_parser(string finish)
    {
        Assert.Throws<InvalidOperationException>(
            () => OpenAiCompatibleClient.ReadAnswer(ChatAnswer("""{"products":[""", finish)));
    }

    [Fact]
    public void A_refusal_throws()
    {
        var body = """{"choices":[{"finish_reason":"stop","message":{"content":null,"refusal":"I can't help with that."}}]}""";

        var ex = Assert.Throws<InvalidOperationException>(() => OpenAiCompatibleClient.ReadAnswer(body));
        Assert.Contains("refused", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_upstream_error_inside_a_200_throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => OpenAiCompatibleClient.ReadAnswer("""{"error":{"code":502,"message":"upstream exploded"}}"""));
        Assert.Contains("upstream exploded", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_http_error_names_the_status_and_the_providers_message()
    {
        var http = new FakeHttp("""{"error":{"message":"Incorrect API key provided"}}""", HttpStatusCode.Unauthorized);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            new OpenAiCompatibleClient(AiProvider.OpenAi, http)
                .CompleteJsonAsync(Call(), Model(AiProvider.OpenAi, "gpt-5.6-luna", null)));

        Assert.Contains("401", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Incorrect API key", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_key_fails_before_anything_is_sent()
    {
        var http = new FakeHttp(ChatAnswer("{}"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new OpenAiCompatibleClient(AiProvider.OpenAi, http)
                .CompleteJsonAsync(Call(), new ResolvedModel(AiProvider.OpenAi, "gpt-5.6-luna", null, null)));

        Assert.Null(http.Request);
    }

    [Fact]
    public async Task A_slow_provider_times_out_as_a_timeout_not_a_cancellation()
    {
        var http = new FakeHttp(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("unreachable");
        });
        var call = Call() with { Timeout = TimeSpan.FromMilliseconds(50) };

        await Assert.ThrowsAsync<TimeoutException>(() =>
            new OpenAiCompatibleClient(AiProvider.OpenAi, http)
                .CompleteJsonAsync(call, Model(AiProvider.OpenAi, "gpt-5.6-luna", null)));
    }

    // ---- Anthropic ----

    private static string AnthropicAnswer(string text, string stopReason = "end_turn") =>
        JsonSerializer.Serialize(new
        {
            id = "msg_test",
            type = "message",
            role = "assistant",
            model = "claude-sonnet-5",
            content = new[] { new { type = "text", text } },
            stop_reason = stopReason,
            stop_sequence = (string?)null,
            usage = new { input_tokens = 10, output_tokens = 5 },
        });

    [Fact]
    public async Task Anthropic_gets_the_key_header_structured_output_and_nested_effort()
    {
        var http = new FakeHttp(AnthropicAnswer("""{"products":["milk"]}"""));

        var answer = await new AnthropicApiClient(http)
            .CompleteJsonAsync(Call(), Model(AiProvider.AnthropicApi, "claude-sonnet-5", AiEffort.XHigh));

        Assert.Equal("""{"products":["milk"]}""", answer);
        Assert.Equal(AnthropicApiClient.HttpClientName, http.ClientName);
        Assert.Equal("sk-test-key-000000000000", http.Request!.Headers.GetValues("x-api-key").Single());

        var body = Body(http);
        Assert.Equal("claude-sonnet-5", body["model"]!.GetValue<string>());
        Assert.Equal("You read receipts.", body["system"]!.GetValue<string>());
        Assert.Equal("xhigh", body["output_config"]!["effort"]!.GetValue<string>());
        Assert.Equal("json_schema", body["output_config"]!["format"]!["type"]!.GetValue<string>());
        Assert.DoesNotContain("minItems", body["output_config"]!.ToJsonString(), StringComparison.Ordinal);
        // No thinking parameter: current models think adaptively without one,
        // and Fable rejects most explicit settings.
        Assert.Null(body["thinking"]);
    }

    [Fact]
    public async Task Anthropic_with_no_effort_omits_it_rather_than_sending_null()
    {
        // Haiku 4.5 rejects the field outright.
        var http = new FakeHttp(AnthropicAnswer("{}"));

        await new AnthropicApiClient(http)
            .CompleteJsonAsync(Call(), Model(AiProvider.AnthropicApi, "claude-haiku-4-5", null));

        var output = Body(http)["output_config"]!.AsObject();
        Assert.False(output.ContainsKey("effort"));
    }

    [Theory]
    [InlineData("claude-opus-5", true)]
    [InlineData("claude-fable-5", true)]
    [InlineData("claude-fable-5-1", true)]
    [InlineData("claude-sonnet-5", false)]
    [InlineData("claude-haiku-4-5", false)]
    public async Task Refusal_fallback_is_asked_for_only_where_the_server_has_a_default(string model, bool expected)
    {
        var http = new FakeHttp(AnthropicAnswer("{}"));

        await new AnthropicApiClient(http)
            .CompleteJsonAsync(Call(), Model(AiProvider.AnthropicApi, model, null));

        var body = Body(http);
        var beta = http.Request!.Headers.TryGetValues("anthropic-beta", out var values)
            ? string.Join(",", values)
            : string.Empty;

        if (expected)
        {
            Assert.Equal("default", body["fallbacks"]!.GetValue<string>());
            Assert.Contains(AnthropicApiClient.FallbackBeta, beta, StringComparison.Ordinal);
        }
        else
        {
            Assert.Null(body["fallbacks"]);
            Assert.DoesNotContain(AnthropicApiClient.FallbackBeta, beta, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Anthropic_sends_a_photo_as_an_image_block_and_a_pdf_as_a_document()
    {
        var photo = new FakeHttp(AnthropicAnswer("{}"));
        await new AnthropicApiClient(photo)
            .CompleteJsonAsync(Call(Photo), Model(AiProvider.AnthropicApi, "claude-sonnet-5", null));
        var photoBlock = Body(photo)["messages"]![0]!["content"]![0]!;
        Assert.Equal("image", photoBlock["type"]!.GetValue<string>());
        Assert.Equal("image/jpeg", photoBlock["source"]!["media_type"]!.GetValue<string>());
        Assert.Equal("AQID", photoBlock["source"]!["data"]!.GetValue<string>());

        var pdf = new FakeHttp(AnthropicAnswer("{}"));
        await new AnthropicApiClient(pdf)
            .CompleteJsonAsync(Call(Pdf), Model(AiProvider.AnthropicApi, "claude-sonnet-5", null));
        var pdfBlock = Body(pdf)["messages"]![0]!["content"]![0]!;
        Assert.Equal("document", pdfBlock["type"]!.GetValue<string>());
        Assert.Equal("application/pdf", pdfBlock["source"]!["media_type"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("refusal")]
    [InlineData("max_tokens")]
    public async Task Anthropic_refusals_and_cut_off_answers_throw(string stopReason)
    {
        var http = new FakeHttp(AnthropicAnswer("""{"products":[""", stopReason));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new AnthropicApiClient(http)
                .CompleteJsonAsync(Call(), Model(AiProvider.AnthropicApi, "claude-sonnet-5", null)));
    }
}
