namespace OpenLineOps.Agent.Tests;

public sealed class AppContainerPythonRuntimeTestSupportTests
{
    [Fact]
    public void MaterializeRuntimeTreeCopiesContainedFileAliasAsRegularFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new RuntimeTreeFixture();
        var target = fixture.WriteRuntimeFile("python.exe", "python-runtime");
        var alias = fixture.CreateRuntimeFileAlias("python3.exe", Path.GetFileName(target));

        fixture.MaterializeRuntimeTree();

        var copiedAlias = Path.Combine(fixture.DestinationRoot, Path.GetFileName(alias));
        Assert.True(File.Exists(copiedAlias));
        Assert.Equal("python-runtime", File.ReadAllText(copiedAlias));
        Assert.Equal(
            0,
            (int)(File.GetAttributes(copiedAlias) & FileAttributes.ReparsePoint));
    }

    [Fact]
    public void MaterializeRuntimeTreeRejectsFileAliasOutsideRuntimeRoot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new RuntimeTreeFixture();
        var outside = fixture.WriteOutsideFile("outside.exe", "must-not-be-copied");
        fixture.CreateRuntimeFileAlias("python3.exe", outside);

        var exception = Assert.Throws<InvalidDataException>(() =>
            fixture.MaterializeRuntimeTree());

        Assert.Contains(
            "cannot leave its runtime root",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Equal("must-not-be-copied", File.ReadAllText(outside));
        Assert.False(File.Exists(Path.Combine(fixture.DestinationRoot, "python3.exe")));
    }

    [Fact]
    public void MaterializeRuntimeTreeRejectsDirectoryReparseTraversal()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new RuntimeTreeFixture();
        var outside = fixture.WriteOutsideFile("outside.py", "must-not-be-copied");
        fixture.CreateRuntimeDirectoryReparsePoint("redirected");

        var exception = Assert.Throws<InvalidDataException>(() =>
            fixture.MaterializeRuntimeTree());

        Assert.Contains(
            "cannot contain a reparse directory",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Equal("must-not-be-copied", File.ReadAllText(outside));
        Assert.False(File.Exists(Path.Combine(fixture.DestinationRoot, "redirected", "outside.py")));
    }

    private sealed class RuntimeTreeFixture : IDisposable
    {
        private readonly Dictionary<string, string> _fileAliasTargets =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _reparseDirectories =
            new(StringComparer.OrdinalIgnoreCase);

        public RuntimeTreeFixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "OpenLineOps.Tests",
                "PythonRuntimeAliases",
                Guid.NewGuid().ToString("N"));
            RuntimeRoot = Path.Combine(Root, "runtime");
            DestinationRoot = Path.Combine(Root, "materialized");
            OutsideRoot = Path.Combine(Root, "outside");
            Directory.CreateDirectory(RuntimeRoot);
            Directory.CreateDirectory(OutsideRoot);
        }

        public string Root { get; }

        public string RuntimeRoot { get; }

        public string DestinationRoot { get; }

        public string OutsideRoot { get; }

        public string WriteRuntimeFile(string relativePath, string content) =>
            WriteFile(RuntimeRoot, relativePath, content);

        public string WriteOutsideFile(string relativePath, string content) =>
            WriteFile(OutsideRoot, relativePath, content);

        public string CreateRuntimeFileAlias(string relativePath, string targetPath)
        {
            var aliasPath = WriteRuntimeFile(relativePath, "file-alias-placeholder");
            var target = Path.IsPathFullyQualified(targetPath)
                ? targetPath
                : Path.Combine(Path.GetDirectoryName(aliasPath)!, targetPath);
            _fileAliasTargets.Add(
                Path.GetFullPath(aliasPath),
                Path.GetFullPath(target));
            return aliasPath;
        }

        public string CreateRuntimeDirectoryReparsePoint(string relativePath)
        {
            var path = Path.Combine(RuntimeRoot, relativePath);
            Directory.CreateDirectory(path);
            _reparseDirectories.Add(Path.GetFullPath(path));
            return path;
        }

        public void MaterializeRuntimeTree()
        {
            AppContainerPythonRuntimeTestSupport.MaterializeRuntimeTree(
                RuntimeRoot,
                DestinationRoot,
                GetAttributes,
                ResolveFileLinkTarget);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private FileAttributes GetAttributes(string path)
        {
            var canonicalPath = Path.GetFullPath(path);
            var attributes = File.GetAttributes(canonicalPath);
            return _fileAliasTargets.ContainsKey(canonicalPath)
                   || _reparseDirectories.Contains(canonicalPath)
                ? attributes | FileAttributes.ReparsePoint
                : attributes;
        }

        private FileSystemInfo? ResolveFileLinkTarget(string path) =>
            _fileAliasTargets.TryGetValue(Path.GetFullPath(path), out var target)
                ? new FileInfo(target)
                : File.ResolveLinkTarget(path, returnFinalTarget: false);

        private static string WriteFile(string root, string relativePath, string content)
        {
            var path = Path.Combine(root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }
    }
}
