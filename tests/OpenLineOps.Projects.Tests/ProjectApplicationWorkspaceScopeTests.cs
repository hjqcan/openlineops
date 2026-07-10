using System.Diagnostics;
using OpenLineOps.Application.Abstractions.ProjectWorkspaces;

namespace OpenLineOps.Projects.Tests;

public sealed class ProjectApplicationWorkspaceScopeTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "openlineops-application-scope-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void ExplicitApplicationProjectPathDefinesApplicationRoot()
    {
        var projectPath = Path.Combine(_testRoot, "project");

        var scope = new ProjectApplicationWorkspaceScope(
            "project.main",
            "application.main",
            projectPath,
            "applications/Main Line/MainLine.oloapp");

        Assert.Equal(
            "applications/Main Line/MainLine.oloapp",
            scope.ApplicationProjectRelativePath);
        Assert.Equal(
            Path.Combine(projectPath, "applications", "Main Line", "MainLine.oloapp"),
            scope.ApplicationProjectFilePath);
        Assert.Equal(
            Path.Combine(projectPath, "applications", "Main Line"),
            scope.ApplicationRootPath);
    }

    [Theory]
    [InlineData("applications/../Outside.oloapp")]
    [InlineData("applications/./Main.oloapp")]
    [InlineData("applications\\Main\\Main.oloapp")]
    [InlineData("C:/applications/Main/Main.oloapp")]
    [InlineData("applications/Main/Main.json")]
    [InlineData("applications/Nested/Deeper/Main.oloapp")]
    [InlineData("Main.oloapp")]
    [InlineData("applications//Main.oloapp")]
    [InlineData("")]
    [InlineData(" applications/Main/Main.oloapp")]
    [InlineData("applications/Main /Main.oloapp")]
    public void ExplicitApplicationProjectPathRejectsUnsafeOrInvalidValues(string relativePath)
    {
        Assert.ThrowsAny<ArgumentException>(() => new ProjectApplicationWorkspaceScope(
            "project.main",
            "application.main",
            Path.Combine(_testRoot, "project"),
            relativePath));
    }

    [Theory]
    [InlineData(" project.main", "application.main")]
    [InlineData("project.main ", "application.main")]
    [InlineData("project.main", " application.main")]
    [InlineData("project.main", "application.main ")]
    public void ScopeRejectsNonCanonicalIdentities(string projectId, string applicationId)
    {
        Assert.Throws<ArgumentException>(() => new ProjectApplicationWorkspaceScope(
            projectId,
            applicationId,
            Path.Combine(_testRoot, "project"),
            "applications/Main/Main.oloapp"));
    }

    [Fact]
    public void ScopeRejectsProjectPathWithOuterWhitespace()
    {
        Assert.Throws<ArgumentException>(() => new ProjectApplicationWorkspaceScope(
            "project.main",
            "application.main",
            $" {Path.Combine(_testRoot, "project")}",
            "applications/Main/Main.oloapp"));
    }

    [Fact]
    public void ScopeRejectsProjectPathThatIsAnExistingFile()
    {
        Directory.CreateDirectory(_testRoot);
        var projectPath = Path.Combine(_testRoot, "project-file");
        File.WriteAllText(projectPath, "not-a-project-directory");

        Assert.Throws<InvalidDataException>(() => new ProjectApplicationWorkspaceScope(
            "project.main",
            "application.main",
            projectPath,
            "applications/Main/Main.oloapp"));
    }

    [Fact]
    public void ScopeRejectsApplicationProjectFilePathThatIsAnExistingDirectory()
    {
        var projectPath = Path.Combine(_testRoot, "project");
        Directory.CreateDirectory(Path.Combine(
            projectPath,
            "applications",
            "Main",
            "Main.oloapp"));

        Assert.Throws<InvalidDataException>(() => new ProjectApplicationWorkspaceScope(
            "project.main",
            "application.main",
            projectPath,
            "applications/Main/Main.oloapp"));
    }

    [Fact]
    public void ScopeRejectsExistingFileInApplicationDirectoryChain()
    {
        var projectPath = Path.Combine(_testRoot, "project");
        Directory.CreateDirectory(projectPath);
        File.WriteAllText(Path.Combine(projectPath, "applications"), "not-a-directory");

        Assert.Throws<InvalidDataException>(() => new ProjectApplicationWorkspaceScope(
            "project.main",
            "application.main",
            projectPath,
            "applications/Main/Main.oloapp"));
    }

    [Fact]
    public void ScopeRejectsExistingReparsePointInApplicationDirectoryChain()
    {
        var projectPath = Path.Combine(_testRoot, "project");
        var targetPath = Path.Combine(_testRoot, "target");
        Directory.CreateDirectory(projectPath);
        Directory.CreateDirectory(targetPath);
        var applicationsPath = Path.Combine(projectPath, "applications");
        CreateDirectoryReparsePoint(applicationsPath, targetPath);

        try
        {
            Assert.Throws<InvalidDataException>(() => new ProjectApplicationWorkspaceScope(
                "project.main",
                "application.main",
                projectPath,
                "applications/Main/Main.oloapp"));
        }
        finally
        {
            Directory.Delete(applicationsPath);
        }
    }

    private static void CreateDirectoryReparsePoint(string path, string targetPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(path, targetPath);
            return;
        }

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList =
            {
                "/d",
                "/c",
                "mklink",
                "/J",
                path,
                targetPath
            }
        }) ?? throw new InvalidOperationException("Failed to start the Windows junction command.");

        process.WaitForExit();
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        Assert.True(
            process.ExitCode == 0,
            $"Failed to create test junction. stdout: {standardOutput} stderr: {standardError}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }
}
