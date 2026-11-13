using Microsoft.AspNetCore.DataProtection;

namespace ScrapBook.Api.Modules.Events;

public static class EventsDataProtection
{
    public static IServiceCollection AddEventsDataProtection(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var protection = services.AddDataProtection().SetApplicationName("ScrapBook.Events.v1");
        var path = configuration["Events:CursorKeyPath"];
        if (string.IsNullOrWhiteSpace(path) && environment.IsDevelopment())
        {
            path = Path.Combine(environment.ContentRootPath, ".local", "cursor-keys");
        }

        if (!string.IsNullOrWhiteSpace(path))
        {
            var directory = new DirectoryInfo(Path.GetFullPath(path));
            if (directory.LinkTarget is not null)
            {
                throw new InvalidOperationException("O diretório de chaves não pode ser um link simbólico.");
            }

            directory.Create();
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(directory.FullName,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            protection.PersistKeysToFileSystem(directory);
        }

        return services;
    }
}
