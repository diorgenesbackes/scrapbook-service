using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;

namespace ScrapBook.Infrastructure.Decoders;

public sealed class ProcessPhotoSubmissionDecoder : IPhotoSubmissionDecoder
{
    public const string Argument = "--decode-private-photo";
    public const int InvalidContentExitCode = 2;
    public const int BudgetExceededExitCode = 3;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public async Task<PhotoImageModel> DecodeAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(Timeout);
        using var process = new Process { StartInfo = StartInfo(bytes.Length) };
        try
        {
            if (!process.Start()) throw new PhotoDecoderUnavailableException();
        }
        catch (Win32Exception)
        {
            throw new PhotoDecoderUnavailableException();
        }

        var output = process.StandardOutput.ReadToEndAsync(budget.Token);
        var drain = process.StandardError.BaseStream.CopyToAsync(Stream.Null, budget.Token);
        try
        {
            await process.StandardInput.BaseStream.WriteAsync(bytes, budget.Token);
            process.StandardInput.Close();
            await Task.WhenAll(process.WaitForExitAsync(budget.Token), output, drain);
            return process.ExitCode switch
            {
                0 => JsonSerializer.Deserialize<PhotoImageModel>(await output) ?? throw new PhotoDecoderUnavailableException(),
                InvalidContentExitCode => throw new PhotoContentInvalidException(),
                BudgetExceededExitCode => throw new PhotoDecoderBudgetException(),
                _ => throw new PhotoDecoderUnavailableException()
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PhotoDecoderBudgetException();
        }
        catch (Exception error) when (error is IOException or JsonException)
        {
            throw new PhotoDecoderUnavailableException();
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    private static ProcessStartInfo StartInfo(int bytes)
    {
        var executable = Environment.ProcessPath ?? throw new PhotoDecoderUnavailableException();
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(executable) == "dotnet")
        {
            start.ArgumentList.Add(Assembly.GetEntryAssembly()?.Location ?? throw new PhotoDecoderUnavailableException());
        }
        start.ArgumentList.Add(Argument);
        start.ArgumentList.Add(bytes.ToString(CultureInfo.InvariantCulture));
        foreach (var key in start.Environment.Keys.Except(new[] { "PATH", "DOTNET_ROOT", "DOTNET_ROOT_X64",
            "DOTNET_ROOT_ARM64", "LD_LIBRARY_PATH", "TMPDIR", "TMP", "TEMP", "SystemRoot" }).ToArray())
        {
            start.Environment.Remove(key);
        }
        return start;
    }
}
