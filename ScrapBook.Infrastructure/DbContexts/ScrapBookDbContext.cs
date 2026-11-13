using ScrapBook.Infrastructure.Configurations;
using ScrapBook.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;

namespace ScrapBook.Infrastructure.DbContexts;

public sealed class ScrapBookDbContext(DbContextOptions<ScrapBookDbContext> options) : DbContext(options)
{
    internal DbSet<EventPersistenceEntity> Events => Set<EventPersistenceEntity>();
    internal DbSet<EventMemberPersistenceEntity> Members => Set<EventMemberPersistenceEntity>();
    internal DbSet<EventAuditPersistenceEntity> Audit => Set<EventAuditPersistenceEntity>();
    internal DbSet<OpeningDispatchPersistenceEntity> Dispatches => Set<OpeningDispatchPersistenceEntity>();
    internal DbSet<AccountPersistenceEntity> Accounts => Set<AccountPersistenceEntity>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.ApplyConfiguration(new EventEntityConfiguration());
        model.ApplyConfiguration(new EventMemberEntityConfiguration());
        model.ApplyConfiguration(new EventAuditEntityConfiguration());
        model.ApplyConfiguration(new OpeningDispatchEntityConfiguration());
        model.ApplyConfiguration(new AccountEntityConfiguration());
    }
}
