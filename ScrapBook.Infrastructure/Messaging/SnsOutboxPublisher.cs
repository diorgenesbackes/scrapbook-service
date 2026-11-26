using System.Net;
using Amazon.Runtime;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Interfaces;

namespace ScrapBook.Infrastructure.Messaging;

public sealed class SnsOutboxPublisher(IAmazonSimpleNotificationService client, string topicArn) : IOutboxPublisher
{
    public async Task PublishAsync(string type, string body, CancellationToken cancellationToken)
    {
        PublishResponse response;
        try
        {
            response = await client.PublishAsync(new PublishRequest
            {
                TopicArn = topicArn,
                Message = body,
                MessageAttributes = new Dictionary<string, MessageAttributeValue>
                {
                    ["type"] = new() { DataType = "String", StringValue = type }
                }
            }, cancellationToken);
        }
        catch (AmazonServiceException error)
        {
            var retryable = error.Retryable is not null || (int)error.StatusCode >= 500
                || error.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or 0;
            throw new OutboxPublishException(retryable, error);
        }
        catch (Exception error) when (error is AmazonClientException or HttpRequestException)
        {
            throw new OutboxPublishException(true, error);
        }

        if (response.HttpStatusCode != HttpStatusCode.OK || string.IsNullOrEmpty(response.MessageId))
        {
            throw new OutboxPublishException(true);
        }
    }
}
