using Microsoft.EntityFrameworkCore;

namespace MealPlanner.Tests;

/// <summary>
/// Concurrent writers are this app's normal case, not an edge case: household
/// members on the LAN and a headless Claude over MCP write at the same time.
/// These are the checks AGENTS.md asks for after touching an InventoryService
/// write path — the ones that used to be rebuilt from prose each time.
///
/// Every test here goes through <see cref="InventoryHarness.InParallelAsync{T}"/>
/// rather than Task.WhenAll over a Select; see its remarks for why the obvious
/// spelling silently doesn't race.
/// </summary>
public class InventoryConcurrencyTests
{
    // Enough writers to lose an insert race reliably, few enough to stay quick.
    private const int Writers = 16;

    [Fact]
    public async Task Parallel_upserts_of_one_name_yield_one_row_and_no_exceptions()
    {
        await using var harness = await InventoryHarness.CreateAsync();

        // Each writer gets its own service, the way separate Blazor circuits
        // and MCP tool scopes each resolve their own.
        var changes = await InventoryHarness.InParallelAsync(Writers, i =>
            harness.NewService().UpsertAsync("Rice", $"{i} bags", IngredientCategory.Grains));

        Assert.Equal(1, await harness.CountAsync());
        // Exactly one writer created the row; the losers retried into updates.
        Assert.Equal(1, changes.Count(c => c.Kind == ChangeKind.Created));
        Assert.All(changes, c => Assert.Contains(
            c.Kind,
            new[] { ChangeKind.Created, ChangeKind.Updated, ChangeKind.Unchanged }));
    }

    [Fact]
    public async Task Parallel_upserts_of_one_name_ignoring_case_still_yield_one_row()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        var spellings = new[] { "Salt", "salt", "SALT", "SaLt" };

        // The unique index is NOCASE, so these all race for the same row.
        await InventoryHarness.InParallelAsync(Writers, i =>
            harness.NewService().UpsertAsync(spellings[i % spellings.Length], $"{i}"));

        Assert.Equal(1, await harness.CountAsync());
    }

    [Fact]
    public async Task Parallel_removes_yield_exactly_one_Removed()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        await harness.Service.UpsertAsync("Rice", "2 bags", IngredientCategory.Grains);

        var changes = await InventoryHarness.InParallelAsync(Writers, _ =>
            harness.NewService().RemoveAsync("Rice"));

        Assert.Equal(1, changes.Count(c => c.Kind == ChangeKind.Removed));
        Assert.Equal(Writers - 1, changes.Count(c => c.Kind == ChangeKind.NotFound));
        Assert.Equal(0, await harness.CountAsync());
    }

    [Fact]
    public async Task Parallel_removes_publish_exactly_one_change()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        await harness.Service.UpsertAsync("Rice", "2 bags", IngredientCategory.Grains);
        var published = 0;
        harness.Notifier.Subscribe(_ =>
        {
            Interlocked.Increment(ref published);
            return Task.CompletedTask;
        });

        await InventoryHarness.InParallelAsync(Writers, _ =>
            harness.NewService().RemoveAsync("Rice"));

        // The NotFound losers must stay off the bus, or every open browser
        // repaints once per racing writer.
        Assert.Equal(1, Volatile.Read(ref published));
    }

    [Fact]
    public async Task Parallel_upserts_of_different_names_all_land()
    {
        await using var harness = await InventoryHarness.CreateAsync();

        await InventoryHarness.InParallelAsync(Writers, i =>
            harness.NewService().UpsertAsync($"Ingredient {i}", $"{i} bags"));

        Assert.Equal(Writers, await harness.CountAsync());
    }

    [Fact]
    public async Task One_upsert_racing_one_remove_leaves_a_consistent_row_count()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        await harness.Service.UpsertAsync("Rice", "2 bags", IngredientCategory.Grains);

        // Two writers never needed more than one retry — whichever way they
        // interleave, the loser's second attempt sees a settled world. The
        // interleaving is opportunistic, so this passes even when they happen
        // not to collide — it is a regression guard, not a proof.
        await InventoryHarness.InParallelAsync(2, i => i == 0
            ? harness.NewService().UpsertAsync("Rice", "1 bag")
            : harness.NewService().RemoveAsync("Rice"));

        Assert.InRange(await harness.CountAsync(), 0, 1);
    }

    [Fact]
    public async Task Upserts_racing_removes_of_the_same_name_never_throw()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        await harness.Service.UpsertAsync("Rice", "2 bags", IngredientCategory.Grains);

        // Issue #5's regression guard. A single retry could not survive this:
        // every losing attempt flips branch, so with removes still in flight a
        // writer could lose twice and throw DbUpdateException at its caller.
        await InventoryHarness.InParallelAsync(Writers, i => i % 2 == 0
            ? harness.NewService().UpsertAsync("Rice", $"{i} bags")
            : harness.NewService().RemoveAsync("Rice"));

        // Whoever wins the last write, the NOCASE unique index must never let
        // a duplicate through and the database must stay readable.
        Assert.InRange(await harness.CountAsync(), 0, 1);
    }

    [Fact]
    public async Task A_write_failure_that_is_not_a_race_is_not_swallowed()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        // Read-only before the first connection opens, so reads still work and
        // only the write fails — with SQLITE_READONLY, which is not a race.
        File.SetAttributes(harness.DatabasePath, FileAttributes.ReadOnly);
        var published = new List<InventoryChange>();
        harness.Notifier.Subscribe(change =>
        {
            published.Add(change);
            return Task.CompletedTask;
        });

        try
        {
            // The wider retry budget must not turn a genuine failure into a
            // diff that never happened. It surfaces, and nothing is announced.
            await Assert.ThrowsAnyAsync<DbUpdateException>(
                () => harness.Service.UpsertAsync("Rice", "2 bags", IngredientCategory.Grains));
            Assert.Empty(published);
        }
        finally
        {
            File.SetAttributes(harness.DatabasePath, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task Concurrent_reads_during_writes_never_observe_a_duplicate()
    {
        await using var harness = await InventoryHarness.CreateAsync();

        await InventoryHarness.InParallelAsync(Writers * 2, async i =>
        {
            if (i % 2 == 0)
            {
                await harness.NewService()
                    .UpsertAsync("Rice", $"{i} bags", IngredientCategory.Grains);
                return;
            }

            var items = await harness.NewService().GetAllAsync();
            Assert.InRange(items.Count(item => item.Name == "Rice"), 0, 1);
        });
    }
}
