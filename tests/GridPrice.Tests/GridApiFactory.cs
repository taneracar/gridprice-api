using GridPrice.Api.Data;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace GridPrice.Tests;

/// <summary>
/// Hosts the real app in memory against a fresh database: a private in-memory SQLite database, or
/// a throwaway SQL Server database when GRIDPRICE_SQLSERVER holds a connection string (CI).
/// </summary>
public sealed class GridApiFactory : WebApplicationFactory<Program>
{
    public const string SqlServerVariable = "GRIDPRICE_SQLSERVER";

    private readonly string _provider;
    private readonly string _connection;
    private readonly SqliteConnection? _keepAlive;

    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 3, 7, 18, 0, 0, TimeSpan.Zero));

    public GridApiFactory(string provider)
    {
        _provider = provider;
        if (provider == "SqlServer")
        {
            var baseConnection = Environment.GetEnvironmentVariable(SqlServerVariable)
                ?? throw new InvalidOperationException($"{SqlServerVariable} is not set");
            _connection = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(baseConnection)
            {
                InitialCatalog = $"gridprice_test_{Guid.NewGuid():N}",
            }.ConnectionString;
        }
        else
        {
            // Shared-cache in-memory database lives as long as one connection to it stays open.
            _connection = $"Data Source=file:gp_{Guid.NewGuid():N}?mode=memory&cache=shared";
            _keepAlive = new SqliteConnection(_connection);
            _keepAlive.Open();
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Database:Provider", _provider);
        builder.UseSetting("ConnectionStrings:Grid", _connection);
        builder.UseSetting("Seed:Prices", "false");
        builder.ConfigureServices(s => s.AddSingleton<TimeProvider>(Clock));
    }

    public GridDbContext NewDbContext() => Services.CreateScope().ServiceProvider.GetRequiredService<GridDbContext>();

    public override async ValueTask DisposeAsync()
    {
        if (_provider == "SqlServer")
        {
            await using var db = NewDbContext();
            await db.Database.EnsureDeletedAsync();
        }
        await base.DisposeAsync();
        _keepAlive?.Dispose();
    }
}