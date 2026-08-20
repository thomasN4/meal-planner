using System.Net;
using MealPlanner.Components;
using MealPlanner.Data;
using MealPlanner.Mcp;
using MealPlanner.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// SQLite lives next to the app as a single local file.
var connectionString = builder.Configuration.GetConnectionString("MealPlanner")
    ?? "Data Source=mealplanner.db";
builder.Services.AddDbContextFactory<MealPlannerDbContext>(options =>
    options.UseSqlite(connectionString));

// Notifier is singleton so every Blazor circuit and every MCP tool scope
// share one bus; InventoryService stays scoped (factory-based DB access).
builder.Services.AddSingleton<InventoryChangeNotifier>();
builder.Services.AddScoped<InventoryService>();

// Auto-categorization. The categorizer is a singleton background service, so it
// cannot hold the scoped InventoryService; it builds one per batch instead.
// Both of that service's dependencies are singletons, so there is no scoped
// dependency being captured here.
builder.Services.Configure<CategorizationOptions>(
    builder.Configuration.GetSection(CategorizationOptions.SectionName));
builder.Services.AddSingleton<IIngredientClassifier, ClaudeIngredientClassifier>();
builder.Services.AddSingleton<Func<InventoryService>>(sp =>
    () => ActivatorUtilities.CreateInstance<InventoryService>(sp));
builder.Services.AddHostedService<IngredientCategorizer>();

// Recipe generation. The generator is a stateless singleton (options + logger,
// no DB access): the page hands it an inventory snapshot, so one list drives
// both the prompt and the have/missing verification. RecipeService is scoped
// like InventoryService (factory-based DB access).
builder.Services.Configure<RecipeGenerationOptions>(
    builder.Configuration.GetSection(RecipeGenerationOptions.SectionName));
builder.Services.AddSingleton<IRecipeGenerator, ClaudeRecipeGenerator>();
builder.Services.AddScoped<RecipeService>();

// Receipt scanning. Stateless singleton like the generator, and with no
// database access at all: it turns an uploaded file into proposed lines, and
// the inventory page writes the ones a household member confirms through the
// InventoryService it already holds.
builder.Services.Configure<ReceiptScanningOptions>(
    builder.Configuration.GetSection(ReceiptScanningOptions.SectionName));
builder.Services.AddSingleton<IReceiptScanner, ClaudeReceiptScanner>();

// Household-wide AI settings. Scoped with factory-based database access, like
// InventoryService and RecipeService; no notifier, for the reasons in the class
// doc.
//
// NOTHING CONSUMES THESE YET. The three services above still read their
// IOptions<T> from appsettings.json, and the settings page says so on screen.
// Wiring them up is a separate pass, and it is the pass that gets to delete the
// page's "not yet in effect" banner.
builder.Services.AddScoped<AiSettingsService>();

builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithTools<InventoryTools>();

var app = builder.Build();

// Apply any pending migrations on startup so the local DB is always ready.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider
        .GetRequiredService<IDbContextFactory<MealPlannerDbContext>>()
        .CreateDbContext();
    db.Database.Migrate();
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

// No HTTPS redirect or HSTS: the household is served plain HTTP over the LAN,
// where there is no certificate to redirect to. Both were no-ops that would
// turn harmful the moment ASPNETCORE_HTTPS_PORT got set, bouncing every
// household member to a port with nothing listening.

// MCP is only for the locally-spawned claude process — household members use
// the Blazor UI over the LAN, but tools must not be reachable from the LAN.
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/mcp"))
    {
        var remote = context.Connection.RemoteIpAddress;
        if (remote is null || !IPAddress.IsLoopback(remote))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("MCP endpoint is loopback-only.");
            return;
        }
    }

    await next();
});

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapMcp("/mcp");

app.Run();
