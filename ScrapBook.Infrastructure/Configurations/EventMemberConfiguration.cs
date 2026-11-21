using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ScrapBook.Domain.Entities;

namespace ScrapBook.Infrastructure.Configurations;

public sealed class EventMemberConfiguration : IEntityTypeConfiguration<EventMemberEntity>
{
    public void Configure(EntityTypeBuilder<EventMemberEntity> builder)
    {
        builder.ToTable("event_members", "scrapbook");
        builder.HasKey(member => member.Id);
        builder.Property(member => member.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        builder.Property(member => member.AccountId).HasColumnName("event_members_accounts_id");
        builder.Property(member => member.EventId).HasColumnName("event_members_events_id");
        builder.Property(member => member.Slot).HasColumnName("slot");
        builder.Property(member => member.JoinedAt).HasColumnName("joined_at");
        builder.HasOne(member => member.Event).WithMany().HasForeignKey(member => member.EventId);
    }
}
