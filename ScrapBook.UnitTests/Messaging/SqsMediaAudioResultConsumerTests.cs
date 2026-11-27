using Amazon.SQS;

using Amazon.SQS.Model;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Infrastructure.Messaging;

namespace ScrapBook.UnitTests.Messaging;

public sealed class SqsMediaAudioResultConsumerTests
{
    private const string QueueUrl = "https://sqs.us-east-1.amazonaws.com/123456789012/audio-results";

    [Fact]
    public async Task ReceivesAndAcknowledgesOnlyThroughTheConfiguredResultQueueAsync()
    {
        using var cancellation = new CancellationTokenSource();
        using var client = new MediaAudioSqsClientStub
        {
            Receive = () => Task.FromResult(new ReceiveMessageResponse
            {
                Messages = [new() { Body = "callback", ReceiptHandle = "opaque-receipt" }]
            })
        };
        var consumer = new SqsMediaAudioResultConsumer(client, QueueUrl);

        var delivery = Assert.Single(await consumer.ReceiveAsync(4, cancellation.Token));
        Assert.Equal(("callback", "opaque-receipt"), (delivery.Body, delivery.ReceiptHandle));
        var (receive, token) = Assert.Single(client.Receives);
        Assert.Equal((QueueUrl, 4, 10), (receive.QueueUrl, receive.MaxNumberOfMessages, receive.WaitTimeSeconds));
        Assert.Equal(cancellation.Token, token);
        Assert.Empty(client.Acknowledgements);

        await consumer.AcknowledgeAsync(delivery.ReceiptHandle, cancellation.Token);

        Assert.Equal((QueueUrl, "opaque-receipt", cancellation.Token), Assert.Single(client.Acknowledgements));
    }

    [Fact]
    public async Task EmptyReceiveDoesNotAcknowledgeAnyReceiptAsync()
    {
        using var client = new MediaAudioSqsClientStub();
        var consumer = new SqsMediaAudioResultConsumer(client, QueueUrl);

        Assert.Empty(await consumer.ReceiveAsync(1, CancellationToken.None));
        Assert.Empty(client.Acknowledgements);
    }

    [Fact]
    public async Task TranslatesTransportFailuresAndPreservesCancellationAsync()
    {
        using var cancellation = new CancellationTokenSource();
        using var client = new MediaAudioSqsClientStub
        {
            Receive = () => throw new AmazonSQSException("Unavailable"),
            Delete = () => throw new HttpRequestException("Unavailable")
        };
        var consumer = new SqsMediaAudioResultConsumer(client, QueueUrl);

        await Assert.ThrowsAsync<MediaAudioResultQueueException>(() => consumer.ReceiveAsync(1, cancellation.Token));
        await Assert.ThrowsAsync<MediaAudioResultQueueException>(() => consumer.AcknowledgeAsync("receipt", cancellation.Token));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consumer.ReceiveAsync(1, cancellation.Token));
    }
}
