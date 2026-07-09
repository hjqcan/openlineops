using OpenLineOps.Plugin.Abstractions;

namespace OpenLineOps.Plugins.Infrastructure.Lifecycle;

public static class ExternalPluginHostProgram
{
    public static async Task<int> RunAsync(
        string[] args,
        TextReader input,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (!TryParseArguments(args, out var loadRequest, out var parseError))
        {
            await error.WriteLineAsync(parseError).ConfigureAwait(false);

            return 2;
        }

        IOpenLineOpsPlugin? plugin = null;
        try
        {
            plugin = await new ExternalPluginHostPluginLoader()
                .LoadAsync(loadRequest, cancellationToken)
                .ConfigureAwait(false);
            var initializationStatus = await plugin
                .InitializeAsync(EmptyServiceProvider.Instance, cancellationToken)
                .ConfigureAwait(false);

            if (initializationStatus == PluginInitializationStatus.Failed)
            {
                await error
                    .WriteLineAsync($"Plugin '{plugin.Manifest.Id}' returned initialization status Failed.")
                    .ConfigureAwait(false);

                return 3;
            }

            await ExternalPluginHostProtocolLoop
                .RunAsync(plugin, input, output, cancellationToken)
                .ConfigureAwait(false);

            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 130;
        }
        catch (Exception exception)
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);

            return 1;
        }
        finally
        {
            if (plugin is not null)
            {
                await plugin.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static bool TryParseArguments(
        string[] args,
        out ExternalPluginHostLoadRequest loadRequest,
        out string error)
    {
        string? manifestPath = null;
        string? entryAssemblyPath = null;
        string? entryType = null;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (string.Equals(argument, "--openlineops-plugin-host", StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(argument, "--manifest", StringComparison.Ordinal))
            {
                if (!TryReadValue(args, ref index, out manifestPath))
                {
                    return Fail(out loadRequest, out error, "--manifest requires a value.");
                }

                continue;
            }

            if (string.Equals(argument, "--entry", StringComparison.Ordinal))
            {
                if (!TryReadValue(args, ref index, out entryAssemblyPath))
                {
                    return Fail(out loadRequest, out error, "--entry requires a value.");
                }

                continue;
            }

            if (string.Equals(argument, "--type", StringComparison.Ordinal))
            {
                if (!TryReadValue(args, ref index, out entryType))
                {
                    return Fail(out loadRequest, out error, "--type requires a value.");
                }

                continue;
            }

            return Fail(out loadRequest, out error, $"Unsupported plugin host argument '{argument}'.");
        }

        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            return Fail(out loadRequest, out error, "--manifest is required.");
        }

        loadRequest = new ExternalPluginHostLoadRequest(
            manifestPath,
            entryAssemblyPath,
            entryType);
        error = "";

        return true;
    }

    private static bool TryReadValue(
        string[] args,
        ref int index,
        out string? value)
    {
        if (index + 1 >= args.Length)
        {
            value = null;

            return false;
        }

        index++;
        value = args[index];

        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool Fail(
        out ExternalPluginHostLoadRequest loadRequest,
        out string error,
        string reason)
    {
        loadRequest = new ExternalPluginHostLoadRequest("");
        error = reason;

        return false;
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static readonly EmptyServiceProvider Instance = new();

        public object? GetService(Type serviceType)
        {
            return null;
        }
    }
}
