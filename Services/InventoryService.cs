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
    NameTaken,
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
    /// <summary>
    /// Set whenever a mutation moved the item between categories — the
    /// auto-categorizer, a row editor's save, anyone. The UI reads it directly
    /// to open the destination group, so it must not depend on how Describe()
    /// happens to word the change. Deliberately not a positional member: adding
    /// one would rewrite every construction site and every test that builds this
    /// record positionally.
    /// </summary>
    public IngredientCategory? PreviousCategory { get; init; }

    /// <summary>
    /// The name the item had before, when a mutation renamed it. Same shape and
    /// same reasoning as <see cref="PreviousCategory"/>. <see cref="Name"/> is
    /// always the name the item has <em>now</em>, except on
    /// <see cref="ChangeKind.NameTaken"/>, where it is the name that was refused.
    /// </summary>
    public string? PreviousName { get; init; }

    public string Describe() => Kind switch
    {
        ChangeKind.Created when string.IsNullOrEmpty(After) => $"Added \"{Name}\" to {Category}",
        ChangeKind.Created => $"Added \"{Name}\" ({After}) to {Category}",
        // A rename reports only the rename, even when the same write changed a
        // quantity too: one headline per change, the same way the branches below
        // already pick one axis. The row is in front of the user showing the rest.
        ChangeKind.Updated when PreviousName is { } previousName =>
            $"Renamed \"{previousName}\" to \"{Name}\"",
        // SetCategoryAsync passes the same quantity on both sides, so it always
        // lands here and reads as a pure move. The row editor can change both at
        // once, which SetCategoryAsync never could — hence the branch after it.
        ChangeKind.Updated when PreviousCategory is { } previous && Before == After =>
            $"\"{Name}\": {previous} → {Category}",
        ChangeKind.Updated when PreviousCategory is { } previous =>
            $"\"{Name}\": {previous} → {Category}, {Display(Before)} → {Display(After)}",
        ChangeKind.Updated => $"\"{Name}\": {Display(Before)} → {Display(After)}",
        ChangeKind.Removed => $"Removed \"{Name}\"",
        ChangeKind.Unchanged => $"\"{Name}\" unchanged ({Display(After)})",
        ChangeKind.NotFound => $"\"{Name}\" not found",
        ChangeKind.NameTaken => $"\"{Name}\" is already on the list",
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

    /// <summary>
    /// Changes only an item's category, leaving quantity and notes alone.
    /// <para>
    /// Not a thin wrapper over <see cref="UpsertAsync"/> on purpose: that method
    /// writes the quantity it was handed, so a caller who wanted to move an item
    /// between categories would have to read the quantity first and hand it back,
    /// erasing anything typed in between. The auto-categorizer writes seconds
    /// after the item was created, which is exactly when someone is still typing.
    /// </para>
    /// <para>
    /// <paramref name="onlyIf"/> makes the write conditional on the category the
    /// caller last saw. The categorizer passes <c>Other</c>, so a household member
    /// who classified the item by hand while Claude was thinking keeps their
    /// choice — the late write becomes a no-op instead of overwriting them.
    /// </para>
    /// </summary>
    public async Task<InventoryChange> SetCategoryAsync(
        string name,
        IngredientCategory category,
        IngredientCategory? onlyIf = null,
        CancellationToken ct = default)
    {
        name = NormalizeName(name);

        // Same bounded retry as UpsertAsync, for the same reason: the row can be
        // deleted between the read and the write by another household member.
        InventoryChange change;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                change = await SetCategoryOnceAsync(name, category, onlyIf, ct);
                break;
            }
            catch (DbUpdateException ex)
                when (attempt < MaxUpsertAttempts - 1 && IsWriteRace(ex))
            {
                if (attempt > 0)
                {
                    await Task.Delay(Random.Shared.Next(2, 10) * attempt, ct);
                }
            }
        }

        await PublishIfMeaningfulAsync(change);
        return change;
    }

    private async Task<InventoryChange> SetCategoryOnceAsync(
        string name,
        IngredientCategory category,
        IngredientCategory? onlyIf,
        CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var existing = await db.InventoryItems.FirstOrDefaultAsync(i => i.Name == name, ct);
        if (existing is null)
        {
            return new InventoryChange(name, ChangeKind.NotFound, null, null, category);
        }

        var before = existing.Category;
        if (before == category || (onlyIf.HasValue && before != onlyIf.Value))
        {
            // Already there, or someone else got here first. Unchanged is not
            // published, so this costs the UI nothing.
            return new InventoryChange(
                existing.Name, ChangeKind.Unchanged, existing.Quantity, existing.Quantity, before);
        }

        existing.Category = category;
        existing.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return new InventoryChange(
            existing.Name, ChangeKind.Updated, existing.Quantity, existing.Quantity, category)
        {
            PreviousCategory = before,
        };
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

    /// <summary>
    /// Rewrites one existing row, identified by <paramref name="id"/>, in a
    /// single write — the inventory page's row editor.
    /// <para>
    /// Keyed by Id rather than name because this is the one mutation that can
    /// change the name, and a name stops being a handle the moment it does.
    /// Everything else here keys on Name, which the NOCASE unique index makes
    /// identity; renaming is precisely the operation that identity can't express.
    /// </para>
    /// <para>
    /// <paramref name="notes"/> of <c>null</c> keeps the stored value, matching
    /// <see cref="UpsertAsync"/>. Callers that mean "clear the notes" pass "".
    /// </para>
    /// <para>
    /// Renaming onto a name another row already holds returns
    /// <see cref="ChangeKind.NameTaken"/> rather than merging. Quantity is free
    /// text, so there is no defensible way to combine "2 bags" with "half a
    /// bottle" — the caller is told, and decides.
    /// </para>
    /// </summary>
    public async Task<InventoryChange> UpdateItemAsync(
        int id,
        string name,
        string quantity,
        IngredientCategory category,
        string? notes = null,
        CancellationToken ct = default)
    {
        name = NormalizeName(name);
        quantity = NormalizeQuantity(quantity);
        notes = NormalizeNotes(notes);

        // Narrower than UpsertAsync's loop: DbUpdateConcurrencyException only,
        // not IsWriteRace. The one race worth retrying here is the row being
        // deleted underneath us, which re-reads into a clean NotFound.
        //
        // A name collision is handled in UpdateOnceAsync instead. Measured, not
        // assumed: widening this to IsWriteRace also ends at NameTaken, because
        // the losing writer's retry re-reads and its pre-check now sees the name
        // taken — so this is one round trip saved, not a correctness guard, and
        // Parallel_renames_onto_one_name_leave_exactly_one_winner passes either
        // way. It is written out because "why isn't this IsWriteRace like the
        // other two loops" is the question a reader will actually have.
        InventoryChange change;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                change = await UpdateOnceAsync(id, name, quantity, category, notes, ct);
                break;
            }
            catch (DbUpdateConcurrencyException)
                when (attempt < MaxUpsertAttempts - 1)
            {
                if (attempt > 0)
                {
                    await Task.Delay(Random.Shared.Next(2, 10) * attempt, ct);
                }
            }
        }

        await PublishIfMeaningfulAsync(change);
        return change;
    }

    private async Task<InventoryChange> UpdateOnceAsync(
        int id,
        string name,
        string quantity,
        IngredientCategory category,
        string? notes,
        CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var existing = await db.InventoryItems.FirstOrDefaultAsync(i => i.Id == id, ct);
        if (existing is null)
        {
            return new InventoryChange(name, ChangeKind.NotFound, null, null, category);
        }

        // i.Id != id is not defensive, it is required: the Name column collates
        // NOCASE, so "Salt" matches the row we are editing when its stored name
        // is "salt". Without it, every case-only fix reports a collision with
        // itself and the one thing the NOCASE index exists to allow is refused.
        if (await db.InventoryItems.AnyAsync(i => i.Name == name && i.Id != id, ct))
        {
            return new InventoryChange(name, ChangeKind.NameTaken, null, null, category);
        }

        var beforeName = existing.Name;
        var beforeQuantity = existing.Quantity;
        var beforeCategory = existing.Category;
        var changed = beforeName != name
            || beforeQuantity != quantity
            || beforeCategory != category
            || (notes is not null && existing.Notes != notes);

        existing.Name = name;
        existing.Quantity = quantity;
        existing.Category = category;
        if (notes is not null) existing.Notes = notes;
        existing.UpdatedAt = DateTime.UtcNow;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsNameCollision(ex))
        {
            // Reached for real — the pre-check above cannot close the gap
            // between itself and this write, and the parallel-rename test drives
            // writers through here every run. Answering directly rather than
            // letting the retry loop handle it, because a retry can only re-read
            // and reach the same NameTaken one round trip later.
            return new InventoryChange(name, ChangeKind.NameTaken, null, null, category);
        }

        return new InventoryChange(
            name,
            changed ? ChangeKind.Updated : ChangeKind.Unchanged,
            beforeQuantity,
            quantity,
            category)
        {
            // Each set only when that field actually moved, so Describe() and
            // the page's accordion both read them as "this changed", not
            // "this was supplied".
            PreviousName = beforeName != name ? beforeName : null,
            PreviousCategory = beforeCategory != category ? beforeCategory : null,
        };
    }

    /// <summary>
    /// The unique Name index rejecting a write. Distinct from
    /// <see cref="IsWriteRace"/>, which folds this together with a deleted row:
    /// here the two need opposite handling.
    /// </summary>
    private static bool IsNameCollision(DbUpdateException ex) =>
        ex.InnerException is SqliteException { SqliteErrorCode: 19 }; // SQLITE_CONSTRAINT

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
