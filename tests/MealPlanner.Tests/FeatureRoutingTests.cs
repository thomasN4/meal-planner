using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MealPlanner.Tests;

/// <summary>
/// The three features read <c>/settings</c> on every call and go where it says:
/// the CLI's argv carries the household's model and effort, and any other
/// provider gets the feature's own prompt and schema through an
/// <see cref="IApiModelClient"/>. The CLI is never spawned — its half is
/// asserted on <c>CliArguments</c>, the same pure-function split
/// <c>BuildPayload</c> exists for.
/// </summary>
public class FeatureRoutingTests
{
    /// <summary>Stands in for one HTTP provider and records what it was asked.</summary>
    private sealed class FakeApiClient(AiProvider provider, string answer) : IApiModelClient
    {
        public AiProvider Provider { get; } = provider;

        public List<(ModelCall Call, ResolvedModel Model)> Calls { get; } = [];

        public Task<string> CompleteJsonAsync(ModelCall call, ResolvedModel model, CancellationToken ct = default)
        {
            Calls.Add((call, model));
            return Task.FromResult(answer);
        }
    }

    /// <summary>What an API client does when the provider answers with nothing usable.</summary>
    private sealed class FailingApiClient(AiProvider provider) : IApiModelClient
    {
        public AiProvider Provider { get; } = provider;

        public Task<string> CompleteJsonAsync(ModelCall call, ResolvedModel model, CancellationToken ct = default) =>
            throw new InvalidOperationException("The response carried no answer text.");
    }

    // ---- the CLI's argv ----

    public static TheoryData<string> Features => [nameof(AiFeature.Categorization), nameof(AiFeature.RecipeGeneration), nameof(AiFeature.ReceiptScanning)];

    private static IReadOnlyList<string> CliArguments(string feature, ResolvedModel model) => feature switch
    {
        nameof(AiFeature.Categorization) => ClaudeIngredientClassifier.CliArguments(model),
        nameof(AiFeature.RecipeGeneration) => ClaudeRecipeGenerator.CliArguments(model),
        _ => ClaudeReceiptScanner.CliArguments(model),
    };

    [Theory]
    [MemberData(nameof(Features))]
    public void The_cli_is_asked_for_the_chosen_model_and_effort(string feature)
    {
        var arguments = CliArguments(feature, new ResolvedModel(AiProvider.ClaudeCli, "haiku", AiEffort.XHigh, null));

        Assert.Equal("haiku", arguments[arguments.ToList().IndexOf("--model") + 1]);
        Assert.Equal("xhigh", arguments[arguments.ToList().IndexOf("--effort") + 1]);
        // The isolation flags are not negotiable whatever the model.
        Assert.Contains("--strict-mcp-config", arguments);
        Assert.Contains("--json-schema", arguments);
    }

    [Theory]
    [MemberData(nameof(Features))]
    public void No_effort_leaves_the_flag_out_rather_than_sending_it_empty(string feature)
    {
        var arguments = CliArguments(feature, new ResolvedModel(AiProvider.ClaudeCli, "sonnet", null, null));

        Assert.DoesNotContain("--effort", arguments);
        // Still a well-formed flag list: every other flag keeps its value.
        Assert.Equal("sonnet", arguments[arguments.ToList().IndexOf("--model") + 1]);
    }

    // ---- the API path ----

    /// <summary>
    /// Every feature under test points at a CLI that does not exist. The suite
    /// must never spawn <c>claude</c>, and a routing regression here would
    /// otherwise do exactly that on any machine where it is installed — this
    /// way it fails the test instead.
    /// </summary>
    private const string NoCli = "/nonexistent/claude-must-not-run";

    [Fact]
    public async Task Classification_goes_to_the_provider_saved_for_it()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        var settings = harness.NewAiSettingsService();
        await settings.SetApiKeyAsync(AiProvider.OpenAi, "sk-proj-routing-test-0000");
        await settings.SaveFeatureAsync(AiFeature.Categorization, AiProvider.OpenAi, "gpt-5.6-luna", AiEffort.Low);

        var api = new FakeApiClient(
            AiProvider.OpenAi,
            """{"results":[{"index":0,"category":"Dairy"},{"index":1,"category":"Produce"}]}""");
        var classifier = new ClaudeIngredientClassifier(
            Options.Create(new CategorizationOptions { ExecutablePath = NoCli, TimeoutSeconds = 42 }),
            settings,
            [new FakeApiClient(AiProvider.AnthropicApi, "{}"), api],
            new CapturingLogger<ClaudeIngredientClassifier>());

        var categories = await classifier.ClassifyAsync(
            [new ClassificationRequest("milk"), new ClassificationRequest("leek", "for soup")]);

        Assert.Equal([IngredientCategory.Dairy, IngredientCategory.Produce], categories);
        var (call, model) = Assert.Single(api.Calls);
        Assert.Equal("gpt-5.6-luna", model.Model);
        Assert.Equal(AiEffort.Low, model.Effort);
        Assert.Equal("sk-proj-routing-test-0000", model.ApiKey);
        // The same prompt, schema and payload the CLI would have had.
        Assert.Contains("\"results\"", call.ResponseSchema, StringComparison.Ordinal);
        Assert.Equal(
            ClaudeIngredientClassifier.BuildPayload(
                [new ClassificationRequest("milk"), new ClassificationRequest("leek", "for soup")]),
            call.UserText);
        Assert.Equal(TimeSpan.FromSeconds(42), call.Timeout);
        Assert.Null(call.Attachment);
    }

    [Fact]
    public async Task A_save_applies_from_the_next_call_without_a_restart()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        var settings = harness.NewAiSettingsService();
        await settings.SetApiKeyAsync(AiProvider.OpenAi, "sk-proj-routing-test-0000");
        await settings.SaveFeatureAsync(AiFeature.Categorization, AiProvider.OpenAi, "gpt-5.6-luna", null);

        var api = new FakeApiClient(AiProvider.OpenAi, """{"results":[]}""");
        var classifier = new ClaudeIngredientClassifier(
            Options.Create(new CategorizationOptions { ExecutablePath = NoCli }), settings, [api],
            new CapturingLogger<ClaudeIngredientClassifier>());

        await classifier.ClassifyAsync([new ClassificationRequest("salt")]);
        // Another tab saves; the same singleton classifier picks it up.
        await harness.NewAiSettingsService()
            .SaveFeatureAsync(AiFeature.Categorization, AiProvider.OpenAi, "gpt-5.6-terra", null);
        await classifier.ClassifyAsync([new ClassificationRequest("pepper")]);

        Assert.Equal(["gpt-5.6-luna", "gpt-5.6-terra"], api.Calls.Select(c => c.Model.Model));
    }

    [Fact]
    public async Task A_provider_with_no_key_fails_the_batch_quietly_and_sends_nothing()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        var settings = harness.NewAiSettingsService();
        await settings.SaveFeatureAsync(AiFeature.Categorization, AiProvider.OpenAi, "gpt-5.6-luna", null);

        // The real client, so "sends nothing" is about the real code path.
        var http = new FakeHttp("""{"choices":[]}""");
        var log = new CapturingLogger<ClaudeIngredientClassifier>();
        var classifier = new ClaudeIngredientClassifier(
            Options.Create(new CategorizationOptions { ExecutablePath = NoCli }), settings,
            [new OpenAiCompatibleClient(AiProvider.OpenAi, http)], log);

        var categories = await classifier.ClassifyAsync([new ClassificationRequest("salt")]);

        Assert.Equal([null], categories);
        Assert.Null(http.Request);
        var warning = Assert.Single(log.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("No OpenAI API key", warning.Exception!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recipe_generation_goes_to_the_provider_saved_for_it()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        var settings = harness.NewAiSettingsService();
        await settings.SetApiKeyAsync(AiProvider.AnthropicApi, "sk-ant-api03-routing-test");
        await settings.SaveFeatureAsync(
            AiFeature.RecipeGeneration, AiProvider.AnthropicApi, "claude-opus-5", AiEffort.High);

        var api = new FakeApiClient(
            AiProvider.AnthropicApi,
            """
            {"recipes":[{"title":"Rice","description":"Plain","minutes":20,
              "ingredients":[{"name":"rice","quantity":"1 cup","inventoryName":"Rice"}],"steps":["Boil"]}]}
            """);
        var generator = new ClaudeRecipeGenerator(
            Options.Create(new RecipeGenerationOptions { ExecutablePath = NoCli }), settings, [api],
            new CapturingLogger<ClaudeRecipeGenerator>());

        var recipes = await generator.GenerateAsync(
            new RecipeRequest(MealType.Dinner, null, [], [], [], ""),
            [new InventoryItem { Name = "Rice", Quantity = "a bag" }]);

        var recipe = Assert.Single(recipes);
        // Have/missing is still verified against the inventory, whoever answered.
        Assert.True(Assert.Single(recipe.Ingredients).Have);
        var (call, model) = Assert.Single(api.Calls);
        Assert.Equal("claude-opus-5", model.Model);
        Assert.Contains("\"recipes\"", call.ResponseSchema, StringComparison.Ordinal);
        Assert.Contains("Rice", call.UserText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Receipt_scanning_sends_the_file_itself_and_parses_a_bare_answer()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        var settings = harness.NewAiSettingsService();
        await settings.SetApiKeyAsync(AiProvider.OpenRouter, "sk-or-v1-routing-test-00");
        await settings.SaveFeatureAsync(
            AiFeature.ReceiptScanning, AiProvider.OpenRouter, "google/gemini-3.6-flash", AiEffort.Low);

        // Fenced, the way some OpenRouter backends answer even under a schema.
        var api = new FakeApiClient(
            AiProvider.OpenRouter,
            """
            ```json
            {"products":[{"name":"Lait 2%","quantity":"2 L","isFood":true,"department":"Laitiers"},
                         {"name":"Sac","quantity":"","isFood":false,"department":""}]}
            ```
            """);
        var scanner = new ClaudeReceiptScanner(
            Options.Create(new ReceiptScanningOptions { ExecutablePath = NoCli }), settings, [api],
            new CapturingLogger<ClaudeReceiptScanner>());

        var result = await scanner.ScanAsync(new ReceiptFile([9, 8, 7], "image/png", "receipt.png"));

        Assert.Equal(
            [new ScannedLine("Lait 2%", "2 L", true, "Laitiers"), new ScannedLine("Sac", "", false)],
            result.Lines);
        Assert.Null(result.Warning);

        var (call, _) = Assert.Single(api.Calls);
        Assert.Equal("image/png", call.Attachment!.MediaType);
        Assert.Equal([9, 8, 7], call.Attachment.Content);
        Assert.Contains("\"products\"", call.ResponseSchema, StringComparison.Ordinal);
    }

    /// <summary>
    /// The two empty scans the page words differently: a call that never
    /// answered is not the photo's fault, and an answer with nothing on it may be.
    /// </summary>
    [Fact]
    public async Task A_scan_whose_call_failed_says_so_and_an_empty_answer_does_not()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        var settings = harness.NewAiSettingsService();
        await settings.SetApiKeyAsync(AiProvider.OpenRouter, "sk-or-v1-routing-test-00");
        await settings.SaveFeatureAsync(
            AiFeature.ReceiptScanning, AiProvider.OpenRouter, "google/gemini-3.6-flash", null);
        var options = Options.Create(new ReceiptScanningOptions { ExecutablePath = NoCli });
        var file = new ReceiptFile([1], "image/png", "r.png");

        var failed = await new ClaudeReceiptScanner(
                options, settings, [new FailingApiClient(AiProvider.OpenRouter)],
                new CapturingLogger<ClaudeReceiptScanner>())
            .ScanAsync(file);

        Assert.True(failed.Failed);
        Assert.Empty(failed.Lines);

        var empty = await new ClaudeReceiptScanner(
                options, settings, [new FakeApiClient(AiProvider.OpenRouter, """{"products":[]}""")],
                new CapturingLogger<ClaudeReceiptScanner>())
            .ScanAsync(file);

        Assert.False(empty.Failed);
        Assert.Empty(empty.Lines);
    }

    [Fact]
    public async Task A_long_receipt_from_an_api_warns_exactly_as_one_from_the_cli()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        var settings = harness.NewAiSettingsService();
        await settings.SetApiKeyAsync(AiProvider.OpenAi, "sk-proj-routing-test-0000");
        await settings.SaveFeatureAsync(AiFeature.ReceiptScanning, AiProvider.OpenAi, "gpt-5.6-luna", null);

        var products = string.Join(",", Enumerable.Range(1, 5).Select(i =>
            $$"""{"name":"Item {{i}}","quantity":"","isFood":true,"department":""}"""));
        var scanner = new ClaudeReceiptScanner(
            Options.Create(new ReceiptScanningOptions { ExecutablePath = NoCli, MaxLines = 3 }), settings,
            [new FakeApiClient(AiProvider.OpenAi, $$"""{"products":[{{products}}]}""")],
            new CapturingLogger<ClaudeReceiptScanner>());

        var result = await scanner.ScanAsync(new ReceiptFile([1], "application/pdf", "r.pdf"));

        Assert.Equal(3, result.Lines.Count);
        Assert.Contains("first 3", result.Warning, StringComparison.Ordinal);
    }
}
