using MealPlanner.Data;
using MealPlanner.Models;
using Microsoft.EntityFrameworkCore;

namespace MealPlanner.Services;

public enum ChangeKind
{
    Created,
    Updated,
    Removed,
    Unchanged,
    NotFound,
}

/// <summary>
/// Describes a single mutation to the inventory so the UI can show Claude's
/// proposed/applied changes as a legible diff rather than a black box.
/// </summary>
public record InventoryChange(
    string Name,
    ChangeKind Kind,
    StockLevel? Before,
    StockLevel? After,
    IngredientCategory Category)
{
    public string Describe() => Kind switch
    {
        ChangeKind.Created => $"Added \"{Name}\" ({After}) to {Category}",
        ChangeKind.Updated => $"\"{Name}\": {Before} → {After}",
        ChangeKind.Removed => $"Removed \"{Name}\"",
        ChangeKind.Unchanged => $"\"{Name}\" unchanged ({After})",
        ChangeKind.NotFound => $"\"{Name}\" not found",
        _ => Name,
    };
}

/// <summary>
/// The single choke point for reading and mutating inventory. Both the Blazor
/// UI and Claude's MCP tools call through here, so there's one source of truth
/// and every change is uniform. Uses a DbContext factory because Blazor Server
/// circuits are long-lived and MCP tool calls arrive on their own scopes.
/// </summary>
public class InventoryService
{
    private readonly IDbContextFactory<MealPlannerDbContext> _factory;

    public InventoryService(IDbContextFactory<MealPlannerDbContext> factory)
    {
        _factory = factory;
    }

    public async Task<List<InventoryItem>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.InventoryItems
            .OrderBy(i => i.Category)
            .ThenBy(i => i.Name)
            .ToListAsync(ct);
    }

    public async Task<InventoryItem?> FindAsync(string name, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.InventoryItems
            .FirstOrDefaultAsync(i => i.Name == name, ct);
    }

    /// <summary>
    /// Creates the item if new, or updates quantity/category/notes if it exists.
    /// Category/notes are only overwritten when a value is supplied.
    /// </summary>
    public async Task<InventoryChange> UpsertAsync(
        string name,
        StockLevel quantity,
        IngredientCategory? category = null,
        string? notes = null,
        CancellationToken ct = default)
    {
        name = name.Trim();
        await using var db = await _factory.CreateDbContextAsync(ct);
        var existing = await db.InventoryItems.FirstOrDefaultAsync(i => i.Name == name, ct);

        if (existing is null)
        {
            var item = new InventoryItem
            {
                Name = name,
                Quantity = quantity,
                Category = category ?? IngredientCategory.Other,
                Notes = notes,
                UpdatedAt = DateTime.UtcNow,
            };
            db.InventoryItems.Add(item);
            await db.SaveChangesAsync(ct);
            return new InventoryChange(item.Name, ChangeKind.Created, null, quantity, item.Category);
        }

        var before = existing.Quantity;
        var changed = before != quantity
            || (category.HasValue && existing.Category != category.Value)
            || (notes is not null && existing.Notes != notes);

        existing.Quantity = quantity;
        if (category.HasValue) existing.Category = category.Value;
        if (notes is not null) existing.Notes = notes;
        existing.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return new InventoryChange(
            existing.Name,
            changed ? ChangeKind.Updated : ChangeKind.Unchanged,
            before,
            quantity,
            existing.Category);
    }

    public async Task<InventoryChange> RemoveAsync(string name, CancellationToken ct = default)
    {
        name = name.Trim();
        await using var db = await _factory.CreateDbContextAsync(ct);
        var existing = await db.InventoryItems.FirstOrDefaultAsync(i => i.Name == name, ct);
        if (existing is null)
        {
            return new InventoryChange(name, ChangeKind.NotFound, null, null, IngredientCategory.Other);
        }

        var before = existing.Quantity;
        db.InventoryItems.Remove(existing);
        await db.SaveChangesAsync(ct);
        return new InventoryChange(existing.Name, ChangeKind.Removed, before, null, existing.Category);
    }

    /// <summary>Save an item edited in the UI (create or update by Id).</summary>
    public async Task SaveAsync(InventoryItem item, CancellationToken ct = default)
    {
        item.Name = item.Name.Trim();
        item.UpdatedAt = DateTime.UtcNow;
        await using var db = await _factory.CreateDbContextAsync(ct);
        if (item.Id == 0)
        {
            db.InventoryItems.Add(item);
        }
        else
        {
            db.InventoryItems.Update(item);
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var item = await db.InventoryItems.FindAsync([id], ct);
        if (item is not null)
        {
            db.InventoryItems.Remove(item);
            await db.SaveChangesAsync(ct);
        }
    }
}
