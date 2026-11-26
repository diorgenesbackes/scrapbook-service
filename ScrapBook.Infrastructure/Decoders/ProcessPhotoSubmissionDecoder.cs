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
    private const int MaximumOutputCharacters = 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public async Task<PhotoImageModel> DecodeAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DirectoryInfo? scratch = null;
        try
        {
            scratch = Directory.CreateTempSubdirectory("scrapbook-photo-");
            return await RunAsync(bytes, scratch.FullName, cancellationToken);
        }
        catch (IOException)
        {
            throw new PhotoDecoderUnavailableException();
        }
        finally
        {
            DeleteScratch(scratch);
        }
    }

    private static async Task<PhotoImageModel> RunAsync(byte[] bytes, string scratchPath, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(Timeout);
        using var process = new Process { StartInfo = StartInfo(bytes.Length, scratchPath) };
        try
        {
            if (!process.Start()) throw new PhotoDecoderUnavailableException();
        }
        catch (Win32Exception)
        {
            throw new PhotoDecoderUnavailableException();
        }

        var output = ReadOutputAsync(process.StandardOutput, budget.Token);
        var drain = process.StandardError.BaseStream.CopyToAsync(Stream.Null, budget.Token);
        try
        {
            await process.StandardInput.BaseStream.WriteAsync(bytes, budget.Token);
            process.StandardInput.Close();
            var result = await output;
            await Task.WhenAll(process.WaitForExitAsync(budget.Token), drain);
            return process.ExitCode switch
            {
                0 => JsonSerializer.Deserialize<PhotoImageModel>(result) ?? throw new PhotoDecoderUnavailableException(),
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
    }

    private static async Task<string> ReadOutputAsync(StreamReader output, CancellationToken cancellationToken)
    {
        var buffer = new char[MaximumOutputCharacters + 1];
        var length = await output.ReadBlockAsync(buffer.AsMemory(), cancellationToken);
        return length <= MaximumOutputCharacters ? new string(buffer, 0, length) : throw new PhotoDecoderUnavailableException();
    }

    private static void DeleteScratch(DirectoryInfo? scratch)
    {
        try
        {
            scratch?.Delete(recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static ProcessStartInfo StartInfo(int bytes, string scratchPath)
    {
        var executable = Environment.ProcessPath ?? throw new PhotoDecoderUnavailableException();
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = scratchPath,
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
        foreach (var key in start.Environment.Keys.Except(new[] { "DOTNET_ROOT", "DOTNET_ROOT_X64",
            "DOTNET_ROOT_ARM64", "SystemRoot" }).ToArray())
        {
            start.Environment.Remove(key);
        }
        start.Environment["TMPDIR"] = scratchPath;
        start.Environment["TMP"] = scratchPath;
        start.Environment["TEMP"] = scratchPath;
        return start;
    }
}
