using GridPrice.Api.Data;
using GridPrice.Api.Endpoints;
using GridPrice.Api.Ingest;

using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var provider = builder.Configuration["Database:Provider"] ?? "Sqlite";
var connection = builder.Configuration.GetConnectionString("Grid") ?? "Data Source=gridprice.db";
builder.Services.AddDbContext<GridDbContext>(o =>
{
    if (provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
        o.UseSqlServer(connection);
    else
        o.UseSqlite(connection);
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<PriceIngest>();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks().AddDbContextCheck<GridDbContext>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.MapOpenApi();
app.MapHealthChecks("/health");
app.MapPriceEndpoints();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<GridDbContext>();
    // SQL Server is the production target and is versioned with migrations. SQLite is the
    // zero-setup local/test store, created straight from the model.
    if (db.Database.IsSqlServer()) await db.Database.MigrateAsync();
    else await db.Database.EnsureCreatedAsync();

    await SeedData.EnsureNodesAsync(db);
    if (app.Configuration.GetValue("Seed:Prices", false) && !await db.Prices.AnyAsync())
        await SeedData.SeedPricesAsync(scope.ServiceProvider.GetRequiredService<PriceIngest>());
}

app.Run();

public partial class Program;