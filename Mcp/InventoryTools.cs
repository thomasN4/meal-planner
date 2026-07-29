using System.ComponentModel;
using System.Text;
using MealPlanner.Models;
using MealPlanner.Services;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace MealPlanner.Mcp;

/// <summary>
/// MCP tools for kitchen inventory. Every mutation goes through
/// <see cref="InventoryService"/> — never the DbContext directly.
/// </summary>
[McpServerToolType]
public sealed class InventoryTools
{
    private const string CategoryValues =
        "Produce, FreshHerbs, DrySeasonings, MeatAndSeafood, Dairy, Grains, " +
        "Canned, Frozen, Condiments, Baking, Beverages, Snacks, Other";

    [McpServerTool(Name = "list_inventory", ReadOnly = true, Idempotent = true),
     Description(
         "List kitchen inventory items currently on hand. Optionally filter to one " +
         "category. Returns name, quantity (free text; empty means amount unspecified), " +
         "category, notes, and updatedAt (UTC) for each item.")]
    public static async Task<string> ListInventory(
        InventoryService inventory,
        [Description(
            "Optional category filter. Case-insensitive. Valid values: " + CategoryValues +
            ". Omit to list everything.")]
        string? category = null,
        CancellationToken cancellationToken = default)
    {
        IngredientCategory? filter = null;
        if (!string.IsNullOrWhiteSpace(category))
        {
            if (!TryParseCategory(category, out var parsed, out var error))
            {
                return error;
            }
            filter = parsed;
        }

        var items = await inventory.GetAllAsync(cancellationToken);
        if (filter.HasValue)
        {
            items = items.Where(i => i.Category == filter.Value).ToList();
        }

        if (items.Count == 0)
        {
            return filter.HasValue
                ? $"No inventory items in category {filter.Value}."
                : "Inventory is empty.";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"count: {items.Count}");
        foreach (var item in items)
        {
            var qty = string.IsNullOrEmpty(item.Quantity) ? "(unspecified)" : item.Quantity;
            var notes = string.IsNullOrEmpty(item.Notes) ? "" : $"; notes={item.Notes}";
            sb.AppendLine(
                $"- {item.Name}: quantity={qty}; category={item.Category}; " +
                $"updatedAt={item.UpdatedAt:O}{notes}");
        }
        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "upsert_item", Destructive = false),
     Description(
         "Create or update a kitchen inventory item by name (case-insensitive match). " +
         "Quantity is free text (e.g. \"2 bags\", \"half a bottle\"); empty means amount " +
         "unspecified. Category and notes are only changed when supplied. Returns a short " +
         "diff describing what changed.")]
    public static async Task<string> UpsertItem(
        InventoryService inventory,
        [Description("Ingredient name (required). Matched case-insensitively; max 100 chars.")]
        string name,
        [Description(
            "Free-text amount, e.g. \"2 bags\", \"3 cartons\", \"none\". " +
            "Empty string means amount unspecified. Max 50 chars.")]
        string? quantity = null,
        [Description(
            "Optional category. Case-insensitive. Valid values: " + CategoryValues +
            ". Omitted on update keeps the existing category; on create defaults to Other.")]
        string? category = null,
        [Description("Optional notes (max 500 chars). Omitted on update keeps existing notes.")]
        string? notes = null,
        CancellationToken cancellationToken = default)
    {
        IngredientCategory? parsedCategory = null;
        if (!string.IsNullOrWhiteSpace(category))
        {
            if (!TryParseCategory(category, out var parsed, out var error))
            {
                return error;
            }
            parsedCategory = parsed;
        }

        try
        {
            var change = await inventory.UpsertAsync(
                name,
                quantity ?? "",
                parsedCategory,
                notes,
                cancellationToken);
            return change.Describe();
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }
        catch (DbUpdateException)
        {
            // The service retries lost write races several times; getting here
            // means household members and Claude were all writing this one
            // ingredient at once. Say so in words — an exception would reach
            // Claude as an opaque transport error it can't act on.
            return $"Could not save \"{name.Trim()}\" — another writer changed it " +
                   "at the same time. Try again.";
        }
    }

    [McpServerTool(Name = "remove_item", Destructive = true),
     Description(
         "Remove an ingredient from kitchen inventory by name (case-insensitive). " +
         "Returns a short diff, or a not-found message if the item was already gone.")]
    public static async Task<string> RemoveItem(
        InventoryService inventory,
        [Description("Ingredient name to remove (required). Matched case-insensitively.")]
        string name,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var change = await inventory.RemoveAsync(name, cancellationToken);
            return change.Describe();
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }
    }

    private static bool TryParseCategory(
        string value,
        out IngredientCategory category,
        out string error)
    {
        if (Enum.TryParse(value.Trim(), ignoreCase: true, out category)
            && Enum.IsDefined(category))
        {
            error = "";
            return true;
        }

        category = default;
        error = $"Unknown category \"{value.Trim()}\". Valid values: {CategoryValues}.";
        return false;
    }
}
