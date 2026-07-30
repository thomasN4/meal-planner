namespace MealPlanner.Services;

/// <summary>
/// Broadcasts inventory mutations to live UI subscribers (Blazor circuits).
/// Singleton: MCP tools and UI pages share one bus so out-of-circuit writes
/// (e.g. Claude via MCP) refresh open Inventory pages without a reload.
/// </summary>
public sealed class InventoryChangeNotifier
{
    private readonly Lock _gate = new();
    private readonly List<Func<InventoryChange, Task>> _handlers = [];
    private readonly ILogger<InventoryChangeNotifier> _logger;

    public InventoryChangeNotifier(ILogger<InventoryChangeNotifier> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Handlers are async so subscribers can await their real work (a DB read,
    /// a re-render) instead of discarding a task — a dropped task swallows the
    /// exception and loses the re-render, which AGENTS.md forbids.
    /// Pass a method group: Unsubscribe matches on delegate equality, so a
    /// fresh lambda would never be removed.
    /// </summary>
    public void Subscribe(Func<InventoryChange, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            _handlers.Add(handler);
        }
    }

    public void Unsubscribe(Func<InventoryChange, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            _handlers.Remove(handler);
        }
    }

    public async Task PublishAsync(InventoryChange change)
    {
        // Snapshot under the gate, then release it: handlers are awaited below
        // and a Lock cannot be held across an await.
        Func<InventoryChange, Task>[] snapshot;
        lock (_gate)
        {
            snapshot = _handlers.ToArray();
        }

        _logger.LogDebug(
            "Publishing {ChangeKind} for {ItemName} to {SubscriberCount} subscriber(s)",
            change.Kind, change.Name, snapshot.Length);

        // Fan out concurrently. Awaiting each handler in turn made a writer wait
        // on every open circuit's DB read and re-render, one after another —
        // including an MCP write, which then waited on the kitchen's tabs
        // (issue #4). The writer still awaits all of them, so delivery is
        // guaranteed before it sees its result; what is traded away is
        // deterministic ordering between subscribers.
        await Task.WhenAll(snapshot.Select(GuardedAsync));

        // A local async function, not the handler call inlined into the Select:
        // this is what puts a handler that throws before its first await inside
        // the try. Guarding per handler also means no task here ever faults, so
        // WhenAll cannot collapse several dead circuits into one
        // AggregateException that hides all but the first.
        async Task GuardedAsync(Func<InventoryChange, Task> handler)
        {
            try
            {
                await handler(change);
            }
            catch (Exception ex)
            {
                // One torn-down circuit must not stop the fan-out to the others;
                // swallow, but never silently — this is the only place a stale
                // subscriber's failure is visible.
                _logger.LogError(
                    ex,
                    "Inventory change subscriber threw handling {ChangeKind} for {ItemName}",
                    change.Kind, change.Name);
            }
        }
    }
}
