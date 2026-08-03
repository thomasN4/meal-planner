using System.Globalization;
using System.Text;
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
    /// <para>
    /// Deliberately strict where <see cref="Suggest"/> is loose: it does
    /// <em>not</em> fold accents. The form autofills from this and then upserts
    /// under the name the user typed, and "nuoc cham" is not the same row as
    /// "nước chấm" to SQLite's NOCASE index — matching them here would silently
    /// create a duplicate under the unaccented spelling. Accented names are
    /// reachable through the suggestion list, which fills the box with the
    /// stored name before anything is written.
    /// </para>
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
    /// with it, then names that contain it, then names within a typo's reach,
    /// then names with a <em>word</em> within a typo's reach.
    /// <para>
    /// Comparison is accent-folded, because half this kitchen is names nobody
    /// can type on the keyboard they own — "nuoc cham" has to find "nước chấm".
    /// The word-level pass exists for the same reason from the other side: most
    /// rows here are long and descriptive, so a misspelling lands far outside a
    /// whole-string budget. "spagetti" is 13 edits from "dry spaghetti
    /// noodles" and one edit from a word in it.
    /// </para>
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

        // Folded once; every name below is compared against this.
        var needle = Fold(text);
        if (needle.Length == 0) return [];

        var budget = MaxDistanceFor(needle.Length);
        var scored = new List<(InventoryItem Item, int Rank, int Distance)>();

        foreach (var item in items)
        {
            var name = item.Name;
            if (string.Equals(name, text, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Folded strings are already lower-cased, so Ordinal is right here
            // — OrdinalIgnoreCase would only pay for a comparison already done.
            var hay = Fold(name);

            if (hay.StartsWith(needle, StringComparison.Ordinal))
            {
                scored.Add((item, 0, 0));
            }
            else if (hay.Contains(needle, StringComparison.Ordinal))
            {
                scored.Add((item, 1, 0));
            }
            else if (budget > 0)
            {
                // The length check is what keeps a keystroke cheap: the table
                // only runs for rows that could still be in reach.
                var distance = Math.Abs(hay.Length - needle.Length) <= budget
                               && hay.Length <= MaxDistanceInput
                               && needle.Length <= MaxDistanceInput
                    ? Distance(hay, needle, budget)
                    : budget + 1;

                if (distance <= budget)
                {
                    scored.Add((item, 2, distance));
                }
                else
                {
                    var word = ClosestWordDistance(hay, needle, budget);
                    if (word <= budget)
                    {
                        // Ranked below whole-name hits on purpose: matching one
                        // word out of five is a weaker claim than matching the
                        // name, and it is the pass most likely to be wrong.
                        scored.Add((item, 3, word));
                    }
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
    /// Lower-cases and strips accents, so a name can be found by typing what is
    /// on the keyboard: "nuoc cham" reaches "nước chấm", "banh da cua" reaches
    /// "dry Bánh Đa Cua noodles".
    /// <para>
    /// Decomposing to FormD turns an accented letter into a base letter plus a
    /// combining mark, which is then dropped — that covers ơ and ư, whose horns
    /// are combining marks. It does not cover đ, ø or ł, which are separate
    /// letters with nothing to decompose, so those are mapped by hand. đ is the
    /// one that actually matters here.
    /// </para>
    /// </summary>
    private static string Fold(string value)
    {
        string decomposed;
        try
        {
            decomposed = value.Normalize(NormalizationForm.FormD);
        }
        catch (ArgumentException)
        {
            // Normalize rejects invalid Unicode, and this string came off a
            // LAN-facing text box. A lone surrogate should make matching miss,
            // not throw on every keystroke.
            decomposed = value;
        }

        var builder = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(char.ToLowerInvariant(Unstroke(ch)));
        }

        return builder.ToString();
    }

    /// <summary>Letters that carry a stroke rather than a combining mark.</summary>
    private static char Unstroke(char c) => c switch
    {
        'Đ' or 'đ' => 'd',
        'Ø' or 'ø' => 'o',
        'Ł' or 'ł' => 'l',
        _ => c,
    };

    /// <summary>
    /// The smallest edit distance between <paramref name="needle"/> and any one
    /// word of <paramref name="hay"/>. Both are already folded.
    /// <para>
    /// Words are runs of letters and digits, so "lao gan ma (peanuts)" and
    /// "dry-fried salmon" break up the way a reader would expect.
    /// </para>
    /// </summary>
    private static int ClosestWordDistance(string hay, string needle, int max)
    {
        var best = max + 1;
        var start = -1;

        for (var i = 0; i <= hay.Length; i++)
        {
            var inWord = i < hay.Length && char.IsLetterOrDigit(hay[i]);
            if (inWord)
            {
                if (start < 0) start = i;
                continue;
            }

            if (start < 0) continue;

            var length = i - start;
            // A single word is never long enough to trouble the stack buffer,
            // but the length gate still skips most of them outright.
            if (Math.Abs(length - needle.Length) <= max)
            {
                var distance = Distance(hay.Substring(start, length), needle, max);
                if (distance < best) best = distance;
                if (best == 0) return 0;
            }

            start = -1;
        }

        return best;
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
