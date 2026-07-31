using System.Text.Json;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.StationRuntime.Contracts;

namespace OpenLineOps.StationRuntime;

public static class StationRuntimeEntrypoint
{
    private const int MaximumRequestBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = StationOperationDocumentJson.CreateOptions();

    public static async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        StationRuntimeHostOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        StationRuntimeCommandLine commandLine;
        try
        {
            commandLine = StationRuntimeCommandLine.Parse(arguments);
        }
        catch (InvalidDataException exception)
        {
            await Console.Error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 64;
        }

        try
        {
            options = options.Validate();
        }
        catch (Exception exception) when (exception is InvalidDataException
                                           or FileNotFoundException)
        {
            await Console.Error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 65;
        }

        StationOperationRequestDocument request;
        try
        {
            request = await ReadRequestAsync(commandLine.RequestFilePath, cancellationToken)
                .ConfigureAwait(false);
            StationOperationDocumentJson.Validate(request);
            ValidateResultPath(commandLine, request);
        }
        catch (Exception exception) when (exception is InvalidDataException
                                           or IOException
                                           or UnauthorizedAccessException
                                           or JsonException)
        {
            await Console.Error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 65;
        }

        var startedAtUtc = DateTimeOffset.UtcNow;
        StationOperationResultDocument result;
        try
        {
            result = await StationOperationExecutor.ExecuteAsync(
                    request,
                    options,
                    Path.GetDirectoryName(commandLine.ResultFilePath)!,
                    startedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or InvalidDataException
                                           or InvalidOperationException
                                           or IOException
                                           or UnauthorizedAccessException
                                           or JsonException)
        {
            result = Failure(request, startedAtUtc, exception);
        }

        try
        {
            StationOperationDocumentJson.Validate(result);
            await WriteResultAsync(commandLine.ResultFilePath, result, cancellationToken)
                .ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception) when (exception is InvalidDataException
                                           or IOException
                                           or UnauthorizedAccessException
                                           or JsonException)
        {
            await Console.Error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 66;
        }
    }

    private static async ValueTask<StationOperationRequestDocument> ReadRequestAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length is <= 0 or > MaximumRequestBytes)
        {
            throw new InvalidDataException(
                $"Station operation request must be between 1 and {MaximumRequestBytes} bytes.");
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<StationOperationRequestDocument>(
                stream,
                JsonOptions,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("Station operation request is null.");
    }

    private static void ValidateResultPath(
        StationRuntimeCommandLine commandLine,
        StationOperationRequestDocument request)
    {
        var requestDirectory = Path.GetFullPath(Path.GetDirectoryName(commandLine.RequestFilePath)!)
            .TrimEnd(Path.DirectorySeparatorChar);
        var resultDirectory = Path.GetFullPath(Path.GetDirectoryName(commandLine.ResultFilePath)!)
            .TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(requestDirectory, resultDirectory, StringComparison.OrdinalIgnoreCase)
            || File.Exists(commandLine.ResultFilePath)
            || Directory.Exists(commandLine.ResultFilePath)
            || !Directory.Exists(request.PackageContentDirectory))
        {
            throw new InvalidDataException(
                "Request and new result files must share one work directory and package content must exist.");
        }
    }

    private static StationOperationResultDocument Failure(
        StationOperationRequestDocument request,
        DateTimeOffset startedAtUtc,
        Exception exception)
    {
        using var outputs = JsonDocument.Parse("{}");
        return new StationOperationResultDocument(
            StationOperationDocumentContract.ResultSchema,
            request.JobId,
            request.RuntimeSessionId,
            ExecutionStatus.Failed,
            ResultJudgement.Unknown,
            outputs.RootElement.Clone(),
            0,
            0,
            0,
            startedAtUtc,
            DateTimeOffset.UtcNow,
            [],
            [],
            [],
            [],
            "StationRuntime.ExecutionFailed",
            CanonicalFailureReason(exception.Message));
    }

    private static string CanonicalFailureReason(string message)
    {
        var normalized = new string(message
            .Select(character => char.IsControl(character) ? ' ' : character)
            .ToArray()).Trim();
        if (normalized.Length == 0)
        {
            normalized = "Station Runtime execution failed.";
        }

        return normalized.Length <= 4096 ? normalized : normalized[..4096];
    }

    private static async ValueTask WriteResultAsync(
        string path,
        StationOperationResultDocument result,
        CancellationToken cancellationToken)
    {
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, result, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, path, overwrite: false);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }
}
