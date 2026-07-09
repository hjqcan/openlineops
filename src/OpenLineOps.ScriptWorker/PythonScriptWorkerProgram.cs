using System.Text.Json;
using OpenLineOps.Runtime.Infrastructure.Scripting;

namespace OpenLineOps.ScriptWorker;

public static class PythonScriptWorkerProgram
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<int> RunAsync(
        string[] args,
        TextReader input,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        _ = args;

        try
        {
            var requestJson = await input.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(requestJson))
            {
                await error.WriteLineAsync("Python script worker request body is required.").ConfigureAwait(false);
                return 2;
            }

            var request = JsonSerializer.Deserialize<PythonScriptExecutionScopeRequest>(
                requestJson,
                JsonOptions);
            if (request is null)
            {
                await error.WriteLineAsync("Python script worker request body is empty.").ConfigureAwait(false);
                return 2;
            }

            var result = PythonScriptExecutionScope.Execute(request, cancellationToken);
            var response = PythonScriptWorkerExecutionResult.FromRuntimeResult(result);

            await output
                .WriteAsync(JsonSerializer.Serialize(response, JsonOptions))
                .ConfigureAwait(false);
            return 0;
        }
        catch (JsonException exception)
        {
            await error.WriteLineAsync($"Python script worker request JSON is invalid: {exception.Message}")
                .ConfigureAwait(false);
            return 2;
        }
        catch (OperationCanceledException)
        {
            await error.WriteLineAsync("Python script worker execution was canceled.")
                .ConfigureAwait(false);
            return 3;
        }
        catch (Exception exception)
        {
            await error.WriteLineAsync(exception.ToString()).ConfigureAwait(false);
            return 1;
        }
    }
}
