using System.Globalization;

namespace OpenLineOps.ReleaseManifest;

public static class ReleaseManifestCommand
{
    public static int Run(string[] args, string currentDirectory)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDirectory);

        if (args.Any(IsHelpArgument))
        {
            Console.WriteLine(UsageText);
            return 0;
        }

        if (!TryParse(args, currentDirectory, out var options, out var error))
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine(UsageText);
            return 2;
        }

        try
        {
            if (options.JsonVerificationPaths is not null)
            {
                foreach (var path in options.JsonVerificationPaths)
                {
                    JsonPropertyUniquenessVerifier.VerifyFile(path);
                    Console.WriteLine($"JSON properties verified: {path}");
                }

                return 0;
            }

            if (options.VerifyOptions is not null)
            {
                var result = ReleaseManifestVerifier.Verify(options.VerifyOptions);
                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Release manifest verified for {result.ArtifactCount} artifact(s)."));
                Console.WriteLine($"Manifest: {options.VerifyOptions.ManifestPath}");
                if (options.VerifyOptions.ChecksumsPath is not null)
                {
                    Console.WriteLine($"Checksums: {options.VerifyOptions.ChecksumsPath}");
                }

                return 0;
            }

            var manifest = ReleaseManifestGenerator.Generate(options.GenerateOptions!);
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Release manifest generated for {manifest.Artifacts.Count} artifact(s)."));
            Console.WriteLine($"Manifest: {options.GenerateOptions!.ManifestPath}");
            Console.WriteLine($"Checksums: {options.GenerateOptions.ChecksumsPath}");
            if (options.GenerateOptions.NotesPath is not null)
            {
                Console.WriteLine($"Release notes: {options.GenerateOptions.NotesPath}");
            }

            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private const string UsageText = """
        Usage:
          OpenLineOps.ReleaseManifest --version <semver> --artifacts <directory> --output <release-manifest.json> --checksums <checksums.sha256> [--notes <release-notes.md>] [--commit <sha>] [--require-kind <kind>]...
          OpenLineOps.ReleaseManifest --verify --artifacts <directory> --manifest <release-manifest.json> [--checksums <checksums.sha256>] [--require-kind <kind>]...
          OpenLineOps.ReleaseManifest --verify-json <document.json> [--verify-json <document.json>]...

        Canonical artifact directories and kind values:
          source, api, agent, runner, plugin-host, script-worker, sample-plugin, desktop

        Example:
          dotnet run --project tools/OpenLineOps.ReleaseManifest/OpenLineOps.ReleaseManifest.csproj -- --version 0.1.0 --artifacts artifacts/release --output artifacts/release-manifest.json --checksums artifacts/checksums.sha256 --notes artifacts/release-notes.md --require-kind api --require-kind desktop
          dotnet run --project tools/OpenLineOps.ReleaseManifest/OpenLineOps.ReleaseManifest.csproj -- --verify --artifacts artifacts/release --manifest artifacts/release/release-manifest.json --checksums artifacts/release/checksums.sha256 --require-kind api --require-kind desktop
        """;

    private static bool TryParse(
        string[] args,
        string currentDirectory,
        out ParsedOptions options,
        out string error)
    {
        options = default!;
        error = string.Empty;
        var values = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                error = $"Unexpected argument '{argument}'.";
                return false;
            }

            var key = argument[2..];
            if (IsFlagOption(key))
            {
                flags.Add(key);
                continue;
            }

            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                error = $"Missing value for '{argument}'.";
                return false;
            }

            if (!values.TryGetValue(key, out var optionValues))
            {
                optionValues = [];
                values[key] = optionValues;
            }

            optionValues.Add(args[++index]);
        }

        var known = new HashSet<string>(
            ["version", "artifacts", "output", "manifest", "checksums", "notes", "commit", "require-kind", "verify-json"],
            StringComparer.OrdinalIgnoreCase);
        var unknown = values.Keys.FirstOrDefault(key => !known.Contains(key));
        if (unknown is not null)
        {
            error = $"Unknown option '--{unknown}'.";
            return false;
        }

        unknown = flags.FirstOrDefault(key => !IsFlagOption(key));
        if (unknown is not null)
        {
            error = $"Unknown option '--{unknown}'.";
            return false;
        }

        if (values.TryGetValue("verify-json", out var jsonPaths))
        {
            if (values.Count != 1 || flags.Count != 0 || jsonPaths.Count == 0
                || jsonPaths.Any(string.IsNullOrWhiteSpace))
            {
                error = "Option '--verify-json' cannot be combined with release generation or verification options.";
                return false;
            }

            options = new ParsedOptions(
                GenerateOptions: null,
                VerifyOptions: null,
                JsonVerificationPaths: jsonPaths
                    .Select(path => ResolvePath(currentDirectory, path))
                    .ToArray());
            return true;
        }

        if (!TryGetRequired(values, "artifacts", out var artifacts, out error))
        {
            return false;
        }

        var requiredArtifactKinds = values.TryGetValue("require-kind", out var kinds)
            ? kinds.ToArray()
            : [];

        if (flags.Contains("verify"))
        {
            if (!VerifyOnlyOptions(values, out error))
            {
                return false;
            }

            if (!TryGetRequired(values, "manifest", out var manifest, out error))
            {
                return false;
            }

            options = new ParsedOptions(
                GenerateOptions: null,
                VerifyOptions: new ReleaseManifestVerificationOptions(
                    ArtifactsDirectory: ResolvePath(currentDirectory, artifacts),
                    ManifestPath: ResolvePath(currentDirectory, manifest),
                    ChecksumsPath: TryGetSingle(values, "checksums", out var verifyChecksums)
                        ? ResolvePath(currentDirectory, verifyChecksums)
                        : null,
                    RequiredArtifactKinds: requiredArtifactKinds),
                JsonVerificationPaths: null);

            return true;
        }

        if (!GenerateOnlyOptions(values, out error))
        {
            return false;
        }

        if (!TryGetRequired(values, "version", out var version, out error)
            || !TryGetRequired(values, "output", out var output, out error)
            || !TryGetRequired(values, "checksums", out var checksums, out error))
        {
            return false;
        }

        options = new ParsedOptions(
            GenerateOptions: new ReleaseManifestOptions(
                Version: version,
                ArtifactsDirectory: ResolvePath(currentDirectory, artifacts),
                ManifestPath: ResolvePath(currentDirectory, output),
                ChecksumsPath: ResolvePath(currentDirectory, checksums),
                NotesPath: TryGetSingle(values, "notes", out var notes) ? ResolvePath(currentDirectory, notes) : null,
                Commit: TryGetSingle(values, "commit", out var commit) ? commit : null,
                GeneratedAtUtc: DateTimeOffset.UtcNow,
                RequiredArtifactKinds: requiredArtifactKinds),
            VerifyOptions: null,
            JsonVerificationPaths: null);

        return true;
    }

    private static bool GenerateOnlyOptions(
        Dictionary<string, List<string>> values,
        out string error)
    {
        var generateOptions = new HashSet<string>(
            ["version", "artifacts", "output", "checksums", "notes", "commit", "require-kind"],
            StringComparer.OrdinalIgnoreCase);
        var verifyOnly = values.Keys.FirstOrDefault(key => !generateOptions.Contains(key));
        if (verifyOnly is not null)
        {
            error = $"Option '--{verifyOnly}' is only valid with '--verify'.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool VerifyOnlyOptions(
        Dictionary<string, List<string>> values,
        out string error)
    {
        var verifyOptions = new HashSet<string>(
            ["artifacts", "manifest", "checksums", "require-kind"],
            StringComparer.OrdinalIgnoreCase);
        var generateOnly = values.Keys.FirstOrDefault(key => !verifyOptions.Contains(key));
        if (generateOnly is not null)
        {
            error = $"Option '--{generateOnly}' is not valid with '--verify'.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryGetRequired(
        Dictionary<string, List<string>> values,
        string key,
        out string value,
        out string error)
    {
        if (!TryGetSingle(values, key, out value) || string.IsNullOrWhiteSpace(value))
        {
            error = $"Missing required option '--{key}'.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryGetSingle(
        Dictionary<string, List<string>> values,
        string key,
        out string value)
    {
        if (!values.TryGetValue(key, out var optionValues) || optionValues.Count == 0)
        {
            value = string.Empty;
            return false;
        }

        value = optionValues[^1];
        return true;
    }

    private static string ResolvePath(string currentDirectory, string path)
    {
        return Path.GetFullPath(
            Path.IsPathRooted(path)
                ? path
                : Path.Combine(currentDirectory, path));
    }

    private static bool IsHelpArgument(string argument)
    {
        return string.Equals(argument, "--help", StringComparison.OrdinalIgnoreCase)
            || string.Equals(argument, "-h", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFlagOption(string key)
    {
        return string.Equals(key, "verify", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ParsedOptions(
        ReleaseManifestOptions? GenerateOptions,
        ReleaseManifestVerificationOptions? VerifyOptions,
        IReadOnlyList<string>? JsonVerificationPaths);
}
