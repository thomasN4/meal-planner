using System.Text;
using AngleSharp.Dom;
using MealPlanner.Components.Pages;
using Microsoft.AspNetCore.Components.Forms;

namespace MealPlanner.Tests;

/// <summary>
/// The receipt-scanning half of the Inventory page, rendered against the real
/// service graph with a fake <see cref="IReceiptScanner"/> in place of the
/// subprocess. What these prove is the rule the whole feature rests on:
/// a scan proposes, a person confirms, and only then does anything reach the
/// database.
/// </summary>
public class ReceiptScanTests
{
    private static readonly ScannedLine[] TypicalReceipt =
    [
        new("Lait demi-écrémé", "2 briques", IsFood: true),
        new("Riz basmati", "1 kg", IsFood: true),
        new("Piles AAA", "4", IsFood: false),
    ];

    /// <summary>
    /// Choosing a file, the way the browser reports it. The bytes are never
    /// looked at by anything under test — the scanner is faked — but they do
    /// travel through the page's own size check and its stream copy.
    /// </summary>
    /// <remarks>
    /// Called the way <c>Click()</c> is, and deliberately not wrapped in
    /// <c>InvokeAsync</c>: awaiting a dispatcher work item that is itself
    /// waiting on the scanner leaves the renderer with nothing free to service
    /// the Cancel click, and the gated test below hangs rather than failing.
    /// </remarks>
    private static void Upload(
        IRenderedComponent<Inventory> cut,
        string name = "receipt.jpg",
        string contentType = "image/jpeg",
        int bytes = 64)
    {
        var content = InputFileContent.CreateFromBinary(
            Encoding.ASCII.GetBytes(new string('x', bytes)), name, contentType: contentType);
        cut.FindComponent<InputFile>().UploadFiles(content);
    }

    private static IElement Row(IRenderedComponent<Inventory> cut, int index) =>
        cut.FindAll("li.scan-row")[index];

    [Fact]
    public async Task A_scanned_receipt_is_proposed_and_written_nowhere()
    {
        await using var page = await PageHarness.CreateAsync();
        page.Scanner.Result = TypicalReceipt;
        var cut = page.RenderInventory();

        Upload(cut);

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("li.scan-row").Count));
        // The whole point of the review step: three lines on screen, nothing in
        // the kitchen.
        Assert.Equal(0, await page.CountAsync());
        Assert.Contains("Lait demi-écrémé", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_file_reaches_the_scanner_with_its_media_type()
    {
        // The scanner decides between an image and a document block on this,
        // and sending either as the other is rejected by the CLI.
        await using var page = await PageHarness.CreateAsync();
        page.Scanner.Result = TypicalReceipt;
        var cut = page.RenderInventory();

        Upload(cut, "receipt.pdf", "application/pdf", bytes: 128);

        cut.WaitForAssertion(() => Assert.Single(page.Scanner.Files));
        var file = page.Scanner.Files[0];
        Assert.Equal("application/pdf", file.MediaType);
        Assert.Equal("receipt.pdf", file.FileName);
        Assert.Equal(128, file.Content.Length);
    }

    [Fact]
    public async Task Non_food_lines_arrive_unticked_but_still_listed()
    {
        await using var page = await PageHarness.CreateAsync();
        page.Scanner.Result = TypicalReceipt;
        var cut = page.RenderInventory();

        Upload(cut);
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("li.scan-row").Count));

        var ticks = cut.FindAll("input.scan-keep");
        Assert.True(ticks[0].HasAttribute("checked"));
        Assert.True(ticks[1].HasAttribute("checked"));
        // The batteries: shown, so the household can disagree, but not selected.
        Assert.False(ticks[2].HasAttribute("checked"));
        Assert.Contains("Piles AAA", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Confirming_writes_only_the_ticked_lines()
    {
        await using var page = await PageHarness.CreateAsync();
        page.Scanner.Result = TypicalReceipt;
        var cut = page.RenderInventory();

        Upload(cut);
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("li.scan-row").Count));
        cut.Find("button.scan-confirm").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("li.scan-row")));
        Assert.Equal(2, await page.CountAsync());
        Assert.NotNull(await page.Service.FindAsync("Riz basmati"));
        Assert.Null(await page.Service.FindAsync("Piles AAA"));
    }

    [Fact]
    public async Task A_line_ticked_by_hand_is_written_too()
    {
        // The mirror of the test above, and the reason non-food rows are shown
        // at all: the guess is only a default.
        await using var page = await PageHarness.CreateAsync();
        page.Scanner.Result = TypicalReceipt;
        var cut = page.RenderInventory();

        Upload(cut);
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("li.scan-row").Count));
        Row(cut, 2).QuerySelector("input.scan-keep")!.Change(true);
        cut.Find("button.scan-confirm").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("li.scan-row")));
        Assert.NotNull(await page.Service.FindAsync("Piles AAA"));
    }

    [Fact]
    public async Task A_name_corrected_in_the_review_is_what_gets_written()
    {
        await using var page = await PageHarness.CreateAsync();
        page.Scanner.Result = [new ScannedLine("LAIT DEMI-ECR", "2", IsFood: true)];
        var cut = page.RenderInventory();

        Upload(cut);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("li.scan-row")));
        cut.Find("input.scan-name").Input("Semi-skimmed milk");
        cut.Find("input.scan-quantity").Input("2 cartons");
        cut.Find("button.scan-confirm").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("li.scan-row")));
        var item = await page.Service.FindAsync("Semi-skimmed milk");
        Assert.NotNull(item);
        Assert.Equal("2 cartons", item.Quantity);
    }

    [Fact]
    public async Task Confirmed_lines_land_in_Other_for_the_categorizer_to_move()
    {
        // No category travels with a scanned line. Passing a guess would take
        // classification away from IngredientCategorizer, which owns it —
        // cache, batching and all.
        await using var page = await PageHarness.CreateAsync();
        page.Scanner.Result = [new ScannedLine("Riz basmati", "1 kg", IsFood: true)];
        var cut = page.RenderInventory();

        Upload(cut);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("li.scan-row")));
        cut.Find("button.scan-confirm").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("li.scan-row")));
        var item = await page.Service.FindAsync("Riz basmati");
        Assert.Equal(IngredientCategory.Other, item!.Category);
    }

    [Fact]
    public async Task Confirming_a_line_that_already_exists_leaves_its_category_alone()
    {
        // The upsert passes a null category, which means "leave it" for a row
        // that exists. A scan must not drag a filed row back into Other.
        await using var page = await PageHarness.CreateAsync();
        await page.Service.UpsertAsync("Riz basmati", "3 sachets", IngredientCategory.Grains);
        page.Scanner.Result = [new ScannedLine("riz basmati", "1 kg", IsFood: true)];
        var cut = page.RenderInventory();

        Upload(cut);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("li.scan-row")));
        cut.Find("button.scan-confirm").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("li.scan-row")));
        var item = await page.Service.FindAsync("Riz basmati");
        Assert.Equal(IngredientCategory.Grains, item!.Category);
        Assert.Equal("1 kg", item.Quantity);
        // NOCASE: one row, not two.
        Assert.Equal(1, await page.CountAsync());
    }

    [Fact]
    public async Task A_line_that_matches_a_stocked_row_says_what_it_will_overwrite()
    {
        // UpsertAsync replaces the quantity, and free text cannot be summed.
        // The badge is the only warning a household member gets, so it has to
        // carry the old value as well as the new one.
        await using var page = await PageHarness.CreateAsync();
        await page.Service.UpsertAsync("Riz basmati", "3 sachets", IngredientCategory.Grains);
        page.Scanner.Result = [new ScannedLine("Riz basmati", "1 kg", IsFood: true)];
        var cut = page.RenderInventory();

        Upload(cut);

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("li.scan-row")));
        var effect = Row(cut, 0).QuerySelector("span.scan-effect")!.TextContent;
        Assert.Contains("Replaces", effect, StringComparison.Ordinal);
        Assert.Contains("3 sachets", effect, StringComparison.Ordinal);
        Assert.Contains("1 kg", effect, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_row_created_elsewhere_mid_review_turns_the_badge_from_new_to_replaces()
    {
        // The badge is derived on every render rather than stored with the row,
        // which is what lets the notifier's refresh keep it honest. Same line
        // SyncToName draws for the add form's hint.
        await using var page = await PageHarness.CreateAsync();
        page.Scanner.Result = [new ScannedLine("Riz basmati", "1 kg", IsFood: true)];
        var cut = page.RenderInventory();

        Upload(cut);
        cut.WaitForAssertion(() =>
            Assert.Contains("New", Row(cut, 0).QuerySelector("span.scan-effect")!.TextContent,
                StringComparison.Ordinal));

        // Another tab, or an MCP tool, stocks it while the review is open.
        await page.OutOfCircuitService().UpsertAsync("Riz basmati", "3 sachets", IngredientCategory.Grains);

        cut.WaitForAssertion(() =>
            Assert.Contains("Replaces", Row(cut, 0).QuerySelector("span.scan-effect")!.TextContent,
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_review_survives_a_refresh_from_another_circuit()
    {
        // The rows live in @code fields, never in the DOM. Every confirmed write
        // publishes and re-renders, so a name someone has just corrected has to
        // survive a re-render it did not ask for.
        await using var page = await PageHarness.CreateAsync();
        page.Scanner.Result = [new ScannedLine("LAIT DEMI-ECR", "2", IsFood: true)];
        var cut = page.RenderInventory();

        Upload(cut);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("li.scan-row")));
        cut.Find("input.scan-name").Input("Semi-skimmed milk");

        await page.OutOfCircuitService().UpsertAsync("Paprika", "1 jar", IngredientCategory.DrySeasonings);

        // The group heading, not the row: categories start collapsed, so the
        // row itself is not in the markup to wait for.
        cut.WaitForAssertion(() =>
            Assert.Contains("Dry Seasonings", cut.Markup, StringComparison.Ordinal));
        Assert.Equal(
            "Semi-skimmed milk",
            cut.Find("input.scan-name").GetAttribute("value"));
    }

    [Fact]
    public async Task A_line_repeating_an_earlier_name_is_flagged_and_arrives_unticked()
    {
        // A real till receipt rings the same thing up on several lines — two
        // pork loins are two lines, not one line saying two. Both rows used to
        // read "New", and confirming wrote the first and then silently updated
        // it with the second: two bought, one row in the kitchen.
        await using var page = await PageHarness.CreateAsync();
        page.Scanner.Result =
        [
            new ScannedLine("Longe de porc", "", IsFood: true),
            new ScannedLine("longe de porc", "", IsFood: true),
        ];
        var cut = page.RenderInventory();

        Upload(cut);
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("li.scan-row").Count));

        // The first of the pair keeps its ordinary badge; only the second is
        // flagged, and case does not save it — the unique index is NOCASE.
        Assert.Contains("New", Row(cut, 0).QuerySelector("span.scan-effect")!.TextContent,
            StringComparison.Ordinal);
        var second = Row(cut, 1).QuerySelector("span.scan-effect")!.TextContent;
        Assert.Contains("Duplicate", second, StringComparison.Ordinal);
        Assert.Contains("line 1", second, StringComparison.Ordinal);

        var ticks = cut.FindAll("input.scan-keep");
        Assert.True(ticks[0].HasAttribute("checked"));
        Assert.False(ticks[1].HasAttribute("checked"));
    }

    [Fact]
    public async Task Confirming_a_flagged_duplicate_leaves_one_row_not_two()
    {
        // The default is what protects the household; this is what happens if
        // they overrule it. One row either way — the point of the badge is that
        // they are told, not that they are stopped.
        await using var page = await PageHarness.CreateAsync();
        page.Scanner.Result =
        [
            new ScannedLine("Longe de porc", "2 pièces", IsFood: true),
            new ScannedLine("Longe de porc", "", IsFood: true),
        ];
        var cut = page.RenderInventory();

        Upload(cut);
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("li.scan-row").Count));
        cut.Find("button.scan-confirm").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("li.scan-row")));
        Assert.Equal(1, await page.CountAsync());
        // The ticked one, not the flagged one that was left alone.
        Assert.Equal("2 pièces", (await page.Service.FindAsync("Longe de porc"))!.Quantity);
    }

    [Fact]
    public async Task Renaming_a_flagged_duplicate_clears_the_flag()
    {
        // The flag is derived on every render, like the Replaces badge beside
        // it: "Aero moyenne" and "Aero petite" are two real products the model
        // may hand back under one name, and fixing that by hand has to be
        // enough.
        await using var page = await PageHarness.CreateAsync();
        page.Scanner.Result =
        [
            new ScannedLine("Aero tablette chocolat", "", IsFood: true),
            new ScannedLine("Aero tablette chocolat", "", IsFood: true),
        ];
        var cut = page.RenderInventory();

        Upload(cut);
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("li.scan-row").Count));

        cut.FindAll("input.scan-name")[1].Input("Aero tablette chocolat petite");

        cut.WaitForAssertion(() => Assert.DoesNotContain(
            "Duplicate", Row(cut, 1).QuerySelector("span.scan-effect")!.TextContent,
            StringComparison.Ordinal));
    }

    [Fact]
    public async Task Discarding_a_review_writes_nothing()
    {
        await using var page = await PageHarness.CreateAsync();
        page.Scanner.Result = TypicalReceipt;
        var cut = page.RenderInventory();

        Upload(cut);
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("li.scan-row").Count));
        cut.Find("button.scan-discard").Click();

        Assert.Empty(cut.FindAll("li.scan-row"));
        Assert.Equal(0, await page.CountAsync());
    }

    [Fact]
    public async Task Confirming_reports_what_it_did_in_the_live_region()
    {
        await using var page = await PageHarness.CreateAsync();
        await page.Service.UpsertAsync("Riz basmati", "3 sachets", IngredientCategory.Grains);
        page.Scanner.Result =
        [
            new ScannedLine("Riz basmati", "1 kg", IsFood: true),
            new ScannedLine("Coriandre fraîche", "1 botte", IsFood: true),
        ];
        var cut = page.RenderInventory();

        Upload(cut);
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("li.scan-row").Count));
        cut.Find("button.scan-confirm").Click();

        cut.WaitForAssertion(() =>
        {
            var status = cut.Find("div[role=status]").TextContent;
            Assert.Contains("Added 1 item", status, StringComparison.Ordinal);
            Assert.Contains("updated 1", status, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task A_scan_that_finds_nothing_says_so_without_opening_a_review()
    {
        // The scanner never throws — an empty list is the whole failure signal.
        await using var page = await PageHarness.CreateAsync();
        page.Scanner.Result = [];
        var cut = page.RenderInventory();

        Upload(cut);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("div.scan-error")));
        Assert.Empty(cut.FindAll("li.scan-row"));
        // role=alert, not a second role=status: the add form owns this page's
        // one status region.
        Assert.Equal("alert", cut.Find("div.scan-error").GetAttribute("role"));
    }

    [Fact]
    public async Task A_file_the_model_cannot_read_is_refused_before_the_scanner()
    {
        // accept= filters the picker, it does not constrain what arrives. HEIC
        // is the realistic case — it is what an iPhone photographs in.
        await using var page = await PageHarness.CreateAsync();
        var cut = page.RenderInventory();

        Upload(cut, "IMG_0421.heic", "image/heic");

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("div.scan-error")));
        Assert.Empty(page.Scanner.Files);
    }

    [Fact]
    public async Task A_file_over_the_size_cap_is_refused_before_the_scanner()
    {
        await using var page = await PageHarness.CreateAsync();
        page.ScanOptions.MaxBytes = 1024;
        var cut = page.RenderInventory();

        Upload(cut, bytes: 4096);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("div.scan-error")));
        Assert.Empty(page.Scanner.Files);
    }

    [Fact]
    public async Task Nothing_can_be_confirmed_while_every_line_is_unticked()
    {
        await using var page = await PageHarness.CreateAsync();
        page.Scanner.Result = [new ScannedLine("Sac plastique", "1", IsFood: false)];
        var cut = page.RenderInventory();

        Upload(cut);

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("li.scan-row")));
        Assert.True(cut.Find("button.scan-confirm").HasAttribute("disabled"));
    }

    [Fact]
    public async Task A_scan_in_flight_offers_a_way_out_and_no_second_upload()
    {
        await using var page = await PageHarness.CreateAsync();
        // RunContinuationsAsynchronously, as the recipe tests' gate is: without
        // it SetResult runs the page's continuation inline on this thread.
        page.Scanner.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        page.Scanner.Result = TypicalReceipt;
        var cut = page.RenderInventory();

        // On its own thread, unlike every other test here: bUnit's UploadFiles
        // blocks until the handler it triggers has finished, and this one is
        // parked on the gate on purpose. Calling it inline would leave no
        // thread to click Cancel with, and the test would hang instead of fail.
        var upload = Task.Run(() => Upload(cut));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button.scan-cancel")));
        Assert.True(cut.Find("input.scan-file").HasAttribute("disabled"));

        cut.Find("button.scan-cancel").Click();
        page.Scanner.Gate.SetResult();
        await upload;

        // Cancelled: no review, and nothing written.
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("button.scan-cancel")));
        Assert.Empty(cut.FindAll("li.scan-row"));
        Assert.Equal(0, await page.CountAsync());
    }

    [Fact]
    public async Task The_whole_card_disappears_when_scanning_is_switched_off()
    {
        await using var page = await PageHarness.CreateAsync();
        page.ScanOptions.Enabled = false;
        var cut = page.RenderInventory();

        Assert.Empty(cut.FindAll("input.scan-file"));
        // The add form is untouched by the flag — it is the fallback.
        Assert.NotEmpty(cut.FindAll("#new-name"));
    }
}
