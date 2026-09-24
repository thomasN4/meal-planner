using System.Text.Json.Nodes;

namespace MealPlanner.Services;

/// <summary>
/// Adapts a feature's <c>--json-schema</c> string to what an API provider's
/// strict structured-output mode accepts.
/// <para>
/// Adapted here, per call, rather than by editing the three schemas: those are
/// what the CLI path sends, their wording was measured (the receipt schema's
/// array is named <c>products</c> because <c>items</c> confused the model), and
/// a provider's restrictions are that provider's business.
/// </para>
/// <para>
/// Strips only the keywords our schemas actually use that a strict mode rejects
/// — today the recipe schema's <c>minItems: 2</c> / <c>maxItems: 3</c>. The
/// Anthropic API accepts <c>minItems</c> of 0 or 1 only, and nothing is gained by
/// finding out which bounds OpenAI and every OpenRouter backend tolerate: the
/// prompt already asks for 2–3 recipes and <c>ParseRecipes</c> takes any count,
/// which is the degradation the schema's own comment already plans for.
/// </para>
/// </summary>
public static class StrictSchema
{
    private static readonly string[] Unsupported = ["minItems", "maxItems"];

    public static JsonObject Adapt(string schema)
    {
        var root = JsonNode.Parse(schema)?.AsObject()
            ?? throw new ArgumentException("A response schema must be a JSON object.", nameof(schema));
        Strip(root);
        return root;
    }

    private static void Strip(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var keyword in Unsupported)
                {
                    obj.Remove(keyword);
                }

                foreach (var (_, child) in obj)
                {
                    Strip(child);
                }

                break;

            case JsonArray array:
                foreach (var child in array)
                {
                    Strip(child);
                }

                break;
        }
    }
}
