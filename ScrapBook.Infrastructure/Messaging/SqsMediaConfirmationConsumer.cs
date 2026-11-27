using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;

namespace ScrapBook.Infrastructure.Messaging;

public sealed class SqsMediaConfirmationConsumer(IAmazonSQS client, string queueUrl) : IMediaConfirmationQueue
{
    public async Task<IReadOnlyList<MediaConfirmationDelivery>> ReceiveAsync(int limit, CancellationToken cancellationToken)
    {
        var response = await ExecuteAsync(() => client.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = limit,
            WaitTimeSeconds = 10
        }, cancellationToken));
        return (response.Messages ?? []).Select(message => new MediaConfirmationDelivery(message.Body, message.ReceiptHandle)).ToArray();
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
            throw new MediaConfirmationQueueException(error);
        }
    }
}
