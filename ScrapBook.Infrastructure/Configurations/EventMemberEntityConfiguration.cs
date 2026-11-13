using ScrapBook.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ScrapBook.Infrastructure.Configurations;

internal sealed class EventMemberEntityConfiguration : IEntityTypeConfiguration<EventMemberPersistenceEntity>
{
    public void Configure(EntityTypeBuilder<EventMemberPersistenceEntity> m)
    {
        m.ToTable("event_members", "events");
        m.HasKey(x => x.Id).HasName("pk_event_members_id");
        m.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        m.Property(x => x.EventId).HasColumnName("event_members_events_id");
        m.Property(x => x.AccountId).HasColumnName("event_members_accounts_id");
        m.Property(x => x.Slot).HasColumnName("slot");
        m.Property(x => x.JoinedAt).HasColumnName("joined_at");
        m.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("CURRENT_TIMESTAMP");
        m.HasOne<EventPersistenceEntity>().WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_event_members_events_id");
        m.HasAlternateKey(x => new { x.EventId, x.Id }).HasName("uq_event_members_event_scope");
    }
}
