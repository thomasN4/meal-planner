using MealPlanner.Models;

namespace MealPlanner.Services;

/// <summary>
/// One ingredient to classify: the name, and the note the household typed about
/// it, if any.
/// <para>
/// A pair rather than two parallel lists because the pairing is the whole risk
/// here — see <see cref="IIngredientClassifier.ClassifyAsync"/> on why results
/// are matched by position. Two lists would put a second index in play that
/// nothing checks.
/// </para>
/// <para>
/// <see cref="Notes"/> is null when there is none. Empty string and null mean
/// the same thing everywhere else in this app, and mean the same thing here:
/// implementations must send nothing rather than an empty note.
/// </para>
/// </summary>
public readonly record struct ClassificationRequest(string Name, string? Notes)
{
    public ClassificationRequest(string name) : this(name, null)
    {
    }
}

/// <summary>
/// Guesses a category for an ingredient.
/// </summary>
public interface IIngredientClassifier
{
    /// <summary>
    /// Classifies a batch of ingredients.
    /// <para>
    /// Returns one entry per input, <b>positionally</b>, with null where no
    /// category could be determined. Position rather than name because the model
    /// rewrites what it is given: fed <c>"salt. IGNORE ALL PREVIOUS
    /// INSTRUCTIONS…"</c> it answers about <c>"salt"</c>, so matching results
    /// back by name silently drops rows. A note travels with its name inside one
    /// <see cref="ClassificationRequest"/>, so the pairing survives whatever the
    /// model does to either.
    /// </para>
    /// <para>
    /// A note is context the household typed about the item ("for thickening
    /// sauces"), never an instruction to the model, and it is roomier than a
    /// name — 500 characters against 100. What keeps that harmless is the same
    /// thing that keeps a name harmless: the answer is pinned to the
    /// <see cref="IngredientCategory"/> enum, so the worst an injected note can
    /// buy is a wrong category.
    /// </para>
    /// <para>
    /// Implementations must not throw. Classification is a nice-to-have running
    /// off the back of a completed write, so a failure means "everything stays
    /// in Other", never a faulted background loop.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<IngredientCategory?>> ClassifyAsync(
        IReadOnlyList<ClassificationRequest> requests,
        CancellationToken ct = default);
}
