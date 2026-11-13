namespace ScrapBook.Api.Options;

public sealed class PublicSiteOptions
{
    public string? Origin { get; set; }
    public bool AllowHttpLoopback { get; set; }
}
