using ScrapBook.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ScrapBook.Infrastructure.Configurations;

internal sealed class OpeningDispatchEntityConfiguration : IEntityTypeConfiguration<OpeningDispatchPersistenceEntity>
{
    public void Configure(EntityTypeBuilder<OpeningDispatchPersistenceEntity> d)
    {
        d.ToTable("opening_dispatches", "events");
        d.HasKey(x => x.Id).HasName("pk_opening_dispatches_id");
        d.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        d.Property(x => x.EventId).HasColumnName("opening_dispatches_events_id");
        d.Property(x => x.MemberId).HasColumnName("opening_dispatches_event_members_id");
        d.Property(x => x.AccessVersion).HasColumnName("access_version").HasDefaultValue(1L);
        d.Property(x => x.DueAt).HasColumnName("due_at").HasColumnType("timestamp without time zone");
        d.Property(x => x.State).HasColumnName("state").HasMaxLength(24);
        d.Property(x => x.RequestedAt).HasColumnName("requested_at");
        d.Property(x => x.DispatchedAt).HasColumnName("dispatched_at");
        d.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("CURRENT_TIMESTAMP");
        d.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("CURRENT_TIMESTAMP").ValueGeneratedOnAddOrUpdate();
        d.Property(x => x.Version).HasColumnName("version").HasDefaultValue(0L).ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
        d.HasOne<EventPersistenceEntity>().WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_opening_dispatches_events_id");
        d.HasOne<EventMemberPersistenceEntity>().WithMany().HasForeignKey(x => new { x.EventId, x.MemberId })
            .HasPrincipalKey(x => new { x.EventId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_opening_dispatches_event_members_event_id");
    }
}
