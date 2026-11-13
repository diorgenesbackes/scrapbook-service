using Microsoft.EntityFrameworkCore;

namespace ScrapBook.Infrastructure.Modules.Identity;

public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options) : DbContext(options)
{
    internal DbSet<IdentityAccountRow> Accounts => Set<IdentityAccountRow>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        var a = model.Entity<IdentityAccountRow>();
        a.ToTable("accounts", "identity");
        a.HasKey(x => x.Id).HasName("pk_accounts_id");
        a.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        a.Property(x => x.Subject).HasColumnName("subject").HasMaxLength(255);
        a.Property(x => x.State).HasColumnName("state").HasMaxLength(32).IsRequired();
        a.Property(x => x.Email).HasColumnName("email").HasMaxLength(320);
        a.Property(x => x.EmailVerified).HasColumnName("email_verified");
    }
}

internal sealed class IdentityAccountRow
{
    public long Id { get; set; }
    public string? Subject { get; set; }
    public string State { get; set; } = "";
    public string? Email { get; set; }
    public bool EmailVerified { get; set; }
}
