using MealPlanner.Models;

namespace MealPlanner.Services;

/// <summary>
/// How each provider spells an <see cref="AiEffort"/> on the wire — the
/// explicit per-provider function the enum's own doc said would be needed.
/// <para>
/// All four spell the levels the same way today (<c>low</c>, <c>xhigh</c>, …),
/// so this is one lowercase table rather than four. It still takes the provider:
/// the day one of them renames a level, that is a row here and not a hunt through
/// four clients. The <em>parameter</em> the word goes into differs, and that stays
/// with each client: <c>--effort</c> on the CLI, <c>output_config.effort</c> on
/// the Anthropic API, <c>reasoning_effort</c> on OpenAI's Chat Completions,
/// <c>reasoning.effort</c> on OpenRouter.
/// </para>
/// <para>
/// Null in, null out, and null means the caller <strong>omits the parameter</strong>
/// — never sends an empty one. A model with no effort knob (Haiku 4.5 rejects the
/// field outright) is exactly the case the null exists for.
/// </para>
/// </summary>
public static class AiEffortSpelling
{
    public static string? For(AiProvider provider, AiEffort? effort) => effort switch
    {
        null => null,
        AiEffort.Minimal => "minimal",
        AiEffort.Low => "low",
        AiEffort.Medium => "medium",
        AiEffort.High => "high",
        AiEffort.XHigh => "xhigh",
        AiEffort.Max => "max",
        _ => throw new ArgumentOutOfRangeException(
            nameof(effort), effort, $"No {provider} spelling for effort '{effort}'."),
    };

    /// <summary>
    /// The inverse, for the free-text <c>Effort</c> in <c>appsettings.json</c>
    /// that supplies a feature's default. Anything unrecognised is null — no
    /// effort sent — rather than an exception at startup over a typo.
    /// </summary>
    public static AiEffort? Parse(string? word) =>
        Enum.TryParse<AiEffort>(word?.Trim(), ignoreCase: true, out var effort) && Enum.IsDefined(effort)
            // Enum.TryParse accepts "3"; a number in a config file is not a
            // spelling of anything.
            && !int.TryParse(word, out _)
                ? effort
                : null;
}
