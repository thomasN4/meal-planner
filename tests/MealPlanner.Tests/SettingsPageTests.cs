using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using MealPlanner.Components.Pages;

namespace MealPlanner.Tests;

public class SettingsPageTests
{
    /// <summary>
    /// Delete this test in the same commit that wires the three services up to
    /// <c>AiSettingsService</c>. Until then, deleting the banner turns it red,
    /// which is the whole point of it.
    /// </summary>
    [Fact]
    public async Task The_page_says_the_settings_are_not_in_effect_yet()
    {
        await using var page = await PageHarness.CreateAsync();
        var cut = page.RenderSettings();

        Assert.Contains("not yet in effect", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Recipe generation, ingredient", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Each_feature_card_names_what_it_is_actually_running_today()
    {
        await using var page = await PageHarness.CreateAsync();
        page.RecipeOptions.Model = "opus";
        page.ClassifyOptions.Effort = "xhigh";

        var cut = page.RenderSettings();

        // Hardcoding "sonnet" into the markup instead of reading IOptions turns
        // this red, which is what keeps the line honest once one feature is wired.
        Assert.Contains("opus", InEffect(cut, AiFeature.RecipeGeneration), StringComparison.Ordinal);
        Assert.Contains("xhigh", InEffect(cut, AiFeature.Categorization), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_feature_switched_off_says_so_in_its_own_card_only()
    {
        await using var page = await PageHarness.CreateAsync();
        page.RecipeOptions.Enabled = false;

        var cut = page.RenderSettings();

        Assert.Contains("RecipeGeneration:Enabled", Card(cut, AiFeature.RecipeGeneration).InnerHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Enabled</code>", Card(cut, AiFeature.ReceiptScanning).InnerHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Choosing_a_provider_reoffers_the_models_that_provider_has()
    {
        await using var page = await PageHarness.CreateAsync();
        var cut = page.RenderSettings();

        Select(cut, AiFeature.RecipeGeneration, "provider-select").Change(nameof(AiProvider.AnthropicApi));

        var models = Options(Select(cut, AiFeature.RecipeGeneration, "model-select"));
        Assert.Contains("claude-opus-5", models);
        Assert.DoesNotContain("sonnet", models);
        Assert.Contains("__other", models);
    }

    [Fact]
    public async Task Picking_Other_reveals_a_free_text_model_box_below_the_row()
    {
        await using var page = await PageHarness.CreateAsync();
        var cut = page.RenderSettings();

        Assert.Empty(cut.FindAll("input.model-other"));
        Select(cut, AiFeature.ReceiptScanning, "model-select").Change("__other");

        var card = cut.Find($"div.feature-card[data-feature={AiFeature.ReceiptScanning}]");
        Assert.Single(card.QuerySelectorAll("input.model-other"));

        // bUnit has no layout, so structure is the honest proxy for "full width
        // below the row, never a fourth column" — the same trade
        // The_editor_spans_the_table… already makes on the inventory page.
        Assert.Empty(card.QuerySelectorAll("div.row input.model-other"));
    }

    [Fact]
    public async Task A_model_with_no_reasoning_knob_says_so_instead_of_offering_one()
    {
        await using var page = await PageHarness.CreateAsync();
        var cut = page.RenderSettings();

        Select(cut, AiFeature.Categorization, "provider-select").Change(nameof(AiProvider.AnthropicApi));
        Select(cut, AiFeature.Categorization, "model-select").Change("claude-haiku-4-5");

        var card = cut.Find($"div.feature-card[data-feature={AiFeature.Categorization}]");
        Assert.Empty(card.QuerySelectorAll("select.effort-select"));
        Assert.Contains("Not applicable", card.TextContent, StringComparison.Ordinal);
    }

    /// <summary>The core of the one-Save decision.</summary>
    [Fact]
    public async Task Nothing_is_written_until_Save_is_pressed()
    {
        await using var page = await PageHarness.CreateAsync();
        var cut = page.RenderSettings();

        Select(cut, AiFeature.RecipeGeneration, "provider-select").Change(nameof(AiProvider.AnthropicApi));

        var stored = await page.OutOfCircuitSettings().GetAsync();
        Assert.Equal(AiProvider.ClaudeCli, Choice(stored, AiFeature.RecipeGeneration).Provider);

        cut.Find("button.save-settings").Click();

        await cut.InvokeAsync(async () =>
        {
            var after = await page.OutOfCircuitSettings().GetAsync();
            Assert.Equal(AiProvider.AnthropicApi, Choice(after, AiFeature.RecipeGeneration).Provider);
        });
    }

    [Fact]
    public async Task Discarding_changes_puts_the_stored_values_back()
    {
        await using var page = await PageHarness.CreateAsync();
        var cut = page.RenderSettings();

        Select(cut, AiFeature.RecipeGeneration, "provider-select").Change(nameof(AiProvider.OpenAi));
        Assert.False(cut.Find("button.save-settings").HasAttribute("disabled"));

        cut.Find("button.revert-settings").Click();

        cut.WaitForAssertion(() =>
            Assert.True(cut.Find("button.save-settings").HasAttribute("disabled")));
        Assert.Empty(cut.FindAll("div.card-dirty"));
    }

    [Fact]
    public async Task A_card_with_a_pending_change_is_marked_and_the_others_are_not()
    {
        await using var page = await PageHarness.CreateAsync();
        var cut = page.RenderSettings();

        Select(cut, AiFeature.RecipeGeneration, "provider-select").Change(nameof(AiProvider.OpenAi));

        var marked = cut.FindAll("div.card-dirty");
        Assert.Single(marked);
        Assert.Equal(nameof(AiFeature.RecipeGeneration), marked[0].GetAttribute("data-feature"));
        Assert.Contains("Unsaved", marked[0].TextContent, StringComparison.Ordinal);
    }

    /// <summary>
    /// The case most likely to be missed: the keys card is the one that is not a
    /// feature card, and an armed clear is a pending change like any other.
    /// </summary>
    [Fact]
    public async Task Arming_a_key_for_clearing_marks_the_keys_card_too()
    {
        await using var page = await PageHarness.CreateAsync();
        await page.OutOfCircuitSettings().SetApiKeyAsync(AiProvider.AnthropicApi, "sk-ant-api03-abcdefgh1234");

        var cut = page.RenderSettings();
        cut.Find("span.key-chip button.key-remove").Click();

        var credentials = cut.Find("div#credentials");
        Assert.Contains("card-dirty", credentials.ClassName!, StringComparison.Ordinal);
        Assert.Contains("clearing", cut.Find("span.key-chip").ClassName!, StringComparison.Ordinal);
        Assert.False(cut.Find("button.save-settings").HasAttribute("disabled"));
    }

    [Fact]
    public async Task A_saved_choice_comes_back_when_the_page_is_rendered_again()
    {
        await using var page = await PageHarness.CreateAsync();
        await page.OutOfCircuitSettings().SaveFeatureAsync(
            AiFeature.ReceiptScanning, AiProvider.OpenRouter, "some/model-we-do-not-list", AiEffort.High);

        var cut = page.RenderSettings();

        // An id the catalog does not carry lands on Other… with the box
        // pre-filled — the whole point of deriving the selection rather than
        // storing a flag beside it.
        var card = cut.Find($"div.feature-card[data-feature={AiFeature.ReceiptScanning}]");
        var other = (IHtmlInputElement)card.QuerySelector("input.model-other")!;
        Assert.Equal("some/model-we-do-not-list", other.Value);
        Assert.Equal("__other", Selected(Select(cut, AiFeature.ReceiptScanning, "model-select")));
    }

    [Fact]
    public async Task Saving_reports_in_the_live_region_that_it_is_not_in_effect()
    {
        await using var page = await PageHarness.CreateAsync();
        var cut = page.RenderSettings();

        Select(cut, AiFeature.Categorization, "provider-select").Change(nameof(AiProvider.OpenAi));
        cut.Find("button.save-settings").Click();

        cut.WaitForAssertion(() =>
        {
            var status = Assert.Single(cut.FindAll("div[role=status]"));
            Assert.Contains("not yet in effect", status.TextContent, StringComparison.Ordinal);
        });
    }

    // ---- keys ----

    [Fact]
    public async Task The_key_box_never_renders_the_stored_key()
    {
        const string secret = "sk-ant-api03-do-not-render-me-9999";

        await using var page = await PageHarness.CreateAsync();
        await page.OutOfCircuitSettings().SetApiKeyAsync(AiProvider.AnthropicApi, secret);

        var cut = page.RenderSettings();

        Assert.DoesNotContain(secret, cut.Markup, StringComparison.Ordinal);
        var box = (IHtmlInputElement)cut.Find("input.key-input");
        Assert.Equal("password", box.Type);
        Assert.Equal(string.Empty, box.Value);
        // Only the tail, and only in the chip.
        Assert.Contains("…9999", cut.Find("span.key-chip").TextContent, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sk-ant-api03-abcdefgh1234", "Anthropic API")]
    [InlineData("sk-or-v1-abcdefgh1234", "OpenRouter")]
    [InlineData("sk-proj-abcdefgh1234", "OpenAI API")]
    public async Task A_pasted_key_names_the_provider_it_was_recognised_as(string key, string expected)
    {
        await using var page = await PageHarness.CreateAsync();
        var cut = page.RenderSettings();

        cut.Find("input.key-input").Input(key);

        Assert.Contains($"Recognised as {expected}", cut.Find("div.key-hint").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("select.key-provider-select"));
    }

    [Fact]
    public async Task A_prefix_we_do_not_know_asks_which_provider_it_is()
    {
        await using var page = await PageHarness.CreateAsync();
        var cut = page.RenderSettings();

        cut.Find("input.key-input").Input("gsk_some_other_vendor_key");

        Assert.Single(cut.FindAll("select.key-provider-select"));
        Assert.Contains("Not a prefix we recognise", cut.Find("div.key-hint").TextContent, StringComparison.Ordinal);
        // Refused, not guessed — Add stays out of reach until somebody says.
        Assert.True(cut.Find("button.add-key").HasAttribute("disabled"));
    }

    /// <summary>
    /// The chip is the only warning before an overwrite, so it has to show what
    /// is being replaced as well as what replaces it.
    /// </summary>
    [Fact]
    public async Task A_key_for_a_provider_we_already_hold_shows_both_tails()
    {
        await using var page = await PageHarness.CreateAsync();
        await page.OutOfCircuitSettings().SetApiKeyAsync(AiProvider.AnthropicApi, "sk-ant-api03-old-key-1111");

        var cut = page.RenderSettings();
        cut.Find("input.key-input").Input("sk-ant-api03-new-key-2222");
        cut.Find("button.add-key").Click();

        var chip = cut.Find("span.key-chip").TextContent;
        Assert.Contains("…1111", chip, StringComparison.Ordinal);
        Assert.Contains("…2222", chip, StringComparison.Ordinal);
        Assert.Contains("replacing", chip, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Adding_a_key_writes_nothing_until_Save()
    {
        await using var page = await PageHarness.CreateAsync();
        var cut = page.RenderSettings();

        cut.Find("input.key-input").Input("sk-or-v1-brand-new-key-3333");
        cut.Find("button.add-key").Click();

        Assert.Null(await page.OutOfCircuitSettings().GetApiKeyAsync(AiProvider.OpenRouter));

        cut.Find("button.save-settings").Click();

        await cut.InvokeAsync(async () =>
            Assert.Equal(
                "sk-or-v1-brand-new-key-3333",
                await page.OutOfCircuitSettings().GetApiKeyAsync(AiProvider.OpenRouter)));
    }

    [Fact]
    public async Task A_provider_with_no_key_warns_without_blocking()
    {
        await using var page = await PageHarness.CreateAsync();
        var cut = page.RenderSettings();

        Select(cut, AiFeature.Categorization, "provider-select").Change(nameof(AiProvider.OpenAi));

        var card = cut.Find($"div.feature-card[data-feature={AiFeature.Categorization}]");
        var warning = Assert.Single(card.QuerySelectorAll("div.needs-key"));
        Assert.Equal("alert", warning.GetAttribute("role"));
        // Warns, never blocks: Save is still reachable.
        Assert.False(cut.Find("button.save-settings").HasAttribute("disabled"));
    }

    // ---- language ----

    [Fact]
    public async Task The_stored_language_is_the_one_marked_current()
    {
        await using var page = await PageHarness.CreateAsync();
        page.StoredLanguage = "fr";

        var cut = page.RenderSettings();

        cut.WaitForAssertion(() =>
            Assert.Equal("Français", cut.Find("button.lang-choice[aria-pressed=true]").TextContent.Trim()));
        Assert.Contains(page.JSInterop.Invocations, i => i.Identifier == "mealPlannerLang.get");
    }

    [Fact]
    public async Task A_language_we_do_not_recognise_falls_back_to_System()
    {
        await using var page = await PageHarness.CreateAsync();
        page.StoredLanguage = "klingon";

        var cut = page.RenderSettings();

        cut.WaitForAssertion(() =>
            Assert.Equal("System", cut.Find("button.lang-choice[aria-pressed=true]").TextContent.Trim()));
    }

    /// <summary>
    /// Language is a deliberate exception to the page-wide Save: it writes
    /// through on click, like the theme, and the copy on the card says so.
    /// </summary>
    [Fact]
    public async Task Choosing_a_language_tells_lang_js_immediately_without_Save()
    {
        await using var page = await PageHarness.CreateAsync();
        var cut = page.RenderSettings();

        cut.FindAll("button.lang-choice")[2].Click();

        cut.WaitForAssertion(() =>
            Assert.Equal("Français", cut.Find("button.lang-choice[aria-pressed=true]").TextContent.Trim()));

        var call = Assert.Single(page.JSInterop.Invocations, i => i.Identifier == "mealPlannerLang.set");
        Assert.Equal("fr", Assert.Single(call.Arguments));
        // Still nothing to save: the language never joined the draft set.
        Assert.True(cut.Find("button.save-settings").HasAttribute("disabled"));
    }

    [Fact]
    public async Task The_current_language_button_carries_the_class_the_painted_edge_hangs_off() =>
        // The honest proxy for the inset bar — bUnit has no layout, so the real
        // check is a browser at a fixed viewport.
        await WithPage(cut => Assert.Single(cut.FindAll("button.lang-choice.active")));

    [Fact]
    public async Task Save_is_the_only_primary_button_on_the_page() =>
        // A convention guard, not a bug guard: it pins the selector trap that
        // Find("button.btn-primary") depends on across this repo's page tests.
        await WithPage(cut =>
        {
            var primary = Assert.Single(cut.FindAll("button.btn-primary"));
            Assert.Contains("save-settings", primary.ClassName!, StringComparison.Ordinal);
        });

    /// <summary>
    /// The fault the mockup shipped: a column class with no matching width rule
    /// collapses to one twelfth without warning anybody.
    /// </summary>
    [Fact]
    public async Task Every_row_on_the_page_spans_twelve_columns() =>
        await WithPage(cut =>
        {
            var rows = cut.FindAll("div.row");
            Assert.NotEmpty(rows);

            foreach (var row in rows)
            {
                var spans = row.Children
                    .SelectMany(c => c.ClassList)
                    .Where(c => c.StartsWith("col-md-", StringComparison.Ordinal))
                    .Select(c => int.Parse(c["col-md-".Length..]))
                    .ToList();

                Assert.Equal(12, spans.Sum());
            }
        });

    private static async Task WithPage(Action<IRenderedComponent<Settings>> assert)
    {
        await using var page = await PageHarness.CreateAsync();
        assert(page.RenderSettings());
    }

    private static FeatureChoice Choice(AiSettings settings, AiFeature feature) =>
        settings.Features.First(f => f.Feature == feature);

    private static IElement Card(IRenderedComponent<Settings> cut, AiFeature feature) =>
        cut.Find($"div.feature-card[data-feature={feature}]");

    private static string InEffect(IRenderedComponent<Settings> cut, AiFeature feature) =>
        Card(cut, feature).QuerySelector("p.in-effect")!.TextContent;

    private static IElement Select(IRenderedComponent<Settings> cut, AiFeature feature, string cls) =>
        Card(cut, feature).QuerySelector($"select.{cls}")!;

    private static IReadOnlyList<string> Options(IElement select) =>
        select.QuerySelectorAll("option").Select(o => o.GetAttribute("value")!).ToList();

    private static string? Selected(IElement select) =>
        select.QuerySelectorAll("option")
            .FirstOrDefault(o => o.HasAttribute("selected"))
            ?.GetAttribute("value");
}
