using System.Text.Json;
using Anthropic;
using Anthropic.Models.Beta.Messages;
using MealPlanner.Models;

namespace MealPlanner.Services;

/// <summary>
/// The Anthropic Messages API, through the official <c>Anthropic</c> SDK.
/// <para>
/// The SDK rather than raw HTTP (unlike <see cref="OpenAiCompatibleClient"/>)
/// because an official C# SDK exists and the <c>claude-api</c> guidance is to use
/// it whenever one does. It still gets the app's own <see cref="HttpClient"/> via
/// <see cref="IHttpClientFactory"/>, which is also what lets a test run the whole
/// request through a fake handler.
/// </para>
/// <para>
/// Three things decided rather than defaulted:
/// <list type="bullet">
///   <item><strong>no <c>thinking</c> parameter.</strong> Opus 5, Sonnet 5 and the
///   Fable models think adaptively when it is omitted, Fable 400s on most explicit
///   settings, and Haiku 4.5 simply doesn't think. Effort is the one knob, and it
///   is omitted when null because Haiku 4.5 rejects it;</item>
///   <item><strong>server-side refusal fallback</strong> on the models that have a
///   default fallback configuration (<see cref="UsesRefusalFallback"/>). Without it
///   a policy decline ends the call; with it the server re-serves the request on
///   its chosen substitute inside the same call. A pantry list is an unlikely
///   refusal, but a photo of a receipt is an image the household did not write;</item>
///   <item><strong>a refusal or a cut-off answer throws</strong> rather than
///   reaching a parser as half a document, for the reason
///   <see cref="OpenAiCompatibleClient.ReadAnswer"/> gives.</item>
/// </list>
/// </para>
/// </summary>
public sealed class AnthropicApiClient : IApiModelClient
{
    public const string HttpClientName = "anthropic";

    /// <summary>The beta that enables <c>fallbacks: "default"</c>.</summary>
    internal const string FallbackBeta = "server-side-fallback-2026-07-01";

    private readonly IHttpClientFactory _http;

    public AnthropicApiClient(IHttpClientFactory http)
    {
        _http = http;
    }

    public AiProvider Provider => AiProvider.AnthropicApi;

    public async Task<string> CompleteJsonAsync(ModelCall call, ResolvedModel model, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(model.ApiKey))
        {
            throw new InvalidOperationException("No Anthropic API key is stored.");
        }

        var client = new AnthropicClient
        {
            ApiKey = model.ApiKey,
            HttpClient = _http.CreateClient(HttpClientName),
            // The feature's own timeout is the one that counts, and one attempt
            // is the whole budget: the SDK's default retries would stack its
            // timeouts past the one the household configured.
            Timeout = call.Timeout,
            MaxRetries = 0,
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(call.Timeout);

        try
        {
            var message = await client.Beta.Messages.Create(BuildParams(call, model), timeout.Token);
            return ReadAnswer(
                message.StopReason?.Raw(),
                message.Content.Select(block => block.TryPickText(out var text) ? text.Text : null));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The Anthropic API did not answer within {call.Timeout.TotalSeconds:0}s.");
        }
    }

    internal static MessageCreateParams BuildParams(ModelCall call, ResolvedModel model)
    {
        var content = new List<BetaContentBlockParam>();
        if (call.Attachment is { } file)
        {
            var data = Convert.ToBase64String(file.Content);
            content.Add(file.IsImage
                ? new BetaImageBlockParam
                {
                    Source = new BetaBase64ImageSource { Data = data, MediaType = file.MediaType },
                }
                : new BetaRequestDocumentBlock { Source = new BetaBase64PdfSource { Data = data } });
        }

        content.Add(new BetaTextBlockParam { Text = call.UserText });

        var schema = StrictSchema.Adapt(call.ResponseSchema)
            .ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value));

        var format = new BetaJsonOutputFormat { Schema = schema };
        var effort = AiEffortSpelling.For(AiProvider.AnthropicApi, model.Effort);

        // The SDK's request types are init-only, and an omitted optional is not
        // the same request as one set to null — so each optional is either in
        // an initializer or absent, never assigned afterwards.
        var output = effort is null
            ? new BetaOutputConfig { Format = format }
            : new BetaOutputConfig { Format = format, Effort = effort };
        List<BetaMessageParam> messages = [new BetaMessageParam { Role = Role.User, Content = content }];

        return UsesRefusalFallback(model.Model)
            ? new MessageCreateParams
            {
                Model = model.Model,
                MaxTokens = call.MaxOutputTokens,
                System = call.SystemPrompt,
                Messages = messages,
                OutputConfig = output,
                Betas = [FallbackBeta],
                // The string "default": the server's own configuration for
                // this model, so there is no fallback list here to go stale.
                Fallbacks = new Default(),
            }
            : new MessageCreateParams
            {
                Model = model.Model,
                MaxTokens = call.MaxOutputTokens,
                System = call.SystemPrompt,
                Messages = messages,
                OutputConfig = output,
            };
    }

    /// <summary>
    /// The models the <c>claude-api</c> guidance names for a default fallback:
    /// Opus 5 and the Fable family. Prefix-matched on purpose — a typed-in
    /// <c>claude-fable-5-1</c> gets it too — and nothing else does, because a
    /// fallback the server has no configuration for is a 400 on every call.
    /// </summary>
    internal static bool UsesRefusalFallback(string model) =>
        model.StartsWith("claude-opus-5", StringComparison.Ordinal)
        || model.StartsWith("claude-fable-5", StringComparison.Ordinal);

    /// <summary>
    /// The answer out of a finished message. Structured output arrives as a
    /// single text block holding the JSON; anything else in the content (a
    /// thinking block, a fallback marker) is skipped.
    /// </summary>
    internal static string ReadAnswer(string? stopReason, IEnumerable<string?> texts)
    {
        switch (stopReason)
        {
            case "refusal":
                throw new InvalidOperationException("The model declined the request.");
            case "max_tokens":
                throw new InvalidOperationException("The answer was cut off at the output limit.");
        }

        return texts.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t))
            ?? throw new InvalidOperationException("The response carried no answer text.");
    }
}
