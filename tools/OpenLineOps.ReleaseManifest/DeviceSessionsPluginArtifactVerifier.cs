using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenLineOps.ReleaseManifest;

internal static class DeviceSessionsPluginArtifactVerifier
{
    private const string ManifestEntryName = "manifest.json";
    private const string AssemblyEntryName = "OpenLineOps.BuiltinPlugins.DeviceSessions.dll";
    private const int MaximumManifestBytes = 64 * 1024;
    private const int MaximumAssemblyBytes = 64 * 1024 * 1024;
    private static readonly string[] ExpectedEntries = [AssemblyEntryName, ManifestEntryName];
    private static readonly string[] ExpectedCapabilities =
    [
        "device.scpi-tcp",
        "device.modbus-tcp",
        "device.tcp-line-scanner",
        "device.record-replay"
    ];
    private static readonly DeviceCommandContract[] ExpectedCommands =
    [
        new(
            "device.sessions:query",
            "device.scpi-tcp",
            "Query",
            "application/json",
            "application/json",
            30_000,
            0),
        new(
            "device.sessions:write",
            "device.scpi-tcp",
            "Write",
            "application/json",
            "application/json",
            30_000,
            0),
        new(
            "device.sessions:modbus-write",
            "device.modbus-tcp",
            "Write",
            "application/json",
            "application/json",
            30_000,
            0),
        new(
            "device.sessions:modbus-reconnect",
            "device.modbus-tcp",
            "Reconnect",
            "application/json",
            "application/json",
            10_000,
            0),
        new(
            "device.sessions:reconnect",
            "device.tcp-line-scanner",
            "Reconnect",
            "application/json",
            "application/json",
            10_000,
            0)
    ];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false
    };

    public static void Verify(string artifactPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactPath);
        if (!string.Equals(Path.GetExtension(artifactPath), ".zip", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The device-sessions-plugin release artifact must be one canonical ZIP archive.");
        }

        using var stream = new FileStream(
            artifactPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count != ExpectedEntries.Length)
        {
            throw new InvalidDataException(
                "The device-sessions-plugin archive must contain exactly manifest.json and "
                + "OpenLineOps.BuiltinPlugins.DeviceSessions.dll; additional executable or payload content is unsupported.");
        }

        var entries = archive.Entries
            .Select(entry => RequireCanonicalFileEntry(entry, artifactPath))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!entries.SequenceEqual(ExpectedEntries, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                "The device-sessions-plugin archive must contain exactly manifest.json and "
                + "OpenLineOps.BuiltinPlugins.DeviceSessions.dll; additional executable or payload content is unsupported.");
        }

        var manifestEntry = archive.GetEntry(ManifestEntryName)
            ?? throw new InvalidDataException("The device-sessions-plugin archive is missing manifest.json.");
        var assemblyEntry = archive.GetEntry(AssemblyEntryName)
            ?? throw new InvalidDataException(
                "The device-sessions-plugin archive is missing its entry assembly.");
        VerifyManifest(manifestEntry);
        VerifyAssembly(assemblyEntry);
    }

    private static string RequireCanonicalFileEntry(ZipArchiveEntry entry, string artifactPath)
    {
        if (string.IsNullOrEmpty(entry.Name)
            || entry.FullName.Contains('\\', StringComparison.Ordinal)
            || !string.Equals(entry.Name, entry.FullName, StringComparison.Ordinal)
            || entry.FullName.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new InvalidDataException(
                $"The device-sessions-plugin archive '{artifactPath}' contains a non-canonical entry '{entry.FullName}'.");
        }

        return entry.FullName;
    }

    private static void VerifyManifest(ZipArchiveEntry entry)
    {
        if (entry.Length is <= 0 or > MaximumManifestBytes)
        {
            throw new InvalidDataException(
                "The device-sessions-plugin manifest has an invalid size.");
        }

        PluginManifestContract manifest;
        try
        {
            using var stream = ReadEntry(entry, MaximumManifestBytes);
            manifest = JsonSerializer.Deserialize<PluginManifestContract>(stream, JsonOptions)
                ?? throw new InvalidDataException(
                    "The device-sessions-plugin manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"The device-sessions-plugin manifest is invalid: {exception.Message}",
                exception);
        }

        ReleaseManifestContract.ValidateVersion(manifest.Version);
        if (!string.Equals(manifest.Id, "openlineops.builtin.device-sessions", StringComparison.Ordinal)
            || !string.Equals(manifest.Name, "Industrial Device Sessions", StringComparison.Ordinal)
            || !string.Equals(manifest.Kind, "DeviceDriver", StringComparison.Ordinal)
            || !string.Equals(manifest.EntryAssembly, AssemblyEntryName, StringComparison.Ordinal)
            || !string.Equals(
                manifest.EntryType,
                "OpenLineOps.BuiltinPlugins.DeviceSessions.IndustrialDeviceSessionPlugin",
                StringComparison.Ordinal)
            || !string.Equals(manifest.ContractVersion, "1.0.0", StringComparison.Ordinal)
            || !string.Equals(manifest.MinimumPlatformVersion, "1.0.0", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The device-sessions-plugin manifest identity or compatibility contract is invalid.");
        }

        if (manifest.Capabilities is null
            || !manifest.Capabilities.SequenceEqual(ExpectedCapabilities, StringComparer.Ordinal)
            || manifest.DeviceCommands is null
            || !manifest.DeviceCommands.SequenceEqual(ExpectedCommands))
        {
            throw new InvalidDataException(
                "The device-sessions-plugin manifest capability or command inventory is invalid.");
        }
    }

    private static void VerifyAssembly(ZipArchiveEntry entry)
    {
        if (entry.Length is <= 0 or > MaximumAssemblyBytes)
        {
            throw new InvalidDataException(
                "The device-sessions-plugin entry assembly has an invalid size.");
        }

        try
        {
            using var stream = ReadEntry(entry, MaximumAssemblyBytes);
            using var reader = new PEReader(stream, PEStreamOptions.LeaveOpen);
            var corHeader = reader.PEHeaders.CorHeader;
            if (!reader.HasMetadata
                || corHeader is null
                || !corHeader.Flags.HasFlag(CorFlags.ILOnly)
                || corHeader.ManagedNativeHeaderDirectory.Size != 0
                || corHeader.EntryPointTokenOrRelativeVirtualAddress != 0)
            {
                throw new InvalidDataException(
                    "The device-sessions-plugin entry assembly must be a managed IL-only class library.");
            }

            var metadata = reader.GetMetadataReader();
            if (!metadata.IsAssembly)
            {
                throw new InvalidDataException(
                    "The device-sessions-plugin entry assembly does not contain assembly metadata.");
            }

            var definition = metadata.GetAssemblyDefinition();
            var assemblyName = metadata.GetString(definition.Name);
            if (!string.Equals(
                    assemblyName,
                    "OpenLineOps.BuiltinPlugins.DeviceSessions",
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The device-sessions-plugin entry assembly identity is invalid.");
            }
        }
        catch (BadImageFormatException exception)
        {
            throw new InvalidDataException(
                "The device-sessions-plugin entry assembly is not a valid managed assembly.",
                exception);
        }
    }

    private static MemoryStream ReadEntry(ZipArchiveEntry entry, int maximumBytes)
    {
        var output = new MemoryStream(checked((int)entry.Length));
        try
        {
            using var input = entry.Open();
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
            {
                if (output.Length > maximumBytes - read)
                {
                    throw new InvalidDataException(
                        $"The device-sessions-plugin entry '{entry.FullName}' exceeds its size limit.");
                }

                output.Write(buffer, 0, read);
            }

            if (output.Length != entry.Length)
            {
                throw new InvalidDataException(
                    $"The device-sessions-plugin entry '{entry.FullName}' expanded length is invalid.");
            }

            output.Position = 0;
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    private sealed record PluginManifestContract(
        string Id,
        string Name,
        string Version,
        string Kind,
        string EntryAssembly,
        string EntryType,
        string ContractVersion,
        string MinimumPlatformVersion,
        string[] Capabilities,
        DeviceCommandContract[] DeviceCommands);

    private sealed record DeviceCommandContract(
        string Id,
        string Capability,
        string CommandName,
        string InputSchema,
        string OutputSchema,
        int TimeoutMilliseconds,
        int MaxRetries);
}
