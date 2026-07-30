using Microsoft.Extensions.Logging;

namespace MealPlanner.Tests;

/// <summary>
/// The notifier's contract is easy to regress silently: a subscriber that
/// faults must be logged and must not stop the fan-out, and Unsubscribe only
/// works for method groups because it matches on delegate equality. The
/// async-faulting case in particular is what distinguishes the current
/// notifier from the fire-and-forget version it replaced.
/// </summary>
public class InventoryChangeNotifierTests
{
    private static InventoryChange SampleChange() =>
        new("Rice", ChangeKind.Updated, "2 bags", "1 bag", IngredientCategory.Grains);

    private static (InventoryChangeNotifier Notifier, CapturingLogger<InventoryChangeNotifier> Log) Build()
    {
        var log = new CapturingLogger<InventoryChangeNotifier>();
        return (new InventoryChangeNotifier(log), log);
    }

    [Fact]
    public async Task Publish_reaches_every_subscriber()
    {
        var (notifier, _) = Build();
        var seen = new List<string>();
        notifier.Subscribe(_ => { seen.Add("first"); return Task.CompletedTask; });
        notifier.Subscribe(_ => { seen.Add("second"); return Task.CompletedTask; });

        await notifier.PublishAsync(SampleChange());

        // Handlers are *started* in subscription order, which is all this pins.
        // Since the fan-out went concurrent (issue #4) the order they *finish*
        // in is not guaranteed — these two happen to complete synchronously.
        Assert.Equal(["first", "second"], seen);
    }

    [Fact]
    public async Task Subscribers_run_concurrently_rather_than_one_after_another()
    {
        var (notifier, _) = Build();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Deliberately interlocked: the first handler cannot finish until the
        // second has run. Awaiting each handler in turn never reaches the
        // second, so this is the assertion a sequential fan-out cannot pass —
        // which is the point of issue #4, where an MCP write waited on every
        // open browser's DB read and re-render in sequence.
        notifier.Subscribe(async _ => { second.SetResult(); await first.Task; });
        notifier.Subscribe(async _ => { await second.Task; first.SetResult(); });

        var publish = notifier.PublishAsync(SampleChange());

        // Bounded, so a regression fails the run instead of wedging it.
        var finished = await Task.WhenAny(publish, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(publish, finished);
        await publish;
    }

    [Fact]
    public async Task Publish_awaits_subscribers_rather_than_dropping_their_tasks()
    {
        var (notifier, _) = Build();
        var finished = false;
        notifier.Subscribe(async _ =>
        {
            await Task.Delay(20);
            finished = true;
        });

        await notifier.PublishAsync(SampleChange());

        // A fire-and-forget notifier would return here with finished still false.
        Assert.True(finished);
    }

    [Fact]
    public async Task A_subscriber_that_throws_synchronously_is_logged_and_does_not_starve_the_others()
    {
        var (notifier, log) = Build();
        var healthy = 0;
        notifier.Subscribe(_ => { healthy++; return Task.CompletedTask; });
        notifier.Subscribe(_ => throw new InvalidOperationException("circuit is gone"));
        notifier.Subscribe(_ => { healthy++; return Task.CompletedTask; });

        await notifier.PublishAsync(SampleChange());

        Assert.Equal(2, healthy);
        var error = Assert.Single(log.Errors);
        Assert.IsType<InvalidOperationException>(error.Exception);
        Assert.Contains("Rice", error.Message);
    }

    [Fact]
    public async Task A_subscriber_whose_task_faults_is_logged_and_does_not_starve_the_others()
    {
        var (notifier, log) = Build();
        var healthy = 0;
        notifier.Subscribe(_ => { healthy++; return Task.CompletedTask; });
        notifier.Subscribe(async _ =>
        {
            // Faulting after an await is the case a fire-and-forget handler
            // would swallow entirely — no log, no re-render, no trace.
            await Task.Yield();
            throw new InvalidOperationException("circuit died mid-render");
        });
        notifier.Subscribe(_ => { healthy++; return Task.CompletedTask; });

        await notifier.PublishAsync(SampleChange());

        Assert.Equal(2, healthy);
        var error = Assert.Single(log.Errors);
        Assert.Equal("circuit died mid-render", error.Exception!.Message);
    }

    [Fact]
    public async Task Both_failure_modes_are_logged_at_Error_in_one_publish()
    {
        var (notifier, log) = Build();
        notifier.Subscribe(_ => throw new InvalidOperationException("sync"));
        notifier.Subscribe(async _ => { await Task.Yield(); throw new InvalidOperationException("async"); });

        await notifier.PublishAsync(SampleChange());

        Assert.Equal(2, log.Errors.Count);
        Assert.All(log.Errors, e => Assert.Equal(LogLevel.Error, e.Level));
    }

    [Fact]
    public async Task Unsubscribe_removes_a_method_group_handler()
    {
        var (notifier, _) = Build();
        var subscriber = new CountingSubscriber();

        // Delegate equality: two delegates over the same method and the same
        // target instance are equal, which is exactly why AGENTS.md says to
        // subscribe with a method group rather than a fresh lambda.
        notifier.Subscribe(subscriber.HandleAsync);
        await notifier.PublishAsync(SampleChange());
        notifier.Unsubscribe(subscriber.HandleAsync);
        await notifier.PublishAsync(SampleChange());

        Assert.Equal(1, subscriber.Calls);
    }

    [Fact]
    public async Task Unsubscribing_an_equivalent_lambda_does_not_remove_the_handler()
    {
        var (notifier, _) = Build();
        var calls = 0;
        Func<InventoryChange, Task> handler = _ => { calls++; return Task.CompletedTask; };
        notifier.Subscribe(handler);

        // A distinct lambda instance is not equal to the registered one; this
        // is the leak the method-group rule exists to prevent, pinned here so
        // nobody "fixes" Unsubscribe into silently matching by shape.
        notifier.Unsubscribe(_ => { calls++; return Task.CompletedTask; });
        await notifier.PublishAsync(SampleChange());

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Unsubscribe_of_an_unknown_handler_is_harmless()
    {
        var (notifier, _) = Build();
        var subscriber = new CountingSubscriber();

        notifier.Unsubscribe(subscriber.HandleAsync);
        await notifier.PublishAsync(SampleChange());

        Assert.Equal(0, subscriber.Calls);
    }

    [Fact]
    public void Subscribe_and_Unsubscribe_reject_null()
    {
        var (notifier, _) = Build();

        Assert.Throws<ArgumentNullException>(() => notifier.Subscribe(null!));
        Assert.Throws<ArgumentNullException>(() => notifier.Unsubscribe(null!));
    }

    [Fact]
    public async Task Unsubscribing_during_a_publish_does_not_disturb_the_in_flight_fan_out()
    {
        var (notifier, _) = Build();
        var subscriber = new CountingSubscriber();
        notifier.Subscribe(subscriber.HandleAsync);
        notifier.Subscribe(_ =>
        {
            // Torn-down circuits really do unsubscribe from inside a handler;
            // the snapshot taken under the gate is what makes that safe.
            notifier.Unsubscribe(subscriber.HandleAsync);
            return Task.CompletedTask;
        });

        await notifier.PublishAsync(SampleChange());
        await notifier.PublishAsync(SampleChange());

        Assert.Equal(1, subscriber.Calls);
    }

    private sealed class CountingSubscriber
    {
        public int Calls { get; private set; }

        public Task HandleAsync(InventoryChange change)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }
}

/// <summary>
/// Which mutations reach the bus at all. The service deliberately withholds
/// Unchanged and NotFound so idle writes don't churn every open circuit.
/// </summary>
public class InventoryPublicationTests
{
    private static List<InventoryChange> Watch(InventoryHarness harness)
    {
        var seen = new List<InventoryChange>();
        harness.Notifier.Subscribe(change =>
        {
            seen.Add(change);
            return Task.CompletedTask;
        });
        return seen;
    }

    [Fact]
    public async Task Created_Updated_and_Removed_are_published()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        var seen = Watch(harness);

        await harness.Service.UpsertAsync("Rice", "2 bags", IngredientCategory.Grains);
        await harness.Service.UpsertAsync("Rice", "1 bag");
        await harness.Service.RemoveAsync("Rice");

        Assert.Equal(
            [ChangeKind.Created, ChangeKind.Updated, ChangeKind.Removed],
            seen.Select(c => c.Kind));
    }

    [Fact]
    public async Task Unchanged_is_not_published()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        await harness.Service.UpsertAsync("Rice", "2 bags", IngredientCategory.Grains);
        var seen = Watch(harness);

        await harness.Service.UpsertAsync("Rice", "2 bags", IngredientCategory.Grains);

        Assert.Empty(seen);
    }

    [Fact]
    public async Task NotFound_is_not_published()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        var seen = Watch(harness);

        await harness.Service.RemoveAsync("Saffron");

        Assert.Empty(seen);
    }

    [Fact]
    public async Task A_failing_subscriber_does_not_fail_the_write()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        harness.Notifier.Subscribe(_ => throw new InvalidOperationException("circuit is gone"));

        var change = await harness.Service.UpsertAsync("Rice", "2 bags", IngredientCategory.Grains);

        Assert.Equal(ChangeKind.Created, change.Kind);
        Assert.Equal(1, await harness.CountAsync());
        Assert.Single(harness.Log.Errors);
    }

    [Fact]
    public async Task A_writer_in_one_scope_notifies_a_subscriber_from_another()
    {
        // The MCP-writes-and-the-browser-refreshes path: separate service
        // instances, one shared singleton notifier.
        await using var harness = await InventoryHarness.CreateAsync();
        var seen = Watch(harness);

        await harness.NewService().UpsertAsync("Rice", "2 bags", IngredientCategory.Grains);

        Assert.Equal(ChangeKind.Created, Assert.Single(seen).Kind);
    }
}
