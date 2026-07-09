namespace OpenLineOps.BoundedContext.Scaffolder;

internal sealed class BoundedContextScaffoldModel
{
    public BoundedContextScaffoldModel(BoundedContextScaffoldOptions options)
    {
        ContextName = options.ContextName;
        AggregateName = options.AggregateName;
        AggregateIdName = $"{AggregateName}Id";
        OperationResultName = $"{ContextName}OperationResult";
        ApplicationResultName = $"{ContextName}ApplicationResult";
        DomainSharedProjectName = $"OpenLineOps.{ContextName}.Domain.Shared";
        DomainProjectName = $"OpenLineOps.{ContextName}.Domain";
        ApplicationContractProjectName = $"OpenLineOps.{ContextName}.Application.Contract";
        ApplicationProjectName = $"OpenLineOps.{ContextName}.Application";
        InfraDataProjectName = $"OpenLineOps.{ContextName}.Infra.Data";
        InfraCrossCuttingIoCProjectName = $"OpenLineOps.{ContextName}.Infra.CrossCutting.IoC";
        ApiProjectName = $"OpenLineOps.{ContextName}.Api";
        TestProjectName = $"OpenLineOps.{ContextName}.Tests";
        DomainSharedProjectDirectory = Path.Combine(options.ModuleRoot, DomainSharedProjectName);
        DomainProjectDirectory = Path.Combine(options.ModuleRoot, DomainProjectName);
        ApplicationContractProjectDirectory = Path.Combine(options.ModuleRoot, ApplicationContractProjectName);
        ApplicationProjectDirectory = Path.Combine(options.ModuleRoot, ApplicationProjectName);
        InfraDataProjectDirectory = Path.Combine(options.ModuleRoot, InfraDataProjectName);
        InfraCrossCuttingIoCProjectDirectory = Path.Combine(options.ModuleRoot, InfraCrossCuttingIoCProjectName);
        ApiProjectDirectory = Path.Combine(options.ModuleRoot, ApiProjectName);
        TestProjectDirectory = Path.Combine(options.TestRoot, TestProjectName);
        DomainSharedProjectPath = Path.Combine(DomainSharedProjectDirectory, $"{DomainSharedProjectName}.csproj");
        DomainProjectPath = Path.Combine(DomainProjectDirectory, $"{DomainProjectName}.csproj");
        ApplicationContractProjectPath = Path.Combine(
            ApplicationContractProjectDirectory,
            $"{ApplicationContractProjectName}.csproj");
        ApplicationProjectPath = Path.Combine(ApplicationProjectDirectory, $"{ApplicationProjectName}.csproj");
        InfraDataProjectPath = Path.Combine(InfraDataProjectDirectory, $"{InfraDataProjectName}.csproj");
        InfraCrossCuttingIoCProjectPath = Path.Combine(
            InfraCrossCuttingIoCProjectDirectory,
            $"{InfraCrossCuttingIoCProjectName}.csproj");
        ApiProjectPath = Path.Combine(ApiProjectDirectory, $"{ApiProjectName}.csproj");
        TestProjectPath = Path.Combine(TestProjectDirectory, $"{TestProjectName}.csproj");

        DomainAbstractionsProjectReference = ProjectReference(
            DomainProjectDirectory,
            Path.Combine(options.RepoRoot, "shared", "OpenLineOps.Domain.Abstractions", "OpenLineOps.Domain.Abstractions.csproj"));
        ApiAbstractionsProjectReference = ProjectReference(
            ApiProjectDirectory,
            Path.Combine(options.RepoRoot, "shared", "OpenLineOps.Api.Abstractions", "OpenLineOps.Api.Abstractions.csproj"));
        InfraDataCoreProjectReference = ProjectReference(
            InfraDataProjectDirectory,
            Path.Combine(options.RepoRoot, "shared", "OpenLineOps.Infrastructure.Data.Core", "OpenLineOps.Infrastructure.Data.Core.csproj"));
        TestDataCoreProjectReference = ProjectReference(
            TestProjectDirectory,
            Path.Combine(options.RepoRoot, "shared", "OpenLineOps.Infrastructure.Data.Core", "OpenLineOps.Infrastructure.Data.Core.csproj"));
        DomainToDomainSharedProjectReference = ProjectReference(DomainProjectDirectory, DomainSharedProjectPath);
        ApplicationContractToDomainSharedProjectReference = ProjectReference(
            ApplicationContractProjectDirectory,
            DomainSharedProjectPath);
        ApplicationToApplicationContractProjectReference = ProjectReference(
            ApplicationProjectDirectory,
            ApplicationContractProjectPath);
        ApplicationToDomainProjectReference = ProjectReference(ApplicationProjectDirectory, DomainProjectPath);
        ApplicationToDomainSharedProjectReference = ProjectReference(ApplicationProjectDirectory, DomainSharedProjectPath);
        InfraDataToDomainProjectReference = ProjectReference(InfraDataProjectDirectory, DomainProjectPath);
        InfraCrossCuttingIoCToApplicationContractProjectReference = ProjectReference(
            InfraCrossCuttingIoCProjectDirectory,
            ApplicationContractProjectPath);
        InfraCrossCuttingIoCToApplicationProjectReference = ProjectReference(
            InfraCrossCuttingIoCProjectDirectory,
            ApplicationProjectPath);
        InfraCrossCuttingIoCToDomainProjectReference = ProjectReference(
            InfraCrossCuttingIoCProjectDirectory,
            DomainProjectPath);
        InfraCrossCuttingIoCToInfraDataProjectReference = ProjectReference(
            InfraCrossCuttingIoCProjectDirectory,
            InfraDataProjectPath);
        ApiToApplicationContractProjectReference = ProjectReference(ApiProjectDirectory, ApplicationContractProjectPath);
        ApiToInfraCrossCuttingIoCProjectReference = ProjectReference(
            ApiProjectDirectory,
            InfraCrossCuttingIoCProjectPath);
        TestToApplicationContractProjectReference = ProjectReference(TestProjectDirectory, ApplicationContractProjectPath);
        TestToApplicationProjectReference = ProjectReference(TestProjectDirectory, ApplicationProjectPath);
        TestToDomainProjectReference = ProjectReference(TestProjectDirectory, DomainProjectPath);
        TestToInfraDataProjectReference = ProjectReference(TestProjectDirectory, InfraDataProjectPath);

        TableName = ToSnakeCase($"{ContextName}{AggregateName}");
        EventName = $"{ContextName}.{AggregateName}.Created";
        AggregateSampleId = $"{ToKebabCase(ContextName)}.{ToKebabCase(AggregateName)}.sample.v1";
        AggregateDisplayName = $"{SplitPascalCase(AggregateName)} Sample";
        AggregatePluralName = $"{AggregateName}s";
        ContextRouteSegment = ToKebabCase(ContextName);
        AggregateRouteSegment = ToKebabCase(AggregateName);
        ApiGroupName = $"{ContextRouteSegment}-v1";
    }

    public string ContextName { get; }

    public string AggregateName { get; }

    public string AggregateIdName { get; }

    public string OperationResultName { get; }

    public string ApplicationResultName { get; }

    public string DomainSharedProjectName { get; }

    public string DomainProjectName { get; }

    public string ApplicationContractProjectName { get; }

    public string ApplicationProjectName { get; }

    public string InfraDataProjectName { get; }

    public string InfraCrossCuttingIoCProjectName { get; }

    public string ApiProjectName { get; }

    public string TestProjectName { get; }

    public string DomainSharedProjectDirectory { get; }

    public string DomainProjectDirectory { get; }

    public string ApplicationContractProjectDirectory { get; }

    public string ApplicationProjectDirectory { get; }

    public string InfraDataProjectDirectory { get; }

    public string InfraCrossCuttingIoCProjectDirectory { get; }

    public string ApiProjectDirectory { get; }

    public string TestProjectDirectory { get; }

    public string DomainSharedProjectPath { get; }

    public string DomainProjectPath { get; }

    public string ApplicationContractProjectPath { get; }

    public string ApplicationProjectPath { get; }

    public string InfraDataProjectPath { get; }

    public string InfraCrossCuttingIoCProjectPath { get; }

    public string ApiProjectPath { get; }

    public string TestProjectPath { get; }

    public string DomainAbstractionsProjectReference { get; }

    public string ApiAbstractionsProjectReference { get; }

    public string InfraDataCoreProjectReference { get; }

    public string TestDataCoreProjectReference { get; }

    public string DomainToDomainSharedProjectReference { get; }

    public string ApplicationContractToDomainSharedProjectReference { get; }

    public string ApplicationToApplicationContractProjectReference { get; }

    public string ApplicationToDomainProjectReference { get; }

    public string ApplicationToDomainSharedProjectReference { get; }

    public string InfraDataToDomainProjectReference { get; }

    public string InfraCrossCuttingIoCToApplicationContractProjectReference { get; }

    public string InfraCrossCuttingIoCToApplicationProjectReference { get; }

    public string InfraCrossCuttingIoCToDomainProjectReference { get; }

    public string InfraCrossCuttingIoCToInfraDataProjectReference { get; }

    public string ApiToApplicationContractProjectReference { get; }

    public string ApiToInfraCrossCuttingIoCProjectReference { get; }

    public string TestToApplicationContractProjectReference { get; }

    public string TestToApplicationProjectReference { get; }

    public string TestToDomainProjectReference { get; }

    public string TestToInfraDataProjectReference { get; }

    public string TableName { get; }

    public string EventName { get; }

    public string AggregateSampleId { get; }

    public string AggregateDisplayName { get; }

    public string AggregatePluralName { get; }

    public string ContextRouteSegment { get; }

    public string AggregateRouteSegment { get; }

    public string ApiGroupName { get; }

    private static string ProjectReference(string fromDirectory, string projectPath)
    {
        return Path.GetRelativePath(fromDirectory, projectPath)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private static string ToSnakeCase(string value)
    {
        return ConvertPascalCase(value, '_');
    }

    private static string ToKebabCase(string value)
    {
        return ConvertPascalCase(value, '-');
    }

    private static string ConvertPascalCase(string value, char separator)
    {
        var characters = new List<char>(value.Length + 8);

        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (index > 0 && char.IsUpper(current))
            {
                characters.Add(separator);
            }

            characters.Add(char.ToLowerInvariant(current));
        }

        return new string(characters.ToArray());
    }

    private static string SplitPascalCase(string value)
    {
        var characters = new List<char>(value.Length + 8);

        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (index > 0 && char.IsUpper(current))
            {
                characters.Add(' ');
            }

            characters.Add(current);
        }

        return new string(characters.ToArray());
    }
}
