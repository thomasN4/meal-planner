using System.Text.Json;
using MealPlanner.Data;
using Microsoft.EntityFrameworkCore;

namespace MealPlanner.Tests;

public class AiSettingsServiceTests
{
    [Fact]
    public async Task A_fresh_database_reports_the_appsettings_defaults()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        var settings = await harness.NewAiSettingsService().GetAsync();

        Assert.Equal(3, settings.Features.Count);
        Assert.All(settings.Features, f =>
        {
            Assert.Equal(AiProvider.ClaudeCli, f.Provider);
            Assert.Equal("sonnet", f.Model);
        });

        // The efforts differ per feature on purpose, and the divergence is
        // documented in the options classes. If someone "tidies" them to one
        // value, this is what says so.
        Assert.Equal(AiEffort.Medium, Choice(settings, AiFeature.RecipeGeneration).Effort);
        Assert.Equal(AiEffort.Low, Choice(settings, AiFeature.Categorization).Effort);
        Assert.Equal(AiEffort.Low, Choice(settings, AiFeature.ReceiptScanning).Effort);
    }

    [Fact]
    public async Task Saving_a_feature_leaves_the_other_two_alone()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        var service = harness.NewAiSettingsService();

        await service.SaveFeatureAsync(
            AiFeature.RecipeGeneration, AiProvider.AnthropicApi, "claude-opus-5", AiEffort.High);

        var settings = await service.GetAsync();
        Assert.Equal("claude-opus-5", Choice(settings, AiFeature.RecipeGeneration).Model);
        Assert.Equal("sonnet", Choice(settings, AiFeature.Categorization).Model);
        Assert.Equal("sonnet", Choice(settings, AiFeature.ReceiptScanning).Model);
    }

    [Fact]
    public async Task Saving_the_same_feature_twice_keeps_one_row()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        var service = harness.NewAiSettingsService();

        await service.SaveFeatureAsync(AiFeature.Categorization, AiProvider.ClaudeCli, "haiku", AiEffort.Low);
        await service.SaveFeatureAsync(AiFeature.Categorization, AiProvider.ClaudeCli, "opus", AiEffort.High);

        await using var db = await harness.Factory.CreateDbContextAsync();
        Assert.Equal(1, await db.FeatureAiSettings.CountAsync());
        Assert.Equal("opus", (await service.GetAsync()).Features
            .First(f => f.Feature == AiFeature.Categorization).Model);
    }

    /// <summary>
    /// The most valuable test in the file: it goes red the day somebody adds a
    /// key field to <c>CredentialStatus</c>, which is the one change that would
    /// let a page render a secret without anybody noticing.
    /// </summary>
    [Fact]
    public async Task An_api_key_is_stored_and_never_handed_back_by_the_snapshot()
    {
        const string secret = "sk-ant-api03-not-a-real-key-1234";

        await using var harness = await InventoryHarness.CreateAsync();
        var service = harness.NewAiSettingsService();
        await service.SetApiKeyAsync(AiProvider.AnthropicApi, secret);

        var settings = await service.GetAsync();
        var status = settings.Credentials.First(c => c.Provider == AiProvider.AnthropicApi);

        Assert.True(status.HasKey);
        Assert.Equal("1234", status.Tail);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(settings), StringComparison.Ordinal);

        // And the material is still reachable by the one method meant to.
        Assert.Equal(secret, await service.GetApiKeyAsync(AiProvider.AnthropicApi));
    }

    [Fact]
    public async Task A_key_too_short_to_hide_gets_no_tail()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        var service = harness.NewAiSettingsService();
        await service.SetApiKeyAsync(AiProvider.OpenAi, "sk-abc");

        var status = (await service.GetAsync()).Credentials.First(c => c.Provider == AiProvider.OpenAi);
        Assert.True(status.HasKey);
        Assert.Null(status.Tail);
    }

    [Fact]
    public async Task Clearing_a_key_keeps_the_row_and_forgets_the_key()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        var service = harness.NewAiSettingsService();
        await service.SetApiKeyAsync(AiProvider.OpenRouter, "sk-or-v1-abcdefghijklmnop");

        var cleared = await service.ClearApiKeyAsync(AiProvider.OpenRouter);

        Assert.False(cleared.HasKey);
        Assert.Null(cleared.Tail);
        Assert.Null(await service.GetApiKeyAsync(AiProvider.OpenRouter));

        // The row surviving is what makes the "no retry ladder needed" argument
        // in the class doc true: nothing here ever deletes a row, so a losing
        // writer cannot find the branch flipped back to insert.
        await using var db = await harness.Factory.CreateDbContextAsync();
        Assert.Equal(1, await db.ProviderApiKeys.CountAsync());
    }

    [Fact]
    public async Task Clearing_a_provider_that_never_had_a_key_is_not_an_error()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        var cleared = await harness.NewAiSettingsService().ClearApiKeyAsync(AiProvider.OpenAi);
        Assert.False(cleared.HasKey);
    }

    [Fact]
    public async Task The_claude_subscription_refuses_a_key()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            harness.NewAiSettingsService().SetApiKeyAsync(AiProvider.ClaudeCli, "sk-whatever"));
    }

    [Fact]
    public async Task An_empty_key_is_refused_rather_than_treated_as_a_clear()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            harness.NewAiSettingsService().SetApiKeyAsync(AiProvider.OpenAi, "   "));
    }

    [Fact]
    public async Task A_model_id_is_trimmed_and_clamped()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        var service = harness.NewAiSettingsService();

        var saved = await service.SaveFeatureAsync(
            AiFeature.ReceiptScanning, AiProvider.OpenRouter, "  " + new string('m', 200) + "  ", null);

        Assert.Equal(100, saved.Model.Length);
    }

    [Fact]
    public async Task An_empty_model_id_is_refused()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            harness.NewAiSettingsService().SaveFeatureAsync(
                AiFeature.Categorization, AiProvider.ClaudeCli, "  ", AiEffort.Low));
    }

    /// <summary>
    /// Haiku 4.5 rejects an effort setting outright, so storing one would be a
    /// stored lie and the page would offer a control that means nothing. This is
    /// enforced below the page so a later caller cannot reintroduce it.
    /// </summary>
    [Fact]
    public async Task An_effort_a_model_has_no_knob_for_is_not_stored()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        var service = harness.NewAiSettingsService();

        var saved = await service.SaveFeatureAsync(
            AiFeature.Categorization, AiProvider.AnthropicApi, "claude-haiku-4-5", AiEffort.High);

        Assert.Null(saved.Effort);
        Assert.Null((await service.GetAsync()).Features
            .First(f => f.Feature == AiFeature.Categorization).Effort);
    }

    [Fact]
    public async Task An_effort_the_provider_does_not_offer_is_not_stored()
    {
        await using var harness = await InventoryHarness.CreateAsync();

        // OpenRouter normalizes to low/medium/high; Minimal is OpenAI's alone.
        var saved = await harness.NewAiSettingsService().SaveFeatureAsync(
            AiFeature.RecipeGeneration, AiProvider.OpenRouter, "openai/gpt-5.6-sol", AiEffort.Minimal);

        Assert.Null(saved.Effort);
    }

    [Fact]
    public async Task A_row_naming_a_provider_we_do_not_know_falls_back_to_the_default()
    {
        await using var harness = await InventoryHarness.CreateAsync();

        // Straight into the table, the way a hand-edit or a downgrade would
        // leave it. Enums are stored as text precisely so this is possible.
        await using (var db = await harness.Factory.CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO FeatureAiSettings (Id, Feature, Provider, Model, Effort, UpdatedAt) " +
                "VALUES (1, 'RecipeGeneration', 'Groq', 'llama-9', 'high', '2026-08-19 00:00:00')");
        }

        var choice = (await harness.NewAiSettingsService().GetAsync()).Features
            .First(f => f.Feature == AiFeature.RecipeGeneration);

        Assert.Equal(AiProvider.ClaudeCli, choice.Provider);
        Assert.Equal("sonnet", choice.Model);
    }

    /// <summary>
    /// Through <see cref="InventoryHarness.InParallelAsync{T}"/>, never
    /// <c>Task.WhenAll</c> over a <c>Select</c>: Microsoft.Data.Sqlite's async
    /// methods complete synchronously, so the obvious spelling runs each writer
    /// to completion before starting the next and races nothing.
    /// </summary>
    [Fact]
    public async Task Parallel_writers_on_one_feature_leave_one_row_and_one_winner()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        var models = new[] { "opus", "sonnet", "haiku", "fable" };

        await InventoryHarness.InParallelAsync(models.Length, async i =>
            await harness.NewAiSettingsService().SaveFeatureAsync(
                AiFeature.RecipeGeneration, AiProvider.ClaudeCli, models[i], AiEffort.Low));

        await using var db = await harness.Factory.CreateDbContextAsync();
        Assert.Equal(1, await db.FeatureAiSettings.CountAsync());

        var stored = (await harness.NewAiSettingsService().GetAsync()).Features
            .First(f => f.Feature == AiFeature.RecipeGeneration).Model;
        Assert.Contains(stored, models);
    }

    [Fact]
    public async Task Parallel_writers_on_one_providers_key_leave_one_row()
    {
        await using var harness = await InventoryHarness.CreateAsync();

        await InventoryHarness.InParallelAsync(4, async i =>
            await harness.NewAiSettingsService().SetApiKeyAsync(
                AiProvider.AnthropicApi, $"sk-ant-api03-parallel-writer-{i}"));

        await using var db = await harness.Factory.CreateDbContextAsync();
        Assert.Equal(1, await db.ProviderApiKeys.CountAsync());
    }

    private static FeatureChoice Choice(AiSettings settings, AiFeature feature) =>
        settings.Features.First(f => f.Feature == feature);
}
