namespace OpenLineOps.BoundedContext.Scaffolder;

public sealed record BoundedContextScaffoldOptions
{
    public BoundedContextScaffoldOptions(
        string contextName,
        string aggregateName,
        string repoRoot,
        string moduleRoot,
        string testRoot,
        bool overwrite,
        bool updateSolution = false,
        IReadOnlyList<string>? solutionPaths = null,
        bool showHelp = false)
    {
        ContextName = contextName;
        AggregateName = aggregateName;
        RepoRoot = repoRoot;
        ModuleRoot = moduleRoot;
        TestRoot = testRoot;
        Overwrite = overwrite;
        UpdateSolution = updateSolution;
        SolutionPaths = solutionPaths?.ToArray() ?? [];
        ShowHelp = showHelp;
    }

    public const string HelpText = """
        OpenLineOps bounded-context scaffolder

        Usage:
          dotnet run --project tools/OpenLineOps.BoundedContext.Scaffolder -- \
            --context Quality --aggregate InspectionPlan [--repo-root .] [--module-root modules] [--test-root tests] [--force] [--update-solution]

        Options:
          --context          Bounded context name, for example Quality.
          --aggregate        Aggregate root name, for example InspectionPlan.
          --repo-root        Repository root. Defaults to the current directory.
          --module-root      Module output root. Defaults to <repo-root>/modules.
          --test-root        Test output root. Defaults to <repo-root>/tests.
          --solution         Solution file to update. Can be repeated. Relative paths resolve from repo root.
          --update-solution  Add generated projects to OpenLineOps.sln/OpenLineOps.slnx, or to --solution paths.
          --force            Overwrite generated files if they already exist.
          --help             Show this help text.
        """;

    public string ContextName { get; init; }

    public string AggregateName { get; init; }

    public string RepoRoot { get; init; }

    public string ModuleRoot { get; init; }

    public string TestRoot { get; init; }

    public bool Overwrite { get; init; }

    public bool UpdateSolution { get; init; }

    public IReadOnlyList<string> SolutionPaths { get; init; }

    public bool ShowHelp { get; init; }

    public static BoundedContextScaffoldOptions Parse(string[] args, string currentDirectory)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Any(argument => string.Equals(argument, "--help", StringComparison.OrdinalIgnoreCase)
            || string.Equals(argument, "-h", StringComparison.OrdinalIgnoreCase)))
        {
            var root = FullPath(currentDirectory, currentDirectory);
            return new BoundedContextScaffoldOptions(
                "Help",
                "HelpAggregate",
                root,
                Path.Combine(root, "modules"),
                Path.Combine(root, "tests"),
                overwrite: false,
                showHelp: true);
        }

        var values = ReadArguments(args);
        var repoRoot = FullPath(
            FirstValue(values, "repo-root") ?? currentDirectory,
            currentDirectory);
        var moduleRoot = FullPath(
            FirstValue(values, "module-root") ?? Path.Combine(repoRoot, "modules"),
            repoRoot);
        var testRoot = FullPath(
            FirstValue(values, "test-root") ?? Path.Combine(repoRoot, "tests"),
            repoRoot);
        var contextName = RequireIdentifier(values, "context");
        var aggregateName = RequireIdentifier(values, "aggregate");
        var solutionPaths = ResolveSolutionPaths(values, repoRoot);
        var updateSolution = HasFlag(values, "update-solution") || solutionPaths.Length > 0;

        return new BoundedContextScaffoldOptions(
            contextName,
            aggregateName,
            repoRoot,
            moduleRoot,
            testRoot,
            HasFlag(values, "force"),
            updateSolution,
            solutionPaths);
    }

    private static Dictionary<string, List<string?>> ReadArguments(string[] args)
    {
        var values = new Dictionary<string, List<string?>>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unexpected argument '{argument}'. Options must start with '--'.");
            }

            var option = argument[2..];
            var separator = option.IndexOf('=', StringComparison.Ordinal);
            if (separator >= 0)
            {
                AddValue(values, option[..separator], option[(separator + 1)..]);
                continue;
            }

            if (IsFlag(option))
            {
                AddValue(values, option, null);
                continue;
            }

            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Option '--{option}' requires a value.");
            }

            AddValue(values, option, args[++index]);
        }

        return values;
    }

    private static bool IsFlag(string option)
    {
        return string.Equals(option, "force", StringComparison.OrdinalIgnoreCase)
            || string.Equals(option, "update-solution", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddValue(
        Dictionary<string, List<string?>> values,
        string option,
        string? value)
    {
        if (!values.TryGetValue(option, out var optionValues))
        {
            optionValues = [];
            values.Add(option, optionValues);
        }

        optionValues.Add(value);
    }

    private static bool HasFlag(Dictionary<string, List<string?>> values, string optionName)
    {
        return values.ContainsKey(optionName);
    }

    private static string? FirstValue(Dictionary<string, List<string?>> values, string optionName)
    {
        return values.TryGetValue(optionName, out var optionValues)
            ? optionValues.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            : null;
    }

    private static string[] ResolveSolutionPaths(
        Dictionary<string, List<string?>> values,
        string repoRoot)
    {
        if (values.TryGetValue("solution", out var explicitValues))
        {
            return explicitValues
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => FullPath(value!, repoRoot))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        if (!HasFlag(values, "update-solution"))
        {
            return [];
        }

        var candidates = new[]
        {
            Path.Combine(repoRoot, "OpenLineOps.sln"),
            Path.Combine(repoRoot, "OpenLineOps.slnx")
        };
        var existingSolutions = candidates
            .Where(File.Exists)
            .ToArray();

        if (existingSolutions.Length == 0)
        {
            throw new InvalidOperationException(
                "No default solution files were found. Pass --solution <path> or omit --update-solution.");
        }

        return existingSolutions;
    }

    private static string RequireIdentifier(
        Dictionary<string, List<string?>> values,
        string optionName)
    {
        var value = FirstValue(values, optionName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Option '--{optionName}' is required.");
        }

        var normalized = value.Trim();
        if (!IsPascalIdentifier(normalized))
        {
            throw new ArgumentException(
                $"Option '--{optionName}' must be a PascalCase C# identifier without punctuation.");
        }

        return normalized;
    }

    private static bool IsPascalIdentifier(string value)
    {
        if (value.Length == 0 || !char.IsUpper(value[0]))
        {
            return false;
        }

        return value.All(char.IsLetterOrDigit);
    }

    private static string FullPath(string path, string basePath)
    {
        return Path.GetFullPath(
            Path.IsPathRooted(path)
                ? path
                : Path.Combine(basePath, path));
    }
}
