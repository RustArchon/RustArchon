// Copyright ©2026 Scott Blomfield

using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Data;
using Testcontainers.PostgreSql;

namespace RustArchon.Api.Tests;

/// <summary>
/// A throwaway Postgres container, shared across one test class via <c>IClassFixture</c> - for tests
/// that need a real relational provider (see <see cref="RustServerRepositoryTests"/>'s remarks on why
/// neither EF Core's InMemory provider nor Sqlite are trustworthy stand-ins for
/// <c>ExecuteUpdateAsync</c>). One container per test class, not per test method: the schema is created
/// once in <see cref="InitializeAsync"/>, and individual tests stay isolated from each other by using
/// fresh <c>Guid.NewGuid()</c> ids rather than needing a clean database between them.
/// </summary>
public class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:17")
        .Build();

    public DbContextOptions<ApiDbContext> Options { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        Options = new DbContextOptionsBuilder<ApiDbContext>()
            .UseNpgsql(_container.GetConnectionString())
            .Options;

        await using var context = new ApiDbContext(Options);
        await context.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();
}
