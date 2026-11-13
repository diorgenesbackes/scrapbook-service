using ScrapBook.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ScrapBook.Infrastructure.Configurations;

internal sealed class AccountEntityConfiguration : IEntityTypeConfiguration<AccountPersistenceEntity>
{
    public void Configure(EntityTypeBuilder<AccountPersistenceEntity> a)
    {
        a.ToTable("accounts", "identity");
        a.HasKey(x => x.Id).HasName("pk_accounts_id");
        a.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        a.Property(x => x.Subject).HasColumnName("subject").HasMaxLength(255);
        a.Property(x => x.State).HasColumnName("state").HasMaxLength(32).IsRequired();
        a.Property(x => x.Email).HasColumnName("email").HasMaxLength(320);
        a.Property(x => x.EmailVerified).HasColumnName("email_verified");
    }
}
