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
        FakeReceiptScanner scanner)
    {
        _inventory = inventory;
        Generator = generator;
        Scanner = scanner;
        Recipes = inventory.NewRecipeService();

        // Registered as singletons rather than scoped: a BunitContext resolves
        // each page from the same container, and sharing one InventoryService
        // is exactly what a single Blazor circuit does anyway.
        Services.AddSingleton(inventory.Notifier);
        Services.AddSingleton(inventory.Service);
        Services.AddSingleton(Recipes);
        Services.AddSingleton<IRecipeGenerator>(generator);
        Services.AddSingleton(Options.Create(RecipeOptions));
        Services.AddSingleton<IReceiptScanner>(scanner);
        Services.AddSingleton(Options.Create(ScanOptions));
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

    /// <summary>
    /// Mutable up until the page is rendered, so a test can switch recipe
    /// generation off before <c>Recipes</c> reads it.
    /// </summary>
    public RecipeGenerationOptions RecipeOptions { get; } = new();

    /// <inheritdoc cref="RecipeOptions"/>
    public ReceiptScanningOptions ScanOptions { get; } = new();

    /// <summary>
    /// A second <see cref="InventoryService"/> over the same database and
    /// notifier: an MCP tool's scope, or another household member's tab. This
    /// is what makes the "the page refreshes itself from the notifier and
    /// nowhere else" tests mean anything.
    /// </summary>
    public InventoryService OutOfCircuitService() => _inventory.NewService();

    public Task<int> CountAsync() => _inventory.CountAsync();

    public static async Task<PageHarness> CreateAsync() =>
        new(await InventoryHarness.CreateAsync(), new FakeRecipeGenerator(), new FakeReceiptScanner());

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
/// throws, and an empty <see cref="Result"/> is how a failure arrives.
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

        return new ScanResult(Result, Warning);
    }
}
