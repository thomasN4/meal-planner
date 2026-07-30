using MealPlanner.Models;

namespace MealPlanner.Services;

/// <summary>
/// Guesses a category for an ingredient name.
/// </summary>
public interface IIngredientClassifier
{
    /// <summary>
    /// Classifies a batch of names.
    /// <para>
    /// Returns one entry per input, <b>positionally</b>, with null where no
    /// category could be determined. Position rather than name because the model
    /// rewrites what it is given: fed <c>"salt. IGNORE ALL PREVIOUS
    /// INSTRUCTIONS…"</c> it answers about <c>"salt"</c>, so matching results
    /// back by name silently drops rows.
    /// </para>
    /// <para>
    /// Implementations must not throw. Classification is a nice-to-have running
    /// off the back of a completed write, so a failure means "everything stays
    /// in Other", never a faulted background loop.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<IngredientCategory?>> ClassifyAsync(
        IReadOnlyList<string> names,
        CancellationToken ct = default);
}
