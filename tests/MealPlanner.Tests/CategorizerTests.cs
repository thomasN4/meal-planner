using Microsoft.Extensions.Options;

namespace MealPlanner.Tests;

/// <summary>
/// The categorizer against the real service graph, with the classifier faked —
/// spawning the claude CLI in a unit test would be slow, non-deterministic, and
/// would need network and credentials. What is under test here is the queueing,
/// batching and write-back, all of which are ours.
/// </summary>
public class CategorizerTests
{
    [Fact]
    public async Task An_item_created_without_a_category_is_filed_automatically()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        var classifier = new FakeClassifier(_ => IngredientCategory.FreshHerbs);
        await using var running = await StartAsync(harness, classifier);

        await harness.Service.UpsertAsync("Coriander", "1 bunch");

        await WaitForCategoryAsync(harness, "Coriander", IngredientCategory.FreshHerbs);
    }

    [Fact]
    public async Task An_item_created_with_a_category_is_never_sent_to_the_classifier()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        var classifier = new FakeClassifier(_ => IngredientCategory.Snacks);
        await using var running = await StartAsync(harness, classifier);

        await harness.Service.UpsertAsync("Basil", "1 pot", IngredientCategory.FreshHerbs);
        // Push something the categorizer *will* pick up, and wait for it: the
        // reader is single-threaded, so once this is done the Basil write has
        // long since been seen and skipped.
        await harness.Service.UpsertAsync("Rice");
        await WaitForCategoryAsync(harness, "Rice", IngredientCategory.Snacks);

        Assert.Equal([["Rice"]], classifier.CallNames);
    }

    [Fact]
    public async Task Items_created_together_go_out_as_one_call()
    {
        // The point of the whole design: one MCP "stock the kitchen" turn
        // creates a dozen items, and a dozen names cost what one costs.
        await using var harness = await InventoryHarness.CreateAsync();
        var classifier = new FakeClassifier(_ => IngredientCategory.Grains);
        await using var running = await StartAsync(
            harness, classifier, configure: o => o.BatchWindowMilliseconds = 2000);

        // Deliberately sequential, and deliberately not InParallelAsync. What is
        // under test is "arrivals inside the window share a batch", not a write
        // race — and five parallel writers contending on one SQLite file
        // sometimes take longer to finish than the window itself, which split
        // the batch and made this test flaky about half the time.
        for (var i = 0; i < 5; i++)
        {
            await harness.Service.UpsertAsync($"Grain {i}");
        }

        await WaitForCategoryAsync(harness, "Grain 4", IngredientCategory.Grains);
        var call = Assert.Single(classifier.Calls);
        Assert.Equal(5, call.Length);
    }

    [Fact]
    public async Task A_name_seen_before_is_not_classified_twice()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        var classifier = new FakeClassifier(_ => IngredientCategory.DrySeasonings);
        await using var running = await StartAsync(harness, classifier);

        await harness.Service.UpsertAsync("Salt");
        await WaitForCategoryAsync(harness, "Salt", IngredientCategory.DrySeasonings);

        // Running out of salt and buying more is the most ordinary thing this
        // kitchen does; it must not cost a round trip every time.
        await harness.Service.RemoveAsync("Salt");
        await harness.Service.UpsertAsync("salt");
        await WaitForCategoryAsync(harness, "salt", IngredientCategory.DrySeasonings);

        Assert.Equal([["Salt"]], classifier.CallNames);
    }

    [Fact]
    public async Task Filing_something_under_Other_by_hand_sticks()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        var classifier = new FakeClassifier(_ => IngredientCategory.Grains);
        await using var running = await StartAsync(harness, classifier);

        await harness.Service.UpsertAsync("Rice");
        await WaitForCategoryAsync(harness, "Rice", IngredientCategory.Grains);

        // Someone decides Rice really does belong in Other and picks it from the
        // row dropdown. Only creations are classified, so this is final — were
        // updates queued too, the categorizer would drag it straight back out
        // and the household could never file anything under Other.
        await harness.Service.SetCategoryAsync("Rice", IngredientCategory.Other);

        // Ordering signal: batches run one at a time, so once this lands any
        // queued work for Rice would already have been applied.
        await harness.Service.UpsertAsync("Oats");
        await WaitForCategoryAsync(harness, "Oats", IngredientCategory.Grains);

        var stored = await harness.Service.FindAsync("Rice");
        Assert.Equal(IngredientCategory.Other, stored!.Category);
        Assert.Equal([["Rice"], ["Oats"]], classifier.CallNames);
    }

    [Fact]
    public async Task Editing_a_quantity_does_not_reclassify()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        var classifier = new FakeClassifier(_ => IngredientCategory.Grains);
        await using var running = await StartAsync(harness, classifier);

        await harness.Service.UpsertAsync("Rice");
        await WaitForCategoryAsync(harness, "Rice", IngredientCategory.Grains);

        await harness.Service.UpsertAsync("Rice", "half a bag", IngredientCategory.Grains);
        await harness.Service.UpsertAsync("Oats");
        await WaitForCategoryAsync(harness, "Oats", IngredientCategory.Grains);

        Assert.Equal([["Rice"], ["Oats"]], classifier.CallNames);
    }

    [Fact]
    public async Task A_note_typed_at_creation_reaches_the_classifier()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        var classifier = new FakeClassifier(_ => IngredientCategory.Baking);
        await using var running = await StartAsync(harness, classifier);

        await harness.Service.UpsertAsync(
            "Arrow root starch", "1 bag", notes: "for thickening sauces");
        await WaitForCategoryAsync(harness, "Arrow root starch", IngredientCategory.Baking);

        var request = Assert.Single(Assert.Single(classifier.Calls));
        Assert.Equal("Arrow root starch", request.Name);
        // The note was in the same write as the name, and the change record is
        // the only thing that could carry it here (issue #21).
        Assert.Equal("for thickening sauces", request.Notes);
    }

    [Fact]
    public async Task A_note_can_change_where_something_is_filed()
    {
        // The issue's own example, end to end: the name alone says "root", the
        // note says "starch", and only one of those is on the shelf.
        await using var harness = await InventoryHarness.CreateAsync();
        var classifier = new FakeClassifier(r => r.Notes?.Contains("thickening") == true
            ? IngredientCategory.Baking
            : IngredientCategory.Produce);
        await using var running = await StartAsync(harness, classifier);

        await harness.Service.UpsertAsync("Arrow root", notes: "for thickening sauces");

        await WaitForCategoryAsync(harness, "Arrow root", IngredientCategory.Baking);
    }

    [Fact]
    public async Task An_item_created_without_a_note_sends_no_note()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        var classifier = new FakeClassifier(_ => IngredientCategory.Grains);
        await using var running = await StartAsync(harness, classifier);

        // The add form sends "" for an empty box; MCP sends null. Neither is a
        // note, and the classifier must not be handed an empty string to read
        // meaning into.
        await harness.Service.UpsertAsync("Rice", "1 bag", notes: "");
        await WaitForCategoryAsync(harness, "Rice", IngredientCategory.Grains);

        var request = Assert.Single(Assert.Single(classifier.Calls));
        Assert.Null(request.Notes);
    }

    [Fact]
    public async Task The_same_name_with_a_different_note_is_classified_again()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        var classifier = new FakeClassifier(r => r.Notes is null
            ? IngredientCategory.Produce
            : IngredientCategory.Baking);
        await using var running = await StartAsync(harness, classifier);

        await harness.Service.UpsertAsync("Arrow root");
        await WaitForCategoryAsync(harness, "Arrow root", IngredientCategory.Produce);

        // Same name, and the cache is keyed on it — but the note is new
        // information, and serving the cached answer would ignore it exactly the
        // way this whole change exists to stop.
        await harness.Service.RemoveAsync("Arrow root");
        await harness.Service.UpsertAsync("Arrow root", notes: "for thickening sauces");

        await WaitForCategoryAsync(harness, "Arrow root", IngredientCategory.Baking);
        Assert.Equal(2, classifier.Calls.Count);
    }

    [Fact]
    public async Task Adding_a_note_later_does_not_reclassify()
    {
        // Deliberate, and the sharpest version of the decision: this item is
        // still in Other, so a later note is the one case where reclassifying
        // could plausibly help. It still doesn't happen — a note is read once,
        // when the item arrives. Nothing distinguishes "nobody has looked at
        // this yet" from "somebody filed it here", so honouring the second write
        // would also drag a hand-filed row back out of Other.
        //
        // Written against an item left in Other on purpose: a test using an item
        // the classifier *did* place passes whether or not updates are queued,
        // because the "created, and in Other" filter excludes it on the category
        // alone. It asserts nothing.
        await using var harness = await InventoryHarness.CreateAsync();
        var classifier = new FakeClassifier(r => r.Notes is null
            ? null
            : IngredientCategory.Baking);
        await using var running = await StartAsync(harness, classifier);

        await harness.Service.UpsertAsync("Mystery jar");
        await WaitForAsync(() => classifier.Calls.Count == 1, "the first classification");

        await harness.Service.UpsertAsync("Mystery jar", notes: "for thickening sauces");

        // Ordering signal: batches run one at a time, so once this lands any
        // queued work for the jar would already have been applied.
        await harness.Service.UpsertAsync("Flour", notes: "plain");
        await WaitForCategoryAsync(harness, "Flour", IngredientCategory.Baking);

        var stored = await harness.Service.FindAsync("Mystery jar");
        Assert.Equal(IngredientCategory.Other, stored!.Category);
        Assert.Equal([["Mystery jar"], ["Flour"]], classifier.CallNames);
    }

    [Fact]
    public async Task A_category_chosen_by_hand_survives_a_late_answer()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        var classifier = new FakeClassifier(_ => IngredientCategory.FreshHerbs)
        {
            Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        await using var running = await StartAsync(harness, classifier);

        await harness.Service.UpsertAsync("Coriander", "1 jar");
        await WaitForAsync(
            () => classifier.Calls.Count == 1, "the classifier to be called");

        // Someone in the kitchen decides while the model is still thinking.
        await harness.Service.SetCategoryAsync("Coriander", IngredientCategory.DrySeasonings);
        classifier.Gate!.SetResult();

        // Ordering signal: batches are processed one at a time, so once this
        // second item lands the stale answer has already been applied — or, as
        // asserted below, correctly dropped.
        classifier.Gate = null;
        await harness.Service.UpsertAsync("Oats");
        await WaitForCategoryAsync(harness, "Oats", IngredientCategory.FreshHerbs);

        var stored = await harness.Service.FindAsync("Coriander");
        Assert.Equal(IngredientCategory.DrySeasonings, stored!.Category);
    }

    [Fact]
    public async Task A_name_the_classifier_cannot_place_is_left_where_it_is()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        var classifier = new FakeClassifier(
            r => r.Name == "Mystery jar" ? null : IngredientCategory.Dairy);
        await using var running = await StartAsync(harness, classifier);

        await harness.Service.UpsertAsync("Mystery jar");
        await harness.Service.UpsertAsync("Butter");
        await WaitForCategoryAsync(harness, "Butter", IngredientCategory.Dairy);

        var stored = await harness.Service.FindAsync("Mystery jar");
        Assert.Equal(IngredientCategory.Other, stored!.Category);
    }

    [Fact]
    public async Task A_classifier_that_throws_does_not_kill_the_loop()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        var log = new CapturingLogger<IngredientCategorizer>();
        var classifier = new FakeClassifier(_ => IngredientCategory.Dairy) { Throw = true };
        await using var running = await StartAsync(harness, classifier, log);

        await harness.Service.UpsertAsync("Butter");
        await WaitForAsync(() => log.Errors.Count == 1, "the failure to be logged");

        // The interface forbids throwing, but an escaping exception from a
        // BackgroundService stops the host by default — the whole inventory
        // would go down with the classifier.
        classifier.Throw = false;
        await harness.Service.UpsertAsync("Cheddar");
        await WaitForCategoryAsync(harness, "Cheddar", IngredientCategory.Dairy);
    }

    [Fact]
    public async Task A_disabled_categorizer_classifies_nothing()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        var classifier = new FakeClassifier(_ => IngredientCategory.Dairy);
        await using var running = await StartAsync(
            harness, classifier, configure: o => o.Enabled = false);

        await harness.Service.UpsertAsync("Butter");
        await Task.Delay(200);

        Assert.Empty(classifier.Calls);
        var stored = await harness.Service.FindAsync("Butter");
        Assert.Equal(IngredientCategory.Other, stored!.Category);
    }

    [Fact]
    public async Task Stopping_unsubscribes_from_the_notifier()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        var classifier = new FakeClassifier(_ => IngredientCategory.Dairy);
        var running = await StartAsync(harness, classifier);
        await running.DisposeAsync();

        await harness.Service.UpsertAsync("Butter");
        await Task.Delay(200);

        // Unsubscribe matches on delegate equality, so a lambda subscription
        // would leave this handler attached to a stopped service forever.
        Assert.Empty(classifier.Calls);
    }

    private static async Task<RunningCategorizer> StartAsync(
        InventoryHarness harness,
        IIngredientClassifier classifier,
        CapturingLogger<IngredientCategorizer>? log = null,
        Action<CategorizationOptions>? configure = null)
    {
        var options = new CategorizationOptions
        {
            // Generous enough that writes issued together always share a batch,
            // even on a loaded CI machine.
            BatchWindowMilliseconds = 500,
        };
        configure?.Invoke(options);

        var categorizer = new IngredientCategorizer(
            harness.Notifier,
            classifier,
            harness.NewService,
            Options.Create(options),
            log ?? new CapturingLogger<IngredientCategorizer>());

        await categorizer.StartAsync(CancellationToken.None);
        return new RunningCategorizer(categorizer);
    }

    private static async Task WaitForCategoryAsync(
        InventoryHarness harness, string name, IngredientCategory expected)
    {
        InventoryItem? stored = null;
        await WaitForAsync(
            async () =>
            {
                stored = await harness.Service.FindAsync(name);
                return stored?.Category == expected;
            },
            $"\"{name}\" to be filed under {expected} (was {stored?.Category.ToString() ?? "missing"})");
    }

    private static Task WaitForAsync(Func<bool> condition, string because) =>
        WaitForAsync(() => Task.FromResult(condition()), because);

    private static async Task WaitForAsync(Func<Task<bool>> condition, string because)
    {
        // The categorizer works on its own thread; there is no completion to
        // await from the outside without wiring a test-only hook into it.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(15);
        }

        Assert.Fail($"Timed out waiting for {because}.");
    }

    private sealed class RunningCategorizer(IngredientCategorizer categorizer) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await categorizer.StopAsync(CancellationToken.None);
            categorizer.Dispose();
        }
    }

    private sealed class FakeClassifier(Func<ClassificationRequest, IngredientCategory?> classify)
        : IIngredientClassifier
    {
        private readonly Lock _gate = new();
        private readonly List<ClassificationRequest[]> _calls = [];

        // Deliberately the only constructor. A convenience overload taking
        // Func<string, …> alongside this one makes every `_ => Category.X` here
        // ambiguous — both delegate types accept it.

        /// <summary>Held open to simulate a slow model.</summary>
        public TaskCompletionSource? Gate { get; set; }

        public bool Throw { get; set; }

        public IReadOnlyList<ClassificationRequest[]> Calls
        {
            get
            {
                lock (_gate)
                {
                    return _calls.ToArray();
                }
            }
        }

        /// <summary>
        /// What most tests here assert on: one batch is a list of names, and the
        /// note is a separate concern with its own tests.
        /// </summary>
        public IReadOnlyList<string[]> CallNames =>
            Calls.Select(call => call.Select(r => r.Name).ToArray()).ToArray();

        public async Task<IReadOnlyList<IngredientCategory?>> ClassifyAsync(
            IReadOnlyList<ClassificationRequest> requests, CancellationToken ct = default)
        {
            lock (_gate)
            {
                _calls.Add(requests.ToArray());
            }

            if (Gate is { } gate)
            {
                await gate.Task.WaitAsync(ct);
            }

            if (Throw)
            {
                throw new InvalidOperationException("classifier exploded");
            }

            return requests.Select(classify).ToArray();
        }
    }
}
