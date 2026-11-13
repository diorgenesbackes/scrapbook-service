using ScrapBook.Domain.Enums;

namespace ScrapBook.Domain.Models;

public sealed record PublicSubmissionWindowModel(SubmissionWindowStateEnum State, DateTime OpensAt, DateTime ClosesAt, int RevalidateAfterMs);
