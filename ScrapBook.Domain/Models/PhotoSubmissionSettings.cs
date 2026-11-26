namespace ScrapBook.Domain.Models;

public sealed record PhotoSubmissionSettings(bool Enabled, string Bucket, bool VideoEnabled = false,
    bool AudioEnabled = false);
