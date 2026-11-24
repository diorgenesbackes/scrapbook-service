using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ScrapBook.Domain.Entities;

namespace ScrapBook.Infrastructure.Configurations;

public sealed class InvitationConfiguration : IEntityTypeConfiguration<InvitationEntity>
{
    public void Configure(EntityTypeBuilder<InvitationEntity> builder)
    {
        builder.ToTable("invitations", "scrapbook");
        builder.HasKey(invitation => invitation.Id);
        builder.Property(invitation => invitation.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        builder.Property(invitation => invitation.EventId).HasColumnName("invitations_events_id");
        builder.Property(invitation => invitation.IssuerMemberId).HasColumnName("invitations_issuer_event_members_id");
        builder.Property(invitation => invitation.RequestKey).HasColumnName("request_key");
        builder.Property(invitation => invitation.HashKeyId).HasColumnName("hash_key_id").HasMaxLength(64);
        builder.Property(invitation => invitation.TokenHash).HasColumnName("token_hash");
        builder.Property(invitation => invitation.EncryptionKeyId).HasColumnName("encryption_key_id").HasMaxLength(64);
        builder.Property(invitation => invitation.TokenCiphertext).HasColumnName("token_ciphertext");
        builder.Property(invitation => invitation.State).HasColumnName("state").HasMaxLength(24);
        builder.Property(invitation => invitation.Version).HasColumnName("version");
        builder.Property(invitation => invitation.CreatedAt).HasColumnName("created_at");
        builder.Property(invitation => invitation.UpdatedAt).HasColumnName("updated_at");
        builder.Property(invitation => invitation.ExpiresAt).HasColumnName("expires_at");
        builder.Property(invitation => invitation.InvalidatedAt).HasColumnName("invalidated_at");
    }
}
