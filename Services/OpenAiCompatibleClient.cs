using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MealPlanner.Models;

namespace MealPlanner.Services;

/// <summary>
/// OpenAI and OpenRouter, over Chat Completions — the one request shape both
/// speak, so one class with two registrations instead of two near-copies.
/// <para>
/// Raw <see cref="HttpClient"/> rather than a vendor SDK: OpenRouter has none,
/// the two differ in exactly three fields (<see cref="BuildRequest"/>), and a
/// fake <see cref="HttpMessageHandler"/> tests the whole thing with no network.
/// The Anthropic API is the exception and goes through its SDK
/// (<see cref="AnthropicApiClient"/>).
/// </para>
/// <para>
/// Chat Completions rather than OpenAI's Responses API because it is the shape
/// OpenRouter shares. OpenAI steers reasoning models towards Responses, and its
/// known gap on Chat Completions — function tools plus reasoning on GPT-5.6 — is
/// a tool-calling problem; nothing here sends a tool.
/// </para>
/// </summary>
public sealed class OpenAiCompatibleClient : IApiModelClient
{
    public const string OpenAiHttpClient = "openai";
    public const string OpenRouterHttpClient = "openrouter";

    private static readonly Uri OpenAiEndpoint = new("https://api.openai.com/v1/chat/completions");
    private static readonly Uri OpenRouterEndpoint = new("https://openrouter.ai/api/v1/chat/completions");

    private readonly IHttpClientFactory _http;

    public OpenAiCompatibleClient(AiProvider provider, IHttpClientFactory http)
    {
        if (provider is not (AiProvider.OpenAi or AiProvider.OpenRouter))
        {
            throw new ArgumentException($"{provider} does not speak Chat Completions.", nameof(provider));
        }

        Provider = provider;
        _http = http;
    }

    public AiProvider Provider { get; }

    public async Task<string> CompleteJsonAsync(ModelCall call, ResolvedModel model, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(model.ApiKey))
        {
            throw new InvalidOperationException($"No {AiCatalog.For(Provider).Label} key is stored.");
        }

        var (clientName, endpoint) = Provider == AiProvider.OpenAi
            ? (OpenAiHttpClient, OpenAiEndpoint)
            : (OpenRouterHttpClient, OpenRouterEndpoint);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(BuildRequest(Provider, call, model), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", model.ApiKey);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(call.Timeout);

        try
        {
            var http = _http.CreateClient(clientName);
            using var response = await http.SendAsync(request, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);

            if (!response.IsSuccessStatusCode)
            {
                // The provider's own error message, never the request: the
                // request is a pantry or a receipt, and neither goes to a log.
                throw new HttpRequestException(
                    $"{Provider} answered {(int)response.StatusCode}: {ErrorMessage(body)}",
                    null,
                    response.StatusCode);
            }

            return ReadAnswer(body);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"{Provider} did not answer within {call.Timeout.TotalSeconds:0}s.");
        }
    }

    /// <summary>
    /// The request body. Internal so the provider differences are testable as
    /// JSON rather than through a handler:
    /// <list type="bullet">
    ///   <item>effort — OpenAI's top-level <c>reasoning_effort</c>, OpenRouter's
    ///   <c>reasoning.effort</c>, omitted entirely when null;</item>
    ///   <item>the output cap — OpenAI renamed <c>max_tokens</c> to
    ///   <c>max_completion_tokens</c> for reasoning models; OpenRouter
    ///   normalizes <c>max_tokens</c>;</item>
    ///   <item>OpenRouter's <c>provider.require_parameters</c>, without which a
    ///   route to a backend that ignores <c>response_format</c> would answer in
    ///   prose — the one thing the schema is there to rule out.</item>
    /// </list>
    /// </summary>
    internal static string BuildRequest(AiProvider provider, ModelCall call, ResolvedModel model)
    {
        var userContent = new JsonArray();
        if (call.Attachment is { } file)
        {
            var dataUrl = $"data:{file.MediaType};base64,{Convert.ToBase64String(file.Content)}";
            userContent.Add(file.IsImage
                ? new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject { ["url"] = dataUrl },
                }
                : new JsonObject
                {
                    ["type"] = "file",
                    // Chat Completions takes PDFs only as a file part, and wants a
                    // filename with it. The real one is the household's, and the
                    // model has no use for it.
                    ["file"] = new JsonObject { ["filename"] = "receipt.pdf", ["file_data"] = dataUrl },
                });
        }

        userContent.Add(new JsonObject { ["type"] = "text", ["text"] = call.UserText });

        var body = new JsonObject
        {
            ["model"] = model.Model,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = call.SystemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = userContent },
            },
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = "answer",
                    ["strict"] = true,
                    ["schema"] = StrictSchema.Adapt(call.ResponseSchema),
                },
            },
        };

        var effort = AiEffortSpelling.For(provider, model.Effort);

        if (provider == AiProvider.OpenAi)
        {
            body["max_completion_tokens"] = call.MaxOutputTokens;
            if (effort is not null)
            {
                body["reasoning_effort"] = effort;
            }
        }
        else
        {
            body["max_tokens"] = call.MaxOutputTokens;
            if (effort is not null)
            {
                body["reasoning"] = new JsonObject { ["effort"] = effort };
            }

            body["provider"] = new JsonObject { ["require_parameters"] = true };
        }

        return body.ToJsonString();
    }

    /// <summary>
    /// The answer text out of a 200 response, or an exception saying why there
    /// isn't one. A refusal, a cut-off answer (<c>length</c>) and a filtered one
    /// all throw: each would otherwise reach a parser as half a JSON document and
    /// be reported as "invalid JSON", which names the wrong failure.
    /// </summary>
    internal static string ReadAnswer(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        // OpenRouter can report an upstream failure inside a 200.
        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            throw new InvalidOperationException($"Provider error: {ErrorMessage(body)}");
        }

        if (!root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("No choices in the response.");
        }

        var choice = choices[0];
        var finish = choice.TryGetProperty("finish_reason", out var f) && f.ValueKind == JsonValueKind.String
            ? f.GetString()
            : null;

        if (!choice.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("No message in the response.");
        }

        if (message.TryGetProperty("refusal", out var refusal)
            && refusal.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(refusal.GetString()))
        {
            throw new InvalidOperationException($"The model refused: {Truncate(refusal.GetString()!)}");
        }

        switch (finish)
        {
            case "length":
                throw new InvalidOperationException("The answer was cut off at the output limit.");
            case "content_filter":
                throw new InvalidOperationException("The answer was withheld by a content filter.");
        }

        if (!message.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(content.GetString()))
        {
            throw new InvalidOperationException("The response carried no answer text.");
        }

        return content.GetString()!;
    }

    private static string ErrorMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("message", out var message)
                    && message.ValueKind == JsonValueKind.String)
                {
                    return Truncate(message.GetString()!);
                }

                if (error.ValueKind == JsonValueKind.String)
                {
                    return Truncate(error.GetString()!);
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON — a proxy's HTML page, say. Fall through to the raw text.
        }

        return Truncate(body);
    }

    private static string Truncate(string value) =>
        value.Length <= 300 ? value.Trim() : value[..300].Trim() + "…";
}
