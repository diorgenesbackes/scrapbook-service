using System.Text.Json;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using ScrapBook.Api.Options;
using ScrapBook.Application.Services;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Infrastructure.Queues;

namespace ScrapBook.Api.Workers;

public sealed class PhotoReceiptWorker(IAmazonSQS queue, PhotoSubmissionsOptions options, IServiceScopeFactory scopes,
    ILogger<PhotoReceiptWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var response = await queue.ReceiveMessageAsync(new ReceiveMessageRequest
                {
                    QueueUrl = options.QueueUrl,
                    MaxNumberOfMessages = 10,
                    WaitTimeSeconds = 10,
                    VisibilityTimeout = 60
                }, stoppingToken);
                foreach (var message in response.Messages ?? [])
                {
                    await ReceiveAsync(message, stoppingToken);
                }
            }
            catch (Exception error) when (error is AmazonServiceException or AmazonClientException or HttpRequestException)
            {
                logger.LogWarning("Photo receipt queue unavailable.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task ReceiveAsync(Message message, CancellationToken cancellationToken)
    {
        try
        {
            var receipts = S3PhotoReceiptReader.Read(message.Body);
            foreach (var receipt in receipts)
            {
                await using var scope = scopes.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<PhotoSubmissionService>();
                await service.RecordReceiptAsync(receipt, cancellationToken);
            }
            await queue.DeleteMessageAsync(options.QueueUrl, message.ReceiptHandle, cancellationToken);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException
            or FormatException or PostgresUnavailableException)
        {
            logger.LogWarning("Photo receipt could not be recorded.");
        }
    }
}
