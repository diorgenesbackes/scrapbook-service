namespace ScrapBook.Domain.Entities;

public sealed record InvitationSecret(string HashKeyId, byte[] Hash, string EncryptionKeyId, byte[] Ciphertext);
