using System.Net;
using System.Net.Http.Headers;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Models;
using MealPlanner.Models;

namespace MealPlanner.Services;

/// <summary>What asking the provider about a key can tell us.</summary>
public enum ApiKeyVerdict
{
    /// <summary>No key to ask about. Not a mistake, and not worth a sentence.</summary>
    Blank,

    /// <summary>The provider authenticated the key. See <see cref="ApiKeyCheck"/> on what that is not.</summary>
    Valid,

    /// <summary>The provider answered 401. The key will not work.</summary>
    Rejected,

    /// <summary>We could not ask. Deliberately distinct from <see cref="Rejected"/>.</summary>
    Unchecked,
}

/// <summary>
/// One provider's answer about one key.
/// <para>
/// <b>Not one member is a string, and that is the point.</b>
/// <see cref="ResolvedModel"/> needs a <c>PrintMembers</c> override to keep key
/// material out of its generated <c>ToString</c>; this record needs nothing, because
/// there is no member a key, a provider's error prose or an exception message could
/// land in. Adding a <c>string? Detail</c> fed from a response body is the change that
/// breaks that — see <see cref="ApiKeyChecker"/> on why the body is never read.
/// </para>
/// <para>
/// <b><see cref="ApiKeyVerdict.Valid"/> means authenticated, not usable.</b> These are
/// auth-only endpoints: they say the provider knows this key, not that it can pay, nor
/// that the key's scope covers the calls this app makes. The page says "accepted" for
/// that reason, never "works".
/// </para>
/// </summary>
public sealed record ApiKeyCheck(ApiKeyVerdict Verdict, AiProvider Provider, int? Status)
{
    public static ApiKeyCheck Blank(AiProvider provider) => new(ApiKeyVerdict.Blank, provider, null);

    public static ApiKeyCheck Valid(AiProvider provider) => new(ApiKeyVerdict.Valid, provider, null);

    public static ApiKeyCheck Rejected(AiProvider provider, int status) =>
        new(ApiKeyVerdict.Rejected, provider, status);

    public static ApiKeyCheck Unchecked(AiProvider provider, int? status = null) =>
        new(ApiKeyVerdict.Unchecked, provider, status);
}

/// <summary>
/// Answers "does this provider accept this key", for a key the page is holding or for
/// one already stored.
/// </summary>
public interface IApiKeyChecker
{
    /// <summary>Checks a key the caller holds — on this page, one the user just pasted.</summary>
    Task<ApiKeyCheck> CheckKeyAsync(AiProvider provider, string? apiKey, CancellationToken ct = default);

    /// <summary>
    /// Checks the key already in the database. The key is read and used inside the
    /// service and only a verdict comes back, so a page can ask the question without
    /// ever holding the answer's subject.
    /// </summary>
    Task<ApiKeyCheck> CheckStoredAsync(AiProvider provider, CancellationToken ct = default);
}

/// <summary>
/// The choke point for "is this key any good", shaped like
/// <see cref="OpenRouterCatalog"/> next door: an interface, records out, and one place
/// the decisions live. Each provider is asked on its cheapest **auth-only** endpoint —
/// no tokens are billed and no model runs.
/// <para>
/// <b>Two methods, because of the page boundary.</b>
/// <see cref="AiSettingsService.GetApiKeyAsync"/> is documented "not for a page", and
/// <see cref="CredentialStatus"/> has no key field so that a page cannot render one by
/// accident. <see cref="CheckStoredAsync"/> is what keeps both true while still letting
/// /settings ask about a stored key: the read happens here. A single key-taking method
/// would have forced the page to fetch the key first.
/// </para>
/// <para>
/// <b>The response body is never read — only the status code.</b> The obvious tidy-up
/// is to reuse <c>OpenAiCompatibleClient.ErrorMessage</c> and put the provider's own
/// sentence on screen, and that is the leak: OpenAI's 401 body echoes the key back in
/// masked form (<c>Incorrect API key provided: sk-pr***XYZ</c>), and a short key is
/// barely masked. Reading nothing means no provider prose can reach a verdict, a log
/// line or the DOM, and it is one assertion to pin.
/// </para>
/// <para>
/// <b>Only 401 is a rejection.</b> 403 is usually a region block, an org permission or
/// a project-scoped key, and 429 is the provider being busy — neither says the key is
/// wrong, and both arrive as <see cref="ApiKeyVerdict.Unchecked"/> carrying their
/// status. The catalogue keeps "we could not ask" apart from "no" for the same reason;
/// here it matters more, because a confident wrong "refused" invites someone to delete
/// a working key.
/// </para>
/// <para>
/// <b>No cache, no TTL, no gate</b> — deliberately unlike the catalogue next door.
/// There is nothing to coalesce: one check is one deliberate click, on a button that
/// disables itself while in flight. A cached "accepted" would also be a lie waiting to
/// happen, since the question is whether the key works <i>now</i>; and the cache key
/// would have to be either the provider (wrong the moment a key is pasted over another)
/// or the key itself, which parks a secret in a long-lived singleton.
/// </para>
/// </summary>
public sealed class ApiKeyChecker : IApiKeyChecker
{
    private static readonly Uri OpenAiModels = new("https://api.openai.com/v1/models");

    /// <summary>
    /// OpenRouter's own "tell me about this key" route, which is what it is for. The
    /// public model list is no use here: it answers the same to everybody.
    /// </summary>
    private static readonly Uri OpenRouterKey = new("https://openrouter.ai/api/v1/key");

    /// <summary>
    /// The page's clock, not a feature's 90/180/120s. Somebody is waiting on this with
    /// a hand still on the keyboard.
    /// </summary>
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(10);

    private readonly IHttpClientFactory _http;
    private readonly AiSettingsService _settings;
    private readonly ILogger<ApiKeyChecker> _logger;

    public ApiKeyChecker(IHttpClientFactory http, AiSettingsService settings, ILogger<ApiKeyChecker> logger)
    {
        _http = http;
        _settings = settings;
        _logger = logger;
    }

    public Task<ApiKeyCheck> CheckKeyAsync(AiProvider provider, string? apiKey, CancellationToken ct = default)
    {
        Guard(provider);
        return AskAsync(provider, apiKey?.Trim(), ct);
    }

    public async Task<ApiKeyCheck> CheckStoredAsync(AiProvider provider, CancellationToken ct = default)
    {
        Guard(provider);

        // The one place a stored key is read for this feature, and it never leaves
        // this method: what goes back to the caller is a verdict.
        var key = await _settings.GetApiKeyAsync(provider, ct);
        return await AskAsync(provider, key?.Trim(), ct);
    }

    /// <summary>
    /// The page renders a chip only for a provider that takes a key, so anything else
    /// arriving here is a programming error — the same answer
    /// <see cref="AiSettingsService.SetApiKeyAsync"/> gives.
    /// </summary>
    private static void Guard(AiProvider provider)
    {
        if (!Enum.IsDefined(provider) || !AiCatalog.For(provider).NeedsApiKey)
        {
            throw new ArgumentException($"{provider} takes no API key.", nameof(provider));
        }
    }

    private async Task<ApiKeyCheck> AskAsync(AiProvider provider, string? key, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(key))
        {
            // No request at all. On the Anthropic path this is load-bearing rather
            // than tidy: the SDK resolves ANTHROPIC_API_KEY, ANTHROPIC_AUTH_TOKEN and
            // ANTHROPIC_PROFILE from the environment when it is given no key, so a
            // blank one would check *this machine's* credentials and report a key
            // nobody stored as accepted.
            return ApiKeyCheck.Blank(provider);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CheckTimeout);

        try
        {
            return provider == AiProvider.AnthropicApi
                ? await AskAnthropicAsync(key, timeout.Token)
                : await AskBearerAsync(provider, key, timeout.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller gave up — a newer key, or the circuit going away.
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            // No network, DNS gone, or our own 10s clock. Not a word about the key.
            _logger.LogWarning(ex, "Could not check the {Provider} API key", provider);
            return ApiKeyCheck.Unchecked(provider);
        }
    }

    /// <summary>
    /// OpenAI and OpenRouter, both <c>Authorization: Bearer</c> on a GET, both on the
    /// named client their chat-completions calls already use — a borrowed client rather
    /// than a fourth registration, as the catalogue borrows one.
    /// </summary>
    private async Task<ApiKeyCheck> AskBearerAsync(AiProvider provider, string key, CancellationToken ct)
    {
        var (clientName, endpoint) = provider == AiProvider.OpenAi
            ? (OpenAiCompatibleClient.OpenAiHttpClient, OpenAiModels)
            : (OpenAiCompatibleClient.OpenRouterHttpClient, OpenRouterKey);

        var client = _http.CreateClient(clientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        // ResponseHeadersRead: the status is the whole answer, and the body is the one
        // thing this class must not look at.
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        return FromStatus(provider, (int)response.StatusCode, response.IsSuccessStatusCode);
    }

    /// <summary>
    /// Anthropic through the official SDK, for <see cref="AnthropicApiClient"/>'s
    /// reason: an official C# SDK exists, so it is what the app uses. The SDK's typed
    /// exceptions discriminate 401 from 429 from 500 better than reading a status off a
    /// raw response would, and it owns the <c>anthropic-version</c> header, which a
    /// hand-rolled ping would pin to a date string in our code and let silently age.
    /// </summary>
    private async Task<ApiKeyCheck> AskAnthropicAsync(string key, CancellationToken ct)
    {
        var client = new AnthropicClient
        {
            ApiKey = key,
            HttpClient = _http.CreateClient(AnthropicApiClient.HttpClientName),
            Timeout = CheckTimeout,
            // One attempt. The SDK's default retries would turn one click into
            // several failed authentications against a provider that rate-limits
            // them — the hazard that also ruled out checking on every keystroke.
            MaxRetries = 0,
        };

        try
        {
            // Nobody wants the models; Limit = 1 says so. The 200 is the answer.
            await client.Models.List(new ModelListParams { Limit = 1 }, ct);
            return ApiKeyCheck.Valid(AiProvider.AnthropicApi);
        }
        catch (AnthropicUnauthorizedException)
        {
            return ApiKeyCheck.Rejected(AiProvider.AnthropicApi, (int)HttpStatusCode.Unauthorized);
        }
        catch (AnthropicForbiddenException)
        {
            return Refused(AiProvider.AnthropicApi, (int)HttpStatusCode.Forbidden);
        }
        catch (AnthropicRateLimitException)
        {
            return Refused(AiProvider.AnthropicApi, (int)HttpStatusCode.TooManyRequests);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Anything else at all, and it has to be anything: AnthropicIOException
            // (a transport fault) is not an AnthropicApiException, and the SDK's own
            // error-body reader throws InvalidOperationException when something that
            // is not Anthropic answers — a proxy, a captive portal, a gateway. Every
            // one of those means "we could not ask", which is what Unchecked is for.
            // Cancellation is excluded so the caller's own token still rethrows
            // upstairs. The type is what we read, never the message: it can quote the
            // provider's error document.
            _logger.LogWarning(ex, "Could not check the {Provider} API key", AiProvider.AnthropicApi);
            return ApiKeyCheck.Unchecked(AiProvider.AnthropicApi);
        }
    }

    private ApiKeyCheck FromStatus(AiProvider provider, int status, bool succeeded)
    {
        if (succeeded)
        {
            return ApiKeyCheck.Valid(provider);
        }

        return status == (int)HttpStatusCode.Unauthorized
            ? ApiKeyCheck.Rejected(provider, status)
            : Refused(provider, status);
    }

    /// <summary>
    /// A non-2xx that is not a 401. The status travels so the page can name it, and
    /// the verdict is Unchecked so nobody is told their key is wrong on the strength
    /// of a 429 or a bad afternoon at the provider.
    /// </summary>
    private ApiKeyCheck Refused(AiProvider provider, int status)
    {
        _logger.LogWarning("The {Provider} key check answered {Status}", provider, status);
        return ApiKeyCheck.Unchecked(provider, status);
    }
}
