using MealPlanner.Components.Pages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MealPlanner.Tests;

/// <summary>
/// A <see cref="BunitContext"/> wired to the same real service graph the
/// service tests use: <see cref="InventoryHarness"/>'s throwaway SQLite file,
/// a real <see cref="InventoryService"/> and a real
/// <see cref="InventoryChangeNotifier"/>. The pages are the only thing under
/// test here — everything below them is the production code, so a component
/// test that passes says something about the real app.
///
/// The one fake is <see cref="IRecipeGenerator"/>: the suite never spawns the
/// claude CLI, and rendering a page must not start being the exception.
/// </summary>
internal sealed class PageHarness : BunitContext
{
    private readonly InventoryHarness _inventory;

    private PageHarness(
        InventoryHarness inventory,
        FakeRecipeGenerator generator,
        FakeReceiptScanner scanner,
        FakeOpenRouterCatalog catalog,
        FakeApiKeyChecker keyChecker)
    {
        _inventory = inventory;
        Generator = generator;
        Scanner = scanner;
        Catalog = catalog;
        KeyChecker = keyChecker;
        Recipes = inventory.NewRecipeService();
        // Handed the page's own option instances, so a test that changes a
        // default before rendering changes what the service falls back to.
        Settings = inventory.NewAiSettingsService(ClassifyOptions, RecipeOptions, ScanOptions);

        // Registered as singletons rather than scoped: a BunitContext resolves
        // each page from the same container, and sharing one InventoryService
        // is exactly what a single Blazor circuit does anyway.
        Services.AddSingleton(inventory.Notifier);
        Services.AddSingleton(inventory.Service);
        Services.AddSingleton(Recipes);
        Services.AddSingleton<IRecipeGenerator>(generator);
        Services.AddSingleton(Options.Create(RecipeOptions));
        Services.AddSingleton<IReceiptScanner>(scanner);
        // Faked for the same reason the other two are: the suite never reaches a
        // provider, and OpenRouterCatalog's own tests cover the real one.
        Services.AddSingleton<IOpenRouterCatalog>(catalog);
        Services.AddSingleton<IApiKeyChecker>(keyChecker);
        Services.AddSingleton(Options.Create(ScanOptions));
        Services.AddSingleton(Settings);
        // Only Recipe and Scan options were registered before the settings page
        // existed. Without this every Settings test dies at render with a DI
        // error that reads like a page bug.
        Services.AddSingleton(Options.Create(ClassifyOptions));
        Services.AddLogging();
    }

    /// <summary>The service the page itself injects — a circuit's own writer.</summary>
    public InventoryService Service => _inventory.Service;

    /// <summary>The same RecipeService the page holds, for reading rows back.</summary>
    public RecipeService Recipes { get; }

    public InventoryChangeNotifier Notifier => _inventory.Notifier;

    /// <inheritdoc cref="InventoryHarness.DatabasePath"/>
    public string DatabasePath => _inventory.DatabasePath;

    /// <summary>Everything the notifier logged, including its subscriber count.</summary>
    public CapturingLogger<InventoryChangeNotifier> Log => _inventory.Log;

    public FakeRecipeGenerator Generator { get; }

    public FakeReceiptScanner Scanner { get; }

    /// <summary>What /settings gets back when it checks a typed model id.</summary>
    public FakeOpenRouterCatalog Catalog { get; }

    /// <summary>What /settings gets back when it asks whether a key is any good.</summary>
    public FakeApiKeyChecker KeyChecker { get; }

    /// <summary>
    /// Mutable up until the page is rendered, so a test can switch recipe
    /// generation off before <c>Recipes</c> reads it.
    /// </summary>
    public RecipeGenerationOptions RecipeOptions { get; } = new();

    /// <inheritdoc cref="RecipeOptions"/>
    public ReceiptScanningOptions ScanOptions { get; } = new();

    /// <inheritdoc cref="RecipeOptions"/>
    public CategorizationOptions ClassifyOptions { get; } = new();

    /// <summary>The same AiSettingsService the settings page injects.</summary>
    public AiSettingsService Settings { get; }

    /// <summary>
    /// What the language picker reads out of localStorage. Settable before the
    /// render; the plan below is registered lazily so a test can change it.
    /// </summary>
    public string StoredLanguage { get; set; } = "system";

    /// <summary>
    /// A second <see cref="InventoryService"/> over the same database and
    /// notifier: an MCP tool's scope, or another household member's tab. This
    /// is what makes the "the page refreshes itself from the notifier and
    /// nowhere else" tests mean anything.
    /// </summary>
    public InventoryService OutOfCircuitService() => _inventory.NewService();

    public Task<int> CountAsync() => _inventory.CountAsync();

    public static async Task<PageHarness> CreateAsync() =>
        new(
            await InventoryHarness.CreateAsync(),
            new FakeRecipeGenerator(),
            new FakeReceiptScanner(),
            new FakeOpenRouterCatalog(),
            new FakeApiKeyChecker());

    /// <summary>
    /// Renders the inventory page as the app hosts it.
    ///
    /// Do not reach for <c>SetAssignedRenderMode</c> here. Both pages declare
    /// <c>@rendermode InteractiveServer</c> in the .razor file, which compiles
    /// to a <em>fixed</em> render mode, and Blazor's own ComponentFactory
    /// throws "it is not valid to specify any rendermode when using this
    /// component" if a caller supplies one as well. bUnit honours the directive
    /// on its own; that advice only applies to components without one.
    /// </summary>
    public IRenderedComponent<Inventory> RenderInventory() => Render<Inventory>();

    /// <inheritdoc cref="RenderInventory"/>
    public IRenderedComponent<Recipes> RenderRecipes() => Render<Recipes>();

    /// <inheritdoc cref="RenderInventory"/>
    public IRenderedComponent<Settings> RenderSettings()
    {
        // Planned here rather than in the constructor so StoredLanguage can be
        // set first. Both plans must exist before the render: under Strict mode
        // an unplanned call throws, and an un-resulted one never completes,
        // which hangs the handler before Blazor re-renders it.
        JSInterop.Setup<string>("mealPlannerLang.get").SetResult(StoredLanguage);
        JSInterop.Setup<string>("mealPlannerTheme.get").SetResult("system");

        // The matcher overload is required: the bare SetupVoid(identifier)
        // matches only a call with *no* arguments, so every real invocation
        // would fall through to Strict mode's exception. Which argument arrived
        // is asserted at the call site instead.
        JSInterop.SetupVoid("mealPlannerLang.set", _ => true).SetVoidResult();
        JSInterop.SetupVoid("mealPlannerTheme.set", _ => true).SetVoidResult();

        return Render<Settings>();
    }

    /// <summary>
    /// A second settings service over the same file — another tab, or a
    /// feature resolving its model. Even with no notifier, this is
    /// how a test proves a write actually landed rather than reading back the
    /// page's own in-memory drafts.
    /// </summary>
    public AiSettingsService OutOfCircuitSettings() =>
        _inventory.NewAiSettingsService(ClassifyOptions, RecipeOptions, ScanOptions);

    protected override async ValueTask DisposeAsyncCore()
    {
        // Components first: Inventory.Dispose unsubscribes from the notifier and
        // cancels its status timer, and both want the database still there.
        await base.DisposeAsyncCore();
        await _inventory.DisposeAsync();
    }
}

/// <summary>
/// Stands in for <see cref="ClaudeRecipeGenerator"/> without a subprocess.
/// Records every request so a test can assert how many times the page called,
/// and can be held open on <see cref="Gate"/> to keep one generation running
/// while the test clicks again.
/// </summary>
internal sealed class FakeRecipeGenerator : IRecipeGenerator
{
    private readonly List<RecipeRequest> _requests = [];

    /// <summary>Set to hold GenerateAsync open until the test releases it.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public IReadOnlyList<RecipeSuggestion> Result { get; set; } = [];

    public IReadOnlyList<RecipeRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return _requests.ToArray();
            }
        }
    }

    public async Task<IReadOnlyList<RecipeSuggestion>> GenerateAsync(
        RecipeRequest request,
        IReadOnlyList<InventoryItem> inventory,
        CancellationToken ct = default)
    {
        lock (_requests)
        {
            _requests.Add(request);
        }

        if (Gate is not null)
        {
            await Gate.Task.WaitAsync(ct);
        }

        return Result;
    }
}

/// <summary>
/// Stands in for <see cref="ClaudeReceiptScanner"/> without a subprocess.
/// Keeps the real thing's contract, which is the part worth faking: it never
/// throws, and an empty <see cref="Result"/> is how a failure arrives —
/// with <see cref="Failed"/> set when the call itself never answered.
/// </summary>
internal sealed class FakeReceiptScanner : IReceiptScanner
{
    private readonly List<ReceiptFile> _files = [];

    /// <summary>Set to hold ScanAsync open until the test releases it.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public IReadOnlyList<ScannedLine> Result { get; set; } = [];

    /// <summary>
    /// What the real scanner says when the parser dropped or truncated part of
    /// the receipt. Separate from <see cref="Result"/> so a test can hold the
    /// lines it wants and still exercise the warning.
    /// </summary>
    public string? Warning { get; set; }

    /// <summary>What the real scanner reports when the call itself never answered.</summary>
    public bool Failed { get; set; }

    public IReadOnlyList<ReceiptFile> Files
    {
        get
        {
            lock (_files)
            {
                return _files.ToArray();
            }
        }
    }

    public async Task<ScanResult> ScanAsync(
        ReceiptFile file,
        CancellationToken ct = default)
    {
        lock (_files)
        {
            _files.Add(file);
        }

        if (Gate is not null)
        {
            await Gate.Task.WaitAsync(ct);
        }

        return new ScanResult(Result, Warning, Failed);
    }
}

/// <summary>
/// Stands in for <see cref="OpenRouterCatalog"/> without a network. The verdict is
/// whatever a test sets; <see cref="OpenRouterCatalogTests"/> covers working the
/// real one out from OpenRouter's answer.
/// </summary>
internal sealed class FakeOpenRouterCatalog : IOpenRouterCatalog
{
    private readonly List<string> _asked = [];

    /// <summary>Returns a Listed, fully-capable verdict unless a test says otherwise.</summary>
    public Func<string, ModelIdCheck> Answer { get; set; } = Listed;

    public IReadOnlyList<string> Asked
    {
        get
        {
            lock (_asked)
            {
                return _asked.ToArray();
            }
        }
    }

    public static ModelIdCheck Listed(string id) =>
        new(ModelIdVerdict.Listed, id, $"Fake: {id}", true, true, true, true, []);

    public static ModelIdCheck NotListed(string id, params string[] suggestions) =>
        new(ModelIdVerdict.NotListed, id, null, false, false, false, false, suggestions);

    public Task<ModelIdCheck> CheckAsync(string? modelId, CancellationToken ct = default)
    {
        var id = modelId?.Trim() ?? string.Empty;
        lock (_asked)
        {
            _asked.Add(id);
        }

        return Task.FromResult(id.Length == 0 ? ModelIdCheck.Blank : Answer(id));
    }
}

/// <summary>
/// Stands in for <see cref="ApiKeyChecker"/> without a network. The verdict is
/// whatever a test sets; <see cref="ApiKeyCheckerTests"/> covers working the real one
/// out from a provider's answer.
/// <para>
/// <b>It records that a key was asked about, never the key.</b> A fake that kept key
/// material would become the leak the service is shaped to avoid, and would invite a
/// page test to assert on a secret. Which key went out is a service question, answered
/// through <see cref="FakeHttp"/> over there.
/// </para>
/// </summary>
internal sealed class FakeApiKeyChecker : IApiKeyChecker
{
    private readonly List<(AiProvider Provider, bool Stored, bool HadKey)> _asked = [];

    /// <summary>Returns Valid unless a test says otherwise.</summary>
    public Func<AiProvider, ApiKeyCheck> Answer { get; set; } = ApiKeyCheck.Valid;

    /// <summary>Set to hold a check open, so a test can see the in-flight state.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public IReadOnlyList<(AiProvider Provider, bool Stored, bool HadKey)> Asked
    {
        get
        {
            lock (_asked)
            {
                return _asked.ToArray();
            }
        }
    }

    public async Task<ApiKeyCheck> CheckKeyAsync(
        AiProvider provider,
        string? apiKey,
        CancellationToken ct = default) =>
        await AnswerAsync(provider, stored: false, !string.IsNullOrWhiteSpace(apiKey), ct);

    public async Task<ApiKeyCheck> CheckStoredAsync(AiProvider provider, CancellationToken ct = default) =>
        await AnswerAsync(provider, stored: true, hadKey: false, ct);

    private async Task<ApiKeyCheck> AnswerAsync(AiProvider provider, bool stored, bool hadKey, CancellationToken ct)
    {
        lock (_asked)
        {
            _asked.Add((provider, stored, hadKey));
        }

        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(ct);
        }

        ct.ThrowIfCancellationRequested();
        return Answer(provider);
    }
}
