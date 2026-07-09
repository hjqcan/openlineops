namespace OpenLineOps.BoundedContext.Scaffolder;

public static class ScaffolderCommand
{
    public static int Run(string[] args, string currentDirectory)
    {
        ArgumentNullException.ThrowIfNull(args);

        try
        {
            var options = BoundedContextScaffoldOptions.Parse(args, currentDirectory);
            if (options.ShowHelp)
            {
                Console.WriteLine(BoundedContextScaffoldOptions.HelpText);
                return 0;
            }

            var result = BoundedContextScaffoldGenerator.Generate(options);

            Console.WriteLine($"Generated OpenLineOps.{options.ContextName} bounded context scaffold.");
            Console.WriteLine("Module projects:");
            foreach (var projectPath in result.ModuleProjectPaths)
            {
                Console.WriteLine($"  {projectPath}");
            }

            Console.WriteLine($"Tests: {result.TestProjectPath}");

            if (options.UpdateSolution)
            {
                var updates = SolutionUpdater.Update(result, options);
                Console.WriteLine();
                Console.WriteLine("Updated solution files:");
                foreach (var update in updates)
                {
                    Console.WriteLine($"  {update.SolutionPath} ({update.SolutionFolder})");
                }
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine("Add projects to the solution with:");
                Console.WriteLine($"  dotnet sln OpenLineOps.sln add {ProjectList(result.ModuleProjectPaths)} --solution-folder modules");
                Console.WriteLine($"  dotnet sln OpenLineOps.sln add \"{result.TestProjectPath}\" --solution-folder tests");
            }

            return 0;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static string ProjectList(IReadOnlyList<string> projectPaths)
    {
        return string.Join(" ", projectPaths.Select(projectPath => $"\"{projectPath}\""));
    }
}
