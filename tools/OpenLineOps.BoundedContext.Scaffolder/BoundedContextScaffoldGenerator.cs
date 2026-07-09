using System.Text;

namespace OpenLineOps.BoundedContext.Scaffolder;

public static class BoundedContextScaffoldGenerator
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static BoundedContextScaffoldResult Generate(BoundedContextScaffoldOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var model = new BoundedContextScaffoldModel(options);
        var files = TemplateCatalog.CreateFiles(model);
        var writtenFiles = new List<string>(files.Count);

        foreach (var file in files)
        {
            WriteFile(file.Path, file.Content, options.Overwrite);
            writtenFiles.Add(file.Path);
        }

        return new BoundedContextScaffoldResult(
            model.DomainSharedProjectPath,
            model.DomainProjectPath,
            model.ApplicationContractProjectPath,
            model.ApplicationProjectPath,
            model.InfraDataProjectPath,
            model.InfraCrossCuttingIoCProjectPath,
            model.ApiProjectPath,
            model.TestProjectPath,
            writtenFiles.ToArray());
    }

    private static void WriteFile(string path, string content, bool overwrite)
    {
        if (File.Exists(path) && !overwrite)
        {
            throw new InvalidOperationException(
                $"File '{path}' already exists. Re-run with --force to overwrite generated files.");
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, content, Utf8NoBom);
    }
}

public sealed record BoundedContextScaffoldResult(
    string DomainSharedProjectPath,
    string DomainProjectPath,
    string ApplicationContractProjectPath,
    string ApplicationProjectPath,
    string InfraDataProjectPath,
    string InfraCrossCuttingIoCProjectPath,
    string ApiProjectPath,
    string TestProjectPath,
    string[] WrittenFiles)
{
    public string InfrastructureProjectPath => InfraDataProjectPath;

    public IReadOnlyList<string> ModuleProjectPaths =>
    [
        DomainSharedProjectPath,
        DomainProjectPath,
        ApplicationContractProjectPath,
        ApplicationProjectPath,
        InfraDataProjectPath,
        InfraCrossCuttingIoCProjectPath,
        ApiProjectPath
    ];
}
