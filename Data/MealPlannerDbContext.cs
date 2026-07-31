using MealPlanner.Models;
using Microsoft.EntityFrameworkCore;

namespace MealPlanner.Data;

public class MealPlannerDbContext : DbContext
{
    public MealPlannerDbContext(DbContextOptions<MealPlannerDbContext> options)
        : base(options)
    {
    }

    public DbSet<InventoryItem> InventoryItems => Set<InventoryItem>();

    public DbSet<Recipe> Recipes => Set<Recipe>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<InventoryItem>(entity =>
        {
            // Store the enum as readable text so the DB is legible and Claude's
            // MCP tool calls map cleanly to string values.
            entity.Property(e => e.Category).HasConversion<string>();

            // One row per ingredient name (case-insensitive) keeps inventory tidy.
            entity.Property(e => e.Name).UseCollation("NOCASE");
            entity.HasIndex(e => e.Name).IsUnique();
        });
    }
}
