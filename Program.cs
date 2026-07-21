using MealPlanner.Components;
using MealPlanner.Data;
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

builder.Services.AddScoped<InventoryService>();

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
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
