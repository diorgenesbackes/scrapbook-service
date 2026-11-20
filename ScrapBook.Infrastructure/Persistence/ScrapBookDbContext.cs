using Microsoft.EntityFrameworkCore;

namespace ScrapBook.Infrastructure.Persistence;

public sealed class ScrapBookDbContext(DbContextOptions<ScrapBookDbContext> options) : DbContext(options)
{
}
