using MealPlanner.Models;

namespace MealPlanner.Services;

/// <summary>One model a provider offers, as the settings page lists it.</summary>
/// <param name="Id">Exactly what goes on the wire — no date suffixes.</param>
/// <param name="Label">What a person reads in the dropdown.</param>
/// <param name="SupportsEffort">
/// False when the model rejects an effort setting outright. The page renders
/// words instead of a control for these, rather than a knob that means nothing.
/// </param>
public sealed record AiModelChoice(string Id, string Label, bool SupportsEffort);

/// <summary>One provider, its curated models, and the efforts it understands.</summary>
/// <param name="KeyPrefixes">
/// What this provider's API keys start with, longest first. Empty for a provider
/// that needs no key. This is what <see cref="AiCatalog.InferProvider"/> matches
/// on, and it is the reason the settings page has one key box rather than one
/// per company.
/// </param>
public sealed record AiProviderInfo(
    AiProvider Provider,
    string Label,
    IReadOnlyList<string> KeyPrefixes,
    IReadOnlyList<AiModelChoice> Models,
    IReadOnlyList<AiEffort> Efforts)
{
    public bool NeedsApiKey => KeyPrefixes.Count > 0;
}

/// <summary>
/// Which models and efforts each provider offers, and which provider a pasted
/// key belongs to.
/// <para>
/// Static and pure, in the spirit of <see cref="IngredientMatcher"/>: it is a
/// table, not a service. Don't give it a DbContext and don't register it in DI.
/// </para>
/// <para>
/// <strong>Snapshot: 2026-08-19</strong>; the OpenAI and OpenRouter ids were
/// re-checked on 2026-09-18 against OpenAI's models page and OpenRouter's
/// <c>/api/v1/models</c>. Ids move, providers add effort levels, and key
/// prefixes are what these companies issue today rather than a contract any of
/// them has made. Nothing here is a whitelist — a model id that leaves
/// this list degrades to the page's "Other…" free-text box with the stored id
/// intact, and a key prefix nobody recognises falls back to asking which
/// provider it is. Both escape hatches exist because this table will go stale.
/// </para>
/// </summary>
public static class AiCatalog
{
    /// <summary>
    /// Every provider, in the order the settings page offers them. The Claude
    /// subscription is first because it is what the app runs on today.
    /// </summary>
    public static IReadOnlyList<AiProviderInfo> Providers { get; } =
    [
        new(
            AiProvider.ClaudeCli,
            "Claude subscription (claude CLI)",
            // No key of its own: the CLI carries the household's login.
            [],
            [
                // The CLI takes aliases as well as full ids, and aliases are
                // what the three options classes already hold ("sonnet").
                new("opus", "Opus (latest)", true),
                new("sonnet", "Sonnet (latest)", true),
                new("haiku", "Haiku (latest)", true),
                new("fable", "Fable (latest)", true),
            ],
            // Measured from `claude --help` on 2026-08-19.
            [AiEffort.Low, AiEffort.Medium, AiEffort.High, AiEffort.XHigh, AiEffort.Max]),

        new(
            AiProvider.AnthropicApi,
            "Anthropic API",
            ["sk-ant-"],
            [
                new("claude-opus-5", "Claude Opus 5", true),
                new("claude-sonnet-5", "Claude Sonnet 5", true),
                new("claude-fable-5-1", "Claude Fable 5.1", true),
                new("claude-fable-5", "Claude Fable 5", true),
                // Effort is not merely ignored on Haiku 4.5 — the request is
                // rejected. This is the case SupportsEffort exists for, and it
                // is a real one rather than a placeholder.
                new("claude-haiku-4-5", "Claude Haiku 4.5", false),
            ],
            [AiEffort.Low, AiEffort.Medium, AiEffort.High, AiEffort.XHigh, AiEffort.Max]),

        new(
            AiProvider.OpenAi,
            "OpenAI API",
            // Project keys first: "sk-proj-" also starts with "sk-", and longest
            // match is what keeps the two apart.
            ["sk-proj-", "sk-"],
            [
                new("gpt-6-astra", "GPT-6 Astra", true),
                new("gpt-5.6-sol", "GPT-5.6 Sol", true),
                new("gpt-5.6-terra", "GPT-5.6 Terra", true),
                new("gpt-5.6-luna", "GPT-5.6 Luna", true),
            ],
            // Minimal exists in the enum only because OpenAI offers it. OpenAI
            // calls the supported subset "model-dependent" (2026-09-18) without
            // publishing it per model, so an effort a model turns down fails that
            // one call — logged, and the feature returns nothing — rather than
            // being guessed away here.
            [AiEffort.Minimal, AiEffort.Low, AiEffort.Medium, AiEffort.High, AiEffort.XHigh, AiEffort.Max]),

        new(
            AiProvider.OpenRouter,
            "OpenRouter",
            ["sk-or-v1-", "sk-or-"],
            [
                new("openai/gpt-5.6-sol", "GPT-5.6 Sol", true),
                new("anthropic/claude-opus-5", "Claude Opus 5", true),
                new("google/gemini-3.6-flash", "Gemini 3.6 Flash", true),
                new("x-ai/grok-4.6", "Grok 4.6", true),
                // The undated "qwen/qwen3.8-max" was listed here and is gone from
                // OpenRouter's catalogue; only the dated snapshot remains.
                new("qwen/qwen3.8-max-0902", "Qwen 3.8 Max", true),
            ],
            // OpenRouter normalizes effort across a catalogue of hundreds, so
            // only the three every backend understands are offered here. A model
            // wanting more is what the free-text box is for.
            [AiEffort.Low, AiEffort.Medium, AiEffort.High]),
    ];

    public static AiProviderInfo For(AiProvider provider) =>
        Providers.First(p => p.Provider == provider);

    /// <summary>True when <paramref name="modelId"/> is one this table lists.</summary>
    public static bool KnowsModel(AiProvider provider, string modelId) =>
        For(provider).Models.Any(m => m.Id == modelId);

    /// <summary>
    /// Whether an effort setting means anything for this pair. A curated model
    /// answers for itself; an unknown id (someone typed it into "Other…")
    /// borrows the provider's own answer, because the person who typed a raw id
    /// is the one who knows what it accepts.
    /// </summary>
    public static bool SupportsEffort(AiProvider provider, string modelId)
    {
        var info = For(provider);
        var known = info.Models.FirstOrDefault(m => m.Id == modelId);
        return known?.SupportsEffort ?? info.Efforts.Count > 0;
    }

    /// <summary>
    /// Which provider issued this key, or null if no prefix matches.
    /// <para>
    /// Longest prefix wins, which is a property of the table rather than of an
    /// <c>if</c> ladder — "sk-proj-" has to beat "sk-", and adding a prefix must
    /// not depend on where in a chain it was inserted.
    /// </para>
    /// <para>
    /// Null is answered by asking the person, never by refusing the key:
    /// refusing would break the app the day a provider changes its prefix, and
    /// would foreclose pasting a key for anything not in this table.
    /// </para>
    /// </summary>
    public static AiProvider? InferProvider(string? apiKey)
    {
        var key = apiKey?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        AiProvider? best = null;
        var longest = 0;

        foreach (var info in Providers)
        {
            foreach (var prefix in info.KeyPrefixes)
            {
                if (prefix.Length > longest && key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    longest = prefix.Length;
                    best = info.Provider;
                }
            }
        }

        return best;
    }
}
