using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ScrapBook.Domain.Entities;

namespace ScrapBook.Infrastructure.Configurations;

public sealed class EventConfiguration : IEntityTypeConfiguration<EventEntity>
{
    public void Configure(EntityTypeBuilder<EventEntity> builder)
    {
        builder.ToTable("events", "scrapbook");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        builder.Property(item => item.CreatorAccountId).HasColumnName("events_creator_accounts_id");
        builder.Property(item => item.Version).HasColumnName("version");
        builder.Property(item => item.Code).HasColumnName("code");
        builder.Property(item => item.Name).HasColumnName("name");
        builder.Property(item => item.WelcomeMessage).HasColumnName("welcome_message");
        builder.Property(item => item.StartsAt).HasColumnName("starts_at").HasColumnType("timestamp without time zone");
        builder.Property(item => item.SubmissionOpensAt).HasColumnName("submission_opens_at").HasColumnType("timestamp without time zone");
        builder.Property(item => item.SubmissionClosesAt).HasColumnName("submission_closes_at").HasColumnType("timestamp without time zone");
        builder.Property(item => item.CompletionDeadlineAt).HasColumnName("completion_deadline_at").HasColumnType("timestamp without time zone");
        builder.Property(item => item.OpeningMode).HasColumnName("opening_mode");
        builder.Property(item => item.OpensAt).HasColumnName("opens_at").HasColumnType("timestamp without time zone");
        builder.Property(item => item.AccessState).HasColumnName("access_state");
        builder.Property(item => item.AccessFrozenAt).HasColumnName("access_frozen_at").HasColumnType("timestamp without time zone");
        builder.Property(item => item.AccessVersion).HasColumnName("access_version");
        builder.Property(item => item.LockedAt).HasColumnName("locked_at");
        builder.Property(item => item.FirstRecordId).HasColumnName("first_record_id");
        builder.Property(item => item.FirstRecordConfirmedAt).HasColumnName("first_record_confirmed_at").HasColumnType("timestamp without time zone");
        builder.Property(item => item.CreationKeyHash).HasColumnName("creation_key_hash");
        builder.Property(item => item.CreationPayloadHash).HasColumnName("creation_payload_hash");
    }
}
