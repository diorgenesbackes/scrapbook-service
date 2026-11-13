namespace ScrapBook.Domain.Entities;

public sealed class AccountPersistenceEntity
{
    public long Id { get; set; }
    public string? Subject { get; set; }
    public string State { get; set; } = "";
    public string? Email { get; set; }
    public bool EmailVerified { get; set; }
}
