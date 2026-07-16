using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace OpenLineOps.VendorTestHelper;

public sealed class VendorTestHelperMarker;

public static class Program
{
    public const string InvocationFileEnvironmentVariable = "OPENLINEOPS_INVOCATION_FILE";
    public const string OutputDirectoryEnvironmentVariable = "OPENLINEOPS_OUTPUT_DIRECTORY";
    public const string CancellationFileName = "cancel.request";
    public const string ProcessIdFileName = "vendor-process-id.txt";
    public const string ChildProcessIdFileName = "child-process-id.txt";

    public const int UsageExitCode = 64;
    public const int CrashExitCode = 23;
    public const int CanceledExitCode = 130;

    private const int DefaultDelayMilliseconds = 300_000;
    private const int MaximumDelayMilliseconds = 3_600_000;
    private const int CancellationPollMilliseconds = 50;
    private const string ForceChildPidPublicationFailureEnvironmentVariable =
        "OPENLINEOPS_VENDOR_TEST_HELPER_FORCE_CHILD_PID_PUBLICATION_FAILURE";

    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions ResultJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly byte[] EvidenceImage = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Utf8WithoutBom;
        Console.InputEncoding = Utf8WithoutBom;

        if (args.Length > 0 && string.Equals(args[0], "sandbox-observe", StringComparison.Ordinal))
        {
            return await ObserveSandboxProcessAsync(args[1..]).ConfigureAwait(false);
        }

        if (args.Length > 0
            && string.Equals(
                args[0],
                "sandbox-probe-immutable-content",
                StringComparison.Ordinal))
        {
            return OperatingSystem.IsWindows()
                ? await ProbeImmutableContentAsync(args[1..]).ConfigureAwait(false)
                : UsageExitCode;
        }

        if (args.Length > 0
            && (string.Equals(args[0], "emergency-stop", StringComparison.Ordinal)
                || string.Equals(args[0], "safe-stop", StringComparison.Ordinal)))
        {
            return 0;
        }

        if (args.Length > 0 && string.Equals(args[0], "sandbox-check-handles", StringComparison.Ordinal))
        {
            return await CheckSandboxHandlesAsync(args[1..]).ConfigureAwait(false);
        }

        if (args.Length > 0 && string.Equals(args[0], "sandbox-large-output", StringComparison.Ordinal))
        {
            return await WriteLargeSandboxOutputAsync(args[1..]).ConfigureAwait(false);
        }

        if (args.Length > 0 && string.Equals(args[0], "sandbox-exit", StringComparison.Ordinal))
        {
            return SandboxExit(args[1..]);
        }

        if (args.Length > 0 && string.Equals(args[0], "sandbox-connect", StringComparison.Ordinal))
        {
            return await TrySandboxConnectionAsync(args[1..]).ConfigureAwait(false);
        }

        if (args.Length > 0
            && string.Equals(args[0], "sandbox-spawn-child-and-exit", StringComparison.Ordinal))
        {
            return SpawnSandboxChildAndExit(args[1..]);
        }

        if (args.Length > 0
            && string.Equals(args[0], "sandbox-child-wait", StringComparison.Ordinal))
        {
            return await RunSandboxChildAsync(args[1..]).ConfigureAwait(false);
        }

        if (args.Length > 0 && string.Equals(args[0], "child-delay", StringComparison.Ordinal))
        {
            return await RunChildDelayAsync(args[1..]).ConfigureAwait(false);
        }

        VendorTestHelperOptions options;
        try
        {
            options = ParseOptions(args);
        }
        catch (ArgumentException exception)
        {
            await Console.Error.WriteLineAsync($"Vendor helper usage error: {exception.Message}")
                .ConfigureAwait(false);
            return UsageExitCode;
        }

        InvocationContext invocation;
        try
        {
            invocation = await LoadInvocationAsync(options.OperationAttempt).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or IOException
                                          or UnauthorizedAccessException
                                          or JsonException
                                          or InvalidDataException)
        {
            await Console.Error.WriteLineAsync($"Vendor helper invocation error: {exception.Message}")
                .ConfigureAwait(false);
            return UsageExitCode;
        }

        using var cancellationSource = new CancellationTokenSource();
        ConsoleCancelEventHandler cancellationHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellationSource.Cancel();
        };
        Console.CancelKeyPress += cancellationHandler;
        try
        {
            try
            {
                return await RunAsync(options, invocation, cancellationSource.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or InvalidOperationException)
            {
                await Console.Error.WriteLineAsync(
                        $"Vendor helper execution failure: {exception.Message}")
                    .ConfigureAwait(false);
                return CrashExitCode;
            }
        }
        finally
        {
            Console.CancelKeyPress -= cancellationHandler;
        }
    }

    private static async Task<int> RunAsync(
        VendorTestHelperOptions options,
        InvocationContext invocation,
        CancellationToken cancellationToken)
    {
        await CreateEvidenceAsync(invocation, cancellationToken).ConfigureAwait(false);
        await Console.Error.WriteLineAsync(
                $"Vendor helper mode={options.Mode} operationAttempt={invocation.OperationAttempt} "
                + $"productionRunId={invocation.ProductionRunId ?? "unavailable"}.")
            .ConfigureAwait(false);

        if (options.Mode == VendorTestMode.Crash)
        {
            await Console.Error.WriteLineAsync("Vendor helper simulated an unexpected vendor process crash.")
                .ConfigureAwait(false);
            return CrashExitCode;
        }

        if (options.Mode is VendorTestMode.Delay
            or VendorTestMode.SpawnChildDelay
            or VendorTestMode.SpawnChildDelayRecovery)
        {
            var completed = options.Mode is VendorTestMode.SpawnChildDelay
                or VendorTestMode.SpawnChildDelayRecovery
                ? await RunSpawnChildDelayAsync(invocation.OutputDirectory, options.DelayMilliseconds, cancellationToken)
                    .ConfigureAwait(false)
                : await WaitForDelayAsync(invocation.OutputDirectory, options.DelayMilliseconds, cancellationToken)
                    .ConfigureAwait(false);
            if (!completed)
            {
                await Console.Error.WriteLineAsync("Vendor helper acknowledged cancellation and stopped.")
                    .ConfigureAwait(false);
                return CanceledExitCode;
            }
        }

        if (options.Mode == VendorTestMode.InvalidJson)
        {
            await Console.Out.WriteAsync("{\"outcome\":").ConfigureAwait(false);
            return 0;
        }

        var outcome = DetermineOutcome(options, invocation.OperationAttempt);
        var result = new VendorResult(
            outcome,
            new VendorMetrics(12.5M, invocation.OperationAttempt),
            new VendorEvidence("measurements.csv", "inspection.png", "report.pdf"),
            invocation.ProductionRunId,
            invocation.ProductionUnitIdentity,
            IsAppContainerProcess(),
            HasTokenCapability(InternetClientCapabilitySid));
        await Console.Out.WriteAsync(JsonSerializer.Serialize(result, ResultJsonOptions)).ConfigureAwait(false);
        return 0;
    }

    private static string DetermineOutcome(VendorTestHelperOptions options, int operationAttempt)
    {
        if (options.Mode == VendorTestMode.UnknownToken)
        {
            return "NeedsReview";
        }

        if (options.Mode == VendorTestMode.Failed
            && options.PassAfterFirstAttempt
            && operationAttempt > 1)
        {
            return nameof(VendorTestMode.Passed);
        }

        return options.Mode switch
        {
            VendorTestMode.Passed
                or VendorTestMode.Delay
                or VendorTestMode.SpawnChildDelay
                or VendorTestMode.SpawnChildDelayRecovery =>
                nameof(VendorTestMode.Passed),
            VendorTestMode.Failed => nameof(VendorTestMode.Failed),
            VendorTestMode.Aborted => nameof(VendorTestMode.Aborted),
            _ => throw new InvalidOperationException($"Mode '{options.Mode}' cannot produce a result outcome.")
        };
    }

    private static VendorTestHelperOptions ParseOptions(string[] args)
    {
        VendorTestMode? mode = null;
        int? operationAttempt = null;
        var delayMilliseconds = DefaultDelayMilliseconds;
        var passAfterFirstAttempt = false;
        var seenOptions = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < args.Length; index++)
        {
            var option = args[index];
            if (!seenOptions.Add(option))
            {
                throw new ArgumentException($"Option '{option}' must not be repeated.", nameof(args));
            }

            switch (option)
            {
                case "--mode":
                    mode = ParseMode(ReadOptionValue(args, ref index, option));
                    break;
                case "--operation-attempt":
                    operationAttempt = ParsePositiveInteger(
                        ReadOptionValue(args, ref index, option),
                        option,
                        int.MaxValue);
                    break;
                case "--delay-milliseconds":
                    delayMilliseconds = ParsePositiveInteger(
                        ReadOptionValue(args, ref index, option),
                        option,
                        MaximumDelayMilliseconds);
                    break;
                case "--pass-after-first-attempt":
                    passAfterFirstAttempt = true;
                    break;
                default:
                    throw new ArgumentException($"Option '{option}' is not supported.", nameof(args));
            }
        }

        if (mode is null)
        {
            throw new ArgumentException("Exactly one --mode option is required.", nameof(args));
        }

        if (passAfterFirstAttempt && mode != VendorTestMode.Failed)
        {
            throw new ArgumentException(
                "--pass-after-first-attempt is only valid with --mode Failed.",
                nameof(args));
        }

        return new VendorTestHelperOptions(
            mode.Value,
            operationAttempt,
            delayMilliseconds,
            passAfterFirstAttempt);
    }

    private static VendorTestMode ParseMode(string value)
    {
        return value switch
        {
            nameof(VendorTestMode.Passed) => VendorTestMode.Passed,
            nameof(VendorTestMode.Failed) => VendorTestMode.Failed,
            nameof(VendorTestMode.Aborted) => VendorTestMode.Aborted,
            nameof(VendorTestMode.Crash) => VendorTestMode.Crash,
            nameof(VendorTestMode.InvalidJson) => VendorTestMode.InvalidJson,
            nameof(VendorTestMode.UnknownToken) => VendorTestMode.UnknownToken,
            nameof(VendorTestMode.Delay) => VendorTestMode.Delay,
            nameof(VendorTestMode.SpawnChildDelay) => VendorTestMode.SpawnChildDelay,
            nameof(VendorTestMode.SpawnChildDelayRecovery) => VendorTestMode.SpawnChildDelayRecovery,
            _ => throw new ArgumentException($"Mode '{value}' is not supported.", nameof(value))
        };
    }

    private static string ReadOptionValue(
        string[] args,
        ref int index,
        string option)
    {
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Option '{option}' requires one value.", nameof(args));
        }

        index++;
        return args[index];
    }

    private static int ParsePositiveInteger(string value, string option, int maximum)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || parsed <= 0
            || parsed > maximum)
        {
            throw new ArgumentException(
                $"Option '{option}' must be an integer from 1 through {maximum.ToString(CultureInfo.InvariantCulture)}.",
                nameof(value));
        }

        return parsed;
    }

    private static async Task<InvocationContext> LoadInvocationAsync(int? commandLineOperationAttempt)
    {
        var invocationPath = RequireAbsoluteEnvironmentPath(InvocationFileEnvironmentVariable);
        var outputDirectory = RequireAbsoluteEnvironmentPath(OutputDirectoryEnvironmentVariable);
        if (!File.Exists(invocationPath))
        {
            throw new InvalidDataException($"Invocation file '{invocationPath}' does not exist.");
        }

        if (!Directory.Exists(outputDirectory))
        {
            throw new InvalidDataException($"Output directory '{outputDirectory}' does not exist.");
        }

        var invocationJson = await File.ReadAllTextAsync(invocationPath, Utf8WithoutBom).ConfigureAwait(false);
        using var document = JsonDocument.Parse(invocationJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || HasDuplicateProperties(document.RootElement))
        {
            throw new InvalidDataException("Invocation file must contain one JSON object without duplicate properties.");
        }

        if (!document.RootElement.TryGetProperty("operationAttempt", out var attemptElement)
            || !attemptElement.TryGetInt32(out var invocationAttempt)
            || invocationAttempt <= 0)
        {
            throw new InvalidDataException("Invocation operationAttempt must be a positive integer.");
        }

        if (commandLineOperationAttempt is not null
            && commandLineOperationAttempt.Value != invocationAttempt)
        {
            throw new InvalidDataException(
                "Command-line operation attempt must exactly match the frozen invocation file.");
        }

        return new InvocationContext(
            outputDirectory,
            invocationJson,
            invocationAttempt,
            ReadOptionalString(document.RootElement, "productionRunId"),
            ReadProductionUnitIdentity(document.RootElement));
    }

    private static string RequireAbsoluteEnvironmentPath(string variableName)
    {
        var value = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || !Path.IsPathFullyQualified(value))
        {
            throw new ArgumentException(
                $"Environment variable {variableName} must contain one absolute canonical path.",
                variableName);
        }

        return Path.GetFullPath(value);
    }

    private static string? ReadOptionalString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value)
               && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string? ReadProductionUnitIdentity(JsonElement root)
    {
        if (!root.TryGetProperty("productionUnit", out var productionUnit)
            || productionUnit.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return ReadOptionalString(productionUnit, "identityValue");
    }

    private static bool HasDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name) || HasDuplicateProperties(property.Value))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (HasDuplicateProperties(item))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static async Task CreateEvidenceAsync(
        InvocationContext invocation,
        CancellationToken cancellationToken)
    {
        await WriteTextAtomicallyAsync(
                Path.Combine(invocation.OutputDirectory, ProcessIdFileName),
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                cancellationToken)
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(
                Path.Combine(invocation.OutputDirectory, "measurements.csv"),
                "name,value,unit\nvoltage,12.5,V\noperationAttempt,"
                + invocation.OperationAttempt.ToString(CultureInfo.InvariantCulture)
                + ",count\n",
                Utf8WithoutBom,
                cancellationToken)
            .ConfigureAwait(false);
        await File.WriteAllBytesAsync(
                Path.Combine(invocation.OutputDirectory, "inspection.png"),
                EvidenceImage,
                cancellationToken)
            .ConfigureAwait(false);
        await File.WriteAllBytesAsync(
                Path.Combine(invocation.OutputDirectory, "report.pdf"),
                CreateMinimalPdf(invocation.OperationAttempt),
                cancellationToken)
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(
                Path.Combine(invocation.OutputDirectory, "invocation-copy.json"),
                invocation.Json,
                Utf8WithoutBom,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static byte[] CreateMinimalPdf(int operationAttempt)
    {
        var content = "BT /F1 12 Tf 72 720 Td (OpenLineOps vendor report - attempt "
                      + operationAttempt.ToString(CultureInfo.InvariantCulture)
                      + ") Tj ET";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources "
            + "<< /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Length "
            + Encoding.ASCII.GetByteCount(content).ToString(CultureInfo.InvariantCulture)
            + " >>\nstream\n"
            + content
            + "\nendstream"
        };

        using var stream = new MemoryStream();
        WriteAscii(stream, "%PDF-1.4\n%OpenLineOps\n");
        var offsets = new List<long>(objects.Length);
        for (var index = 0; index < objects.Length; index++)
        {
            offsets.Add(stream.Position);
            WriteAscii(
                stream,
                (index + 1).ToString(CultureInfo.InvariantCulture)
                + " 0 obj\n"
                + objects[index]
                + "\nendobj\n");
        }

        var crossReferenceOffset = stream.Position;
        WriteAscii(stream, "xref\n0 6\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            WriteAscii(
                stream,
                offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        }

        WriteAscii(
            stream,
            "trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n"
            + crossReferenceOffset.ToString(CultureInfo.InvariantCulture)
            + "\n%%EOF\n");
        return stream.ToArray();
    }

    private static void WriteAscii(Stream stream, string value)
    {
        stream.Write(Encoding.ASCII.GetBytes(value));
    }

    private static async Task<bool> WaitForDelayAsync(
        string outputDirectory,
        int delayMilliseconds,
        CancellationToken cancellationToken)
    {
        var cancellationFile = Path.Combine(outputDirectory, CancellationFileName);
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < delayMilliseconds)
        {
            if (File.Exists(cancellationFile))
            {
                return false;
            }

            try
            {
                await Task.Delay(CancellationPollMilliseconds, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
        }

        return !File.Exists(cancellationFile) && !cancellationToken.IsCancellationRequested;
    }

    private static async Task<bool> RunSpawnChildDelayAsync(
        string outputDirectory,
        int delayMilliseconds,
        CancellationToken cancellationToken)
    {
        var startInfo = CreateSelfStartInfo(delayMilliseconds);
        using var child = Process.Start(startInfo)
                          ?? throw new InvalidOperationException("Vendor helper child process could not be started.");
        var childStandardOutput = child.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var childStandardError = child.StandardError.ReadToEndAsync(CancellationToken.None);

        try
        {
            await Console.Error.WriteLineAsync(
                    $"Vendor helper started child process {child.Id.ToString(CultureInfo.InvariantCulture)}.")
                .ConfigureAwait(false);
            if (string.Equals(
                    Environment.GetEnvironmentVariable(ForceChildPidPublicationFailureEnvironmentVariable),
                    "1",
                    StringComparison.Ordinal))
            {
                throw new IOException("Vendor helper simulated child PID publication failure.");
            }

            await WriteTextAtomicallyAsync(
                    Path.Combine(outputDirectory, ChildProcessIdFileName),
                    child.Id.ToString(CultureInfo.InvariantCulture),
                    cancellationToken)
                .ConfigureAwait(false);
            return await WaitForDelayAsync(outputDirectory, delayMilliseconds, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
            }

            await child.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(childStandardOutput, childStandardError).ConfigureAwait(false);
        }
    }

    private static ProcessStartInfo CreateSelfStartInfo(int delayMilliseconds)
    {
        return CreateSelfStartInfo(
            "child-delay",
            "--delay-milliseconds",
            delayMilliseconds.ToString(CultureInfo.InvariantCulture));
    }

    private static ProcessStartInfo CreateSelfStartInfo(params string[] arguments)
    {
        var processPath = Environment.ProcessPath
                          ?? throw new InvalidOperationException("Current process path is unavailable.");
        var entryAssemblyPath = Assembly.GetEntryAssembly()?.Location
                                ?? throw new InvalidOperationException("Entry assembly path is unavailable.");
        var launchThroughDotNet = string.Equals(
            Path.GetFileNameWithoutExtension(processPath),
            "dotnet",
            StringComparison.OrdinalIgnoreCase);
        if (OperatingSystem.IsWindows() && processPath.Length >= 260)
        {
            var runtimeDirectory = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
            var dotnetRoot = runtimeDirectory.Parent?.Parent?.Parent
                             ?? throw new InvalidOperationException(
                                 "The .NET installation root is unavailable for a long-path helper child.");
            processPath = Path.Combine(dotnetRoot.FullName, "dotnet.exe");
            if (!File.Exists(processPath))
            {
                throw new FileNotFoundException(
                    "The .NET host is unavailable for a long-path helper child.",
                    processPath);
            }

            launchThroughDotNet = true;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = processPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8WithoutBom,
            StandardErrorEncoding = Utf8WithoutBom
        };

        if (launchThroughDotNet)
        {
            startInfo.ArgumentList.Add(entryAssemblyPath);
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static async Task<int> ObserveSandboxProcessAsync(string[] arguments)
    {
        var environment = Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(
                entry => (string)entry.Key,
                entry => (string?)entry.Value ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);
        await Console.Out.WriteAsync(JsonSerializer.Serialize(
                new SandboxObservation(
                    arguments,
                    environment,
                    IsAppContainerProcess(),
                    HasTokenCapability(InternetClientCapabilitySid)),
                ResultJsonOptions))
            .ConfigureAwait(false);
        return 0;
    }

    [SupportedOSPlatform("windows")]
    private static async Task<int> ProbeImmutableContentAsync(string[] arguments)
    {
        if (arguments.Length != 6
            || !arguments.Skip(1).All(File.Exists))
        {
            return UsageExitCode;
        }

        var writeSucceeded = TryMutation(() =>
            File.WriteAllText(arguments[1], "mutated", Utf8WithoutBom));
        var renameSucceeded = TryMutation(() =>
            File.Move(arguments[2], arguments[2] + ".moved"));
        var deleteSucceeded = TryMutation(() => File.Delete(arguments[3]));
        var changePermissionsSucceeded = TryMutation(() =>
        {
            var file = new FileInfo(arguments[4]);
            var security = FileSystemAclExtensions.GetAccessControl(file);
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                FileSystemRights.FullControl,
                AccessControlType.Allow));
            FileSystemAclExtensions.SetAccessControl(file, security);
        });
        var takeOwnershipSucceeded = TryMutation(() =>
        {
            var file = new FileInfo(arguments[5]);
            var security = FileSystemAclExtensions.GetAccessControl(file);
            security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null));
            FileSystemAclExtensions.SetAccessControl(file, security);
        });

        await Console.Out.WriteAsync(JsonSerializer.Serialize(
                new ImmutableContentMutationObservation(
                    IsAppContainerProcess(),
                    HasTokenCapability(arguments[0]),
                    writeSucceeded,
                    renameSucceeded,
                    deleteSucceeded,
                    changePermissionsSucceeded,
                    takeOwnershipSucceeded),
                ResultJsonOptions))
            .ConfigureAwait(false);
        return 0;
    }

    private static bool TryMutation(Action mutation)
    {
        try
        {
            mutation();
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
                                          or IOException
                                          or System.Security.SecurityException
                                          or PrivilegeNotHeldException)
        {
            return false;
        }
    }

    private static async Task<int> CheckSandboxHandlesAsync(string[] arguments)
    {
        if (!OperatingSystem.IsWindows() || arguments.Length == 0)
        {
            return UsageExitCode;
        }

        var results = new bool[arguments.Length];
        for (var index = 0; index < arguments.Length; index++)
        {
            if (!long.TryParse(
                    arguments[index],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var value))
            {
                return UsageExitCode;
            }

            results[index] = GetHandleInformation(new IntPtr(value), out _);
        }

        await Console.Out.WriteAsync(JsonSerializer.Serialize(results, ResultJsonOptions))
            .ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> WriteLargeSandboxOutputAsync(string[] arguments)
    {
        if (arguments.Length != 0)
        {
            return UsageExitCode;
        }

        const string chunk = "0123456789abcdef";
        while (true)
        {
            await Console.Out.WriteAsync(chunk).ConfigureAwait(false);
        }
    }

    private static int SandboxExit(string[] arguments)
    {
        if (arguments.Length != 1
            || !int.TryParse(
                arguments[0],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var exitCode)
            || exitCode is < 1 or > 255)
        {
            return UsageExitCode;
        }

        return exitCode;
    }

    private static async Task<int> TrySandboxConnectionAsync(string[] arguments)
    {
        if (arguments.Length != 2
            || !int.TryParse(
                arguments[1],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var port)
            || port is < 1 or > 65535)
        {
            return UsageExitCode;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var client = new TcpClient();
        var connected = false;
        try
        {
            await client.ConnectAsync(arguments[0], port, timeout.Token).ConfigureAwait(false);
            connected = true;
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException)
        {
            connected = false;
        }

        await Console.Out.WriteAsync(JsonSerializer.Serialize(connected, ResultJsonOptions))
            .ConfigureAwait(false);
        return 0;
    }

    private static int SpawnSandboxChildAndExit(string[] args)
    {
        if (args.Length != 2)
        {
            return UsageExitCode;
        }

        var pidFile = Path.GetFullPath(args[0]);
        var delayMilliseconds = ParsePositiveInteger(
            args[1],
            "delayMilliseconds",
            MaximumDelayMilliseconds);
        using var child = Process.Start(CreateSelfStartInfo(
            "sandbox-child-wait",
            pidFile,
            delayMilliseconds.ToString(CultureInfo.InvariantCulture)))
            ?? throw new InvalidOperationException("Sandbox child process could not be started.");
        return 0;
    }

    private static async Task<int> RunSandboxChildAsync(string[] args)
    {
        if (args.Length != 2)
        {
            return UsageExitCode;
        }

        var pidFile = Path.GetFullPath(args[0]);
        var delayMilliseconds = ParsePositiveInteger(
            args[1],
            "delayMilliseconds",
            MaximumDelayMilliseconds);
        await WriteTextAtomicallyAsync(
                pidFile,
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                CancellationToken.None)
            .ConfigureAwait(false);
        await Task.Delay(delayMilliseconds).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> RunChildDelayAsync(string[] args)
    {
        if (args.Length != 2 || !string.Equals(args[0], "--delay-milliseconds", StringComparison.Ordinal))
        {
            await Console.Error.WriteLineAsync(
                    "Vendor helper internal child requires --delay-milliseconds exactly once.")
                .ConfigureAwait(false);
            return UsageExitCode;
        }

        int delayMilliseconds;
        try
        {
            delayMilliseconds = ParsePositiveInteger(
                args[1],
                "--delay-milliseconds",
                MaximumDelayMilliseconds);
        }
        catch (ArgumentException exception)
        {
            await Console.Error.WriteLineAsync($"Vendor helper child usage error: {exception.Message}")
                .ConfigureAwait(false);
            return UsageExitCode;
        }

        await Task.Delay(delayMilliseconds).ConfigureAwait(false);
        return 0;
    }

    private static async Task WriteTextAtomicallyAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        var destinationPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(destinationPath)
                        ?? throw new InvalidOperationException("Atomic output path has no parent directory.");
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await File.WriteAllTextAsync(
                    temporaryPath,
                    content,
                    Utf8WithoutBom,
                    cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private enum VendorTestMode
    {
        Passed,
        Failed,
        Aborted,
        Crash,
        InvalidJson,
        UnknownToken,
        Delay,
        SpawnChildDelay,
        SpawnChildDelayRecovery
    }

    private sealed record VendorTestHelperOptions(
        VendorTestMode Mode,
        int? OperationAttempt,
        int DelayMilliseconds,
        bool PassAfterFirstAttempt);

    private sealed record InvocationContext(
        string OutputDirectory,
        string Json,
        int OperationAttempt,
        string? ProductionRunId,
        string? ProductionUnitIdentity);

    private sealed record VendorResult(
        string Outcome,
        VendorMetrics Metrics,
        VendorEvidence Evidence,
        string? ProductionRunId,
        string? ProductionUnitIdentity,
        bool IsAppContainer,
        bool InternetClientCapability);

    private sealed record VendorMetrics(decimal Voltage, int Attempt);

    private sealed record VendorEvidence(string Csv, string Image, string Report);

    private sealed record SandboxObservation(
        IReadOnlyCollection<string> Arguments,
        IReadOnlyDictionary<string, string> Environment,
        bool IsAppContainer,
        bool HasInternetClientCapability);

    private sealed record ImmutableContentMutationObservation(
        bool IsAppContainer,
        bool HasExpectedContentCapability,
        bool WriteSucceeded,
        bool RenameSucceeded,
        bool DeleteSucceeded,
        bool ChangePermissionsSucceeded,
        bool TakeOwnershipSucceeded);

    private static bool IsAppContainerProcess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out var token))
        {
            throw new InvalidOperationException(
                $"Could not open the vendor helper process token: {Marshal.GetLastWin32Error()}.");
        }

        using (token)
        {
            if (!GetTokenInformation(
                    token,
                    TokenIsAppContainer,
                    out var isAppContainer,
                    sizeof(int),
                    out _))
            {
                throw new InvalidOperationException(
                    $"Could not inspect the vendor helper process token: {Marshal.GetLastWin32Error()}.");
            }

            return isAppContainer != 0;
        }
    }

    private static bool HasTokenCapability(string expectedSid)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out var token))
        {
            throw new InvalidOperationException(
                $"Could not open the vendor helper process token: {Marshal.GetLastWin32Error()}.");
        }

        using (token)
        {
            _ = GetTokenInformation(
                token,
                TokenCapabilities,
                IntPtr.Zero,
                tokenInformationLength: 0,
                out var requiredLength);
            var sizingError = Marshal.GetLastWin32Error();
            if (requiredLength <= 0 || sizingError != ErrorInsufficientBuffer)
            {
                throw new InvalidOperationException(
                    $"Could not size the vendor helper token capabilities: {sizingError}.");
            }

            var buffer = Marshal.AllocHGlobal(requiredLength);
            try
            {
                if (!GetTokenInformation(
                        token,
                        TokenCapabilities,
                        buffer,
                        requiredLength,
                        out _))
                {
                    throw new InvalidOperationException(
                        $"Could not inspect the vendor helper token capabilities: {Marshal.GetLastWin32Error()}.");
                }

                var count = checked((uint)Marshal.ReadInt32(buffer));
                var offset = Marshal.OffsetOf<TokenGroupsHeader>(
                    nameof(TokenGroupsHeader.FirstGroup)).ToInt32();
                var stride = Marshal.SizeOf<TokenSidAndAttributes>();
                for (var index = 0u; index < count; index++)
                {
                    var group = Marshal.PtrToStructure<TokenSidAndAttributes>(
                        IntPtr.Add(buffer, checked(offset + (int)index * stride)));
                    if (group.Sid != IntPtr.Zero
                        && string.Equals(
                            new SecurityIdentifier(group.Sid).Value,
                            expectedSid,
                            StringComparison.Ordinal))
                    {
                        return true;
                    }
                }

                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    private const uint TokenQuery = 0x0008;
    private const int TokenIsAppContainer = 29;
    private const int TokenCapabilities = 30;
    private const int ErrorInsufficientBuffer = 122;
    private const string InternetClientCapabilitySid = "S-1-15-3-1";

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenGroupsHeader
    {
        public uint GroupCount;
        public TokenSidAndAttributes FirstGroup;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenSidAndAttributes
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        IntPtr processHandle,
        uint desiredAccess,
        out SafeAccessTokenHandle tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        SafeAccessTokenHandle tokenHandle,
        int tokenInformationClass,
        out int tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        SafeAccessTokenHandle tokenHandle,
        int tokenInformationClass,
        IntPtr tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetHandleInformation(IntPtr handle, out uint flags);
}
