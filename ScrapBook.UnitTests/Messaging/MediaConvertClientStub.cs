using Amazon;
using Amazon.MediaConvert;
using Amazon.MediaConvert.Model;

namespace ScrapBook.UnitTests.Messaging;

internal sealed class MediaConvertClientStub()
    : AmazonMediaConvertClient("test-access", "test-secret", RegionEndpoint.USEast1)
{
    public Func<CreateJobRequest, CancellationToken, Task<CreateJobResponse>> Create { get; set; } =
        (_, _) => Task.FromResult(new CreateJobResponse());
    public Func<GetJobRequest, CancellationToken, Task<GetJobResponse>> Get { get; set; } =
        (_, _) => Task.FromResult(new GetJobResponse());
    public Func<ListJobsRequest, CancellationToken, Task<ListJobsResponse>> List { get; set; } =
        (_, _) => Task.FromResult(new ListJobsResponse());
    public List<CreateJobRequest> Creates { get; } = [];
    public List<GetJobRequest> Gets { get; } = [];
    public List<(string? Queue, string? Order, int? Maximum, string? NextToken)> Queries { get; } = [];

    public override Task<CreateJobResponse> CreateJobAsync(CreateJobRequest request, CancellationToken cancellationToken)
    {
        Creates.Add(request);
        return Create(request, cancellationToken);
    }

    public override Task<GetJobResponse> GetJobAsync(GetJobRequest request, CancellationToken cancellationToken)
    {
        Gets.Add(request);
        return Get(request, cancellationToken);
    }

    public override Task<ListJobsResponse> ListJobsAsync(ListJobsRequest request, CancellationToken cancellationToken)
    {
        Queries.Add((request.Queue, request.Order?.Value, request.MaxResults, request.NextToken));
        return List(request, cancellationToken);
    }
}
