using System.Reflection;
using System.Xml.Linq;

namespace OpenLineOps.Api.Tests;

public sealed class DesktopProcessTreeLifetimeWiringTests
{
    [Fact]
    public void ApiAssemblyCarriesTheProcessIsolationRuntimeDependency()
    {
        Assert.Contains(
            typeof(Program).Assembly.GetReferencedAssemblies(),
            assembly => string.Equals(
                assembly.Name,
                "OpenLineOps.ProcessIsolation",
                StringComparison.Ordinal));
    }

    [Fact]
    public void DesktopHandshakeBindsTheCurrentProcessTreeBeforeHostConstruction()
    {
        var repositoryRoot = FindRepositoryRoot();
        var programPath = Path.Combine(repositoryRoot, "src", "OpenLineOps.Api", "Program.cs");
        var projectPath = Path.Combine(
            repositoryRoot,
            "src",
            "OpenLineOps.Api",
            "OpenLineOps.Api.csproj");
        var source = File.ReadAllText(programPath);
        var handshakeIndex = source.IndexOf(
            "DesktopProcessHandshake.FromEnvironment()",
            StringComparison.Ordinal);
        var bindingIndex = source.IndexOf(
            "WindowsCurrentProcessTreeLifetime.BindCurrentProcess()",
            StringComparison.Ordinal);
        var parentBindingIndex = source.IndexOf(
            "DesktopParentProcessLifetime.FromEnvironment(",
            StringComparison.Ordinal);
        var builderIndex = source.IndexOf(
            "WebApplication.CreateBuilder(args)",
            StringComparison.Ordinal);
        var buildIndex = source.IndexOf(
            "var app = builder.Build()",
            StringComparison.Ordinal);
        var parentMonitorIndex = source.IndexOf(
            "desktopParentProcessLifetime?.MonitorAsync(app.Lifetime)",
            StringComparison.Ordinal);
        var startIndex = source.IndexOf(
            "await app.StartAsync()",
            StringComparison.Ordinal);
        var keepAliveIndex = source.IndexOf(
            "GC.KeepAlive(desktopProcessTreeLifetime)",
            StringComparison.Ordinal);
        var shutdownIndex = source.IndexOf(
            "await app.WaitForShutdownAsync()",
            StringComparison.Ordinal);

        Assert.True(handshakeIndex >= 0, "Desktop handshake discovery is missing.");
        Assert.True(
            parentBindingIndex > handshakeIndex,
            "Desktop parent identity binding must follow handshake discovery.");
        Assert.True(
            bindingIndex > parentBindingIndex,
            "Desktop parent identity must be verified before process-tree binding.");
        Assert.True(
            bindingIndex > handshakeIndex,
            "Desktop process-tree binding must follow validated handshake discovery.");
        Assert.True(
            builderIndex > bindingIndex,
            "Desktop process-tree binding must happen before WebApplication host construction.");
        Assert.True(buildIndex > builderIndex, "The WebApplication build is missing.");
        Assert.True(
            parentMonitorIndex > buildIndex,
            "Desktop parent monitoring must begin after the host is built.");
        Assert.True(
            startIndex > parentMonitorIndex,
            "Desktop parent monitoring must begin before hosted-service startup.");
        Assert.True(
            shutdownIndex > startIndex,
            "The API shutdown wait is missing.");
        Assert.True(
            keepAliveIndex > shutdownIndex,
            "The process-tree lifetime must remain rooted through graceful host shutdown.");
        Assert.Contains(
            "desktopProcessHandshake is null",
            source,
            StringComparison.Ordinal);

        var project = XDocument.Load(projectPath);
        Assert.Contains(
            project.Descendants("ProjectReference"),
            reference => string.Equals(
                reference.Attribute("Include")?.Value.Replace('\\', '/'),
                "../../shared/OpenLineOps.ProcessIsolation/OpenLineOps.ProcessIsolation.csproj",
                StringComparison.Ordinal));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "OpenLineOps.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new DirectoryNotFoundException("OpenLineOps repository root was not found.");
    }
}
