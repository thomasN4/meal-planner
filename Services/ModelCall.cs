using System.Text;
using MealPlanner.Models;

namespace MealPlanner.Services;

/// <summary>
/// Which provider, model, effort and key one call will use — what
/// <see cref="AiSettingsService.ResolveAsync"/> hands a feature at the start of
/// every call.
/// <para>
/// <see cref="ApiKey"/> is the one field in this app that holds key material
/// outside <see cref="AiSettingsService"/>, which is why <see cref="PrintMembers"/>
/// is overridden: a record's generated <c>ToString</c> prints every property,
/// so a <c>LogInformation("… {Model}", resolved)</c> would otherwise write the
/// key into the log. A test in <c>ModelCallTests</c> pins that.
/// </para>
/// </summary>
public sealed record ResolvedModel(AiProvider Provider, string Model, AiEffort? Effort, string? ApiKey)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Provider = {Provider}, Model = {Model}, Effort = {Effort}, ");
        builder.Append($"ApiKey = {(ApiKey is null ? "none" : "set")}");
        return true;
    }
}

/// <summary>A file sent alongside the prompt — today, only a receipt.</summary>
public sealed record ModelAttachment(byte[] Content, string MediaType)
{
    public bool IsImage => MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// One structured-output request, independent of who answers it: the feature's
/// own system prompt and JSON schema, the user turn, and the limits.
/// <para>
/// <see cref="ResponseSchema"/> is the exact string the CLI path passes to
/// <c>--json-schema</c>. API clients adapt it to their provider's strict mode
/// (<see cref="StrictSchema"/>) rather than the features keeping a second copy,
/// so the measured CLI schema never changes to suit an API.
/// </para>
/// </summary>
public sealed record ModelCall(
    string SystemPrompt,
    string ResponseSchema,
    string UserText,
    ModelAttachment? Attachment,
    int MaxOutputTokens,
    TimeSpan Timeout);

/// <summary>
/// A provider reached over HTTP with an API key. The <c>claude</c> CLI is not
/// one of these: its subprocess, flag set and stdout format are each feature's
/// own, measured, and stay where they are.
/// </summary>
public interface IApiModelClient
{
    AiProvider Provider { get; }

    /// <summary>
    /// Returns the model's answer — JSON conforming to
    /// <see cref="ModelCall.ResponseSchema"/> — as text.
    /// <para>
    /// Throws on every failure: a refusal, a truncated answer, an HTTP error,
    /// the timeout. Every caller already has a catch-all that logs and returns
    /// "nothing", which is what keeps the features' own must-not-throw
    /// contracts true; a second layer of swallowing here would only hide the
    /// reason from that log line.
    /// </para>
    /// </summary>
    Task<string> CompleteJsonAsync(ModelCall call, ResolvedModel model, CancellationToken ct = default);
}

/// <summary>Finds the registered client for a provider.</summary>
public static class ApiModelClients
{
    /// <summary>
    /// Throws when nothing is registered: a stored provider this build has no
    /// client for is a configuration fault, and the feature's catch-all turns
    /// it into a logged "nothing" like any other failed call.
    /// </summary>
    public static IApiModelClient For(IEnumerable<IApiModelClient> clients, AiProvider provider) =>
        clients.FirstOrDefault(c => c.Provider == provider)
        ?? throw new InvalidOperationException($"No client is registered for {provider}.");
}
