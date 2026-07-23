namespace OpenLineOps.BoundedContext.Scaffolder;

internal static class TemplateCatalog
{
    public static IReadOnlyList<TemplateFile> CreateFiles(BoundedContextScaffoldModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        return
        [
            new TemplateFile(model.DomainSharedProjectPath, DomainSharedProject(model)),
            new TemplateFile(Path.Combine(model.DomainSharedProjectDirectory, "IntegrationEvents", $"{model.AggregateName}CreatedIntegrationDto.cs"), CreatedIntegrationDto(model)),
            new TemplateFile(model.DomainProjectPath, DomainProject(model)),
            new TemplateFile(Path.Combine(model.DomainProjectDirectory, "Identifiers", $"{model.AggregateIdName}.cs"), AggregateId(model)),
            new TemplateFile(Path.Combine(model.DomainProjectDirectory, "Identifiers", $"{model.ContextName}IdGuard.cs"), IdGuard(model)),
            new TemplateFile(Path.Combine(model.DomainProjectDirectory, "Operations", $"{model.OperationResultName}.cs"), OperationResult(model)),
            new TemplateFile(Path.Combine(model.DomainProjectDirectory, "Events", $"{model.AggregateName}CreatedDomainEvent.cs"), CreatedEvent(model)),
            new TemplateFile(Path.Combine(model.DomainProjectDirectory, "Events", "Converters", $"{model.AggregateName}EventToIntegrationDtoConverter.cs"), EventToIntegrationDtoConverter(model)),
            new TemplateFile(Path.Combine(model.DomainProjectDirectory, "Events", "Converters", $"{model.AggregateName}IntegrationDtoConverter.cs"), IntegrationDtoConverter(model)),
            new TemplateFile(Path.Combine(model.DomainProjectDirectory, "Aggregates", $"{model.AggregateName}.cs"), Aggregate(model)),
            new TemplateFile(Path.Combine(model.DomainProjectDirectory, "Repositories", $"I{model.AggregateName}Repository.cs"), RepositoryPort(model)),
            new TemplateFile(model.ApplicationContractProjectPath, ApplicationContractProject(model)),
            new TemplateFile(Path.Combine(model.ApplicationContractProjectDirectory, "Results", $"{model.ApplicationResultName}.cs"), ApplicationResult(model)),
            new TemplateFile(Path.Combine(model.ApplicationContractProjectDirectory, model.AggregatePluralName, $"{model.AggregateName}Contracts.cs"), ApplicationContracts(model)),
            new TemplateFile(Path.Combine(model.ApplicationContractProjectDirectory, "Services", $"I{model.AggregateName}AppService.cs"), ApplicationServiceContract(model)),
            new TemplateFile(model.ApplicationProjectPath, ApplicationProject(model)),
            new TemplateFile(Path.Combine(model.ApplicationProjectDirectory, "Services", $"{model.AggregateName}AppService.cs"), ApplicationService(model)),
            new TemplateFile(model.InfraDataProjectPath, InfraDataProject(model)),
            new TemplateFile(Path.Combine(model.InfraDataProjectDirectory, "Persistence", $"{model.ContextName}DbContext.cs"), DbContext(model)),
            new TemplateFile(Path.Combine(model.InfraDataProjectDirectory, "Persistence", $"{model.ContextName}DbContextDesignTimeFactory.cs"), DbContextDesignTimeFactory(model)),
            new TemplateFile(Path.Combine(model.InfraDataProjectDirectory, "Persistence", $"{model.AggregateName}Configuration.cs"), EntityConfiguration(model)),
            new TemplateFile(Path.Combine(model.InfraDataProjectDirectory, "Persistence", $"Ef{model.AggregateName}Repository.cs"), EfRepository(model)),
            new TemplateFile(Path.Combine(model.InfraDataProjectDirectory, "Persistence", "Migrations", $"{InitialMigrationId(model)}.cs"), InitialMigration(model)),
            new TemplateFile(Path.Combine(model.InfraDataProjectDirectory, "Persistence", "Migrations", $"{InitialMigrationId(model)}.Designer.cs"), InitialMigrationDesigner(model)),
            new TemplateFile(Path.Combine(model.InfraDataProjectDirectory, "Persistence", "Migrations", $"{model.ContextName}DbContextModelSnapshot.cs"), ModelSnapshot(model)),
            new TemplateFile(model.InfraCrossCuttingIoCProjectPath, InfraCrossCuttingIoCProject(model)),
            new TemplateFile(Path.Combine(model.InfraCrossCuttingIoCProjectDirectory, "DependencyInjection", $"{model.ContextName}NativeInjectorBootStrapper.cs"), NativeInjectorBootStrapper(model)),
            new TemplateFile(model.ApiProjectPath, ApiProject(model)),
            new TemplateFile(Path.Combine(model.ApiProjectDirectory, "DependencyInjection", $"{model.ContextName}ApiMvcBuilderExtensions.cs"), ApiMvcBuilderExtensions(model)),
            new TemplateFile(Path.Combine(model.ApiProjectDirectory, "Controllers", $"{model.AggregatePluralName}Controller.cs"), ApiController(model)),
            new TemplateFile(model.TestProjectPath, TestProject(model)),
            new TemplateFile(Path.Combine(model.TestProjectDirectory, $"{model.AggregateName}PersistenceTests.cs"), PersistenceTests(model)),
            new TemplateFile(Path.Combine(model.TestProjectDirectory, $"{model.AggregateName}AppServiceTests.cs"), ApplicationServiceTests(model))
        ];
    }

    private static string DomainSharedProject(BoundedContextScaffoldModel model)
    {
        return $$"""
            <Project Sdk="Microsoft.NET.Sdk">

              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <NoWarn>$(NoWarn);CA1716</NoWarn>
              </PropertyGroup>

            </Project>
            """;
    }

    private static string CreatedIntegrationDto(BoundedContextScaffoldModel model)
    {
        return $$"""
            namespace OpenLineOps.{{model.ContextName}}.Domain.Shared.IntegrationEvents;

            public sealed record {{model.AggregateName}}CreatedIntegrationDto(
                string {{model.AggregateName}}Id,
                string DisplayName,
                DateTimeOffset OccurredAtUtc)
            {
                public const string EventName = "{{model.EventName}}";
            }
            """;
    }

    private static string DomainProject(BoundedContextScaffoldModel model)
    {
        return $$"""
            <Project Sdk="Microsoft.NET.Sdk">

              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>

              <ItemGroup>
                <ProjectReference Include="{{model.DomainAbstractionsProjectReference}}" />
                <ProjectReference Include="{{model.DomainToDomainSharedProjectReference}}" />
              </ItemGroup>

            </Project>
            """;
    }

    private static string AggregateId(BoundedContextScaffoldModel model)
    {
        return $$"""
            namespace OpenLineOps.{{model.ContextName}}.Domain.Identifiers;

            public sealed record {{model.AggregateIdName}}
            {
                public {{model.AggregateIdName}}(string value)
                {
                    Value = {{model.ContextName}}IdGuard.NotBlank(value, nameof(value));
                }

                public string Value { get; }

                public override string ToString()
                {
                    return Value;
                }
            }
            """;
    }

    private static string IdGuard(BoundedContextScaffoldModel model)
    {
        return $$"""
            namespace OpenLineOps.{{model.ContextName}}.Domain.Identifiers;

            internal static class {{model.ContextName}}IdGuard
            {
                public static string NotBlank(string value, string parameterName)
                {
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        throw new ArgumentException("Identifier values cannot be blank.", parameterName);
                    }

                    return value.Trim();
                }
            }
            """;
    }

    private static string OperationResult(BoundedContextScaffoldModel model)
    {
        return $$"""
            namespace OpenLineOps.{{model.ContextName}}.Domain.Operations;

            public sealed record {{model.OperationResultName}}(bool Succeeded, string Code, string Message)
            {
                public static {{model.OperationResultName}} Accepted(string message = "Accepted.")
                {
                    return new {{model.OperationResultName}}(true, "{{model.ContextName}}.Accepted", message);
                }

                public static {{model.OperationResultName}} Rejected(string code, string message)
                {
                    return new {{model.OperationResultName}}(false, code, message);
                }
            }
            """;
    }

    private static string CreatedEvent(BoundedContextScaffoldModel model)
    {
        return $$"""
            using OpenLineOps.Domain.Abstractions.EventBus;
            using OpenLineOps.Domain.Abstractions.Events;
            using OpenLineOps.{{model.ContextName}}.Domain.Identifiers;
            using OpenLineOps.{{model.ContextName}}.Domain.Shared.IntegrationEvents;

            namespace OpenLineOps.{{model.ContextName}}.Domain.Events;

            public sealed record {{model.AggregateName}}CreatedDomainEvent(
                {{model.AggregateIdName}} AggregateId,
                string DisplayName,
                DateTimeOffset CreatedAtUtc)
                : DomainEvent({{model.AggregateName}}CreatedIntegrationDto.EventName),
                    IIntegrationEvent;
            """;
    }

    private static string EventToIntegrationDtoConverter(BoundedContextScaffoldModel model)
    {
        return $$"""
            using OpenLineOps.{{model.ContextName}}.Domain.Shared.IntegrationEvents;

            namespace OpenLineOps.{{model.ContextName}}.Domain.Events.Converters;

            public static class {{model.AggregateName}}EventToIntegrationDtoConverter
            {
                public static {{model.AggregateName}}CreatedIntegrationDto ToIntegrationDto(
                    this {{model.AggregateName}}CreatedDomainEvent domainEvent)
                {
                    ArgumentNullException.ThrowIfNull(domainEvent);

                    return new {{model.AggregateName}}CreatedIntegrationDto(
                        domainEvent.AggregateId.Value,
                        domainEvent.DisplayName,
                        domainEvent.CreatedAtUtc);
                }
            }
            """;
    }

    private static string IntegrationDtoConverter(BoundedContextScaffoldModel model)
    {
        return $$"""
            using OpenLineOps.Domain.Abstractions.EventBus;

            namespace OpenLineOps.{{model.ContextName}}.Domain.Events.Converters;

            public sealed class {{model.AggregateName}}IntegrationDtoConverter : IIntegrationDtoConverter
            {
                public bool CanConvert(object domainEvent)
                {
                    return domainEvent is {{model.AggregateName}}CreatedDomainEvent;
                }

                public object Convert(object domainEvent)
                {
                    return domainEvent switch
                    {
                        {{model.AggregateName}}CreatedDomainEvent created => created.ToIntegrationDto(),
                        _ => throw new NotSupportedException(
                            $"Unsupported integration event type: {domainEvent.GetType().Name}.")
                    };
                }
            }
            """;
    }

    private static string Aggregate(BoundedContextScaffoldModel model)
    {
        return $$"""
            using OpenLineOps.Domain.Abstractions.Entities;
            using OpenLineOps.{{model.ContextName}}.Domain.Events;
            using OpenLineOps.{{model.ContextName}}.Domain.Identifiers;
            using OpenLineOps.{{model.ContextName}}.Domain.Operations;

            namespace OpenLineOps.{{model.ContextName}}.Domain.Aggregates;

            public sealed class {{model.AggregateName}} : AggregateRoot<{{model.AggregateIdName}}>
            {
                private {{model.AggregateName}}()
                    : base(new {{model.AggregateIdName}}("__ef_materialization__"))
                {
                    DisplayName = string.Empty;
                }

                private {{model.AggregateName}}(
                    {{model.AggregateIdName}} id,
                    string displayName,
                    DateTimeOffset createdAtUtc)
                    : base(id)
                {
                    DisplayName = RequiredText(displayName, nameof(displayName));
                    CreatedAtUtc = createdAtUtc;
                }

                public string DisplayName { get; private set; }

                public DateTimeOffset CreatedAtUtc { get; private set; }

                public static {{model.AggregateName}} Create(
                    {{model.AggregateIdName}} id,
                    string displayName,
                    DateTimeOffset createdAtUtc)
                {
                    var aggregate = new {{model.AggregateName}}(id, displayName, createdAtUtc);
                    aggregate.RaiseDomainEvent(new {{model.AggregateName}}CreatedDomainEvent(
                        id,
                        aggregate.DisplayName,
                        createdAtUtc));

                    return aggregate;
                }

                public {{model.OperationResultName}} Rename(string displayName)
                {
                    DisplayName = RequiredText(displayName, nameof(displayName));

                    return {{model.OperationResultName}}.Accepted("{{model.AggregateName}} renamed.");
                }

                private static string RequiredText(string value, string parameterName)
                {
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        throw new ArgumentException("Text values cannot be blank.", parameterName);
                    }

                    return value.Trim();
                }
            }
            """;
    }

    private static string RepositoryPort(BoundedContextScaffoldModel model)
    {
        return $$"""
            using OpenLineOps.Domain.Abstractions.Repositories;
            using OpenLineOps.{{model.ContextName}}.Domain.Aggregates;
            using OpenLineOps.{{model.ContextName}}.Domain.Identifiers;

            namespace OpenLineOps.{{model.ContextName}}.Domain.Repositories;

            public interface I{{model.AggregateName}}Repository :
                IAggregateRepository<{{model.AggregateName}}, {{model.AggregateIdName}}>;
            """;
    }

    private static string ApplicationContractProject(BoundedContextScaffoldModel model)
    {
        return $$"""
            <Project Sdk="Microsoft.NET.Sdk">

              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>

              <ItemGroup>
                <ProjectReference Include="{{model.ApplicationContractToDomainSharedProjectReference}}" />
              </ItemGroup>

            </Project>
            """;
    }

    private static string ApplicationResult(BoundedContextScaffoldModel model)
    {
        return $$"""
            namespace OpenLineOps.{{model.ContextName}}.Application.Contract.Results;

            public sealed record {{model.ApplicationResultName}}(bool Succeeded, string Code, string Message)
            {
                public static {{model.ApplicationResultName}} Accepted(string message = "Accepted.")
                {
                    return new {{model.ApplicationResultName}}(true, "{{model.ContextName}}.Accepted", message);
                }

                public static {{model.ApplicationResultName}} Rejected(string code, string message)
                {
                    return new {{model.ApplicationResultName}}(false, code, message);
                }
            }
            """;
    }

    private static string ApplicationContracts(BoundedContextScaffoldModel model)
    {
        return $$"""
            namespace OpenLineOps.{{model.ContextName}}.Application.Contract.{{model.AggregatePluralName}};

            public sealed record {{model.AggregateName}}Details(
                string Id,
                string DisplayName,
                DateTimeOffset CreatedAtUtc);

            public sealed record Create{{model.AggregateName}}Request(
                string Id,
                string DisplayName);

            public sealed record Rename{{model.AggregateName}}Request(string DisplayName);
            """;
    }

    private static string ApplicationServiceContract(BoundedContextScaffoldModel model)
    {
        return $$"""
            using OpenLineOps.{{model.ContextName}}.Application.Contract.{{model.AggregatePluralName}};
            using OpenLineOps.{{model.ContextName}}.Application.Contract.Results;

            namespace OpenLineOps.{{model.ContextName}}.Application.Contract.Services;

            public interface I{{model.AggregateName}}AppService
            {
                Task<{{model.AggregateName}}Details> CreateAsync(
                    Create{{model.AggregateName}}Request request,
                    CancellationToken cancellationToken = default);

                Task<{{model.AggregateName}}Details?> GetAsync(
                    string id,
                    CancellationToken cancellationToken = default);

                Task<{{model.ApplicationResultName}}> RenameAsync(
                    string id,
                    Rename{{model.AggregateName}}Request request,
                    CancellationToken cancellationToken = default);
            }
            """;
    }

    private static string ApplicationProject(BoundedContextScaffoldModel model)
    {
        return $$"""
            <Project Sdk="Microsoft.NET.Sdk">

              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>

              <ItemGroup>
                <ProjectReference Include="{{model.ApplicationToApplicationContractProjectReference}}" />
                <ProjectReference Include="{{model.ApplicationToDomainProjectReference}}" />
                <ProjectReference Include="{{model.ApplicationToDomainSharedProjectReference}}" />
              </ItemGroup>

            </Project>
            """;
    }

    private static string ApplicationService(BoundedContextScaffoldModel model)
    {
        return $$"""
            using OpenLineOps.{{model.ContextName}}.Application.Contract.{{model.AggregatePluralName}};
            using OpenLineOps.{{model.ContextName}}.Application.Contract.Results;
            using OpenLineOps.{{model.ContextName}}.Application.Contract.Services;
            using OpenLineOps.{{model.ContextName}}.Domain.Aggregates;
            using OpenLineOps.{{model.ContextName}}.Domain.Identifiers;
            using OpenLineOps.{{model.ContextName}}.Domain.Repositories;

            namespace OpenLineOps.{{model.ContextName}}.Application.Services;

            public sealed class {{model.AggregateName}}AppService(I{{model.AggregateName}}Repository repository)
                : I{{model.AggregateName}}AppService
            {
                public async Task<{{model.AggregateName}}Details> CreateAsync(
                    Create{{model.AggregateName}}Request request,
                    CancellationToken cancellationToken = default)
                {
                    ArgumentNullException.ThrowIfNull(request);

                    var aggregate = {{model.AggregateName}}.Create(
                        new {{model.AggregateIdName}}(request.Id),
                        request.DisplayName,
                        DateTimeOffset.UtcNow);

                    repository.Add(aggregate);

                    var committed = await repository.UnitOfWork.Commit().ConfigureAwait(false);
                    if (!committed)
                    {
                        throw new InvalidOperationException("{{model.AggregateName}} creation did not persist any changes.");
                    }

                    return ToDetails(aggregate);
                }

                public async Task<{{model.AggregateName}}Details?> GetAsync(
                    string id,
                    CancellationToken cancellationToken = default)
                {
                    var aggregate = await repository
                        .GetByIdAsync(new {{model.AggregateIdName}}(id), cancellationToken)
                        .ConfigureAwait(false);

                    return aggregate is null
                        ? null
                        : ToDetails(aggregate);
                }

                public async Task<{{model.ApplicationResultName}}> RenameAsync(
                    string id,
                    Rename{{model.AggregateName}}Request request,
                    CancellationToken cancellationToken = default)
                {
                    ArgumentNullException.ThrowIfNull(request);

                    var aggregate = await repository
                        .GetByIdAsync(new {{model.AggregateIdName}}(id), cancellationToken)
                        .ConfigureAwait(false);
                    if (aggregate is null)
                    {
                        return {{model.ApplicationResultName}}.Rejected(
                            "{{model.ContextName}}.{{model.AggregateName}}.NotFound",
                            "{{model.AggregateName}} was not found.");
                    }

                    var result = aggregate.Rename(request.DisplayName);
                    repository.Update(aggregate);

                    var committed = await repository.UnitOfWork.Commit().ConfigureAwait(false);
                    return committed && result.Succeeded
                        ? {{model.ApplicationResultName}}.Accepted(result.Message)
                        : {{model.ApplicationResultName}}.Rejected(result.Code, result.Message);
                }

                private static {{model.AggregateName}}Details ToDetails({{model.AggregateName}} aggregate)
                {
                    return new {{model.AggregateName}}Details(
                        aggregate.Id.Value,
                        aggregate.DisplayName,
                        aggregate.CreatedAtUtc);
                }
            }
            """;
    }

    private static string InfraDataProject(BoundedContextScaffoldModel model)
    {
        return $$"""
            <Project Sdk="Microsoft.NET.Sdk">

              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>

              <ItemGroup>
                <PackageReference Include="Microsoft.EntityFrameworkCore" />
                <PackageReference Include="Microsoft.EntityFrameworkCore.Design">
                  <PrivateAssets>all</PrivateAssets>
                  <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
                </PackageReference>
                <PackageReference Include="Microsoft.EntityFrameworkCore.Relational" />
                <PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" />
                <PackageReference Include="SQLitePCLRaw.bundle_e_sqlite3" />
              </ItemGroup>

              <ItemGroup>
                <ProjectReference Include="{{model.InfraDataCoreProjectReference}}" />
                <ProjectReference Include="{{model.InfraDataToDomainProjectReference}}" />
              </ItemGroup>

            </Project>
            """;
    }

    private static string DbContext(BoundedContextScaffoldModel model)
    {
        return $$"""
            using Microsoft.EntityFrameworkCore;
            using Microsoft.Extensions.Logging;
            using OpenLineOps.Domain.Abstractions.EventBus;
            using OpenLineOps.Infrastructure.Data.Core.Context;
            using OpenLineOps.Infrastructure.Data.Core.EventBus;
            using OpenLineOps.{{model.ContextName}}.Domain.Aggregates;

            namespace OpenLineOps.{{model.ContextName}}.Infra.Data.Persistence;

            public sealed class {{model.ContextName}}DbContext(
                DbContextOptions<{{model.ContextName}}DbContext> options,
                IntegrationEventPublicationPolicy? integrationEventPublicationPolicy = null,
                IIntegrationEventPublisher? integrationEventPublisher = null,
                ITransactionalIntegrationEventPublisher? transactionalIntegrationEventPublisher = null,
                IIntegrationEventTransactionCoordinator? integrationEventTransactionCoordinator = null,
                ILogger<BaseDbContext>? logger = null)
                : BaseDbContext(
                    options,
                    integrationEventPublicationPolicy,
                    integrationEventPublisher: integrationEventPublisher,
                    transactionalIntegrationEventPublisher: transactionalIntegrationEventPublisher,
                    integrationEventTransactionCoordinator: integrationEventTransactionCoordinator,
                    logger: logger)
            {
                public DbSet<{{model.AggregateName}}> {{model.AggregatePluralName}} => Set<{{model.AggregateName}}>();

                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    ArgumentNullException.ThrowIfNull(modelBuilder);

                    base.OnModelCreating(modelBuilder);

                    modelBuilder.ApplyConfigurationsFromAssembly(typeof({{model.ContextName}}DbContext).Assembly);
                }
            }
            """;
    }

    private static string DbContextDesignTimeFactory(BoundedContextScaffoldModel model)
    {
        return $$"""
            using Microsoft.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore.Design;

            namespace OpenLineOps.{{model.ContextName}}.Infra.Data.Persistence;

            public sealed class {{model.ContextName}}DbContextDesignTimeFactory : IDesignTimeDbContextFactory<{{model.ContextName}}DbContext>
            {
                public {{model.ContextName}}DbContext CreateDbContext(string[] args)
                {
                    var options = new DbContextOptionsBuilder<{{model.ContextName}}DbContext>()
                        .UseSqlite("Data Source=openlineops-{{model.ContextRouteSegment}}-design-time.sqlite")
                        .Options;

                    return new {{model.ContextName}}DbContext(options);
                }
            }
            """;
    }

    private static string EntityConfiguration(BoundedContextScaffoldModel model)
    {
        return $$"""
            using Microsoft.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore.Metadata.Builders;
            using OpenLineOps.Infrastructure.Data.Core.Identifiers;
            using OpenLineOps.{{model.ContextName}}.Domain.Aggregates;
            using OpenLineOps.{{model.ContextName}}.Domain.Identifiers;

            namespace OpenLineOps.{{model.ContextName}}.Infra.Data.Persistence;

            internal sealed class {{model.AggregateName}}Configuration : IEntityTypeConfiguration<{{model.AggregateName}}>
            {
                public void Configure(EntityTypeBuilder<{{model.AggregateName}}> builder)
                {
                    ArgumentNullException.ThrowIfNull(builder);

                    builder.ToTable("{{model.TableName}}s");

                    builder.HasKey(aggregate => aggregate.Id);

                    builder.Property(aggregate => aggregate.Id)
                        .HasStronglyTypedIdConversion<{{model.AggregateIdName}}, string>()
                        .HasMaxLength(160)
                        .IsRequired();

                    builder.Property(aggregate => aggregate.DisplayName)
                        .HasMaxLength(200)
                        .IsRequired();

                    builder.Property(aggregate => aggregate.CreatedAtUtc)
                        .IsRequired();

                    builder.Ignore(aggregate => aggregate.DomainEvents);
                }
            }
            """;
    }

    private static string InitialMigration(BoundedContextScaffoldModel model)
    {
        return $$"""
            using System;
            using Microsoft.EntityFrameworkCore.Migrations;

            #nullable disable

            namespace OpenLineOps.{{model.ContextName}}.Infra.Data.Persistence.Migrations
            {
                /// <inheritdoc />
                public partial class {{InitialMigrationClassName(model)}} : Migration
                {
                    /// <inheritdoc />
                    protected override void Up(MigrationBuilder migrationBuilder)
                    {
                        migrationBuilder.CreateTable(
                            name: "{{model.TableName}}s",
                            columns: table => new
                            {
                                Id = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                                DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                                CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                            },
                            constraints: table =>
                            {
                                table.PrimaryKey("PK_{{model.TableName}}s", x => x.Id);
                            });
                    }

                    /// <inheritdoc />
                    protected override void Down(MigrationBuilder migrationBuilder)
                    {
                        migrationBuilder.DropTable(
                            name: "{{model.TableName}}s");
                    }
                }
            }
            """;
    }

    private static string InitialMigrationDesigner(BoundedContextScaffoldModel model)
    {
        return $$"""
            // <auto-generated />
            using System;
            using Microsoft.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore.Infrastructure;
            using Microsoft.EntityFrameworkCore.Migrations;
            using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
            using OpenLineOps.{{model.ContextName}}.Infra.Data.Persistence;

            #nullable disable

            namespace OpenLineOps.{{model.ContextName}}.Infra.Data.Persistence.Migrations
            {
                [DbContext(typeof({{model.ContextName}}DbContext))]
                [Migration("{{InitialMigrationId(model)}}")]
                partial class {{InitialMigrationClassName(model)}}
                {
                    /// <inheritdoc />
                    protected override void BuildTargetModel(ModelBuilder modelBuilder)
                    {
            #pragma warning disable 612, 618
                        modelBuilder.HasAnnotation("ProductVersion", "10.0.9");

                        modelBuilder.Entity("OpenLineOps.{{model.ContextName}}.Domain.Aggregates.{{model.AggregateName}}", b =>
                            {
                                b.Property<string>("Id")
                                    .HasMaxLength(160)
                                    .HasColumnType("TEXT");

                                b.Property<DateTimeOffset>("CreatedAtUtc")
                                    .HasColumnType("TEXT");

                                b.Property<string>("DisplayName")
                                    .IsRequired()
                                    .HasMaxLength(200)
                                    .HasColumnType("TEXT");

                                b.HasKey("Id");

                                b.ToTable("{{model.TableName}}s", (string)null);
                            });
            #pragma warning restore 612, 618
                    }
                }
            }
            """;
    }

    private static string ModelSnapshot(BoundedContextScaffoldModel model)
    {
        return $$"""
            // <auto-generated />
            using System;
            using Microsoft.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore.Infrastructure;
            using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
            using OpenLineOps.{{model.ContextName}}.Infra.Data.Persistence;

            #nullable disable

            namespace OpenLineOps.{{model.ContextName}}.Infra.Data.Persistence.Migrations
            {
                [DbContext(typeof({{model.ContextName}}DbContext))]
                partial class {{model.ContextName}}DbContextModelSnapshot : ModelSnapshot
                {
                    protected override void BuildModel(ModelBuilder modelBuilder)
                    {
            #pragma warning disable 612, 618
                        modelBuilder.HasAnnotation("ProductVersion", "10.0.9");

                        modelBuilder.Entity("OpenLineOps.{{model.ContextName}}.Domain.Aggregates.{{model.AggregateName}}", b =>
                            {
                                b.Property<string>("Id")
                                    .HasMaxLength(160)
                                    .HasColumnType("TEXT");

                                b.Property<DateTimeOffset>("CreatedAtUtc")
                                    .HasColumnType("TEXT");

                                b.Property<string>("DisplayName")
                                    .IsRequired()
                                    .HasMaxLength(200)
                                    .HasColumnType("TEXT");

                                b.HasKey("Id");

                                b.ToTable("{{model.TableName}}s", (string)null);
                            });
            #pragma warning restore 612, 618
                    }
                }
            }
            """;
    }

    private static string InitialMigrationId(BoundedContextScaffoldModel model)
    {
        return $"20260630000000_{InitialMigrationClassName(model)}";
    }

    private static string InitialMigrationClassName(BoundedContextScaffoldModel model)
    {
        return $"Initial{model.ContextName}{model.AggregateName}Sqlite";
    }

    private static string EfRepository(BoundedContextScaffoldModel model)
    {
        return $$"""
            using OpenLineOps.Infrastructure.Data.Core.Repositories;
            using OpenLineOps.{{model.ContextName}}.Domain.Aggregates;
            using OpenLineOps.{{model.ContextName}}.Domain.Identifiers;
            using OpenLineOps.{{model.ContextName}}.Domain.Repositories;

            namespace OpenLineOps.{{model.ContextName}}.Infra.Data.Persistence;

            public sealed class Ef{{model.AggregateName}}Repository({{model.ContextName}}DbContext context)
                : BaseRepository<{{model.ContextName}}DbContext, {{model.AggregateName}}, {{model.AggregateIdName}}>(context),
                    I{{model.AggregateName}}Repository;
            """;
    }

    private static string InfraCrossCuttingIoCProject(BoundedContextScaffoldModel model)
    {
        return $$"""
            <Project Sdk="Microsoft.NET.Sdk">

              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>

              <ItemGroup>
                <PackageReference Include="Microsoft.EntityFrameworkCore" />
              </ItemGroup>

              <ItemGroup>
                <ProjectReference Include="{{model.InfraCrossCuttingIoCToApplicationContractProjectReference}}" />
                <ProjectReference Include="{{model.InfraCrossCuttingIoCToApplicationProjectReference}}" />
                <ProjectReference Include="{{model.InfraCrossCuttingIoCToDomainProjectReference}}" />
                <ProjectReference Include="{{model.InfraCrossCuttingIoCToInfraDataProjectReference}}" />
              </ItemGroup>

            </Project>
            """;
    }

    private static string NativeInjectorBootStrapper(BoundedContextScaffoldModel model)
    {
        return $$"""
            using Microsoft.EntityFrameworkCore;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.DependencyInjection.Extensions;
            using OpenLineOps.Domain.Abstractions.EventBus;
            using OpenLineOps.{{model.ContextName}}.Application.Contract.Services;
            using OpenLineOps.{{model.ContextName}}.Application.Services;
            using OpenLineOps.{{model.ContextName}}.Domain.Events.Converters;
            using OpenLineOps.{{model.ContextName}}.Domain.Repositories;
            using OpenLineOps.{{model.ContextName}}.Infra.Data.Persistence;

            namespace OpenLineOps.{{model.ContextName}}.Infra.CrossCutting.IoC.DependencyInjection;

            public static class {{model.ContextName}}NativeInjectorBootStrapper
            {
                public static IServiceCollection AddOpenLineOps{{model.ContextName}}Module(
                    this IServiceCollection services,
                    Action<DbContextOptionsBuilder> configureDbContext)
                {
                    ArgumentNullException.ThrowIfNull(services);
                    ArgumentNullException.ThrowIfNull(configureDbContext);

                    services.AddDbContext<{{model.ContextName}}DbContext>(configureDbContext);
                    services.TryAddSingleton<IntegrationDtoConverterRegistry>();
                    services.AddSingleton<IIntegrationDtoConverter, {{model.AggregateName}}IntegrationDtoConverter>();
                    services.AddScoped<I{{model.AggregateName}}Repository, Ef{{model.AggregateName}}Repository>();
                    services.AddScoped<I{{model.AggregateName}}AppService, {{model.AggregateName}}AppService>();

                    return services;
                }
            }
            """;
    }

    private static string ApiProject(BoundedContextScaffoldModel model)
    {
        return $$"""
            <Project Sdk="Microsoft.NET.Sdk">

              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>

              <ItemGroup>
                <FrameworkReference Include="Microsoft.AspNetCore.App" />
              </ItemGroup>

              <ItemGroup>
                <ProjectReference Include="{{model.ApiAbstractionsProjectReference}}" />
                <ProjectReference Include="{{model.ApiToApplicationContractProjectReference}}" />
                <ProjectReference Include="{{model.ApiToInfraCrossCuttingIoCProjectReference}}" />
              </ItemGroup>

            </Project>
            """;
    }

    private static string ApiMvcBuilderExtensions(BoundedContextScaffoldModel model)
    {
        return $$"""
            using Microsoft.Extensions.DependencyInjection;
            using OpenLineOps.{{model.ContextName}}.Api.Controllers;

            namespace OpenLineOps.{{model.ContextName}}.Api.DependencyInjection;

            public static class {{model.ContextName}}ApiMvcBuilderExtensions
            {
                public static IMvcBuilder AddOpenLineOps{{model.ContextName}}Api(this IMvcBuilder mvcBuilder)
                {
                    ArgumentNullException.ThrowIfNull(mvcBuilder);

                    return mvcBuilder.AddApplicationPart(typeof({{model.AggregatePluralName}}Controller).Assembly);
                }
            }
            """;
    }

    private static string ApiController(BoundedContextScaffoldModel model)
    {
        return $$"""
            using Microsoft.AspNetCore.Mvc;
            using OpenLineOps.{{model.ContextName}}.Application.Contract.{{model.AggregatePluralName}};
            using OpenLineOps.{{model.ContextName}}.Application.Contract.Results;
            using OpenLineOps.{{model.ContextName}}.Application.Contract.Services;

            namespace OpenLineOps.{{model.ContextName}}.Api.Controllers;

            [ApiController]
            [ApiExplorerSettings(GroupName = "{{model.ApiGroupName}}")]
            [Route("api/{{model.ContextRouteSegment}}/{{model.AggregateRouteSegment}}s")]
            public sealed class {{model.AggregatePluralName}}Controller(I{{model.AggregateName}}AppService appService)
                : ControllerBase
            {
                [HttpGet("{id}")]
                public async Task<ActionResult<{{model.AggregateName}}Details>> Get(
                    string id,
                    CancellationToken cancellationToken)
                {
                    var details = await appService.GetAsync(id, cancellationToken).ConfigureAwait(false);

                    return details is null
                        ? NotFound()
                        : Ok(details);
                }

                [HttpPost]
                public async Task<ActionResult<{{model.AggregateName}}Details>> Create(
                    Create{{model.AggregateName}}Request request,
                    CancellationToken cancellationToken)
                {
                    var details = await appService.CreateAsync(request, cancellationToken).ConfigureAwait(false);

                    return CreatedAtAction(nameof(Get), new { id = details.Id }, details);
                }

                [HttpPut("{id}/name")]
                public async Task<ActionResult<{{model.ApplicationResultName}}>> Rename(
                    string id,
                    Rename{{model.AggregateName}}Request request,
                    CancellationToken cancellationToken)
                {
                    var result = await appService.RenameAsync(id, request, cancellationToken).ConfigureAwait(false);

                    return result.Succeeded
                        ? Ok(result)
                        : NotFound(result);
                }
            }
            """;
    }

    private static string TestProject(BoundedContextScaffoldModel model)
    {
        return $$"""
            <Project Sdk="Microsoft.NET.Sdk">

              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <IsPackable>false</IsPackable>
                <IsTestProject>true</IsTestProject>
              </PropertyGroup>

              <ItemGroup>
                <PackageReference Include="coverlet.collector" />
                <PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" />
                <PackageReference Include="Microsoft.NET.Test.Sdk" />
                <PackageReference Include="SQLitePCLRaw.bundle_e_sqlite3" />
                <PackageReference Include="xunit" />
                <PackageReference Include="xunit.runner.visualstudio" />
              </ItemGroup>

              <ItemGroup>
                <Using Include="Xunit" />
              </ItemGroup>

              <ItemGroup>
                <ProjectReference Include="{{model.TestToApplicationContractProjectReference}}" />
                <ProjectReference Include="{{model.TestToApplicationProjectReference}}" />
                <ProjectReference Include="{{model.TestToDomainProjectReference}}" />
                <ProjectReference Include="{{model.TestToInfraDataProjectReference}}" />
                <ProjectReference Include="{{model.TestDataCoreProjectReference}}" />
              </ItemGroup>

            </Project>
            """;
    }

    private static string PersistenceTests(BoundedContextScaffoldModel model)
    {
        return $$"""
            using Microsoft.Data.Sqlite;
            using Microsoft.EntityFrameworkCore;
            using OpenLineOps.Domain.Abstractions.EventBus;
            using OpenLineOps.Infrastructure.Data.Core.EventBus;
            using OpenLineOps.{{model.ContextName}}.Domain.Aggregates;
            using OpenLineOps.{{model.ContextName}}.Domain.Events;
            using OpenLineOps.{{model.ContextName}}.Domain.Events.Converters;
            using OpenLineOps.{{model.ContextName}}.Domain.Identifiers;
            using OpenLineOps.{{model.ContextName}}.Infra.Data.Persistence;

            namespace OpenLineOps.{{model.ContextName}}.Tests;

            public sealed class {{model.AggregateName}}PersistenceTests
            {
                [Fact]
                public async Task EfDataCoreRepositoryPersistsAggregateAndPublishesIntegrationEvents()
                {
                    await using var connection = new SqliteConnection("Data Source=:memory:");
                    await connection.OpenAsync();

                    var publisher = new CapturingIntegrationEventPublisher();
                    var options = new DbContextOptionsBuilder<{{model.ContextName}}DbContext>()
                        .UseSqlite(connection)
                        .Options;
                    var aggregate = {{model.AggregateName}}.Create(
                        new {{model.AggregateIdName}}("{{model.AggregateSampleId}}"),
                        "{{model.AggregateDisplayName}}",
                        new DateTimeOffset(2026, 6, 30, 8, 0, 0, TimeSpan.Zero));

                    await using (var context = new {{model.ContextName}}DbContext(
                                     options,
                                     new IntegrationEventPublicationPolicy(IntegrationEventPublicationMode.PostCommit),
                                     integrationEventPublisher: publisher))
                    {
                        await context.Database.MigrateAsync();

                        var repository = new Ef{{model.AggregateName}}Repository(context);
                        repository.Add(aggregate);

                        var committed = await repository.UnitOfWork.Commit();

                        Assert.True(committed);
                        Assert.Empty(aggregate.DomainEvents);
                    }

                    await using (var context = new {{model.ContextName}}DbContext(options))
                    {
                        var repository = new Ef{{model.AggregateName}}Repository(context);

                        var restored = await repository.GetByIdAsync(aggregate.Id);

                        Assert.NotNull(restored);
                        Assert.Equal(aggregate.Id, restored.Id);
                        Assert.Equal("{{model.AggregateDisplayName}}", restored.DisplayName);
                        Assert.Empty(restored.DomainEvents);
                    }

                    var createdEvent = Assert.IsType<{{model.AggregateName}}CreatedDomainEvent>(
                        Assert.Single(publisher.Published));

                    var integrationDto = createdEvent.ToIntegrationDto();

                    Assert.Equal(aggregate.Id.Value, integrationDto.{{model.AggregateName}}Id);
                    Assert.Equal("{{model.AggregateDisplayName}}", integrationDto.DisplayName);
                }

                [Fact]
                public async Task EfDataCoreRepositoryAppliesInitialMigration()
                {
                    await using var connection = new SqliteConnection("Data Source=:memory:");
                    await connection.OpenAsync();

                    var options = new DbContextOptionsBuilder<{{model.ContextName}}DbContext>()
                        .UseSqlite(connection)
                        .Options;
                    await using var context = new {{model.ContextName}}DbContext(options);

                    await context.Database.MigrateAsync();
                    var pendingMigrations = await context.Database.GetPendingMigrationsAsync();

                    Assert.Empty(pendingMigrations);
                }

                private sealed class CapturingIntegrationEventPublisher : IIntegrationEventPublisher
                {
                    public List<object> Published { get; } = [];

                    public Task PublishAsync(
                        IEnumerable<object> domainEvents,
                        CancellationToken cancellationToken = default)
                    {
                        Published.AddRange(domainEvents);
                        return Task.CompletedTask;
                    }
                }
            }
            """;
    }

    private static string ApplicationServiceTests(BoundedContextScaffoldModel model)
    {
        return $$"""
            using Microsoft.Data.Sqlite;
            using Microsoft.EntityFrameworkCore;
            using OpenLineOps.Domain.Abstractions.EventBus;
            using OpenLineOps.Infrastructure.Data.Core.EventBus;
            using OpenLineOps.{{model.ContextName}}.Application.Contract.{{model.AggregatePluralName}};
            using OpenLineOps.{{model.ContextName}}.Application.Services;
            using OpenLineOps.{{model.ContextName}}.Infra.Data.Persistence;

            namespace OpenLineOps.{{model.ContextName}}.Tests;

            public sealed class {{model.AggregateName}}AppServiceTests
            {
                [Fact]
                public async Task CreateAsyncPersistsAggregateThroughApplicationContract()
                {
                    await using var connection = new SqliteConnection("Data Source=:memory:");
                    await connection.OpenAsync();

                    var options = new DbContextOptionsBuilder<{{model.ContextName}}DbContext>()
                        .UseSqlite(connection)
                        .Options;

                    await using var context = new {{model.ContextName}}DbContext(
                        options,
                        new IntegrationEventPublicationPolicy(IntegrationEventPublicationMode.PostCommit),
                        integrationEventPublisher: new CapturingIntegrationEventPublisher());
                    await context.Database.MigrateAsync();

                    var repository = new Ef{{model.AggregateName}}Repository(context);
                    var appService = new {{model.AggregateName}}AppService(repository);

                    var details = await appService.CreateAsync(new Create{{model.AggregateName}}Request(
                        "{{model.AggregateSampleId}}",
                        "{{model.AggregateDisplayName}}"));

                    Assert.Equal("{{model.AggregateSampleId}}", details.Id);
                    Assert.Equal("{{model.AggregateDisplayName}}", details.DisplayName);
                }

                private sealed class CapturingIntegrationEventPublisher : IIntegrationEventPublisher
                {
                    public Task PublishAsync(
                        IEnumerable<object> domainEvents,
                        CancellationToken cancellationToken = default)
                    {
                        return Task.CompletedTask;
                    }
                }
            }
            """;
    }
}

internal sealed record TemplateFile(string Path, string Content);
