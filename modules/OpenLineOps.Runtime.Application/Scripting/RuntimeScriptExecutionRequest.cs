using System.Text.Json;
using OpenLineOps.Runtime.Application.Commands;

namespace OpenLineOps.Runtime.Application.Scripting;

public sealed record RuntimeScriptExecutionRequest(
    RuntimeCommandExecutionContext CommandContext,
    string ScriptLanguage,
    string ScriptSourceCode,
    string? ScriptVersion,
    string? InputPayload)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static bool TryCreate(
        RuntimeCommandExecutionContext context,
        out RuntimeScriptExecutionRequest? request,
        out string? error)
    {
        request = null;
        error = null;

        if (!RuntimeScriptCommand.IsPythonScript(context))
        {
            error = $"Runtime command {context.CommandName} is not a Python script command.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(context.InputPayload))
        {
            error = "Python script command payload is required.";
            return false;
        }

        RuntimeScriptCommandPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<RuntimeScriptCommandPayload>(
                context.InputPayload,
                JsonOptions);
        }
        catch (JsonException exception)
        {
            error = $"Python script command payload is invalid JSON: {exception.Message}";
            return false;
        }

        if (payload is null)
        {
            error = "Python script command payload is empty.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(payload.ScriptLanguage))
        {
            error = "Python script command payload must declare a script language.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(payload.ScriptSourceCode))
        {
            error = "Python script command payload must include Python source code.";
            return false;
        }

        request = new RuntimeScriptExecutionRequest(
            context,
            payload.ScriptLanguage,
            payload.ScriptSourceCode,
            payload.ScriptVersion,
            payload.InputPayload);
        return true;
    }
}
