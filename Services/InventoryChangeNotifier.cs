namespace MealPlanner.Services;

/// <summary>
/// Broadcasts inventory mutations to live UI subscribers (Blazor circuits).
/// Singleton: MCP tools and UI pages share one bus so out-of-circuit writes
/// (e.g. Claude via MCP) refresh open Inventory pages without a reload.
/// </summary>
public sealed class InventoryChangeNotifier
{
    private readonly Lock _gate = new();
    private readonly List<Action<InventoryChange>> _handlers = [];
    private readonly ILogger<InventoryChangeNotifier> _logger;

    public InventoryChangeNotifier(ILogger<InventoryChangeNotifier> logger)
    {
        _logger = logger;
    }

    public void Subscribe(Action<InventoryChange> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            _handlers.Add(handler);
        }
    }

    public void Unsubscribe(Action<InventoryChange> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            _handlers.Remove(handler);
        }
    }

    public void Publish(InventoryChange change)
    {
        Action<InventoryChange>[] snapshot;
        lock (_gate)
        {
            snapshot = _handlers.ToArray();
        }

        _logger.LogDebug(
            "Publishing {ChangeKind} for {ItemName} to {SubscriberCount} subscriber(s)",
            change.Kind, change.Name, snapshot.Length);

        foreach (var handler in snapshot)
        {
            try
            {
                handler(change);
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
