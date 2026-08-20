using MealPlanner.Data;
using MealPlanner.Models;
using Microsoft.EntityFrameworkCore;

namespace MealPlanner.Services;

/// <summary>One feature's stored choice, as callers see it.</summary>
public sealed record FeatureChoice(AiFeature Feature, AiProvider Provider, string Model, AiEffort? Effort);

/// <summary>
/// What is known about a provider's key <em>without</em> knowing the key.
/// <para>
/// This type deliberately has no field holding the secret. That is what stops a
/// page ever rendering one — not page discipline, which would have to be got
/// right again every time somebody adds a control. Reading the material takes
/// the separate <see cref="AiSettingsService.GetApiKeyAsync"/>.
/// </para>
/// </summary>
/// <param name="Tail">
/// The last four characters, or null when the key is too short for four
/// characters to give nothing away.
/// </param>
public sealed record CredentialStatus(AiProvider Provider, bool HasKey, string? Tail, DateTime? UpdatedAt);

/// <summary>Everything the settings page needs, and nothing it must not have.</summary>
public sealed record AiSettings(
    IReadOnlyList<FeatureChoice> Features,
    IReadOnlyList<CredentialStatus> Credentials);

/// <summary>
/// The single choke point for household-wide AI settings: which provider, model
/// and effort each feature uses, and one API key per provider.
/// <para>
/// <strong>Nothing consumes these yet.</strong> <c>ClaudeRecipeGenerator</c>,
/// <c>ClaudeIngredientClassifier</c> and <c>ClaudeReceiptScanner</c> all still
/// read <c>IOptions&lt;T&gt;</c> from <c>appsettings.json</c>. This service
/// stores the household's choices and reads them back; wiring them into the
/// three callers is a separate pass. The settings page says so on screen, in
/// several places, on purpose.
/// </para>
/// <para>
/// Shaped like <see cref="RecipeService"/>: a DbContext factory, a short-lived
/// context per call, records out rather than entities, and its own clamping —
/// EF's <c>[MaxLength]</c> is not enforced and SQLite ignores TEXT lengths, so
/// the service is the trust boundary.
/// </para>
/// <para>
/// <strong>No retry ladder, and no notifier publish.</strong>
/// <see cref="InventoryService"/> retries because upsert-by-name is
/// read-then-write against a NOCASE unique index <em>and</em> a concurrent
/// delete flips the branch back (issue #5). Here the primary key is the enum
/// value, so racing writers contend for one row rather than racing to insert
/// two, and nothing ever deletes a row — clearing a key nulls a column. One
/// catch-and-reread on the insert path covers it. And there is no
/// out-of-circuit writer: MCP does not touch settings and must not, so a page
/// refreshes itself. Revisit the notifier decision when a running feature reads
/// settings live and a second tab's stale save could revert a model
/// mid-generation.
/// </para>
/// </summary>
public class AiSettingsService
{
    private const int MaxModelLength = 100;
    private const int MaxKeyLength = 200;

    /// <summary>
    /// A key must be at least this long before its last four characters are
    /// shown. Four characters of a six-character key is most of the key.
    /// </summary>
    private const int MinLengthForTail = 12;

    /// <summary>
    /// What a feature uses before anybody has chosen. These mirror
    /// <c>appsettings.json</c> exactly the way the three options classes mirror
    /// it in C#, and they are the answer for a missing row — which is why no
    /// migration seeds anything. Seeding would freeze today's defaults into SQL
    /// and give the same question two answers.
    /// </summary>
    private static readonly Dictionary<AiFeature, FeatureChoice> Defaults = new()
    {
        [AiFeature.Categorization] =
            new(AiFeature.Categorization, AiProvider.ClaudeCli, "sonnet", AiEffort.Low),
        [AiFeature.RecipeGeneration] =
            new(AiFeature.RecipeGeneration, AiProvider.ClaudeCli, "sonnet", AiEffort.Medium),
        [AiFeature.ReceiptScanning] =
            new(AiFeature.ReceiptScanning, AiProvider.ClaudeCli, "sonnet", AiEffort.Low),
    };

    private readonly IDbContextFactory<MealPlannerDbContext> _factory;

    public AiSettingsService(IDbContextFactory<MealPlannerDbContext> factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// Every feature's choice and every provider's credential status, with
    /// defaults filled in for rows that do not exist yet.
    /// </summary>
    public async Task<AiSettings> GetAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        // Filtered in SQL, and the filter is the semantic rule rather than a
        // performance one: a row naming a feature or a provider this build does
        // not have is skipped *whole*, so the feature falls back to its default
        // — model included, because a model id without the provider it was
        // chosen for means nothing. Keeping "llama-9" and pairing it with the
        // fallback provider would hand the page a combination nobody picked and
        // then offer to save it.
        //
        // The tolerant converters in MealPlannerDbContext are the second layer,
        // not this one: they stop any *other* query over these tables (a count,
        // something added later) throwing during materialization, which is where
        // EF rejects an unmapped enum string — before a guard here could run.
        var features = Enum.GetValues<AiFeature>();
        var providers = Enum.GetValues<AiProvider>();

        var rows = await db.FeatureAiSettings.AsNoTracking()
            .Where(r => features.Contains(r.Feature) && providers.Contains(r.Provider))
            .ToListAsync(ct);
        var keys = await db.ProviderApiKeys.AsNoTracking()
            .Where(k => providers.Contains(k.Provider))
            .ToListAsync(ct);

        var choices = features
            .Select(f => Read(rows.FirstOrDefault(r => r.Feature == f), f))
            .ToList();

        var credentials = AiCatalog.Providers
            .Where(p => p.NeedsApiKey)
            .Select(p => Describe(p.Provider, keys.FirstOrDefault(k => k.Provider == p.Provider)))
            .ToList();

        return new AiSettings(choices, credentials);
    }

    /// <summary>
    /// Stores one feature's choice. Throws <see cref="ArgumentException"/> for an
    /// empty model or an unknown provider — the UI guards before calling, the
    /// same contract <see cref="InventoryService"/> has for an empty name.
    /// </summary>
    public async Task<FeatureChoice> SaveFeatureAsync(
        AiFeature feature,
        AiProvider provider,
        string model,
        AiEffort? effort,
        CancellationToken ct = default)
    {
        if (!Enum.IsDefined(feature))
        {
            throw new ArgumentException($"Unknown feature '{feature}'.", nameof(feature));
        }

        if (!Enum.IsDefined(provider))
        {
            throw new ArgumentException($"Unknown provider '{provider}'.", nameof(provider));
        }

        var cleaned = Clamp(model?.Trim(), MaxModelLength);
        if (string.IsNullOrEmpty(cleaned))
        {
            throw new ArgumentException("A model is required.", nameof(model));
        }

        var settled = NormalizeEffort(provider, cleaned, effort);

        await using var db = await _factory.CreateDbContextAsync(ct);
        var row = await db.FeatureAiSettings.FirstOrDefaultAsync(r => r.Feature == feature, ct);

        if (row is null)
        {
            db.FeatureAiSettings.Add(new FeatureAiSetting
            {
                Id = (int)feature,
                Feature = feature,
                Provider = provider,
                Model = cleaned,
                Effort = settled,
                UpdatedAt = DateTime.UtcNow,
            });

            try
            {
                await db.SaveChangesAsync(ct);
                return new FeatureChoice(feature, provider, cleaned, settled);
            }
            catch (DbUpdateException)
            {
                // Another writer inserted this feature's row between the read and
                // the save. One re-read is enough here where InventoryService
                // needs a loop: the row cannot be deleted out from under us, so
                // the branch cannot flip back to "insert" a second time.
                db.ChangeTracker.Clear();
                row = await db.FeatureAiSettings.FirstAsync(r => r.Feature == feature, ct);
            }
        }

        row.Provider = provider;
        row.Model = cleaned;
        row.Effort = settled;
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return new FeatureChoice(feature, provider, cleaned, settled);
    }

    /// <summary>
    /// Stores one provider's key. An empty key throws rather than clearing —
    /// clearing is <see cref="ClearApiKeyAsync"/>, and conflating the two makes
    /// an accidental empty submit silently destructive.
    /// </summary>
    public async Task<CredentialStatus> SetApiKeyAsync(
        AiProvider provider,
        string apiKey,
        CancellationToken ct = default)
    {
        if (!Enum.IsDefined(provider))
        {
            throw new ArgumentException($"Unknown provider '{provider}'.", nameof(provider));
        }

        if (!AiCatalog.For(provider).NeedsApiKey)
        {
            throw new ArgumentException(
                $"{AiCatalog.For(provider).Label} does not take an API key.", nameof(provider));
        }

        var cleaned = Clamp(apiKey?.Trim(), MaxKeyLength);
        if (string.IsNullOrEmpty(cleaned))
        {
            throw new ArgumentException("An API key is required.", nameof(apiKey));
        }

        var row = await UpsertKeyAsync(provider, cleaned, ct);
        return Describe(provider, row);
    }

    /// <summary>
    /// Forgets one provider's key, keeping the row. Idempotent: clearing a
    /// provider that has no key is not an error, because the page's undo can
    /// arrive after another tab already cleared it.
    /// </summary>
    public async Task<CredentialStatus> ClearApiKeyAsync(AiProvider provider, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(provider))
        {
            throw new ArgumentException($"Unknown provider '{provider}'.", nameof(provider));
        }

        var row = await UpsertKeyAsync(provider, null, ct);
        return Describe(provider, row);
    }

    /// <summary>
    /// The one method that hands back key material.
    /// <para>
    /// For the provider clients that will eventually make the calls. <strong>Not
    /// for a page</strong> — a Razor component that holds a key will render it
    /// sooner or later, which is why <see cref="GetAsync"/> answers with
    /// <see cref="CredentialStatus"/> instead.
    /// </para>
    /// </summary>
    public async Task<string?> GetApiKeyAsync(AiProvider provider, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var row = await db.ProviderApiKeys.AsNoTracking()
            .FirstOrDefaultAsync(k => k.Provider == provider, ct);
        return row?.ApiKey;
    }

    private async Task<ProviderApiKey> UpsertKeyAsync(AiProvider provider, string? apiKey, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var row = await db.ProviderApiKeys.FirstOrDefaultAsync(k => k.Provider == provider, ct);

        if (row is null)
        {
            row = new ProviderApiKey
            {
                Id = (int)provider,
                Provider = provider,
                ApiKey = apiKey,
                UpdatedAt = DateTime.UtcNow,
            };
            db.ProviderApiKeys.Add(row);

            try
            {
                await db.SaveChangesAsync(ct);
                return row;
            }
            catch (DbUpdateException)
            {
                // Same one-shot re-read as SaveFeatureAsync, for the same reason.
                db.ChangeTracker.Clear();
                row = await db.ProviderApiKeys.FirstAsync(k => k.Provider == provider, ct);
            }
        }

        row.ApiKey = apiKey;
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return row;
    }

    /// <summary>
    /// Turns a stored row into a choice, falling back to the default for
    /// anything this build does not recognise. A hand-edited database, or a
    /// downgrade after a provider is removed, degrades to the default rather
    /// than throwing on every page load — the rule <c>ThemeToggle</c> applies to
    /// localStorage and <see cref="RecipeService"/> applies to malformed JSON.
    /// </summary>
    private static FeatureChoice Read(FeatureAiSetting? row, AiFeature feature)
    {
        var fallback = Defaults[feature];
        if (row is null || !Enum.IsDefined(row.Provider) || string.IsNullOrWhiteSpace(row.Model))
        {
            return fallback;
        }

        AiEffort? effort = row.Effort is { } e && Enum.IsDefined(e) ? e : null;
        return new FeatureChoice(
            feature,
            row.Provider,
            row.Model,
            NormalizeEffort(row.Provider, row.Model, effort));
    }

    private static CredentialStatus Describe(AiProvider provider, ProviderApiKey? row)
    {
        var key = row?.ApiKey;
        if (string.IsNullOrEmpty(key))
        {
            return new CredentialStatus(provider, false, null, row?.UpdatedAt);
        }

        var tail = key.Length >= MinLengthForTail ? key[^4..] : null;
        return new CredentialStatus(provider, true, tail, row?.UpdatedAt);
    }

    /// <summary>
    /// Drops an effort the pair cannot use, and one the provider does not offer.
    /// Enforced here rather than only in the page, so a later caller cannot
    /// reintroduce a setting that means nothing — a non-null effort on a model
    /// that rejects it is a stored lie.
    /// </summary>
    private static AiEffort? NormalizeEffort(AiProvider provider, string model, AiEffort? effort)
    {
        if (effort is not { } value || !AiCatalog.SupportsEffort(provider, model))
        {
            return null;
        }

        return AiCatalog.For(provider).Efforts.Contains(value) ? value : (AiEffort?)null;
    }

    private static string Clamp(string? value, int max) =>
        value is null ? string.Empty : value.Length <= max ? value : value[..max];
}
