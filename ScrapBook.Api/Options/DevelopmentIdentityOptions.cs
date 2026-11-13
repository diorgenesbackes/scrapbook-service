namespace ScrapBook.Api.Options;

public sealed class DevelopmentIdentityOptions
{
    public bool Enabled { get; set; }
    public bool AllowContainerBinding { get; set; }
    public long AccountId { get; set; }
    public string[] AllowedOrigins { get; set; } = ["http://127.0.0.1:5173"];

    public void Validate(IHostEnvironment environment, IConfiguration configuration)
    {
        if (!Enabled) return;
        if (!environment.IsDevelopment())
            throw new InvalidOperationException("Identidade simulada exige ambiente Development.");
        if (AccountId <= 0 || string.IsNullOrWhiteSpace(configuration.GetConnectionString("ScrapBook")))
            throw new InvalidOperationException("Identidade local exige fixture e conexão DML explicitamente configuradas.");
        var addresses = configuration["urls"] ?? "http://localhost:5000";
        var containerBinding = AllowContainerBinding
            && configuration.GetValue<bool>("DOTNET_RUNNING_IN_CONTAINER");
        if (addresses.Split(';').Any(address => !Uri.TryCreate(address, UriKind.Absolute, out var uri)
            || uri.Scheme != "http" || (!uri.IsLoopback && !(containerBinding && uri.Host == "0.0.0.0"))))
            throw new InvalidOperationException("Identidade simulada exige HTTP loopback ou opt-in explícito no container local.");
        if (AllowedOrigins.Length == 0 || AllowedOrigins.Any(origin => !Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo) || origin.Contains('*')))
            throw new InvalidOperationException("AllowedOrigins deve conter origens explícitas e válidas.");
    }

    public bool AllowsOrigin(string origin) => AllowedOrigins.Any(allowed =>
        string.Equals(allowed.TrimEnd('/'), origin, StringComparison.OrdinalIgnoreCase));
}
