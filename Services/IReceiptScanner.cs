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
/// <para>
/// <paramref name="Department"/> is the receipt's department heading the line
/// sat under, or <c>null</c>. Display-only context, never persisted: the field
/// exists so a heading has somewhere to be that is not the next line's name
/// (issue #31), and so the review can show the residue when the model welds
/// one in anyway.
/// </para>
/// </summary>
public sealed record ScannedLine(string Name, string Quantity, bool IsFood, string? Department = null);

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
/// in output"), and those arrive here as an empty <paramref name="Lines"/>
/// without a warning, because the page says "nothing readable on that one" for
/// itself.
/// </para>
/// <para>
/// <paramref name="Failed"/> means the call itself never produced an answer —
/// no key, no network, a timeout, a provider that answered with nothing. The
/// page used to see that as an empty list like any other and told the household
/// to try a flatter photo, blaming a photograph nothing had looked at. Measured
/// over OpenRouter on 2026-09-23: one scan in three came back with no answer
/// text, and the same request replayed worked. An answer with nothing usable on
/// it is still an empty <paramref name="Lines"/> with <paramref name="Failed"/>
/// false; that one may well be the photo.
/// </para>
/// </summary>
public sealed record ScanResult(IReadOnlyList<ScannedLine> Lines, string? Warning = null, bool Failed = false);

/// <summary>
/// Reads the grocery lines off a photographed or scanned receipt.
/// <para>
/// Implementations must not throw. A failed scan means "nothing to review",
/// never a broken page: the household's fallback is the add form that has
/// always been there. A failure arrives as an empty list with
/// <see cref="ScanResult.Failed"/> set, and the log carries the reason; the only
/// exception let out is <see cref="OperationCanceledException"/> when the
/// caller's own token fired.
/// </para>
/// </summary>
public interface IReceiptScanner
{
    Task<ScanResult> ScanAsync(ReceiptFile file, CancellationToken ct = default);
}
