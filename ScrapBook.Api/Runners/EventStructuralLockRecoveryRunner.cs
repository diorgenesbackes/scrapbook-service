using System.Text;
using Microsoft.EntityFrameworkCore;
using ScrapBook.Api.Options;
using ScrapBook.Application.Services;
using ScrapBook.Domain.Commands;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;
using ScrapBook.Infrastructure.Persistence;
using ScrapBook.Infrastructure.Repositories;

namespace ScrapBook.Api.Runners;

public static class EventStructuralLockRecoveryRunner
{
    public const string Argument = "event-structural-lock-recover";
    private const int MaximumEvidenceChars = 16 * 1024;

    public static async Task<int> RunAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var command = ReadCommand(arguments);
        if (command is null || command.Mode == EventStructuralLockRecoveryMode.ReconcileDlq && !Console.IsInputRedirected)
        {
            return WriteResult(EventStructuralLockRecoveryResult.InvalidArguments);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            if (command.Mode == EventStructuralLockRecoveryMode.ReconcileDlq)
            {
                command = command with { Evidence = await ReadEvidenceAsync(deadline.Token) };
            }
            if (!EventStructuralLockRecoveryService.IsValid(command))
            {
                return WriteResult(EventStructuralLockRecoveryResult.InvalidArguments);
            }

            var builder = Host.CreateApplicationBuilder();
            var database = DatabaseOptions.FromConfiguration(builder.Configuration);
            builder.Logging.ClearProviders();
            builder.Services.AddDbContext<ScrapBookDbContext>(options => options.UseNpgsql(database.ConnectionString));
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddScoped<IEventWriteRepository, EventWriteRepository>();
            builder.Services.AddScoped<IOutboxRepository, OutboxRepository>();
            builder.Services.AddScoped<IEventStructuralLockRepository, EventStructuralLockRepository>();
            builder.Services.AddScoped<EventStructuralLockRecoveryService>();
            using var host = builder.Build();
            await using var scope = host.Services.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<EventStructuralLockRecoveryService>();
            return WriteResult(await service.RecoverAsync(command, deadline.Token));
        }
        catch (Exception error) when (error is StartupConfigurationException or PostgresUnavailableException
            or PostgresWriteRejectedException or IOException)
        {
            Console.Error.WriteLine("RECOVERY_UNAVAILABLE");
            return 4;
        }
        catch (DecoderFallbackException)
        {
            return WriteResult(EventStructuralLockRecoveryResult.InvalidArguments);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            Console.Error.WriteLine("RECOVERY_UNAVAILABLE");
            return 4;
        }
    }

    private static EventStructuralLockRecoveryCommand? ReadCommand(string[] arguments)
    {
        if (arguments is not [var operation, var id, var incident, var sha, .. var options]
            || !Guid.TryParseExact(id, "D", out var messageId) || messageId == Guid.Empty)
        {
            return null;
        }

        var mode = operation switch
        {
            "publish" => EventStructuralLockRecoveryMode.Publish,
            "consume" => EventStructuralLockRecoveryMode.Consume,
            "reconcile-dlq" => EventStructuralLockRecoveryMode.ReconcileDlq,
            _ => (EventStructuralLockRecoveryMode?)null
        };

        if (mode is null || options.Any(option => option is not ("--rearm" or "--reconcile-expired"))
            || options.Distinct(StringComparer.Ordinal).Count() != options.Length)
        {
            return null;
        }

        return new EventStructuralLockRecoveryCommand(mode.Value, messageId, incident, sha,
            options.Contains("--rearm", StringComparer.Ordinal), options.Contains("--reconcile-expired", StringComparer.Ordinal));
    }

    private static async Task<string?> ReadEvidenceAsync(CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false, true), false);
        var buffer = new char[MaximumEvidenceChars + 1];
        var length = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken);
        return length <= MaximumEvidenceChars ? new string(buffer, 0, length) : null;
    }

    private static int WriteResult(EventStructuralLockRecoveryResult result)
    {
        var (message, code) = result switch
        {
            EventStructuralLockRecoveryResult.Reopened => ("RECOVERY_REOPENED", 0),
            EventStructuralLockRecoveryResult.Reconciled => ("RECOVERY_RECONCILED", 0),
            EventStructuralLockRecoveryResult.AlreadyCompleted => ("RECOVERY_ALREADY_COMPLETED", 0),
            EventStructuralLockRecoveryResult.AlreadyTerminal => ("RECOVERY_ALREADY_TERMINAL", 0),
            EventStructuralLockRecoveryResult.AlreadyScheduled => ("RECOVERY_ALREADY_SCHEDULED", 0),
            EventStructuralLockRecoveryResult.Rejected => ("RECOVERY_REJECTED", 3),
            _ => ("RECOVERY_INVALID_ARGUMENTS", 2)
        };
        (code == 0 ? Console.Out : Console.Error).WriteLine(message);
        return code;
    }
}
