using System.ComponentModel.DataAnnotations;

namespace MealPlanner.Models;

/// <summary>
/// One feature's choice of provider, model and effort — household-wide, one row
/// per <see cref="AiFeature"/>.
/// <para>
/// Rows rather than a JSON blob, against <c>Recipe</c>'s precedent: that one
/// uses JSON for variable-length lists nobody queries, which does not describe a
/// fixed three-tuple with a natural key. Rows also keep a bad value's blast
/// radius to one setting instead of every setting at once.
/// </para>
/// <para>
/// <see cref="Id"/> <em>is</em> the enum value (<c>ValueGeneratedNever</c>), so
/// two writers racing on one feature target the same row. That is why this needs
/// nothing like <c>InventoryService</c>'s retry ladder: there, upsert-by-name is
/// read-then-write against a NOCASE index <em>and</em> a concurrent delete can
/// flip the branch back (issue #5). Neither holds here — nothing ever deletes a
/// settings row, because clearing a key nulls a column.
/// </para>
/// </summary>
public class FeatureAiSetting
{
    /// <summary>Always <c>(int)Feature</c>. See the class remarks.</summary>
    public int Id { get; set; }

    public AiFeature Feature { get; set; }

    public AiProvider Provider { get; set; }

    [Required]
    [MaxLength(100)]
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// Null when the chosen model has no effort knob at all. Nullable precisely
    /// so that case can be stored honestly — a non-null effort on a model that
    /// ignores it is a stored lie, and the page would then offer a control that
    /// means nothing.
    /// </summary>
    public AiEffort? Effort { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One API key per provider, shared by every feature — credentials are global
/// and the provider is chosen per feature, so a key on a feature row would
/// either be stored three times or make one row special.
/// <para>
/// The key is plaintext in <c>mealplanner.db</c>. That is the honest consequence
/// of a no-auth LAN app and is not papered over anywhere: the database file
/// becomes a credential store (backups, a synced folder, an <c>scp</c>), and
/// anyone who can reach the app can replace or clear a key. What the design
/// <em>does</em> guarantee is that the key never leaves the service —
/// <c>AiSettingsService.GetAsync</c> returns a type with no key field on it, so
/// a page cannot render one by accident. Encryption at rest would only move the
/// problem to wherever the decryption key lived.
/// </para>
/// </summary>
public class ProviderApiKey
{
    /// <summary>Always <c>(int)Provider</c>.</summary>
    public int Id { get; set; }

    public AiProvider Provider { get; set; }

    /// <summary>
    /// Null means "no key". The row itself is never deleted — clearing nulls
    /// this column and keeps <see cref="UpdatedAt"/>, which is what makes the
    /// no-retry argument on <see cref="FeatureAiSetting"/> true here too.
    /// </summary>
    [MaxLength(200)]
    public string? ApiKey { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
