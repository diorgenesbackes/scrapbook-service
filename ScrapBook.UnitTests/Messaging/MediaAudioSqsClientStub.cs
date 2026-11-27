using Amazon;
using Amazon.SQS;
using Amazon.SQS.Model;

namespace ScrapBook.UnitTests.Messaging;

internal sealed class MediaAudioSqsClientStub() : AmazonSQSClient("test-access", "test-secret", RegionEndpoint.USEast1)
{
    public Func<Task<ReceiveMessageResponse>> Receive { get; set; } = () => Task.FromResult(new ReceiveMessageResponse());
    public Func<Task<DeleteMessageResponse>> Delete { get; set; } = () => Task.FromResult(new DeleteMessageResponse());
    public List<(ReceiveMessageRequest Request, CancellationToken Token)> Receives { get; } = [];
    public List<(string QueueUrl, string ReceiptHandle, CancellationToken Token)> Acknowledgements { get; } = [];

    public override Task<ReceiveMessageResponse> ReceiveMessageAsync(ReceiveMessageRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Receives.Add((request, cancellationToken));
        return Receive();
    }

    public override Task<DeleteMessageResponse> DeleteMessageAsync(string queueUrl, string receiptHandle, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Acknowledgements.Add((queueUrl, receiptHandle, cancellationToken));
        return Delete();
    }
}
