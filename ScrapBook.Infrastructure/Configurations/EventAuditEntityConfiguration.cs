using ScrapBook.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ScrapBook.Infrastructure.Configurations;

internal sealed class EventAuditEntityConfiguration : IEntityTypeConfiguration<EventAuditPersistenceEntity>
{
    public void Configure(EntityTypeBuilder<EventAuditPersistenceEntity> a)
    {
        a.ToTable("audit_entries", "events");
        a.HasKey(x => x.Id).HasName("pk_audit_entries_id");
        a.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        a.Property(x => x.AccountId).HasColumnName("audit_entries_accounts_id");
        a.Property(x => x.EventId).HasColumnName("audit_entries_events_id");
        a.Property(x => x.ActorType).HasColumnName("actor_type").HasMaxLength(16);
        a.Property(x => x.Action).HasColumnName("action").HasMaxLength(80);
        a.Property(x => x.SubjectType).HasColumnName("subject_type").HasMaxLength(64);
        a.Property(x => x.SubjectId).HasColumnName("subject_id");
        a.Property(x => x.Outcome).HasColumnName("outcome").HasMaxLength(16);
        a.Property(x => x.ReasonCode).HasColumnName("reason_code").HasMaxLength(80);
        a.Property(x => x.CorrelationId).HasColumnName("correlation_id");
        a.Property(x => x.BeforeState).HasColumnName("before_state").HasColumnType("jsonb");
        a.Property(x => x.AfterState).HasColumnName("after_state").HasColumnType("jsonb");
        a.Property(x => x.ExpiresAt).HasColumnName("expires_at").HasDefaultValueSql("CURRENT_TIMESTAMP + INTERVAL '4320 hours'");
        a.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("CURRENT_TIMESTAMP");
        a.HasOne<EventPersistenceEntity>().WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_audit_entries_events_id");
    }
}
