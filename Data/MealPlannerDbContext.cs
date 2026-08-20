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

    public DbSet<FeatureAiSetting> FeatureAiSettings => Set<FeatureAiSetting>();

    public DbSet<ProviderApiKey> ProviderApiKeys => Set<ProviderApiKey>();

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

        modelBuilder.Entity<FeatureAiSetting>(entity =>
        {
            // The key is the feature, so the app supplies the Id rather than
            // letting SQLite pick one. Two writers on one feature then contend
            // for the same row instead of racing to insert two.
            entity.Property(e => e.Id).ValueGeneratedNever();

            // Same reason as InventoryItem.Category: someone debugging "which
            // model is it using" reads this file, and enums-as-text is what
            // makes that possible without a lookup.
            //
            // Feature keeps the plain converter: it says *which* row this is, so
            // there is no honest fallback for an unreadable one. AiSettingsService
            // filters those out in SQL rather than materializing them. Provider
            // and Effort are tolerant, because there the row is still ours and
            // one field has gone unreadable — see TolerantEnumConverters.
            entity.Property(e => e.Feature).HasConversion<string>();
            entity.Property(e => e.Provider)
                .HasConversion(TolerantEnumConverters.For(AiProvider.ClaudeCli));
            entity.Property(e => e.Effort)
                .HasConversion(TolerantEnumConverters.ForNullable<AiEffort>());

            // Redundant with the primary key by construction, and kept anyway:
            // it is what still holds if a row is hand-edited to the wrong Id.
            entity.HasIndex(e => e.Feature).IsUnique();
        });

        modelBuilder.Entity<ProviderApiKey>(entity =>
        {
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.Provider).HasConversion<string>();
            entity.HasIndex(e => e.Provider).IsUnique();
        });
    }
}
