using Microsoft.EntityFrameworkCore;
using NexusErp.Application.Abstractions;

namespace NexusErp.Infrastructure.Persistence;

/// <summary>EF Core'un IDbContextFactory'sini Application arayüzüne bağlar.</summary>
public sealed class AppDbContextFactoryAdapter(IDbContextFactory<AppDbContext> inner)
    : IAppDbContextFactory
{
    public IAppDbContext Create() => inner.CreateDbContext();
}
