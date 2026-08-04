using MealPlanner.Components.Layout;

namespace MealPlanner.Tests;

/// <summary>
/// The theme control's three-state machine.
///
/// A bare <see cref="BunitContext"/> rather than <see cref="PageHarness"/>: the
/// toggle touches no database and no <c>InventoryService</c>, so standing up a
/// throwaway SQLite file would say nothing about it. What does the work here is
/// bUnit's default <em>Strict</em> JS interop — the component's only outside
/// contact is <c>theme.js</c>, and Strict mode turns every unplanned call into
/// a failure, which is the whole assertion surface.
///
/// What these cannot cover is whether the theme is actually applied: that lives
/// in theme.js and in the browser, and is listed as a manual check on the PR.
/// These pin the half that is ours — which button claims to be current, and
/// what the component tells theme.js.
/// </summary>
public class ThemeToggleTests
{
    private const string Get = "mealPlannerTheme.get";
    private const string Set = "mealPlannerTheme.set";

    [Fact]
    public void All_three_states_are_offered_and_exactly_one_is_current()
    {
        using var ctx = Toggle("system");

        var cut = ctx.Render<ThemeToggle>();

        var buttons = cut.FindAll("button.theme-choice");
        Assert.Equal(["System", "Light", "Dark"], buttons.Select(b => b.TextContent.Trim()));

        // Two states would be cheaper and would lose "follow my OS" the first
        // time anyone touched it. The third is the point of the control.
        cut.WaitForAssertion(() =>
            Assert.Single(cut.FindAll("button.theme-choice[aria-pressed=true]")));
    }

    [Fact]
    public void The_stored_choice_is_the_one_marked_current()
    {
        using var ctx = Toggle("dark");

        var cut = ctx.Render<ThemeToggle>();

        // The preference lives in localStorage, which the server never sees, so
        // the buttons can only learn it by asking the browser after the first
        // render. Nothing else in this component talks to theme.js on load.
        cut.WaitForAssertion(() => Assert.Equal("Dark", Current(cut)));
        Assert.Contains(ctx.JSInterop.Invocations, i => i.Identifier == Get);
    }

    [Fact]
    public void A_stored_value_we_do_not_recognise_falls_back_to_System()
    {
        // localStorage is user-writable and outlives any rename of these
        // values. "We cannot tell what you wanted" and "follow the system" are
        // the same answer, so an unknown mode must not leave every button
        // unpressed.
        using var ctx = Toggle("chartreuse");

        var cut = ctx.Render<ThemeToggle>();

        cut.WaitForAssertion(() => Assert.Equal("System", Current(cut)));
    }

    [Fact]
    public void Choosing_a_theme_tells_theme_js_and_moves_the_marker()
    {
        using var ctx = Toggle("system");
        var cut = ctx.Render<ThemeToggle>();

        cut.FindAll("button.theme-choice")[2].Click();

        cut.WaitForAssertion(() => Assert.Equal("Dark", Current(cut)));

        var call = Assert.Single(ctx.JSInterop.Invocations, i => i.Identifier == Set);
        Assert.Equal("dark", Assert.Single(call.Arguments));
    }

    [Fact]
    public void The_current_button_carries_the_class_the_painted_edge_hangs_off()
    {
        using var ctx = Toggle("light");

        var cut = ctx.Render<ThemeToggle>();

        // ThemeToggle.razor.css keys the inset bar off .theme-choice.active,
        // because Bootstrap's own .active signals the current choice by fill
        // alone and this household has been bitten by colour-only state before.
        // bUnit has no layout, so the class is the honest proxy for the bar —
        // the same trade The_editor_spans_the_table… already makes.
        cut.WaitForAssertion(() =>
        {
            var current = cut.Find("button.theme-choice[aria-pressed=true]");
            Assert.Contains("active", current.ClassList);
        });
    }

    /// <summary>
    /// A context whose theme.js reports <paramref name="stored"/>. Both plans
    /// are registered before the render: under Strict mode an unplanned call
    /// throws, and an un-resulted one never completes, which would hang the
    /// click handler before Blazor re-rendered it.
    ///
    /// The set plan needs the matcher overload — the bare
    /// <c>SetupVoid(identifier)</c> only matches a call with <em>no</em>
    /// arguments, so it lets every real invocation through to Strict mode's
    /// exception. Which argument arrived is asserted at the call site instead.
    /// </summary>
    private static BunitContext Toggle(string stored)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Setup<string>(Get).SetResult(stored);
        ctx.JSInterop.SetupVoid(Set, _ => true).SetVoidResult();
        return ctx;
    }

    private static string Current(IRenderedComponent<ThemeToggle> cut) =>
        cut.Find("button.theme-choice[aria-pressed=true]").TextContent.Trim();
}
