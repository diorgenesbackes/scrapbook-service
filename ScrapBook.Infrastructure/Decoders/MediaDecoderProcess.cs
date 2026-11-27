using System.Globalization;
using System.ComponentModel;
using System.Diagnostics;
using ScrapBook.Domain.Exceptions;

namespace ScrapBook.Infrastructure.Decoders;

internal static class MediaDecoderProcess
{
    private const int MaximumDiagnosticOutput = 24 * 1024;

    public static string[] InputOptions(string formats, bool iso)
    {
        string[] options = ["-v", "error", "-protocol_whitelist", "file", "-format_whitelist", formats,
            "-max_streams", "16", "-max_alloc", "134217728", "-max_pixels", "33177600", "-threads", "1",
            "-probesize", "10000000", "-analyzeduration", "10000000", "-err_detect", "crccheck+explode"];
        return iso ? [.. options, "-enable_drefs", "0", "-use_absolute_path", "0"] : options;
    }

    public static async Task<TResult> RunAsync<TResult>(string executable, string path, string[] arguments,
        Func<StreamReader, CancellationToken, Task<TResult>> captureOutputAsync, CancellationToken cancellationToken)
    {
        return await RunAsync(executable, path, arguments, captureOutputAsync, null, cancellationToken);
    }

    public static async Task<TResult> RunAsync<TResult>(string executable, string path, string[] arguments,
        Func<StreamReader, CancellationToken, Task<TResult>> captureOutputAsync, long? maximumFileBytes,
        CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = StartInfo(executable, path, arguments, maximumFileBytes) };
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!process.Start()) throw new PhotoDecoderUnavailableException();
        }
        catch (Win32Exception)
        {
            throw new PhotoDecoderUnavailableException();
        }

        process.StandardInput.Close();
        var output = captureOutputAsync(process.StandardOutput, cancellationToken);
        var errors = ReadBoundedAsync(process.StandardError, MaximumDiagnosticOutput, cancellationToken);
        var exit = process.WaitForExitAsync(cancellationToken);
        Task[] tasks = [output, errors, exit];
        try
        {
            await foreach (var task in Task.WhenEach(tasks))
                await task;

            RequireSuccess(process.ExitCode, await errors);
            return await output;
        }
        finally
        {
            await StopAsync(process);
            await Task.WhenAll(tasks).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    public static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, CancellationToken cancellationToken)
    {
        var buffer = new char[limit + 1];
        var length = await reader.ReadBlockAsync(buffer, cancellationToken);
        return length <= limit ? new string(buffer, 0, length) : throw new PhotoDecoderBudgetException();
    }

    private static void RequireSuccess(int exitCode, string diagnostics)
    {
        if (exitCode is 137 or 152 || diagnostics.Contains("Cannot allocate memory", StringComparison.Ordinal))
            throw new PhotoDecoderBudgetException();
        if (diagnostics.StartsWith("prlimit:", StringComparison.Ordinal)
            || diagnostics.Contains("Unrecognized option", StringComparison.Ordinal))
            throw new PhotoDecoderUnavailableException();
        if (exitCode != 0 || !string.IsNullOrWhiteSpace(diagnostics)) throw new PhotoContentInvalidException();
    }

    private static ProcessStartInfo StartInfo(string executable, string path, string[] arguments, long? maximumFileBytes)
    {
        if (!Path.IsPathFullyQualified(executable)) throw new PhotoDecoderUnavailableException();

        var linux = OperatingSystem.IsLinux();
        var info = new ProcessStartInfo(linux ? "/usr/bin/prlimit" : executable)
        {
            WorkingDirectory = Path.GetDirectoryName(path)!,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (linux)
        {
            info.ArgumentList.Add("--as=805306368");
            info.ArgumentList.Add("--cpu=25");
            if (maximumFileBytes is { } limit)
                info.ArgumentList.Add("--fsize=" + limit.ToString(CultureInfo.InvariantCulture));
            info.ArgumentList.Add("--");
            info.ArgumentList.Add(executable);
        }

        info.Environment.Clear();
        info.Environment["LC_ALL"] = "C";
        info.Environment["TMPDIR"] = info.WorkingDirectory;
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }

    private static async Task StopAsync(Process process)
    {
        if (process.HasExited) return;
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception error) when ((error is InvalidOperationException or Win32Exception) && process.HasExited)
        {
        }
        await process.WaitForExitAsync(CancellationToken.None);
    }
}
