using System.Net;
using MealPlanner.Components;
using MealPlanner.Data;
using MealPlanner.Mcp;
using MealPlanner.Models;
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

// Household-wide AI settings: which provider, model and effort each of the
// three services above uses, read from here at the start of every call. A
// feature with nothing saved runs its appsettings.json Model/Effort on the
// claude CLI, as it always did. A singleton because the three features are, and
// it holds no state of its own (factory-based DB access per call); no notifier,
// for the reasons in the class doc.
builder.Services.AddSingleton<AiSettingsService>();

// The HTTP providers a feature can be pointed at instead of the CLI. Named
// clients with no HttpClient timeout: each call carries its feature's own
// TimeoutSeconds, and a second, shorter clock underneath it would fire first
// and report a timeout nobody configured.
builder.Services.AddHttpClient(AnthropicApiClient.HttpClientName, c => c.Timeout = Timeout.InfiniteTimeSpan);
builder.Services.AddHttpClient(OpenAiCompatibleClient.OpenAiHttpClient, c => c.Timeout = Timeout.InfiniteTimeSpan);
builder.Services.AddHttpClient(OpenAiCompatibleClient.OpenRouterHttpClient, c => c.Timeout = Timeout.InfiniteTimeSpan);
builder.Services.AddSingleton<IApiModelClient, AnthropicApiClient>();
builder.Services.AddSingleton<IApiModelClient>(sp =>
    new OpenAiCompatibleClient(AiProvider.OpenAi, sp.GetRequiredService<IHttpClientFactory>()));
builder.Services.AddSingleton<IApiModelClient>(sp =>
    new OpenAiCompatibleClient(AiProvider.OpenRouter, sp.GetRequiredService<IHttpClientFactory>()));

// Reads OpenRouter's public model list so /settings can say whether a typed id is
// real. It borrows the openrouter client above and sends no key — the check has to
// work before one is stored.
builder.Services.AddSingleton<IOpenRouterCatalog, OpenRouterCatalog>();

// Asks each provider whether a key is any good, on its cheapest auth-only endpoint.
// Borrows the three named clients above for the same reason the catalogue does, and
// reads a stored key inside itself so /settings never holds one.
builder.Services.AddSingleton<IApiKeyChecker, ApiKeyChecker>();

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
