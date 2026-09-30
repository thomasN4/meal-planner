using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MealPlanner.Data;

/// <summary>
/// Enum-as-text converters that answer with a fallback instead of throwing when
/// the database holds a value this build does not recognise.
/// <para>
/// EF's own <c>HasConversion&lt;TEnum&gt;()</c> throws during <em>materialization</em>
/// — before any service code runs — so an <c>Enum.IsDefined</c> guard in a
/// service never gets the chance to degrade gracefully. That is not theoretical:
/// enums here are stored as text precisely so the file stays legible and
/// hand-editable, and a downgrade after a provider is removed leaves exactly
/// this state. Without these converters one unreadable row takes down every page
/// that reads the table.
/// </para>
/// <para>
/// Only for columns where a fallback is <em>honest</em> — where the row is still
/// ours and one field has become unreadable. A column that identifies <em>which</em>
/// row this is has no such fallback: mapping an unknown feature name onto a real
/// feature would make a foreign row masquerade as one of ours. Those are filtered
/// out in SQL instead, before they are ever materialized.
/// </para>
/// </summary>
public static class TolerantEnumConverters
{
    /// <summary>Text in, <paramref name="fallback"/> for anything unparseable.</summary>
    public static ValueConverter<T, string> For<T>(T fallback)
        where T : struct, Enum =>
        // The read side calls out to a method rather than inlining TryParse: a
        // converter body is an expression tree, and those cannot declare an
        // `out var`.
        new(value => value.ToString(), text => Parse(text, fallback));

    /// <summary>As <see cref="For{T}"/>, but unreadable text reads as null.</summary>
    public static ValueConverter<T?, string?> ForNullable<T>()
        where T : struct, Enum =>
        new(
            value => value.HasValue ? value.Value.ToString() : null,
            text => ParseOrNull<T>(text));

    private static T Parse<T>(string? text, T fallback)
        where T : struct, Enum =>
        // Enum.TryParse also accepts numeric text ("7"), which parses happily
        // into a value no member has — IsDefined is what rejects it.
        Enum.TryParse<T>(text, out var parsed) && Enum.IsDefined(parsed) ? parsed : fallback;

    private static T? ParseOrNull<T>(string? text)
        where T : struct, Enum =>
        Enum.TryParse<T>(text, out var parsed) && Enum.IsDefined(parsed) ? parsed : null;
}
