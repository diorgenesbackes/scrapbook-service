using Microsoft.EntityFrameworkCore;

namespace ScrapBook.Infrastructure.Entities;

internal sealed class AccountPersistenceEntity
{
    public long Id { get; set; }
    public string? Subject { get; set; }
    public string State { get; set; } = "";
    public string? Email { get; set; }
    public bool EmailVerified { get; set; }
}
