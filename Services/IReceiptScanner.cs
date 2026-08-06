namespace MealPlanner.Services;

/// <summary>
/// An uploaded receipt, in memory. Never a path: the file is read straight off
/// the browser upload into a byte array and handed to the subprocess over
/// stdin, so a receipt never touches this machine's disk and there is no temp
/// file to clean up, race, or leak.
/// <para>
/// <paramref name="MediaType"/> decides whether it travels as an image or a
/// document block, so it is part of the payload rather than a hint.
/// <paramref name="FileName"/> is for the log and the page's own status line
/// only — nothing keys on it.
/// </para>
/// </summary>
public sealed record ReceiptFile(byte[] Content, string MediaType, string FileName)
{
    /// <summary>
    /// What a receipt is allowed to arrive as. An allow-list rather than a
    /// check for the ones we know break, because the browser's file picker
    /// hands over whatever the operating system called the file: the
    /// <c>accept</c> attribute is a filter for the person choosing, not a
    /// guarantee to the server, exactly as <c>maxlength</c> is for a text box.
    /// <para>
    /// HEIC is the notable absence — it is what an iPhone photographs in by
    /// default, and neither the model nor a browser canvas reads it. Safari
    /// converts to JPEG on upload, so a phone lands here as one; a HEIC that
    /// arrives anyway is refused with a message rather than sent to be
    /// rejected six seconds later by the CLI.
    /// </para>
    /// </summary>
    public static bool IsSupportedMediaType(string? mediaType) => mediaType?.ToLowerInvariant() switch
    {
        "image/png" or "image/jpeg" or "image/webp" or "image/gif" or "application/pdf" => true,
        _ => false,
    };
}

/// <summary>
/// One line the scanner believes it read off a receipt: a
/// <paramref name="Name"/>, a free-text <paramref name="Quantity"/> in this
/// app's usual style ("2 bags", "1 kg"), and whether it is food at all.
/// <para>
/// <paramref name="IsFood"/> is false for the plastic bag, the batteries and
/// the loyalty-card discount, which every real receipt carries. It decides
/// only whether the review row arrives ticked — the row is always shown,
/// because this is a guess about groceries and the household is the authority
/// on its own kitchen.
/// </para>
/// <para>
/// A proposal, not a change: nothing here has been near the database. Every
/// value is still whatever a model read off a photograph, and
/// <see cref="InventoryService"/> normalizes and clamps it like any other
/// input when — and only when — someone confirms the row.
/// </para>
/// </summary>
public sealed record ScannedLine(string Name, string Quantity, bool IsFood);

/// <summary>
/// What one scan came back with: the lines to review, and — when some of the
/// receipt did not survive the parse — a sentence saying so.
/// <para>
/// <paramref name="Warning"/> exists because the alternative is this feature's
/// worst failure mode. A receipt of seventy lines reviewed as sixty, or three
/// entries dropped for having no name, leaves nothing on screen saying they were
/// ever there — the same silence the prompt's department-heading rule exists to
/// prevent on the model's side. It is prose for the household, and only ever
/// about lines that are <em>missing</em> from <paramref name="Lines"/>.
/// </para>
/// <para>
/// Not to be confused with the parser's <c>problem</c> string, which stays in
/// the log: that one also names outright failures ("no output", "no result line
/// in output"), and those arrive here as an empty <paramref name="Lines"/> —
/// still the whole of the failure signal, still without a warning, because the
/// page says "nothing readable on that one" for itself.
/// </para>
/// </summary>
public sealed record ScanResult(IReadOnlyList<ScannedLine> Lines, string? Warning = null);

/// <summary>
/// Reads the grocery lines off a photographed or scanned receipt.
/// <para>
/// Implementations must not throw. A failed scan means "nothing to review",
/// never a broken page: the household's fallback is the add form that has
/// always been there. An empty list is the whole of the failure signal and the
/// log carries the reason; the only exception let out is
/// <see cref="OperationCanceledException"/> when the caller's own token fired.
/// </para>
/// </summary>
public interface IReceiptScanner
{
    Task<ScanResult> ScanAsync(ReceiptFile file, CancellationToken ct = default);
}
