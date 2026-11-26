namespace ScrapBook.Domain.Models;

public sealed record EventStructuralLockDelivery(string Body, string ReceiptHandle, int ReceiveCount = 0);
