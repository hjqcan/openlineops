using System.Collections;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace OpenLineOps.Agent;

internal static partial class StationAgentDiagnostics
{
    private const int MaximumDiagnosticMessageLength = 4_096;
    private const int MaximumExceptionSummaryCount = 16;
    private const string RedactedValue = "[REDACTED]";

    public static string FormatFailureMessage(
        string summary,
        Exception exception)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);
        ArgumentNullException.ThrowIfNull(exception);
        var exceptionDetails = SafeExceptionSummary(exception);
        return SanitizeMessage($"{summary}: {exceptionDetails}");
    }

    public static string SanitizeMessage(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return SanitizeAndBound(message);
    }

    public static Exception CreateLogSafeException(
        string summary,
        Exception exception) =>
        new LogSafeException(FormatFailureMessage(summary, exception));

    public static void ProtectLoggingProviders(
        IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        for (var index = 0; index < services.Count; index++)
        {
            var descriptor = services[index];
            if (descriptor.ServiceType != typeof(ILoggerProvider)
                || descriptor.IsKeyedService)
            {
                continue;
            }

            services[index] = ServiceDescriptor.Describe(
                typeof(ILoggerProvider),
                serviceProvider => new RedactingLoggerProvider(
                    CreateLoggerProvider(serviceProvider, descriptor)),
                descriptor.Lifetime);
        }
    }

    private static string SanitizeAndBound(string message)
    {
        message = RedactSensitiveEnvironmentValues(message);
        message = CredentialUri().Replace(
            message,
            "${scheme}" + RedactedValue + "@");
        message = AuthorizationValue().Replace(
            message,
            "${prefix}" + RedactedValue);
        message = AuthorizationSchemeValue().Replace(
            message,
            "${scheme} " + RedactedValue);
        message = SensitiveKeyValue().Replace(
            message,
            "${prefix}" + RedactedValue);
        var normalized = new string(message
            .Select(static character => char.IsControl(character) ? ' ' : character)
            .ToArray());
        return normalized.Length <= MaximumDiagnosticMessageLength
            ? normalized
            : normalized[..MaximumDiagnosticMessageLength];
    }

    private static string RedactSensitiveEnvironmentValues(string message)
    {
        try
        {
            foreach (DictionaryEntry variable in Environment.GetEnvironmentVariables())
            {
                if (variable.Key is not string name
                    || variable.Value is not string value
                    || value.Length < 4
                    || !IsSensitiveEnvironmentName(name))
                {
                    continue;
                }

                message = message.Replace(
                    value,
                    RedactedValue,
                    StringComparison.Ordinal);
            }
        }
        catch
        {
            // Diagnostics must never mask the failure they are reporting.
        }

        return message;
    }

    private static string SafeExceptionSummary(Exception exception)
    {
        try
        {
            var summaries = new List<string>();
            AppendExceptionSummaries(
                exception,
                summaries,
                new HashSet<Exception>(ReferenceEqualityComparer.Instance));
            return string.Join(" ---> ", summaries);
        }
        catch
        {
            return exception.GetType().FullName
                   ?? exception.GetType().Name;
        }
    }

    private static void AppendExceptionSummaries(
        Exception exception,
        List<string> summaries,
        HashSet<Exception> visited)
    {
        if (summaries.Count >= MaximumExceptionSummaryCount
            || !visited.Add(exception))
        {
            return;
        }

        var typeName = exception.GetType().FullName
                       ?? exception.GetType().Name;
        var message = exception.Message;
        if (message.Length > MaximumDiagnosticMessageLength)
        {
            message = message[..MaximumDiagnosticMessageLength];
        }
        summaries.Add($"{typeName}: {message}");

        if (exception is AggregateException aggregate)
        {
            foreach (var innerException in aggregate.Flatten().InnerExceptions)
            {
                AppendExceptionSummaries(innerException, summaries, visited);
            }
            return;
        }

        if (exception.InnerException is not null)
        {
            AppendExceptionSummaries(exception.InnerException, summaries, visited);
        }
    }

    private static ILoggerProvider CreateLoggerProvider(
        IServiceProvider serviceProvider,
        ServiceDescriptor descriptor)
    {
        object? provider = descriptor.ImplementationInstance;
        provider ??= descriptor.ImplementationFactory?.Invoke(serviceProvider);
        if (provider is null
            && descriptor.ImplementationType is not null)
        {
            provider = ActivatorUtilities.CreateInstance(
                serviceProvider,
                descriptor.ImplementationType);
        }

        return provider as ILoggerProvider
               ?? throw new InvalidOperationException(
                   "A registered logging provider could not be created.");
    }

    private static bool IsSensitiveEnvironmentName(string name) =>
        name.Contains("TOKEN", StringComparison.OrdinalIgnoreCase)
        || name.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase)
        || name.Contains("SECRET", StringComparison.OrdinalIgnoreCase)
        || name.Contains("CREDENTIAL", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("URI", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(
        @"\b(?<scheme>[a-z][a-z0-9+.-]*://)[^/@\s]+@",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CredentialUri();

    [GeneratedRegex(
        @"\b(?<prefix>Authorization\s*[:=]\s*)[^\r\n,;]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationValue();

    [GeneratedRegex(
        @"\b(?<scheme>Bearer|Basic)\s+[A-Za-z0-9._~+/=-]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationSchemeValue();

    [GeneratedRegex(
        @"\b(?<prefix>(?:password|passwd|pwd|secret|token|api[_-]?key|access[_-]?token)\s*[:=]\s*)(?:""[^""]*""|'[^']*'|[^\s,;]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveKeyValue();

    private sealed class RedactingLoggerProvider(
        ILoggerProvider provider) : ILoggerProvider, ISupportExternalScope
    {
        public ILogger CreateLogger(string categoryName) =>
            new RedactingLogger(provider.CreateLogger(categoryName));

        public void SetScopeProvider(IExternalScopeProvider scopeProvider)
        {
            if (provider is ISupportExternalScope externalScopeProvider)
            {
                externalScopeProvider.SetScopeProvider(
                    new RedactingExternalScopeProvider(scopeProvider));
            }
        }

        public void Dispose() => provider.Dispose();
    }

    private sealed class RedactingLogger(
        ILogger logger) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            logger.BeginScope(SanitizeScope(state));

        public bool IsEnabled(LogLevel logLevel) =>
            logger.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            string summary;
            try
            {
                summary = formatter(state, exception);
            }
            catch
            {
                summary = "An ILogger message formatter failed while reporting a diagnostic.";
            }

            if (exception is null)
            {
                var diagnostic = SanitizeMessage(summary);
                logger.Log(
                    logLevel,
                    eventId,
                    diagnostic,
                    null,
                    static (message, _) => message);
                return;
            }

            var failureDiagnostic = FormatFailureMessage(summary, exception);
            logger.Log(
                logLevel,
                eventId,
                failureDiagnostic,
                null,
                static (message, _) => message);
        }
    }

    private sealed class RedactingExternalScopeProvider(
        IExternalScopeProvider scopeProvider) : IExternalScopeProvider
    {
        public void ForEachScope<TState>(
            Action<object?, TState> callback,
            TState state)
        {
            scopeProvider.ForEachScope(
                (scope, callbackState) =>
                    callback(SanitizeScope(scope), callbackState),
                state);
        }

        public IDisposable Push(object? state) =>
            scopeProvider.Push(SanitizeScope(state));
    }

    private static string SanitizeScope(object? scope)
    {
        try
        {
            return SanitizeMessage(scope?.ToString() ?? string.Empty);
        }
        catch
        {
            return "An ILogger scope could not be formatted safely.";
        }
    }

    private sealed class LogSafeException(
        string diagnostic) : Exception(diagnostic)
    {
        public override string ToString() => Message;
    }
}
