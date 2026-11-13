using ScrapBook.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ScrapBook.Infrastructure.Configurations;

internal sealed class EventEntityConfiguration : IEntityTypeConfiguration<EventPersistenceEntity>
{
    public void Configure(EntityTypeBuilder<EventPersistenceEntity> e)
    {
        e.ToTable("events", "events");
        e.HasKey(x => x.Id).HasName("pk_events_id");
        e.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        e.Property(x => x.CreatorId).HasColumnName("events_creator_accounts_id");
        var coverId = e.Property(x => x.CoverId).HasColumnName("events_covers_id").ValueGeneratedOnAddOrUpdate();
        coverId.Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        coverId.Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);
        e.Property(x => x.Code).HasColumnName("code").HasMaxLength(43).IsRequired();
        e.Property(x => x.Name).HasColumnName("name").HasMaxLength(120).IsRequired();
        e.Property(x => x.WelcomeMessage).HasColumnName("welcome_message").HasMaxLength(2000);
        e.Property(x => x.StartsAt).HasColumnName("starts_at").HasColumnType("timestamp without time zone");
        e.Property(x => x.SubmissionOpensAt).HasColumnName("submission_opens_at").HasColumnType("timestamp without time zone");
        e.Property(x => x.SubmissionClosesAt).HasColumnName("submission_closes_at").HasColumnType("timestamp without time zone");
        e.Property(x => x.CompletionDeadlineAt).HasColumnName("completion_deadline_at").HasColumnType("timestamp without time zone");
        e.Property(x => x.OpeningMode).HasColumnName("opening_mode").HasMaxLength(16).HasDefaultValue("scheduled");
        e.Property(x => x.OpensAt).HasColumnName("opens_at").HasColumnType("timestamp without time zone");
        e.Property(x => x.AccessState).HasColumnName("access_state").HasMaxLength(24).IsRequired();
        e.Property(x => x.AccessFrozenAt).HasColumnName("access_frozen_at").HasColumnType("timestamp without time zone");
        var accessVersion = e.Property(x => x.AccessVersion).HasColumnName("access_version").HasDefaultValue(1L).ValueGeneratedOnAddOrUpdate();
        accessVersion.Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        accessVersion.Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);
        var lockedAt = e.Property(x => x.LockedAt).HasColumnName("locked_at").ValueGeneratedOnAddOrUpdate();
        lockedAt.Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        lockedAt.Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);
        e.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("CURRENT_TIMESTAMP");
        var updated = e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("CURRENT_TIMESTAMP").ValueGeneratedOnAddOrUpdate();
        updated.Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        updated.Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);
        var version = e.Property(x => x.Version).HasColumnName("version").HasDefaultValue(0L).ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
        version.Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        version.Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);
    }
}
