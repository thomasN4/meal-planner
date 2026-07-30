namespace MealPlanner.Services;

/// <summary>
/// Settings for auto-categorization, bound from the "Categorization" section
/// of appsettings.json.
/// </summary>
public sealed class CategorizationOptions
{
    public const string SectionName = "Categorization";

    /// <summary>
    /// Turns the whole feature off, including the background service. Tests set
    /// this false: the suite must never spawn the claude CLI.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Resolved on PATH unless given an absolute path.</summary>
    public string ExecutablePath { get; set; } = "claude";

    public string Model { get; set; } = "sonnet";

    /// <summary>
    /// Deliberately "low". The output is pinned to a 13-value enum by the JSON
    /// schema, so there is no deliberation to buy — higher effort spends latency
    /// and tokens on a decision that is one word long.
    /// </summary>
    public string Effort { get; set; } = "low";

    public int TimeoutSeconds { get; set; } = 90;

    /// <summary>
    /// Names classified per CLI call. Measured: one name costs ~3.3s and eight
    /// cost ~3.4s, because spawning the process dominates. Batching is what
    /// makes "Claude, stock the kitchen" one call instead of twenty.
    /// </summary>
    public int MaxBatchSize { get; set; } = 20;

    /// <summary>
    /// How long to keep collecting names after the first one arrives before
    /// sending the batch. Long enough to catch an MCP burst, short enough that a
    /// lone hand-typed ingredient isn't left sitting in Other.
    /// </summary>
    public int BatchWindowMilliseconds { get; set; } = 400;

    /// <summary>Remembered name→category pairs, so re-adding "Salt" is free.</summary>
    public int CacheSize { get; set; } = 500;
}
