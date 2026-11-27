using ScrapBook.Application.Codecs;
using ScrapBook.Application.Services;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Models;

namespace ScrapBook.UnitTests.Services;

public sealed class MediaAdmissionServiceTests
{
    private readonly MediaAdmissionRepositoryStub repository = new();
    private readonly OutboxTestRepository outbox = new() { Origin = MediaPreparationTestFixture.Outbox() };
    private readonly EventTestTimeProvider clock = new() { Now = MediaPreparationTestFixture.Confirmation.ConfirmedAt.AddHours(2) };
    private static MediaConfirmationModel Confirmation => MediaPreparationTestFixture.Confirmation;
    private static string Envelope => MediaConfirmationCodec.WriteEnvelope(Confirmation);
    private MediaAdmissionService Service => new(repository, outbox, clock);

    [Fact]
    public async Task AdmitsAnEarlierConfirmationAndCommitsTheJobAndDeliveryTogetherAsync()
    {
        using var cancellation = new CancellationTokenSource();

        var result = await Service.AdmitAsync(Envelope, cancellation.Token);

        Assert.Equal(MediaAdmissionResult.Admitted, result);
        var job = Assert.Single(repository.Jobs);
        Assert.Equal((Confirmation, 1, "photo_prepare", clock.Now, cancellation.Token),
            (job.Message, job.ProfileVersion, job.Kind, job.Now, job.Token));
        Assert.Equal(["image", "thumbnail"], job.VariantKinds);
        var delivery = Assert.Single(repository.Deliveries);
        Assert.Equal((Confirmation, clock.Now, cancellation.Token), (delivery.Message, delivery.Now, delivery.Token));
        Assert.Equal(32, delivery.Hash.Length);
        Assert.Contains((Confirmation, true, cancellation.Token), repository.SourceReads);
        var transaction = Assert.Single(repository.Transactions);
        Assert.True(transaction.Committed);
        Assert.Equal(cancellation.Token, transaction.CommitToken);
    }

    [Theory]
    [InlineData("audio")]
    [InlineData("video")]
    public async Task PersistsAudioAndVideoAsPendingTranscodeWorkAsync(string kind)
    {
        repository.Source = repository.Source! with { Kind = kind };

        Assert.Equal(MediaAdmissionResult.Admitted, await Service.AdmitAsync(Envelope, CancellationToken.None));

        var job = Assert.Single(repository.Jobs);
        Assert.Equal("av_transcode", job.Kind);
        Assert.Equal("playback", Assert.Single(job.VariantKinds));
        Assert.Single(repository.Deliveries);
    }

    [Theory]
    [InlineData(0, MediaAdmissionResult.Admitted)]
    [InlineData(30 * 24 * 60 * 60, MediaAdmissionResult.Admitted)]
    [InlineData(30 * 24 * 60 * 60 + 1, MediaAdmissionResult.Quarantined)]
    public async Task AppliesTheOrdinaryReplayWindowAtItsBoundariesAsync(int seconds, MediaAdmissionResult expected)
    {
        clock.Now = Confirmation.ConfirmedAt.AddSeconds(seconds);

        Assert.Equal(expected, await Service.AdmitAsync(Envelope, CancellationToken.None));

        Assert.Equal(expected == MediaAdmissionResult.Admitted, repository.Jobs.Count == 1);
    }

    [Fact]
    public async Task DoesNotCommitADeliveryWhenTheJobInsertIsRejectedAsync()
    {
        repository.JobInserted = false;

        Assert.Equal(MediaAdmissionResult.Quarantined, await Service.AdmitAsync(Envelope, CancellationToken.None));

        Assert.Empty(repository.Deliveries);
        var transaction = Assert.Single(repository.Transactions);
        Assert.False(transaction.Committed);
        Assert.True(transaction.Disposed);
    }

    [Fact]
    public async Task DuplicateDeliveryUsesThePersistedHashAfterRestartAsync()
    {
        Assert.Equal(MediaAdmissionResult.Admitted, await Service.AdmitAsync(Envelope, CancellationToken.None));
        var delivery = Assert.Single(repository.Deliveries);
        repository.Delivery = new ProcessedMessageEntity(1, delivery.Hash, "completed");

        Assert.Equal(MediaAdmissionResult.Duplicate, await Service.AdmitAsync(Envelope, CancellationToken.None));

        Assert.Single(repository.Jobs);
        Assert.Single(repository.Deliveries);
    }

    [Fact]
    public async Task RejectsTheSameIdentityWithADifferentPersistedPayloadHashAsync()
    {
        repository.Delivery = new ProcessedMessageEntity(1, new byte[32], "completed");

        Assert.Equal(MediaAdmissionResult.Quarantined, await Service.AdmitAsync(Envelope, CancellationToken.None));

        Assert.Empty(repository.Jobs);
        Assert.Empty(repository.Deliveries);
    }

    [Theory]
    [InlineData("missing fact")]
    [InlineData("other event")]
    [InlineData("other file")]
    [InlineData("other correlation")]
    [InlineData("other instant")]
    public async Task RejectsDeliveriesThatDoNotMatchTheOriginalOutboxFactAsync(string mismatch)
    {
        var original = outbox.Origin!;
        outbox.Origin = mismatch switch
        {
            "missing fact" => null,
            "other event" => original with { EventId = original.EventId + 1 },
            "other file" => original with { Payload = original.Payload.Replace("701", "702", StringComparison.Ordinal) },
            "other correlation" => original with { CorrelationId = Guid.NewGuid() },
            "other instant" => original with { OccurredAt = original.OccurredAt.AddSeconds(1) },
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch))
        };

        Assert.Equal(MediaAdmissionResult.Quarantined, await Service.AdmitAsync(Envelope, CancellationToken.None));

        Assert.Empty(repository.Jobs);
        Assert.Empty(repository.Deliveries);
    }

    [Theory]
    [InlineData("missing original")]
    [InlineData("unconfirmed record")]
    [InlineData("invalid file")]
    [InlineData("unpinned version")]
    public async Task RejectsFilesIneligibleForPreparationAsync(string invalid)
    {
        var source = repository.Source!;
        repository.Source = invalid switch
        {
            "missing original" => null,
            "unconfirmed record" => source with { RecordState = "pending", RecordConfirmedAt = null },
            "invalid file" => source with { ValidationState = "invalid" },
            "unpinned version" => source with { VersionId = "" },
            _ => throw new ArgumentOutOfRangeException(nameof(invalid))
        };

        Assert.Equal(MediaAdmissionResult.Quarantined, await Service.AdmitAsync(Envelope, CancellationToken.None));

        Assert.Empty(repository.Jobs);
        Assert.Empty(repository.Deliveries);
    }
}
