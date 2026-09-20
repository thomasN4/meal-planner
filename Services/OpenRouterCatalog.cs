using System.Text.Json;

namespace MealPlanner.Services;

/// <summary>What the catalogue can say about a typed model id.</summary>
public enum ModelIdVerdict
{
    /// <summary>Nothing typed yet. Not a mistake, and not worth a sentence.</summary>
    Blank,

    /// <summary>OpenRouter lists it. The capability flags say whether it is usable here.</summary>
    Listed,

    /// <summary>OpenRouter's list does not hold it.</summary>
    NotListed,

    /// <summary>We could not ask. Deliberately distinct from <see cref="NotListed"/>.</summary>
    Unchecked,
}

/// <param name="Name">OpenRouter's own display name, e.g. "Anthropic: Claude Opus 5". Null unless Listed.</param>
/// <param name="SupportsStructuredOutput">
/// Every feature here sends a JSON schema, so this is the flag that decides whether an
/// id that exists will actually work. Without it the answer comes back as prose.
/// </param>
/// <param name="Suggestions">At most three, and empty unless <see cref="ModelIdVerdict.NotListed"/>.</param>
public sealed record ModelIdCheck(
    ModelIdVerdict Verdict,
    string Id,
    string? Name,
    bool SupportsStructuredOutput,
    bool AcceptsImages,
    bool AcceptsFiles,
    bool SupportsEffort,
    IReadOnlyList<string> Suggestions)
{
    public static ModelIdCheck Blank { get; } = Empty(ModelIdVerdict.Blank, string.Empty);

    public static ModelIdCheck Unchecked(string id) => Empty(ModelIdVerdict.Unchecked, id);

    private static ModelIdCheck Empty(ModelIdVerdict verdict, string id) =>
        new(verdict, id, null, false, false, false, false, []);
}

/// <summary>
/// Answers "does OpenRouter list this id, and can it do what we would ask of it".
/// </summary>
public interface IOpenRouterCatalog
{
    Task<ModelIdCheck> CheckAsync(string? modelId, CancellationToken ct = default);
}

/// <summary>
/// The choke point for OpenRouter's public model list, in the shape
/// <see cref="AiSettingsService"/> and <see cref="RecipeService"/> already use: an
/// interface, records out, and one place the decisions live.
/// <para>
/// <b>OpenRouter only, and that is not an oversight.</b> Its
/// <c>GET /api/v1/models</c> is public and unauthenticated; OpenAI's and Anthropic's
/// both need a key, so no equivalent check exists for their "Other…" boxes and the
/// page's "Nothing checks it." help text stays honest there.
/// </para>
/// <para>
/// <b>No Authorization header, on purpose.</b> The whole value of this is that it
/// works <i>before</i> a key is stored — which is exactly when someone is setting a
/// feature up and most likely to mistype. Sending a key would also make a failed
/// check ambiguous between "bad id" and "bad key", which is the one thing the
/// verdicts exist to keep apart.
/// </para>
/// <para>
/// <b>The whole list rather than <c>/models/{id}/endpoints</c>.</b> The per-id route
/// is cheaper and answers 200/404 cleanly, but it can only ever say <i>no</i>. The
/// list is what makes "did you mean…" possible, and the capability flags come with
/// it. One fetch, cached for <see cref="Ttl"/>, costs less than a request per
/// keystroke.
/// </para>
/// <para>
/// <b>A failed fetch keeps the last good snapshot</b> rather than degrading a working
/// check into <see cref="ModelIdVerdict.Unchecked"/> on one flaky request. Only a cold
/// cache reports Unchecked — a stale answer about a catalogue that changes weekly beats
/// no answer at all.
/// </para>
/// </summary>
public sealed class OpenRouterCatalog : IOpenRouterCatalog
{
    private static readonly Uri ModelsEndpoint = new("https://openrouter.ai/api/v1/models");

    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The page's timeout, not the feature's (90/180/120s). This runs while someone is
    /// typing; waiting three minutes to say a word about a typo is the same as saying
    /// nothing.
    /// </summary>
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(10);

    private readonly IHttpClientFactory _http;
    private readonly ILogger<OpenRouterCatalog> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Snapshot? _snapshot;

    public OpenRouterCatalog(IHttpClientFactory http, ILogger<OpenRouterCatalog> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<ModelIdCheck> CheckAsync(string? modelId, CancellationToken ct = default)
    {
        var id = modelId?.Trim() ?? string.Empty;
        if (id.Length == 0)
        {
            // No request at all: an empty box is not a question.
            return ModelIdCheck.Blank;
        }

        var snapshot = await SnapshotAsync(ct);
        if (snapshot is null)
        {
            return ModelIdCheck.Unchecked(id);
        }

        return snapshot.ById.TryGetValue(id, out var entry)
            ? new ModelIdCheck(
                ModelIdVerdict.Listed,
                entry.Id,
                entry.Name,
                entry.StructuredOutput,
                entry.Images,
                entry.Files,
                entry.Effort,
                [])
            : new ModelIdCheck(
                ModelIdVerdict.NotListed,
                id,
                null,
                false,
                false,
                false,
                false,
                Suggest(id, snapshot.Ids));
    }

    private async Task<Snapshot?> SnapshotAsync(CancellationToken ct)
    {
        if (Fresh(_snapshot))
        {
            return _snapshot;
        }

        await _gate.WaitAsync(ct);
        try
        {
            // Another caller may have refreshed it while we queued, which is the
            // point of the gate: concurrent keystrokes share one fetch.
            if (Fresh(_snapshot))
            {
                return _snapshot;
            }

            try
            {
                _snapshot = await FetchAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The caller gave up — a newer keystroke, or the circuit going away.
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
            {
                _logger.LogWarning(ex, "Could not read OpenRouter's model list");
            }

            return _snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool Fresh(Snapshot? snapshot) =>
        snapshot is not null && DateTimeOffset.UtcNow - snapshot.FetchedAt < Ttl;

    private async Task<Snapshot> FetchAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(FetchTimeout);

        var client = _http.CreateClient(OpenAiCompatibleClient.OpenRouterHttpClient);
        using var request = new HttpRequestMessage(HttpMethod.Get, ModelsEndpoint);

        // No Authorization header. See the class doc — this has to work before a key
        // exists, and OpenRouter serves the list to anyone.
        using var response = await client.SendAsync(request, timeout.Token);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(timeout.Token);
        return ParseCatalog(body);
    }

    /// <summary>
    /// Pure, and split out for the reason <c>BuildPayload</c> and <c>ParseResults</c>
    /// are in <see cref="ClaudeIngredientClassifier"/>: the suite tests it with no
    /// network.
    /// </summary>
    internal static Snapshot ParseCatalog(string json)
    {
        using var document = JsonDocument.Parse(json);

        var byId = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        var ids = new List<string>();

        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            return new Snapshot(DateTimeOffset.UtcNow, byId, ids);
        }

        foreach (var model in data.EnumerateArray())
        {
            if (model.ValueKind != JsonValueKind.Object ||
                model.TryGetProperty("id", out var idNode) is false ||
                idNode.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var id = idNode.GetString();
            if (string.IsNullOrWhiteSpace(id) || byId.ContainsKey(id))
            {
                continue;
            }

            var parameters = Strings(model, "supported_parameters");
            var modalities = model.TryGetProperty("architecture", out var architecture)
                ? Strings(architecture, "input_modalities")
                : [];

            var name = model.TryGetProperty("name", out var nameNode) && nameNode.ValueKind == JsonValueKind.String
                ? nameNode.GetString() ?? id
                : id;

            byId[id] = new Entry(
                id,
                name,
                // "structured_outputs" rather than "response_format": the request sends a
                // json_schema with strict: true, plus provider.require_parameters, which
                // routes only to backends that honour it.
                parameters.Contains("structured_outputs", StringComparer.OrdinalIgnoreCase),
                modalities.Contains("image", StringComparer.OrdinalIgnoreCase),
                modalities.Contains("file", StringComparer.OrdinalIgnoreCase),
                parameters.Contains("reasoning", StringComparer.OrdinalIgnoreCase));
            ids.Add(id);
        }

        return new Snapshot(DateTimeOffset.UtcNow, byId, ids);
    }

    private static List<string> Strings(JsonElement parent, string property)
    {
        var values = new List<string>();
        if (!parent.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return values;
        }

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } value)
            {
                values.Add(value);
            }
        }

        return values;
    }

    /// <summary>
    /// Deliberately conservative, the same argument <see cref="IngredientMatcher"/>
    /// makes for its bidirectional word coverage: a wrong suggestion invites a wrong
    /// adopt, and this one rewrites the box in a click.
    /// <para>
    /// Not <see cref="IngredientMatcher"/> itself — that compares word sets of free-text
    /// ingredient names, and would happily claim a match across two unrelated
    /// <c>author/slug</c> pairs.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<string> Suggest(string typed, IReadOnlyList<string> ids)
    {
        var slash = typed.IndexOf('/');
        if (slash <= 0 || slash == typed.Length - 1)
        {
            return [];
        }

        var author = typed[..slash];
        var slug = typed[(slash + 1)..];
        if (slug.Length < MinOverlap)
        {
            return [];
        }

        // Same author first: an id that agrees on who makes the model and most of its
        // name is a typo; one that merely shares a substring is a guess.
        var sameAuthor = ids
            .Select(id => (Id: id, Slash: id.IndexOf('/')))
            .Where(c => c.Slash > 0 && c.Id.AsSpan(0, c.Slash).Equals(author, StringComparison.OrdinalIgnoreCase))
            .Select(c => (c.Id, Slug: c.Id[(c.Slash + 1)..]))
            .Where(c => !c.Slug.Equals(slug, StringComparison.OrdinalIgnoreCase))
            .Select(c => (c.Id, Shared: CommonPrefixLength(c.Slug, slug)))
            .Where(c => c.Shared >= MinOverlap)
            .OrderByDescending(c => c.Shared)
            .ThenBy(c => c.Id.Length)
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .Select(c => c.Id)
            .Take(MaxSuggestions)
            .ToList();

        if (sameAuthor.Count > 0)
        {
            return sameAuthor;
        }

        return ids
            .Select(id => (Id: id, Slash: id.IndexOf('/')))
            .Where(c => c.Slash > 0)
            .Select(c => (c.Id, Slug: c.Id[(c.Slash + 1)..]))
            .Where(c => Contains(c.Slug, slug) || (c.Slug.Length >= MinOverlap && Contains(slug, c.Slug)))
            .OrderBy(c => c.Id.Length)
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .Select(c => c.Id)
            .Take(MaxSuggestions)
            .ToList();
    }

    private const int MaxSuggestions = 3;

    private const int MinOverlap = 4;

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static int CommonPrefixLength(string a, string b)
    {
        var shared = 0;
        var limit = Math.Min(a.Length, b.Length);
        while (shared < limit && char.ToLowerInvariant(a[shared]) == char.ToLowerInvariant(b[shared]))
        {
            shared++;
        }

        return shared;
    }

    internal sealed record Entry(
        string Id,
        string Name,
        bool StructuredOutput,
        bool Images,
        bool Files,
        bool Effort);

    internal sealed record Snapshot(
        DateTimeOffset FetchedAt,
        IReadOnlyDictionary<string, Entry> ById,
        IReadOnlyList<string> Ids);
}
