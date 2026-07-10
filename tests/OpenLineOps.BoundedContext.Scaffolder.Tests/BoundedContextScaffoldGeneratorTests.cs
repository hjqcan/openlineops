using OpenLineOps.BoundedContext.Scaffolder;

namespace OpenLineOps.BoundedContext.Scaffolder.Tests;

public sealed class BoundedContextScaffoldGeneratorTests
{
    [Fact]
    public void GenerateCreatesModularDddProjectsWithDataCoreEfPersistence()
    {
        using var workspace = TemporaryWorkspace.Create();
        var options = workspace.CreateOptions("Quality", "InspectionPlan");

        var result = BoundedContextScaffoldGenerator.Generate(options);

        Assert.Equal(33, result.WrittenFiles.Length);
        Assert.True(File.Exists(result.DomainSharedProjectPath));
        Assert.True(File.Exists(result.DomainProjectPath));
        Assert.True(File.Exists(result.ApplicationContractProjectPath));
        Assert.True(File.Exists(result.ApplicationProjectPath));
        Assert.True(File.Exists(result.InfraDataProjectPath));
        Assert.True(File.Exists(result.InfraCrossCuttingIoCProjectPath));
        Assert.True(File.Exists(result.ApiProjectPath));
        Assert.True(File.Exists(result.TestProjectPath));

        var aggregate = File.ReadAllText(Path.Combine(
            workspace.ModuleRoot,
            "OpenLineOps.Quality.Domain",
            "Aggregates",
            "InspectionPlan.cs"));
        var repositoryPort = File.ReadAllText(Path.Combine(
            workspace.ModuleRoot,
            "OpenLineOps.Quality.Domain",
            "Repositories",
            "IInspectionPlanRepository.cs"));
        var repository = File.ReadAllText(Path.Combine(
            workspace.ModuleRoot,
            "OpenLineOps.Quality.Infra.Data",
            "Persistence",
            "EfInspectionPlanRepository.cs"));
        var infraDataProject = File.ReadAllText(result.InfraDataProjectPath);
        var dbContext = File.ReadAllText(Path.Combine(
            workspace.ModuleRoot,
            "OpenLineOps.Quality.Infra.Data",
            "Persistence",
            "QualityDbContext.cs"));
        var designTimeFactory = File.ReadAllText(Path.Combine(
            workspace.ModuleRoot,
            "OpenLineOps.Quality.Infra.Data",
            "Persistence",
            "QualityDbContextDesignTimeFactory.cs"));
        var initialMigration = File.ReadAllText(Path.Combine(
            workspace.ModuleRoot,
            "OpenLineOps.Quality.Infra.Data",
            "Persistence",
            "Migrations",
            "20260630000000_InitialQualityInspectionPlanSqlite.cs"));
        var modelSnapshot = File.ReadAllText(Path.Combine(
            workspace.ModuleRoot,
            "OpenLineOps.Quality.Infra.Data",
            "Persistence",
            "Migrations",
            "QualityDbContextModelSnapshot.cs"));
        var appContract = File.ReadAllText(Path.Combine(
            workspace.ModuleRoot,
            "OpenLineOps.Quality.Application.Contract",
            "Services",
            "IInspectionPlanAppService.cs"));
        var ioc = File.ReadAllText(Path.Combine(
            workspace.ModuleRoot,
            "OpenLineOps.Quality.Infra.CrossCutting.IoC",
            "DependencyInjection",
            "QualityNativeInjectorBootStrapper.cs"));
        var api = File.ReadAllText(Path.Combine(
            workspace.ModuleRoot,
            "OpenLineOps.Quality.Api",
            "Controllers",
            "InspectionPlansController.cs"));
        var testProject = File.ReadAllText(result.TestProjectPath);
        var persistenceTests = File.ReadAllText(Path.Combine(
            workspace.TestRoot,
            "OpenLineOps.Quality.Tests",
            "InspectionPlanPersistenceTests.cs"));
        var appServiceTests = File.ReadAllText(Path.Combine(
            workspace.TestRoot,
            "OpenLineOps.Quality.Tests",
            "InspectionPlanAppServiceTests.cs"));

        Assert.Contains("AggregateRoot<InspectionPlanId>", aggregate, StringComparison.Ordinal);
        Assert.Contains("IAggregateRepository<InspectionPlan, InspectionPlanId>", repositoryPort, StringComparison.Ordinal);
        Assert.Contains("BaseRepository<QualityDbContext, InspectionPlan, InspectionPlanId>", repository, StringComparison.Ordinal);
        Assert.Contains("IntegrationEventPublicationPolicy? integrationEventPublicationPolicy = null", dbContext, StringComparison.Ordinal);
        Assert.Contains("IIntegrationEventPublisher? integrationEventPublisher = null", dbContext, StringComparison.Ordinal);
        Assert.Contains("ITransactionalIntegrationEventPublisher? transactionalIntegrationEventPublisher = null", dbContext, StringComparison.Ordinal);
        Assert.Contains("IIntegrationEventTransactionCoordinator? integrationEventTransactionCoordinator = null", dbContext, StringComparison.Ordinal);
        Assert.Contains("integrationEventPublicationPolicy", dbContext, StringComparison.Ordinal);
        Assert.Contains("integrationEventTransactionCoordinator: integrationEventTransactionCoordinator", dbContext, StringComparison.Ordinal);
        Assert.Contains("IDesignTimeDbContextFactory<QualityDbContext>", designTimeFactory, StringComparison.Ordinal);
        Assert.Contains("openlineops-quality-design-time.sqlite", designTimeFactory, StringComparison.Ordinal);
        Assert.Contains("InitialQualityInspectionPlanSqlite", initialMigration, StringComparison.Ordinal);
        Assert.Contains("migrationBuilder.CreateTable", initialMigration, StringComparison.Ordinal);
        Assert.Contains("QualityDbContextModelSnapshot", modelSnapshot, StringComparison.Ordinal);
        Assert.Contains("IInspectionPlanAppService", appContract, StringComparison.Ordinal);
        Assert.Contains("TryAddSingleton<IntegrationDtoConverterRegistry>", ioc, StringComparison.Ordinal);
        Assert.Contains("IIntegrationDtoConverter, InspectionPlanIntegrationDtoConverter", ioc, StringComparison.Ordinal);
        Assert.Contains("AddOpenLineOpsQualityModule", ioc, StringComparison.Ordinal);
        Assert.Contains("[Route(\"api/quality/inspection-plans\")]", api, StringComparison.Ordinal);
        Assert.Contains("Microsoft.EntityFrameworkCore.Design", infraDataProject, StringComparison.Ordinal);
        Assert.Contains("Microsoft.EntityFrameworkCore.Sqlite", infraDataProject, StringComparison.Ordinal);
        Assert.Contains("OpenLineOps.Infrastructure.Data.Core.csproj", testProject, StringComparison.Ordinal);
        Assert.Contains("MigrateAsync", persistenceTests, StringComparison.Ordinal);
        Assert.Contains("GetPendingMigrationsAsync", persistenceTests, StringComparison.Ordinal);
        Assert.DoesNotContain("EnsureCreatedAsync", persistenceTests, StringComparison.Ordinal);
        Assert.Contains("MigrateAsync", appServiceTests, StringComparison.Ordinal);
        Assert.DoesNotContain("EnsureCreatedAsync", appServiceTests, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerateRefusesToOverwriteExistingFilesUnlessForced()
    {
        using var workspace = TemporaryWorkspace.Create();
        var options = workspace.CreateOptions("Quality", "InspectionPlan");

        BoundedContextScaffoldGenerator.Generate(options);

        var ex = Assert.Throws<InvalidOperationException>(() => BoundedContextScaffoldGenerator.Generate(options));
        Assert.Contains("--force", ex.Message, StringComparison.Ordinal);

        var forcedOptions = options with { Overwrite = true };
        var result = BoundedContextScaffoldGenerator.Generate(forcedOptions);

        Assert.NotEmpty(result.WrittenFiles);
    }

    [Theory]
    [InlineData("--context", "Quality", "--aggregate", "InspectionPlan")]
    [InlineData("--context=Quality", "--aggregate=InspectionPlan")]
    public void ParseAcceptsSupportedArgumentForms(params string[] args)
    {
        using var workspace = TemporaryWorkspace.Create();

        var options = BoundedContextScaffoldOptions.Parse(args, workspace.RepoRoot);

        Assert.Equal("Quality", options.ContextName);
        Assert.Equal("InspectionPlan", options.AggregateName);
        Assert.Equal(Path.Combine(workspace.RepoRoot, "modules"), options.ModuleRoot);
        Assert.Equal(Path.Combine(workspace.RepoRoot, "tests"), options.TestRoot);
    }

    [Fact]
    public void ParseAcceptsSolutionUpdateOptions()
    {
        using var workspace = TemporaryWorkspace.Create();
        var firstSolution = Path.Combine(workspace.Directory, "OpenLineOps.sln");
        var secondSolution = Path.Combine(workspace.Directory, "OpenLineOps.slnx");

        Directory.CreateDirectory(workspace.Directory);
        File.WriteAllText(firstSolution, string.Empty);
        File.WriteAllText(secondSolution, string.Empty);

        var options = BoundedContextScaffoldOptions.Parse(
            [
                "--context",
                "Quality",
                "--aggregate",
                "InspectionPlan",
                "--repo-root",
                workspace.Directory,
                "--solution",
                "OpenLineOps.sln",
                "--solution=OpenLineOps.slnx"
            ],
            workspace.RepoRoot);

        Assert.True(options.UpdateSolution);
        Assert.Equal([firstSolution, secondSolution], options.SolutionPaths);
    }

    [Fact]
    public void SolutionUpdaterAddsModuleAndTestProjectsToEachSolution()
    {
        using var workspace = TemporaryWorkspace.Create();
        var solutionPath = Path.Combine(workspace.Directory, "OpenLineOps.sln");
        Directory.CreateDirectory(workspace.Directory);
        File.WriteAllText(solutionPath, string.Empty);

        var options = workspace.CreateOptions("Quality", "InspectionPlan") with
        {
            UpdateSolution = true,
            SolutionPaths = [solutionPath]
        };
        var result = BoundedContextScaffoldGenerator.Generate(options);
        var runner = new CapturingSolutionCommandRunner();

        var updates = SolutionUpdater.Update(result, options, runner);

        Assert.Equal(2, updates.Count);
        Assert.Equal(2, runner.Calls.Count);
        Assert.Contains(result.DomainSharedProjectPath, runner.Calls[0].Arguments);
        Assert.Contains(result.ApiProjectPath, runner.Calls[0].Arguments);
        Assert.Contains("modules", runner.Calls[0].Arguments);
        Assert.Contains(result.TestProjectPath, runner.Calls[1].Arguments);
        Assert.Contains("tests", runner.Calls[1].Arguments);
    }

    [Theory]
    [InlineData("quality")]
    [InlineData("Quality-Inspection")]
    [InlineData("Quality_Inspection")]
    public void ParseRejectsNonPascalIdentifiers(string contextName)
    {
        using var workspace = TemporaryWorkspace.Create();

        var ex = Assert.Throws<ArgumentException>(() => BoundedContextScaffoldOptions.Parse(
            ["--context", contextName, "--aggregate", "InspectionPlan"],
            workspace.RepoRoot));

        Assert.Contains("PascalCase", ex.Message, StringComparison.Ordinal);
    }

    private sealed class CapturingSolutionCommandRunner : ISolutionCommandRunner
    {
        public List<SolutionCommandCall> Calls { get; } = [];

        public int Run(string fileName, IReadOnlyList<string> arguments)
        {
            Calls.Add(new SolutionCommandCall(fileName, arguments.ToArray()));
            return 0;
        }
    }

    private sealed record SolutionCommandCall(string FileName, IReadOnlyList<string> Arguments);

    private sealed class TemporaryWorkspace : IDisposable
    {
        private TemporaryWorkspace(string directory, string repoRoot, string moduleRoot, string testRoot)
        {
            Directory = directory;
            RepoRoot = repoRoot;
            ModuleRoot = moduleRoot;
            TestRoot = testRoot;
        }

        public string Directory { get; }

        public string RepoRoot { get; }

        public string ModuleRoot { get; }

        public string TestRoot { get; }

        public static TemporaryWorkspace Create()
        {
            var directory = Path.Combine(Path.GetTempPath(), "OpenLineOps", "Scaffolder", Guid.NewGuid().ToString("N"));
            var repoRoot = FindRepoRoot();

            return new TemporaryWorkspace(
                directory,
                repoRoot,
                Path.Combine(directory, "modules"),
                Path.Combine(directory, "tests"));
        }

        public BoundedContextScaffoldOptions CreateOptions(string contextName, string aggregateName)
        {
            return new BoundedContextScaffoldOptions(
                contextName,
                aggregateName,
                RepoRoot,
                ModuleRoot,
                TestRoot,
                overwrite: false);
        }

        public void Dispose()
        {
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
        }

        private static string FindRepoRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Directory.Packages.props")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException("Could not locate repository root.");
        }
    }
}
