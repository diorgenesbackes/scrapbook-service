using Microsoft.EntityFrameworkCore;
using ScrapBook.Infrastructure.Configurations;

namespace ScrapBook.Infrastructure.Persistence;

public sealed class ScrapBookDbContext(DbContextOptions<ScrapBookDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new EventConfiguration());
        modelBuilder.ApplyConfiguration(new EventMemberConfiguration());
    }
}
