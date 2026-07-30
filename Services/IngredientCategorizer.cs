using System.Collections.Concurrent;
using System.Threading.Channels;
using MealPlanner.Models;
using Microsoft.Extensions.Options;

namespace MealPlanner.Services;

/// <summary>
/// Fills in the category of items that were created without one.
/// <para>
/// Runs off the back of <see cref="InventoryChangeNotifier"/> rather than
/// inside <see cref="InventoryService"/>: classification takes seconds, and the
/// service is on the path of every write from every household member. An item
/// therefore appears in Other immediately and moves a moment later — the second
/// write publishes like any other, so open pages re-render themselves and no
/// page needs to know this service exists.
/// </para>
/// </summary>
public sealed class IngredientCategorizer : BackgroundService
{
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true });

    // Names already classified this run. Re-adding "Salt" after someone deleted
    // it is common, and costs nothing here.
    private readonly ConcurrentDictionary<string, IngredientCategory> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly InventoryChangeNotifier _notifier;
    private readonly IIngredientClassifier _classifier;
    private readonly Func<InventoryService> _newService;
    private readonly CategorizationOptions _options;
    private readonly ILogger<IngredientCategorizer> _logger;

    public IngredientCategorizer(
        InventoryChangeNotifier notifier,
        IIngredientClassifier classifier,
        Func<InventoryService> newService,
        IOptions<CategorizationOptions> options,
        ILogger<IngredientCategorizer> logger)
    {
        _notifier = notifier;
        _classifier = classifier;
        _newService = newService;
        _options = options.Value;
        _logger = logger;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Ingredient auto-categorization is disabled.");
            return Task.CompletedTask;
        }

        // Method group, not a lambda: Unsubscribe matches on delegate equality.
        _notifier.Subscribe(OnInventoryChangedAsync);
        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Unsubscribe first, so nothing new is queued while the loop winds down.
        _notifier.Unsubscribe(OnInventoryChangedAsync);
        if (_options.Enabled)
        {
            await base.StopAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Queues newly-created items that landed in Other, and returns at once.
    /// <para>
    /// Nothing is awaited here on purpose. Every writer awaits
    /// <see cref="InventoryChangeNotifier.PublishAsync"/>, so anything slow in a
    /// handler is time a household member spends watching a frozen Add button.
    /// </para>
    /// <para>
    /// The filter is "created, and in Other" rather than "created, and the
    /// caller named no category", because <see cref="InventoryChange"/> does not
    /// carry that distinction. The practical difference is that an item somebody
    /// deliberately filed under Other may get moved; the per-row dropdown puts
    /// it back, and that correction is an update, so it is never revisited.
    /// </para>
    /// </summary>
    private Task OnInventoryChangedAsync(InventoryChange change)
    {
        if (change is { Kind: ChangeKind.Created, Category: IngredientCategory.Other })
        {
            // Unbounded channel: TryWrite only fails on a completed writer.
            _queue.Writer.TryWrite(change.Name);
        }

        return Task.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await _queue.Reader.WaitToReadAsync(stoppingToken))
                {
                    return;
                }

                var batch = await CollectBatchAsync(stoppingToken);
                if (batch.Count > 0)
                {
                    await CategorizeAsync(batch, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // An escaping exception stops the host by default, so a bad batch
                // would take the whole inventory down with it. Log and carry on:
                // the worst outcome of this service failing is items staying in
                // Other.
                _logger.LogError(ex, "Ingredient categorization batch failed");
            }
        }
    }

    /// <summary>
    /// Takes everything already queued, then keeps collecting for a short window.
    /// One MCP "stock the kitchen" call creates a dozen items in quick
    /// succession, and a dozen names cost the same as one to classify.
    /// </summary>
    private async Task<List<string>> CollectBatchAsync(CancellationToken ct)
    {
        var batch = new List<string>();
        // The same name can be created twice (deleted in between) before the
        // batch goes out; the NOCASE index means casing does not make it a
        // different ingredient.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void DrainReady()
        {
            while (batch.Count < _options.MaxBatchSize && _queue.Reader.TryRead(out var name))
            {
                if (seen.Add(name))
                {
                    batch.Add(name);
                }
            }
        }

        DrainReady();

        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        window.CancelAfter(TimeSpan.FromMilliseconds(_options.BatchWindowMilliseconds));
        try
        {
            while (batch.Count < _options.MaxBatchSize
                   && await _queue.Reader.WaitToReadAsync(window.Token))
            {
                DrainReady();
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Window closed. This is how a batch normally ends.
        }

        return batch;
    }

    private async Task CategorizeAsync(List<string> batch, CancellationToken ct)
    {
        var service = _newService();
        var pending = new List<string>();

        foreach (var name in batch)
        {
            if (_cache.TryGetValue(name, out var cached))
            {
                await ApplyAsync(service, name, cached, ct);
            }
            else
            {
                pending.Add(name);
            }
        }

        if (pending.Count == 0)
        {
            return;
        }

        var categories = await _classifier.ClassifyAsync(pending, ct);

        for (var i = 0; i < pending.Count; i++)
        {
            // Positional by contract, but this loop is what a broken
            // implementation would turn into an IndexOutOfRangeException.
            if (i >= categories.Count || categories[i] is not { } category)
            {
                continue;
            }

            Remember(pending[i], category);
            await ApplyAsync(service, pending[i], category, ct);
        }
    }

    private async Task ApplyAsync(
        InventoryService service,
        string name,
        IngredientCategory category,
        CancellationToken ct)
    {
        if (category == IngredientCategory.Other)
        {
            // Where it already is.
            return;
        }

        try
        {
            // onlyIf keeps a household member who classified the item by hand
            // during the round trip from being overwritten by a stale answer.
            var change = await service.SetCategoryAsync(
                name, category, onlyIf: IngredientCategory.Other, ct);

            if (change.Kind == ChangeKind.Updated)
            {
                _logger.LogInformation("Auto-categorized {Change}", change.Describe());
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Retries exhausted, or the row went away mid-write. One ingredient
            // stuck in Other is not worth losing the rest of the batch over.
            _logger.LogWarning(
                ex, "Could not file \"{Name}\" under {Category}", name, category);
        }
    }

    private void Remember(string name, IngredientCategory category)
    {
        if (_cache.Count >= _options.CacheSize)
        {
            // Emptying the whole cache is blunt, but a household's shopping
            // vocabulary is far short of the cap, so in practice this never
            // runs — and evicting properly would cost a second data structure
            // to hold the order.
            _cache.Clear();
        }

        _cache[name] = category;
    }
}
