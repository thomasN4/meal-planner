using MealPlanner.Models;

namespace MealPlanner.Services;

/// <summary>
/// Matches typed text against ingredient names the page already has in hand.
/// <para>
/// Pure and static on purpose: it reads no database and holds no state, so the
/// inventory page can run it on every keystroke against the list
/// <see cref="InventoryService.GetAllAsync"/> already gave it. That keeps
/// <see cref="InventoryService"/> the single choke point for real reads — this
/// is a view over data that came through it, not a second way in.
/// </para>
/// <para>
/// Complexity is O(items × |name| × |query|) in the worst case. At this app's
/// scale — one household's kitchen, dozens of rows — that is microseconds, and
/// the length prefilters mean the edit-distance loop runs on a small minority
/// of rows. Nothing here is optimised beyond that, deliberately.
/// </para>
/// </summary>
internal static class IngredientMatcher
{
    /// <summary>How many rows the dropdown offers. Five fits without scrolling.</summary>
    internal const int MaxSuggestions = 5;

    /// <summary>
    /// Longest pair the edit-distance table will run over. Names clamp to 100
    /// characters in <see cref="InventoryService"/>, but a query comes straight
    /// off a LAN-facing text box, so the bound is what keeps the stack buffer
    /// below safe.
    /// </summary>
    private const int MaxDistanceInput = 128;

    /// <summary>
    /// The row a name is editing, or <c>null</c>. Exact after trimming, ignoring
    /// case — SQLite's NOCASE collation makes "Salt" and "salt" one row in the
    /// database, but it does not reach in-memory C#, so the comparison has to
    /// say so itself.
    /// </summary>
    internal static InventoryItem? ExactMatch(IReadOnlyList<InventoryItem>? items, string? name)
    {
        // items is null while the page's first render beats OnInitializedAsync.
        if (items is null) return null;

        var query = name?.Trim();
        if (string.IsNullOrEmpty(query)) return null;

        foreach (var item in items)
        {
            if (string.Equals(item.Name, query, StringComparison.OrdinalIgnoreCase))
            {
                return item;
            }
        }

        return null;
    }

    /// <summary>
    /// Near matches for <paramref name="query"/>, best first: names that start
    /// with it, then names that contain it, then names within a typo's reach.
    /// <para>
    /// An exact match is deliberately left out. The form states that case
    /// outright in its own hint, and a row in this list can be arrowed onto —
    /// which would turn Enter into "fill the form" instead of "add the item"
    /// for the most common keystroke sequence in the app.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<InventoryItem> Suggest(
        IReadOnlyList<InventoryItem>? items,
        string? query,
        int limit = MaxSuggestions)
    {
        if (items is null || limit <= 0) return [];

        var text = query?.Trim();
        if (string.IsNullOrEmpty(text)) return [];

        var budget = MaxDistanceFor(text.Length);
        var scored = new List<(InventoryItem Item, int Rank, int Distance)>();

        foreach (var item in items)
        {
            var name = item.Name;
            if (string.Equals(name, text, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (name.StartsWith(text, StringComparison.OrdinalIgnoreCase))
            {
                scored.Add((item, 0, 0));
            }
            else if (name.Contains(text, StringComparison.OrdinalIgnoreCase))
            {
                scored.Add((item, 1, 0));
            }
            else if (budget > 0
                     && Math.Abs(name.Length - text.Length) <= budget
                     && name.Length <= MaxDistanceInput
                     && text.Length <= MaxDistanceInput)
            {
                // The length checks above are what keep a keystroke cheap: the
                // table below only runs for rows that could still be in reach.
                var distance = Distance(name, text, budget);
                if (distance <= budget)
                {
                    scored.Add((item, 2, distance));
                }
            }
        }

        // Ordered down to the name so the list is stable between keystrokes and
        // the tests have something to assert. Shorter first within a rank: a
        // prefix hit on a short name is more likely the row that was meant.
        return scored
            .OrderBy(s => s.Rank)
            .ThenBy(s => s.Distance)
            .ThenBy(s => s.Item.Name.Length)
            .ThenBy(s => s.Item.Name, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(s => s.Item)
            .ToList();
    }

    /// <summary>
    /// How many edits a query of this length may be off by.
    /// <para>
    /// Zero below four characters, i.e. typo matching is off for short queries:
    /// at that length "sal", "oil" and "egg" are each one edit from half the
    /// kitchen, and prefix and substring matching already cover them well.
    /// </para>
    /// </summary>
    internal static int MaxDistanceFor(int queryLength) =>
        queryLength < 4 ? 0
        : queryLength < 8 ? 1
        : 2;

    /// <summary>
    /// Optimal string alignment distance between <paramref name="a"/> and
    /// <paramref name="b"/>, ignoring case, giving up once it passes
    /// <paramref name="max"/>.
    /// <para>
    /// Transpositions cost one edit rather than two, so "cilnatro" is one slip
    /// away from "cilantro" — swapped letters are the typo a keyboard actually
    /// produces. Returns <paramref name="max"/> + 1 for anything further apart;
    /// the caller only needs to know whether it is within budget.
    /// </para>
    /// </summary>
    internal static int Distance(string a, string b, int max)
    {
        if (max < 0) return 1;
        if (a.Length == 0) return Math.Min(b.Length, max + 1);
        if (b.Length == 0) return Math.Min(a.Length, max + 1);
        if (Math.Abs(a.Length - b.Length) > max) return max + 1;
        if (a.Length > MaxDistanceInput || b.Length > MaxDistanceInput) return max + 1;

        // Three rolling rows: the previous two are all a transposition needs.
        // Bounded by MaxDistanceInput above, so the stack allocation is safe.
        Span<int> twoAgo = stackalloc int[b.Length + 1];
        Span<int> previous = stackalloc int[b.Length + 1];
        Span<int> current = stackalloc int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var rowBest = current[0];
            var ai = char.ToLowerInvariant(a[i - 1]);

            for (var j = 1; j <= b.Length; j++)
            {
                var bj = char.ToLowerInvariant(b[j - 1]);
                var cost = ai == bj ? 0 : 1;

                var value = Math.Min(
                    Math.Min(previous[j] + 1, current[j - 1] + 1),
                    previous[j - 1] + cost);

                if (i > 1 && j > 1
                    && ai == char.ToLowerInvariant(b[j - 2])
                    && char.ToLowerInvariant(a[i - 2]) == bj)
                {
                    value = Math.Min(value, twoAgo[j - 2] + 1);
                }

                current[j] = value;
                rowBest = Math.Min(rowBest, value);
            }

            // Every later row is at least this good, so nothing below can come
            // back under budget.
            if (rowBest > max) return max + 1;

            var spare = twoAgo;
            twoAgo = previous;
            previous = current;
            current = spare;
        }

        var distance = previous[b.Length];
        return distance > max ? max + 1 : distance;
    }
}
