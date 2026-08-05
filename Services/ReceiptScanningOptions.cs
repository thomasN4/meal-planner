namespace MealPlanner.Services;

/// <summary>
/// Settings for receipt scanning, bound from the "ReceiptScanning" section of
/// appsettings.json. Third of a set — see <see cref="CategorizationOptions"/>
/// and <see cref="RecipeGenerationOptions"/>; where a value here differs from
/// theirs, the difference is measured and the reason is on the property.
/// </summary>
public sealed class ReceiptScanningOptions
{
    public const string SectionName = "ReceiptScanning";

    /// <summary>
    /// Turns the feature off, including the upload control on the inventory
    /// page. Tests set this false: the suite must never spawn the claude CLI.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Resolved on PATH unless given an absolute path.</summary>
    public string ExecutablePath { get; set; } = "claude";

    public string Model { get; set; } = "sonnet";

    /// <summary>
    /// "low", like categorization and unlike recipe generation. Reading the
    /// lines off a receipt is transcription, not deliberation: measured at low
    /// effort, a printed receipt came back with every line correct including
    /// accented French, so higher effort would buy latency and nothing else.
    /// </summary>
    public string Effort { get; set; } = "low";

    /// <summary>
    /// Between categorization's 90s and recipe generation's 180s. Two measured
    /// runs took 6.3s and 11.4s; the headroom is for a photograph of a long
    /// receipt on a slow day, not for a second attempt.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Hard ceiling on the uploaded file, in bytes, and the reason nothing here
    /// resizes a photograph first.
    /// <para>
    /// That resizing looked necessary — a phone photo is megabytes and the API
    /// caps an image at 5 MB — so it was measured rather than assumed: a
    /// 3024×4032 JPEG at 2.05 MB scanned in 8.0s and the same image at 4.47 MB
    /// in 7.3s, both correct in one turn. The CLI handles the shrinking. A
    /// canvas re-encode in the browser would have bought nothing and cost a JS
    /// file, an interop call, and a stubbed call in every page test.
    /// </para>
    /// <para>
    /// So this is the whole of the size story, and it sits on the server for the
    /// same reason <see cref="InventoryService"/>'s clamping does: the browser's
    /// <c>accept</c> attribute is a convenience for whoever is choosing a file,
    /// not a promise to us. 5 MB matches the API's own per-image limit, so a
    /// bigger file would fail anyway — better here, with a message that says
    /// what to do about it.
    /// </para>
    /// </summary>
    public int MaxBytes { get; set; } = 5 * 1024 * 1024;

    /// <summary>
    /// How many lines one receipt may propose. A weekly shop is tens of items;
    /// a number far past that means the model has started inventing, and a
    /// review table nobody can read is worse than a truncated one.
    /// </summary>
    public int MaxLines { get; set; } = 60;
}
