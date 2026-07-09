using System.Diagnostics;

namespace OpenLineOps.BoundedContext.Scaffolder;

public interface ISolutionCommandRunner
{
    int Run(string fileName, IReadOnlyList<string> arguments);
}

public sealed class DotnetSolutionCommandRunner : ISolutionCommandRunner
{
    public int Run(string fileName, IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start '{fileName}'.");
        process.WaitForExit();

        return process.ExitCode;
    }
}

public static class SolutionUpdater
{
    public static IReadOnlyList<SolutionUpdateResult> Update(
        BoundedContextScaffoldResult result,
        BoundedContextScaffoldOptions options,
        ISolutionCommandRunner? runner = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(options);

        if (options.SolutionPaths.Count == 0)
        {
            return [];
        }

        runner ??= new DotnetSolutionCommandRunner();

        var updates = new List<SolutionUpdateResult>(options.SolutionPaths.Count * 2);
        foreach (var solutionPath in options.SolutionPaths)
        {
            if (!File.Exists(solutionPath))
            {
                throw new InvalidOperationException($"Solution file '{solutionPath}' does not exist.");
            }

            AddProjects(solutionPath, "modules", result.ModuleProjectPaths, runner, updates);
            AddProjects(solutionPath, "tests", [result.TestProjectPath], runner, updates);
        }

        return updates;
    }

    private static void AddProjects(
        string solutionPath,
        string solutionFolder,
        IReadOnlyList<string> projectPaths,
        ISolutionCommandRunner runner,
        List<SolutionUpdateResult> updates)
    {
        var arguments = new List<string>(projectPaths.Count + 5)
        {
            "sln",
            solutionPath,
            "add"
        };
        arguments.AddRange(projectPaths);
        arguments.Add("--solution-folder");
        arguments.Add(solutionFolder);

        var exitCode = runner.Run("dotnet", arguments);
        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"dotnet sln add failed for '{solutionPath}' with exit code {exitCode}.");
        }

        updates.Add(new SolutionUpdateResult(solutionPath, solutionFolder, projectPaths.ToArray()));
    }
}

public sealed record SolutionUpdateResult(
    string SolutionPath,
    string SolutionFolder,
    IReadOnlyList<string> ProjectPaths);
