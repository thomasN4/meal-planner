using System.Net;
using MealPlanner.Models;
using MealPlanner.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace MealPlanner.Tests;

/// <summary>
/// The API key check, run end to end through <see cref="FakeHttp"/> — the Anthropic
/// path included, since the SDK takes the app's own <see cref="HttpClient"/>. No
/// network, and no key here is real.
/// </summary>
public class ApiKeyCheckerTests
{
    private const string Key = "sk-test-key-000000000000";

    /// <summary>
    /// Shaped the way Anthropic answers an error, because the SDK reads the body to
    /// decide which exception to throw. The other two providers never have their body
    /// read at all — see <see cref="The_providers_own_error_text_never_leaves_the_service"/>.
    /// </summary>
    private const string ErrorBody =
        """{"type":"error","error":{"type":"permission_error","message":"nope"}}""";

    /// <summary>
    /// Every provider that takes one. ClaudeCli has no key and no endpoint, which
    /// <see cref="The_CLI_takes_no_key_and_cannot_be_checked"/> covers instead.
    /// </summary>
    public static TheoryData<AiProvider> Providers =>
        [AiProvider.AnthropicApi, AiProvider.OpenAi, AiProvider.OpenRouter];

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task A_key_the_provider_accepts_comes_back_valid(AiProvider provider)
    {
        await using var harness = await InventoryHarness.CreateAsync();
        var http = new FakeHttp("""{"data":[]}""");

        var check = await Checker(harness, http).CheckKeyAsync(provider, Key);

        Assert.Equal(ApiKeyVerdict.Valid, check.Verdict);
        Assert.Equal(provider, check.Provider);
        Assert.Null(check.Status);
    }

    /// <summary>
    /// A check pointed at a completions endpoint would run a model and bill tokens
    /// for a question about a credential. The URL is the guard.
    /// </summary>
    [Theory]
    [InlineData(AiProvider.AnthropicApi, "https://api.anthropic.com/v1/models", "anthropic")]
    [InlineData(AiProvider.OpenAi, "https://api.openai.com/v1/models", "openai")]
    [InlineData(AiProvider.OpenRouter, "https://openrouter.ai/api/v1/key", "openrouter")]
    public async Task Each_provider_is_asked_on_its_own_auth_only_endpoint(
        AiProvider provider,
        string endpoint,
        string clientName)
    {
        await using var harness = await InventoryHarness.CreateAsync();
        var http = new FakeHttp("""{"data":[]}""");

        await Checker(harness, http).CheckKeyAsync(provider, Key);

        Assert.Equal(HttpMethod.Get, http.Request!.Method);
        Assert.StartsWith(endpoint, http.Request.RequestUri!.ToString(), StringComparison.Ordinal);
        // Borrowed named clients, never a fourth registration.
        Assert.Equal(clientName, http.ClientName);
    }

    [Fact]
    public async Task Anthropic_sends_the_key_as_x_api_key_with_a_version_header()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        var http = new FakeHttp("""{"data":[]}""");

        await Checker(harness, http).CheckKeyAsync(AiProvider.AnthropicApi, Key);

        Assert.Equal(Key, http.Request!.Headers.GetValues("x-api-key").Single());
        // Present, never its value: the version string is the SDK's to bump.
        Assert.True(http.Request.Headers.Contains("anthropic-version"));
        Assert.Null(http.Request.Headers.Authorization);
    }

    [Theory]
    [InlineData(AiProvider.OpenAi)]
    [InlineData(AiProvider.OpenRouter)]
    public async Task OpenAi_and_OpenRouter_send_the_key_as_a_bearer_token(AiProvider provider)
    {
        await using var harness = await InventoryHarness.CreateAsync();
        var http = new FakeHttp("""{"data":[]}""");

        await Checker(harness, http).CheckKeyAsync(provider, Key);

        Assert.Equal("Bearer", http.Request!.Headers.Authorization!.Scheme);
        Assert.Equal(Key, http.Request.Headers.Authorization.Parameter);
        Assert.False(http.Request.Headers.Contains("x-api-key"));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task A_401_is_a_rejection(AiProvider provider)
    {
        await using var harness = await InventoryHarness.CreateAsync();
        var http = new FakeHttp(ErrorBody, HttpStatusCode.Unauthorized);

        var check = await Checker(harness, http).CheckKeyAsync(provider, Key);

        Assert.Equal(ApiKeyVerdict.Rejected, check.Verdict);
        Assert.Equal(401, check.Status);
    }

    /// <summary>
    /// The distinction the third verdict exists for. Folding every non-2xx into a
    /// rejection tells somebody their key is wrong on the strength of the provider
    /// having a bad afternoon — and invites them to delete a working one.
    /// </summary>
    [Theory]
    [MemberData(nameof(Providers))]
    public async Task A_500_is_unchecked_rather_than_a_rejection(AiProvider provider)
    {
        await using var harness = await InventoryHarness.CreateAsync();
        var http = new FakeHttp(ErrorBody, HttpStatusCode.InternalServerError);

        var check = await Checker(harness, http).CheckKeyAsync(provider, Key);

        Assert.Equal(ApiKeyVerdict.Unchecked, check.Verdict);
    }

    /// <summary>
    /// 429 says the provider is busy, never that the key is bad — and it is likeliest
    /// exactly when the household is using the app hardest. The status travels so the
    /// page can say which of the two happened.
    /// </summary>
    [Theory]
    [MemberData(nameof(Providers))]
    public async Task A_rate_limit_is_unchecked_and_keeps_its_status(AiProvider provider)
    {
        await using var harness = await InventoryHarness.CreateAsync();
        var http = new FakeHttp(ErrorBody, HttpStatusCode.TooManyRequests);

        var check = await Checker(harness, http).CheckKeyAsync(provider, Key);

        Assert.Equal(ApiKeyVerdict.Unchecked, check.Verdict);
        Assert.Equal(429, check.Status);
    }

    /// <summary>
    /// 403 is a region block, an org permission or a project-scoped key far more often
    /// than it is a bad key, so it joins 429 rather than 401.
    /// </summary>
    [Theory]
    [MemberData(nameof(Providers))]
    public async Task A_403_is_unchecked_rather_than_a_rejection(AiProvider provider)
    {
        await using var harness = await InventoryHarness.CreateAsync();
        var http = new FakeHttp(ErrorBody, HttpStatusCode.Forbidden);

        var check = await Checker(harness, http).CheckKeyAsync(provider, Key);

        Assert.Equal(ApiKeyVerdict.Unchecked, check.Verdict);
        Assert.Equal(403, check.Status);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task A_network_failure_is_unchecked_rather_than_thrown(AiProvider provider)
    {
        await using var harness = await InventoryHarness.CreateAsync();
        var http = new FakeHttp((_, _) => throw new HttpRequestException("no route to host"));

        var check = await Checker(harness, http).CheckKeyAsync(provider, Key);

        Assert.Equal(ApiKeyVerdict.Unchecked, check.Verdict);
        Assert.Null(check.Status);
    }

    /// <summary>
    /// Found by a test body the SDK did not expect: its error-body reader throws
    /// <see cref="InvalidOperationException"/> when <c>error</c> is a string rather
    /// than an object, which is what something that is not Anthropic answering —
    /// a proxy, a captive portal, a gateway — looks like from here. That is "we could
    /// not ask", not a rejection, and it must not escape a fire-and-forget check.
    /// </summary>
    [Fact]
    public async Task An_error_body_the_SDK_cannot_read_is_unchecked_rather_than_thrown()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        var http = new FakeHttp("""<html><body>502 Bad Gateway</body></html>""", HttpStatusCode.BadGateway);

        var check = await Checker(harness, http).CheckKeyAsync(AiProvider.AnthropicApi, Key);

        Assert.Equal(ApiKeyVerdict.Unchecked, check.Verdict);
    }

    /// <summary>
    /// The page cancels on a newer key and on disposal. Reporting that as a verdict
    /// would repaint a line about a question nobody is waiting for.
    /// </summary>
    [Fact]
    public async Task Caller_cancellation_is_rethrown_rather_than_reported_as_unchecked()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        using var cts = new CancellationTokenSource();

        // Handed the request's own token, which is linked to ours: a responder
        // waiting on anything else hangs the run instead of failing it.
        var http = new FakeHttp(async (_, token) =>
        {
            await cts.CancelAsync();
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Checker(harness, http).CheckKeyAsync(AiProvider.OpenAi, Key, cts.Token));
    }

    /// <summary>
    /// The leak this class is shaped against: OpenAI's 401 echoes the key back in
    /// masked form, so the body is never read and the verdict has no member it could
    /// land in. Reusing <c>OpenAiCompatibleClient.ErrorMessage</c> here is the obvious
    /// tidy-up and is what this forbids.
    /// </summary>
    [Fact]
    public async Task The_providers_own_error_text_never_leaves_the_service()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        var http = new FakeHttp(
            """{"error":{"message":"Incorrect API key provided: sk-te***0000","code":"invalid_api_key"}}""",
            HttpStatusCode.Unauthorized);

        var check = await Checker(harness, http).CheckKeyAsync(AiProvider.OpenAi, Key);

        var rendered = check.ToString();
        Assert.DoesNotContain("Incorrect API key", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-te", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// The whole reason the interface has two methods: /settings can ask about a
    /// stored key without ever holding one, because the read happens in here.
    /// </summary>
    [Fact]
    public async Task The_stored_key_is_read_inside_the_service()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        await harness.NewAiSettingsService().SetApiKeyAsync(AiProvider.OpenRouter, "sk-or-v1-stored-9999");
        var http = new FakeHttp("""{"data":{"label":"test"}}""");

        var check = await Checker(harness, http).CheckStoredAsync(AiProvider.OpenRouter);

        Assert.Equal(ApiKeyVerdict.Valid, check.Verdict);
        Assert.Equal("sk-or-v1-stored-9999", http.Request!.Headers.Authorization!.Parameter);
    }

    /// <summary>
    /// Load-bearing rather than tidy on the Anthropic path: given no key the SDK
    /// resolves one from the environment (ANTHROPIC_API_KEY and friends), so a blank
    /// key would check this machine's credentials and report a key nobody stored as
    /// accepted.
    /// </summary>
    [Theory]
    [MemberData(nameof(Providers))]
    public async Task A_provider_with_no_stored_key_asks_nothing_of_the_network(AiProvider provider)
    {
        await using var harness = await InventoryHarness.CreateAsync();
        var http = new FakeHttp("""{"data":[]}""");

        var check = await Checker(harness, http).CheckStoredAsync(provider);

        Assert.Equal(ApiKeyVerdict.Blank, check.Verdict);
        Assert.Null(http.Request);
    }

    [Fact]
    public async Task A_blank_pasted_key_asks_nothing_of_the_network()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        var http = new FakeHttp("""{"data":[]}""");

        var check = await Checker(harness, http).CheckKeyAsync(AiProvider.OpenAi, "   ");

        Assert.Equal(ApiKeyVerdict.Blank, check.Verdict);
        Assert.Null(http.Request);
    }

    [Fact]
    public async Task The_CLI_takes_no_key_and_cannot_be_checked()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        var http = new FakeHttp("""{"data":[]}""");

        await Assert.ThrowsAsync<ArgumentException>(
            () => Checker(harness, http).CheckKeyAsync(AiProvider.ClaudeCli, Key));
        Assert.Null(http.Request);
    }

    // The 10s timeout is not tested, for the catalogue's reason: it would cost the
    // suite ten seconds to assert a constant.
    private static ApiKeyChecker Checker(InventoryHarness harness, FakeHttp http) =>
        new(http, harness.NewAiSettingsService(), NullLogger<ApiKeyChecker>.Instance);
}
