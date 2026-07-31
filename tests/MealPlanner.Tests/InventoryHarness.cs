using MealPlanner.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MealPlanner.Tests;

/// <summary>
/// A disposable throwaway SQLite file with the real service graph on top of it:
/// the same <c>AddDbContextFactory</c> registration Program.cs uses, a real
/// <see cref="InventoryChangeNotifier"/>, and a real
/// <see cref="InventoryService"/>. Nothing is faked — the NOCASE unique index
/// and the write races these tests exercise only exist in actual SQLite.
///
/// One harness per test, so tests stay independent and can run in parallel.
/// </summary>
internal sealed class InventoryHarness : IAsyncDisposable
{
    // Migrating a fresh database costs far more than copying one. Build the
    // schema once per test run, then stamp each test's file out of it.
    private static readonly Lazy<Task<string>> TemplateDatabase =
        new(CreateTemplateAsync, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly string _databasePath;
    private readonly ServiceProvider _provider;

    private InventoryHarness(
        string databasePath,
        ServiceProvider provider,
        CapturingLogger<InventoryChangeNotifier> logger)
    {
        _databasePath = databasePath;
        _provider = provider;
        Factory = provider.GetRequiredService<IDbContextFactory<MealPlannerDbContext>>();
        Notifier = new InventoryChangeNotifier(logger);
        Log = logger;
        Service = new InventoryService(Factory, Notifier);
    }

    public IDbContextFactory<MealPlannerDbContext> Factory { get; }

    /// <summary>The temp SQLite file, for tests that need to break the store.</summary>
    public string DatabasePath => _databasePath;

    public InventoryChangeNotifier Notifier { get; }

    public InventoryService Service { get; }

    /// <summary>Everything the notifier logged, for the subscriber-failure tests.</summary>
    public CapturingLogger<InventoryChangeNotifier> Log { get; }

    public static async Task<InventoryHarness> CreateAsync()
    {
        var template = await TemplateDatabase.Value;
        var databasePath = TempDatabasePath();
        File.Copy(template, databasePath);

        var provider = new ServiceCollection()
            .AddDbContextFactory<MealPlannerDbContext>(options =>
                options.UseSqlite($"Data Source={databasePath}"))
            .BuildServiceProvider();

        return new InventoryHarness(
            databasePath,
            provider,
            new CapturingLogger<InventoryChangeNotifier>());
    }

    /// <summary>
    /// A second <see cref="InventoryService"/> over the same database and the
    /// same notifier — what a concurrent MCP tool scope looks like next to a
    /// Blazor circuit.
    /// </summary>
    public InventoryService NewService() => new(Factory, Notifier);

    /// <summary>A <see cref="RecipeService"/> over the same database.</summary>
    public RecipeService NewRecipeService() => new(Factory);

    /// <summary>
    /// Runs <paramref name="work"/> <paramref name="count"/> times genuinely in
    /// parallel, each on its own thread-pool thread behind a starting gate.
    ///
    /// This is not ceremony. Handing the calls straight to Task.WhenAll does
    /// not race them: Microsoft.Data.Sqlite's async methods complete
    /// synchronously, so each call runs to completion the moment it is created
    /// and the writers quietly serialize — the tests then pass even with
    /// InventoryService's retry deleted.
    /// </summary>
    public static async Task<T[]> InParallelAsync<T>(int count, Func<int, Task<T>> work)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, count)
            .Select(i => Task.Run(async () =>
            {
                await gate.Task;
                return await work(i);
            }))
            .ToArray();

        gate.SetResult();
        return await Task.WhenAll(tasks);
    }

    /// <inheritdoc cref="InParallelAsync{T}"/>
    public static async Task InParallelAsync(int count, Func<int, Task> work) =>
        await InParallelAsync(count, async i =>
        {
            await work(i);
            return true;
        });

    /// <summary>Row count straight from the database, bypassing the service.</summary>
    public async Task<int> CountAsync()
    {
        await using var db = await Factory.CreateDbContextAsync();
        return await db.InventoryItems.CountAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        // Pooled connections keep the file open on Windows, where an open
        // handle blocks File.Delete. The clear is process-wide, but it only
        // discards idle connections, so parallel tests are unaffected.
        SqliteConnection.ClearAllPools();
        Delete(_databasePath);
    }

    private static async Task<string> CreateTemplateAsync()
    {
        var path = TempDatabasePath();
        var options = new DbContextOptionsBuilder<MealPlannerDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;

        // Migrate rather than EnsureCreated: the committed migrations are what
        // production runs, so the schema under test is the schema shipped.
        await using (var db = new MealPlannerDbContext(options))
        {
            await db.Database.MigrateAsync();
        }

        SqliteConnection.ClearAllPools();

        // Nothing owns the template, so tie it to the test run itself rather
        // than leaving one database in the temp directory per invocation.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Delete(path);
        return path;
    }

    private static void Delete(string databasePath)
    {
        foreach (var path in new[] { databasePath, databasePath + "-shm", databasePath + "-wal" })
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // A leaked handle is not worth failing a green test over.
            }
        }
    }

    private static string TempDatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"mealplanner-tests-{Guid.NewGuid():N}.db");
}
