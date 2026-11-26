using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;

namespace ScrapBook.Infrastructure.Messaging;

public sealed class SqsEventStructuralLockConsumer(IAmazonSQS client, string queueUrl) : IEventStructuralLockQueue
{
    public async Task<IReadOnlyList<EventStructuralLockDelivery>> ReceiveAsync(int limit, CancellationToken cancellationToken)
    {
        var response = await ExecuteAsync(() => client.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = limit,
            WaitTimeSeconds = 10,
            VisibilityTimeout = 30
        }, cancellationToken));
        return (response.Messages ?? []).Select(message => new EventStructuralLockDelivery(message.Body, message.ReceiptHandle)).ToArray();
    }

    public Task AcknowledgeAsync(string receiptHandle, CancellationToken cancellationToken) =>
        ExecuteAsync(() => client.DeleteMessageAsync(queueUrl, receiptHandle, cancellationToken));

    private static async Task<T> ExecuteAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation();
        }
        catch (Exception error) when (error is AmazonServiceException or AmazonClientException or HttpRequestException)
        {
            throw new EventStructuralLockQueueException(error);
        }
    }
}
