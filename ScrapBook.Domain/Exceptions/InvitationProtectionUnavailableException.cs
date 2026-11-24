namespace ScrapBook.Domain.Exceptions;

public sealed class InvitationProtectionUnavailableException(Exception? cause = null) : Exception("Invitation protection unavailable.", cause);
