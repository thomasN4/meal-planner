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

        foreach (var handler in snapshot)
        {
            handler(change);
        }
    }
}
