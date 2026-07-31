namespace OpenLineOps.Plugins.Tests;

internal static class PluginTestScope
{
    public static ProjectApplicationWorkspaceScope Create() => new(
        "project.test",
        "application.test",
        Path.Combine(Path.GetTempPath(), "openlineops-plugin-scope"),
        "applications/test/test.oloapp");
}
