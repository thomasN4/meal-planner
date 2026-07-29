using MealPlanner.Data;
using MealPlanner.Models;
using Microsoft.Data.Sqlite;
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
    string? Before,
    string? After,
    IngredientCategory Category)
{
    public string Describe() => Kind switch
    {
        ChangeKind.Created when string.IsNullOrEmpty(After) => $"Added \"{Name}\" to {Category}",
        ChangeKind.Created => $"Added \"{Name}\" ({After}) to {Category}",
        ChangeKind.Updated => $"\"{Name}\": {Display(Before)} → {Display(After)}",
        ChangeKind.Removed => $"Removed \"{Name}\"",
        ChangeKind.Unchanged => $"\"{Name}\" unchanged ({Display(After)})",
        ChangeKind.NotFound => $"\"{Name}\" not found",
        _ => Name,
    };

    private static string Display(string? quantity) =>
        string.IsNullOrEmpty(quantity) ? "unspecified" : quantity;
}

/// <summary>
/// The single choke point for reading and mutating inventory. Both the Blazor
/// UI and Claude's MCP tools call through here, so there's one source of truth
/// and every change is uniform. Uses a DbContext factory because Blazor Server
/// circuits are long-lived and MCP tool calls arrive on their own scopes.
/// </summary>
public class InventoryService
{
    // EF doesn't enforce [MaxLength] at save time and SQLite ignores TEXT
    // lengths, so the service is the trust boundary — especially for values
    // arriving from Claude's MCP tools rather than the constrained UI form.
    private const int MaxNameLength = 100;
    private const int MaxQuantityLength = 50;
    private const int MaxNotesLength = 500;

    // How many times an upsert may lose a write race before giving up. One is
    // not enough: every losing attempt flips branch (a lost insert retries into
    // an update, an update whose row was deleted retries into an insert), so
    // three or more writers on one name can keep flipping a writer into a fresh
    // loss. Raising the count alone still lost under load — writers retry in
    // lockstep and re-collide — hence the jittered backoff below.
    private const int MaxUpsertAttempts = 8;

    private readonly IDbContextFactory<MealPlannerDbContext> _factory;
    private readonly InventoryChangeNotifier _notifier;

    public InventoryService(
        IDbContextFactory<MealPlannerDbContext> factory,
        InventoryChangeNotifier notifier)
    {
        _factory = factory;
        _notifier = notifier;
    }

    private async Task PublishIfMeaningfulAsync(InventoryChange change)
    {
        // Skip no-ops so UI circuits don't churn on Unchanged / NotFound.
        if (change.Kind is ChangeKind.Created or ChangeKind.Updated or ChangeKind.Removed)
        {
            await _notifier.PublishAsync(change);
        }
    }

    private static string NormalizeName(string name)
    {
        name = name.Trim();
        if (name.Length == 0)
        {
            throw new ArgumentException("Ingredient name must not be empty.", nameof(name));
        }
        return name.Length <= MaxNameLength ? name : name[..MaxNameLength].TrimEnd();
    }

    private static string NormalizeQuantity(string? quantity)
    {
        quantity = quantity?.Trim() ?? string.Empty;
        return quantity.Length <= MaxQuantityLength
            ? quantity
            : quantity[..MaxQuantityLength].TrimEnd();
    }

    private static string? NormalizeNotes(string? notes) =>
        notes is null || notes.Length <= MaxNotesLength ? notes : notes[..MaxNotesLength];

    /// <summary>
    /// True for failures caused by two writers racing (the UI and Claude can
    /// genuinely write at the same time): a lost insert race tripping the
    /// unique Name index, or updating/removing a row another writer deleted.
    /// </summary>
    private static bool IsWriteRace(DbUpdateException ex) =>
        ex is DbUpdateConcurrencyException
        || ex.InnerException is SqliteException { SqliteErrorCode: 19 }; // SQLITE_CONSTRAINT

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
        string quantity = "",
        IngredientCategory? category = null,
        string? notes = null,
        CancellationToken ct = default)
    {
        name = NormalizeName(name);
        quantity = NormalizeQuantity(quantity);
        notes = NormalizeNotes(notes);

        // Read-then-write is racy, so retry a bounded number of times: each
        // attempt re-reads and takes whichever branch the world is now in.
        // Exhausting the budget rethrows rather than returning a diff that
        // never happened — a write this contended should fail loudly.
        InventoryChange change;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                change = await UpsertOnceAsync(name, quantity, category, notes, ct);
                break;
            }
            catch (DbUpdateException ex)
                when (attempt < MaxUpsertAttempts - 1 && IsWriteRace(ex))
            {
                // The first retry goes straight back: a plain two-writer race
                // is already settled by the time we re-read, and this is the
                // common case worth keeping fast. Losing twice means a genuine
                // storm, so back off by a growing jittered few milliseconds —
                // without it, the same writers keep retrying in step and
                // re-colliding, which is how the budget got exhausted at all.
                if (attempt > 0)
                {
                    await Task.Delay(Random.Shared.Next(2, 10) * attempt, ct);
                }
            }
        }

        // Publishing sits outside the retry loop: inside it, a DbUpdateException
        // surfacing from a subscriber would re-enter the loop and write twice.
        await PublishIfMeaningfulAsync(change);
        return change;
    }

    private async Task<InventoryChange> UpsertOnceAsync(
        string name,
        string quantity,
        IngredientCategory? category,
        string? notes,
        CancellationToken ct)
    {
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
        name = NormalizeName(name);
        await using var db = await _factory.CreateDbContextAsync(ct);
        var existing = await db.InventoryItems.FirstOrDefaultAsync(i => i.Name == name, ct);
        if (existing is null)
        {
            return new InventoryChange(name, ChangeKind.NotFound, null, null, IngredientCategory.Other);
        }

        var before = existing.Quantity;
        db.InventoryItems.Remove(existing);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another writer already removed it.
            return new InventoryChange(name, ChangeKind.NotFound, null, null, existing.Category);
        }

        var change = new InventoryChange(existing.Name, ChangeKind.Removed, before, null, existing.Category);
        await PublishIfMeaningfulAsync(change);
        return change;
    }

    /// <summary>Save an item edited in the UI (create or update by Id).</summary>
    public async Task SaveAsync(InventoryItem item, CancellationToken ct = default)
    {
        item.Name = NormalizeName(item.Name);
        item.Quantity = NormalizeQuantity(item.Quantity);
        item.Notes = NormalizeNotes(item.Notes);
        item.UpdatedAt = DateTime.UtcNow;
        var creating = item.Id == 0;
        await using var db = await _factory.CreateDbContextAsync(ct);

        // The caller hands us an item it has already edited, so the old
        // quantity only exists in the database. Read it before saving, or the
        // diff reports "unspecified → 3 bags" for every update. Projecting to
        // the string rather than loading the row keeps EF from tracking a
        // second instance of this key, which Update(item) would then reject.
        var before = creating
            ? null
            : await db.InventoryItems
                .Where(i => i.Id == item.Id)
                .Select(i => i.Quantity)
                .FirstOrDefaultAsync(ct);

        if (creating)
        {
            db.InventoryItems.Add(item);
        }
        else
        {
            db.InventoryItems.Update(item);
        }
        await db.SaveChangesAsync(ct);

        await PublishIfMeaningfulAsync(new InventoryChange(
            item.Name,
            creating ? ChangeKind.Created : ChangeKind.Updated,
            Before: before,
            After: item.Quantity,
            Category: item.Category));
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var item = await db.InventoryItems.FindAsync([id], ct);
        if (item is null)
        {
            return;
        }

        var name = item.Name;
        var before = item.Quantity;
        var category = item.Category;
        db.InventoryItems.Remove(item);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another writer already removed it — nothing to do.
            return;
        }

        await PublishIfMeaningfulAsync(new InventoryChange(name, ChangeKind.Removed, before, null, category));
    }
}
