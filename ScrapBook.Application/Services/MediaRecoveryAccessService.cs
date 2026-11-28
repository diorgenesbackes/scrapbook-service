using ScrapBook.Domain.Commands;
using ScrapBook.Domain.Interfaces;

namespace ScrapBook.Application.Services;

public sealed class MediaRecoveryAccessService(IMediaRecoveryAccessRepository repository)
{
    public Task<string?> FindOperatorAsync(CancellationToken cancellationToken) =>
        repository.FindOperatorAsync(cancellationToken);

    public static bool IsValid(MediaRecoveryBatchCommand command) =>
        command.EventId > 0 && command.AfterId >= 0 && command.Limit is >= 1 and <= 100
        && IsValidAudit(command.Incident, command.Revision);

    public static bool IsValidAudit(string incident, string revision) =>
        incident.Length is >= 3 and <= 80 && char.IsAsciiLetterOrDigit(incident[0])
        && incident.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')
        && revision.Length is (40 or 64) && revision.All(character => char.IsAsciiHexDigitLower(character))
        && revision.Any(character => character != '0');
}
