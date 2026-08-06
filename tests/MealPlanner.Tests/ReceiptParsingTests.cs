using System.Text;
using System.Text.Json;

namespace MealPlanner.Tests;

/// <summary>
/// The two ends of the receipt subprocess that are pure functions:
/// <see cref="ClaudeReceiptScanner.BuildPayload"/> writes the stream-json
/// message that goes in, and <see cref="ClaudeReceiptScanner.ParseScan"/> reads
/// the JSON lines that come back. No process is spawned here.
/// <para>
/// The fixtures below keep the shape of a real measured run — a system init
/// line, an assistant turn carrying the model's tool call, and one
/// <c>{"type":"result"}</c> line with the answer in both
/// <c>structured_output</c> and <c>result</c>.
/// </para>
/// </summary>
public class ReceiptParsingTests
{
    private static IReadOnlyList<ScannedLine> Parse(string output, out string? problem) =>
        ClaudeReceiptScanner.ParseScan(output, maxLines: 60, out problem);

    /// <summary>
    /// A stream-json run, as the CLI actually prints one. <paramref name="assistantJson"/>
    /// is what the model's own turn claimed, which is not always what the
    /// result line ends up carrying.
    /// </summary>
    private static string Transcript(string resultJson, string? assistantJson = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine("""{"type":"system","subtype":"init","tools":["StructuredOutput"]}""");
        builder.AppendLine(
            """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"StructuredOutput","input":"""
            + (assistantJson ?? resultJson) + "}]}}");
        builder.AppendLine("""{"type":"rate_limit_event","rate_limit_info":{"status":"allowed"}}""");
        builder.AppendLine(
            """{"type":"result","subtype":"success","is_error":false,"result":"""
            + JsonSerializer.Serialize(resultJson)
            + ""","structured_output":""" + resultJson + "}");
        return builder.ToString();
    }

    [Fact]
    public void A_well_formed_receipt_yields_every_line()
    {
        var lines = Parse(
            Transcript(
                """{"products":[{"name":"Lait demi-écrémé","quantity":"2 briques","isFood":true},{"name":"Riz basmati","quantity":"1 kg","isFood":true}]}"""),
            out var problem);

        Assert.Null(problem);
        Assert.Equal(["Lait demi-écrémé", "Riz basmati"], lines.Select(l => l.Name));
        Assert.Equal(["2 briques", "1 kg"], lines.Select(l => l.Quantity));
        Assert.All(lines, line => Assert.True(line.IsFood));
    }

    [Fact]
    public void Non_food_lines_come_back_flagged_rather_than_dropped()
    {
        // Every real receipt carries them, and the household is the authority
        // on its own kitchen — the flag decides a checkbox, not the row.
        var lines = Parse(
            Transcript(
                """{"products":[{"name":"Pois chiches","quantity":"400g","isFood":true},{"name":"Piles AAA","quantity":"4","isFood":false}]}"""),
            out _);

        Assert.Equal(2, lines.Count);
        Assert.True(lines[0].IsFood);
        Assert.False(lines[1].IsFood);
    }

    [Fact]
    public void The_result_line_wins_over_the_model_s_own_turn()
    {
        // Measured: an answer the schema rejects is retried, so the assistant
        // turn can hold a shape that never became the answer. Scanning the
        // stream for the first '{' — which is how the other two parsers work,
        // and they are right to — would read the rejected one here.
        var lines = Parse(
            Transcript(
                """{"products":[{"name":"Coriandre fraîche","quantity":"1 botte","isFood":true}]}""",
                assistantJson: """{"products":{"products":[{"name":"WRONG SHAPE","quantity":"","isFood":true}]}}"""),
            out var problem);

        Assert.Null(problem);
        var line = Assert.Single(lines);
        Assert.Equal("Coriandre fraîche", line.Name);
    }

    [Fact]
    public void The_result_string_carries_the_answer_when_structured_output_does_not()
    {
        // Both fields were present in every measured run. Preferring the parsed
        // one costs nothing, but a CLI that stopped sending it must not turn a
        // working scan into "no output".
        var lines = Parse(
            """
            {"type":"system","subtype":"init"}
            {"type":"result","subtype":"success","result":"{\"products\":[{\"name\":\"Tomates grappe\",\"quantity\":\"500g\",\"isFood\":true}]}"}
            """,
            out var problem);

        Assert.Null(problem);
        Assert.Equal("Tomates grappe", Assert.Single(lines).Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n  \n")]
    [InlineData("""{"type":"system","subtype":"init"}""")]
    [InlineData("claude: command not found")]
    public void A_run_with_no_result_line_reads_nothing_and_says_why(string output)
    {
        var lines = Parse(output, out var problem);

        Assert.Empty(lines);
        Assert.NotNull(problem);
    }

    [Fact]
    public void A_result_line_with_no_products_reads_nothing_and_says_why()
    {
        var lines = Parse(
            """{"type":"result","subtype":"success","structured_output":{"answer":"no idea"}}""",
            out var problem);

        Assert.Empty(lines);
        Assert.Equal("no \"products\" array", problem);
    }

    [Fact]
    public void An_empty_receipt_is_reported_as_unreadable_rather_than_as_success()
    {
        // The page shows this one to the user: an empty review table with a
        // cheerful nothing in it would read as "your receipt has no food on it".
        var lines = Parse(Transcript("""{"products":[]}"""), out var problem);

        Assert.Empty(lines);
        Assert.Equal("nothing readable on the receipt", problem);
    }

    [Fact]
    public void Lines_that_cannot_be_used_are_dropped_alone()
    {
        // One unreadable line out of a receipt is the case this feature exists
        // for. The rest are still worth reviewing.
        var lines = Parse(
            Transcript(
                """{"products":[{"quantity":"2","isFood":true},{"name":"","quantity":"1","isFood":true},{"name":42},{"name":"Riz basmati","quantity":"1 kg","isFood":true}]}"""),
            out var problem);

        Assert.Equal("Riz basmati", Assert.Single(lines).Name);
        Assert.Equal("3 unusable line(s)", problem);
    }

    [Fact]
    public void A_line_with_no_quantity_is_kept_with_an_empty_one()
    {
        // Empty means "have some, amount unspecified" everywhere in this app,
        // and a receipt that only prints a price is exactly that.
        var lines = Parse(
            Transcript("""{"products":[{"name":"Sel","isFood":true},{"name":"Poivre","quantity":null,"isFood":true}]}"""),
            out _);

        Assert.Equal(2, lines.Count);
        Assert.All(lines, line => Assert.Equal(string.Empty, line.Quantity));
    }

    [Fact]
    public void A_missing_food_flag_fails_towards_showing_the_row_ticked()
    {
        var lines = Parse(
            Transcript("""{"products":[{"name":"Riz basmati","quantity":"1 kg"},{"name":"Sac plastique","quantity":"1","isFood":"no"}]}"""),
            out _);

        Assert.All(lines, line => Assert.True(line.IsFood));
    }

    [Fact]
    public void Names_and_quantities_are_clamped_to_what_the_service_would_store()
    {
        // The service clamps again and is still the trust boundary — but these
        // go into a table a person reads first, and one 4,000-character name
        // would make that table unusable before anything reached the database.
        var lines = Parse(
            Transcript(
                $$"""{"products":[{"name":"{{new string('n', 400)}}","quantity":"{{new string('q', 400)}}","isFood":true}]}"""),
            out _);

        var line = Assert.Single(lines);
        Assert.Equal(100, line.Name.Length);
        Assert.Equal(50, line.Quantity.Length);
    }

    [Fact]
    public void A_receipt_longer_than_the_cap_stops_at_it()
    {
        var products = string.Join(',', Enumerable.Range(0, 10)
            .Select(i => $$"""{"name":"Item {{i}}","quantity":"1","isFood":true}"""));

        var lines = ClaudeReceiptScanner.ParseScan(
            Transcript($$"""{"products":[{{products}}]}"""), maxLines: 4, out var problem);

        Assert.Equal(4, lines.Count);
        Assert.Equal("stopped at 4 line(s)", problem);
    }

    [Fact]
    public void A_cap_of_zero_means_no_cap_rather_than_no_lines()
    {
        // Read the other way, a MaxLines of 0 is a setting that switches the
        // feature off while looking like a limit, and the only sign of it would
        // be an empty review nobody could account for. Enabled is the switch.
        var products = string.Join(',', Enumerable.Range(0, 10)
            .Select(i => $$"""{"name":"Item {{i}}","quantity":"1","isFood":true}"""));

        var lines = ClaudeReceiptScanner.ParseScan(
            Transcript($$"""{"products":[{{products}}]}"""), maxLines: 0, out var problem);

        Assert.Equal(10, lines.Count);
        Assert.Null(problem);
    }

    [Theory]
    // The two problems that mean "there was more on that receipt than you are
    // looking at" — the only ones the household can act on.
    [InlineData("stopped at 60 line(s)", 60, "more lines than fit")]
    [InlineData("3 unusable line(s)", 12, "3 line(s)")]
    // Outright failures arrive as an empty list, which the page already has one
    // sentence for. A warning as well would be two messages about one event.
    [InlineData("no output", 0, null)]
    [InlineData("no result line in output", 0, null)]
    [InlineData("nothing readable on the receipt", 0, null)]
    [InlineData("no \"products\" array", 0, null)]
    public void Only_a_partly_lost_receipt_is_worth_warning_about(
        string problem, int kept, string? expected)
    {
        var warning = ClaudeReceiptScanner.WarnAbout(problem, kept);

        if (expected is null)
        {
            Assert.Null(warning);
        }
        else
        {
            Assert.Contains(expected, warning!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_clean_scan_carries_no_warning()
    {
        Assert.Null(ClaudeReceiptScanner.WarnAbout(problem: null, kept: 7));
    }

    [Fact]
    public void An_image_travels_as_an_image_block()
    {
        var payload = ClaudeReceiptScanner.BuildPayload(
            new ReceiptFile([1, 2, 3], "image/png", "receipt.png"));

        var content = JsonDocument.Parse(payload).RootElement
            .GetProperty("message").GetProperty("content");
        var block = content[0];

        Assert.Equal("user", JsonDocument.Parse(payload).RootElement.GetProperty("type").GetString());
        Assert.Equal("image", block.GetProperty("type").GetString());
        Assert.Equal("image/png", block.GetProperty("source").GetProperty("media_type").GetString());
        Assert.Equal(
            Convert.ToBase64String([1, 2, 3]),
            block.GetProperty("source").GetProperty("data").GetString());
        Assert.Equal("text", content[1].GetProperty("type").GetString());
    }

    [Fact]
    public void A_pdf_travels_as_a_document_block()
    {
        // Send either one as the other and the CLI rejects the message, which
        // surfaces as "the scanner is broken" rather than as anything about
        // file types — so this is worth an assertion of its own.
        var payload = ClaudeReceiptScanner.BuildPayload(
            new ReceiptFile([4, 5, 6], "application/pdf", "receipt.pdf"));

        var block = JsonDocument.Parse(payload).RootElement
            .GetProperty("message").GetProperty("content")[0];

        Assert.Equal("document", block.GetProperty("type").GetString());
        Assert.Equal("application/pdf", block.GetProperty("source").GetProperty("media_type").GetString());
    }

    [Fact]
    public void The_payload_is_one_line_because_the_protocol_is_line_delimited()
    {
        var payload = ClaudeReceiptScanner.BuildPayload(
            new ReceiptFile(Enumerable.Range(0, 4096).Select(i => (byte)i).ToArray(),
                "image/jpeg", "receipt.jpg"));

        Assert.DoesNotContain('\n', payload);
    }

    [Theory]
    [InlineData("image/png", true)]
    [InlineData("image/jpeg", true)]
    [InlineData("IMAGE/JPEG", true)]
    [InlineData("application/pdf", true)]
    [InlineData("image/heic", false)]
    [InlineData("text/plain", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_what_the_model_can_read_is_accepted(string? mediaType, bool supported) =>
        Assert.Equal(supported, ReceiptFile.IsSupportedMediaType(mediaType));
}
