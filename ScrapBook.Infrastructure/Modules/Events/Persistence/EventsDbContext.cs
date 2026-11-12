using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace ScrapBook.Infrastructure.Modules.Events.Persistence;

public sealed class EventsDbContext(DbContextOptions<EventsDbContext> options) : DbContext(options)
{
    internal DbSet<EventRow> Events => Set<EventRow>();
    internal DbSet<EventMemberRow> Members => Set<EventMemberRow>();
    internal DbSet<EventAuditRow> Audit => Set<EventAuditRow>();
    internal DbSet<OpeningDispatchRow> Dispatches => Set<OpeningDispatchRow>();

    internal IQueryable<EventRow> ForActor(long accountId) => Events.Where(e =>
        Members.Any(m => m.EventId == e.Id && m.AccountId == accountId));

    protected override void OnModelCreating(ModelBuilder model)
    {
        var e = model.Entity<EventRow>();
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

        var m = model.Entity<EventMemberRow>();
        m.ToTable("event_members", "events");
        m.HasKey(x => x.Id).HasName("pk_event_members_id");
        m.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        m.Property(x => x.EventId).HasColumnName("event_members_events_id");
        m.Property(x => x.AccountId).HasColumnName("event_members_accounts_id");
        m.Property(x => x.Slot).HasColumnName("slot");
        m.Property(x => x.JoinedAt).HasColumnName("joined_at");
        m.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("CURRENT_TIMESTAMP");
        m.HasOne<EventRow>().WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_event_members_events_id");
        m.HasAlternateKey(x => new { x.EventId, x.Id }).HasName("uq_event_members_event_scope");

        var a = model.Entity<EventAuditRow>();
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
        a.HasOne<EventRow>().WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_audit_entries_events_id");

        var d = model.Entity<OpeningDispatchRow>();
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
        d.HasOne<EventRow>().WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_opening_dispatches_events_id");
        d.HasOne<EventMemberRow>().WithMany().HasForeignKey(x => new { x.EventId, x.MemberId })
            .HasPrincipalKey(x => new { x.EventId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_opening_dispatches_event_members_event_id");
    }
}
