using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GridPrice.Api.Data;

/// <summary>
/// Used only by `dotnet ef migrations add`. Migrations are generated for SQL Server, the production
/// database; no server is contacted to generate them.
/// </summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<GridDbContext>
{
    public GridDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<GridDbContext>()
            .UseSqlServer("Server=localhost;Database=gridprice;Trusted_Connection=False;TrustServerCertificate=True")
            .Options);
}