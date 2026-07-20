using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Agent.Infrastructure.Transport;
using OpenLineOps.ContentProtection;
using OpenLineOps.ProcessIsolation;
using RabbitMQ.Client;

namespace OpenLineOps.Agent.Tests;

public sealed partial class StagedAgentRabbitMqProcessE2ETests
{
    internal sealed record StudioApiCredential(
        string CredentialId,
        string ActorId,
        string Token,
        string TokenSha256,
        string Role,
        string? StationId);

    internal sealed record StudioCoordinatorDeployment(
        string ProjectId,
        string ApplicationId,
        string StationSystemId,
        string AgentId,
        string StationId);

    internal sealed record StudioAgentEndpoint(
        string AgentId,
        string StationId,
        string StationSystemId,
        string PackageContentSha256,
        string Token,
        string RootPath,
        string DataPath,
        string PackageCachePath,
        string RuntimeWorkPath);

    internal sealed record StudioVendorProcessStart(
        long Sequence,
        int ProcessId,
        int ParentProcessId,
        DateTimeOffset StartedAtUtc,
        IReadOnlyList<int> AncestorProcessIds);

    internal sealed record StudioVendorProcessLedgerEvidence(
        IReadOnlyList<StudioVendorProcessStart> Entries,
        long SizeBytes,
        string Sha256,
        string Base64);

    internal sealed record StudioRabbitMqCleanupEvidence(
        bool Attempted,
        bool Succeeded,
        int QueueCount);

    internal sealed record StudioMaterialArrivalIpcIsolationEvidence(
        bool EntryServiceTokenConnected,
        bool EntryPipeExactAclVerified,
        bool DownstreamServiceTokenExplicitAccessDenied,
        bool BothServicesRunningOnOriginalPids);

    [SupportedOSPlatform("windows")]
    internal sealed class StudioTwoAgentExternalProcessHarness : IAsyncDisposable
    {
        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const uint ScManagerConnect = 0x0001;
        private const uint ServiceQueryStatus = 0x0004;
        private const uint ServiceRunning = 0x00000004;
        private const uint TokenDuplicate = 0x0002;
        private const uint TokenImpersonate = 0x0004;
        private const uint TokenQuery = 0x0008;
        private const uint ScStatusProcessInfo = 0;
        private const int ErrorAccessDenied = 5;
        private const int ErrorFileNotFound = 2;
        private const int ErrorPipeBusy = 231;

        private static readonly TimeSpan MaterialArrivalPipeConnectTimeout =
            TimeSpan.FromSeconds(10);

        private static readonly string[] SafeAgentEnvironmentVariables =
        [
            "ALLUSERSPROFILE",
            "COMSPEC",
            "CommonProgramFiles",
            "CommonProgramFiles(x86)",
            "CommonProgramW6432",
            "DOTNET_ROOT",
            "DOTNET_ROOT_X64",
            "NUMBER_OF_PROCESSORS",
            "OS",
            "PATH",
            "PATHEXT",
            "PROCESSOR_ARCHITECTURE",
            "PROCESSOR_IDENTIFIER",
            "PROCESSOR_LEVEL",
            "PROCESSOR_REVISION",
            "ProgramData",
            "ProgramFiles",
            "ProgramFiles(x86)",
            "ProgramW6432",
            "PYTHONNET_PYDLL",
            "SystemDrive",
            "SystemRoot",
            "WINDIR"
        ];

        private readonly string _agentBundleRoot;
        private readonly Uri _brokerUri;
        private readonly RestrictedAgentIdentity _entryIdentity;
        private readonly RestrictedAgentIdentity _downstreamIdentity;
        private readonly string _expectedAgentExecutableSha256;
        private readonly string _safetyExecutable;
        private readonly StudioVendorProcessStartObserver _vendorObserver;
        private readonly StudioRabbitMqConsumerProbe _consumerProbe;
        private WindowsAgentService? _entryService;
        private WindowsAgentService? _downstreamService;
        private WindowsAgentProcess? _entryProcess;
        private WindowsAgentProcess? _downstreamProcess;
        private StudioMaterialArrivalIpcIsolationEvidence? _materialArrivalIpcIsolation;
        private StudioRabbitMqCleanupEvidence _rabbitCleanup = new(false, false, 0);
        private bool _rabbitProbeDisposed;
        private bool _disposed;

        private StudioTwoAgentExternalProcessHarness(
            StudioProductionFixture fixture,
            string agentBundleRoot,
            Uri brokerUri,
            string rootPath,
            string distributionPath,
            string deploymentCatalogPath,
            string trustedSigningKeyPath,
            string coordinatorId,
            RestrictedAgentIdentity entryIdentity,
            RestrictedAgentIdentity downstreamIdentity,
            string expectedAgentExecutableSha256,
            string safetyExecutable,
            StudioVendorProcessStartObserver vendorObserver,
            StudioRabbitMqConsumerProbe consumerProbe,
            StudioAgentEndpoint entryAgent,
            StudioAgentEndpoint downstreamAgent,
            IReadOnlyList<StudioApiCredential> credentials)
        {
            Fixture = fixture;
            _agentBundleRoot = agentBundleRoot;
            _brokerUri = brokerUri;
            RootPath = rootPath;
            PackageDistributionPath = distributionPath;
            DeploymentCatalogPath = deploymentCatalogPath;
            TrustedSigningKeyPath = trustedSigningKeyPath;
            CoordinatorId = coordinatorId;
            _entryIdentity = entryIdentity;
            _downstreamIdentity = downstreamIdentity;
            _expectedAgentExecutableSha256 = expectedAgentExecutableSha256;
            _safetyExecutable = safetyExecutable;
            _vendorObserver = vendorObserver;
            _consumerProbe = consumerProbe;
            EntryAgent = entryAgent;
            DownstreamAgent = downstreamAgent;
            Credentials = credentials;
        }

        public StudioProductionFixture Fixture { get; }

        public string RootPath { get; }

        public string PackageDistributionPath { get; }

        public string DeploymentCatalogPath { get; }

        public string TrustedSigningKeyPath { get; }

        public string CoordinatorId { get; }

        public StudioAgentEndpoint EntryAgent { get; }

        public StudioAgentEndpoint DownstreamAgent { get; }

        public IReadOnlyList<StudioApiCredential> Credentials { get; }

        public int EntryAgentProcessId => _entryProcess?.Id
            ?? throw new InvalidOperationException("Entry Agent has not started.");

        public int DownstreamAgentProcessId => _downstreamProcess?.Id
            ?? throw new InvalidOperationException("Downstream Agent has not started.");

        public bool EntryAgentNonAdministrative => _entryProcess?.TokenEvidence.NonAdministrative
            ?? throw new InvalidOperationException("Entry Agent has not started.");

        public bool DownstreamAgentNonAdministrative =>
            _downstreamProcess?.TokenEvidence.NonAdministrative
            ?? throw new InvalidOperationException("Downstream Agent has not started.");

        public string ServiceAccountName
        {
            get
            {
                _ = ServiceAccountSid;
                return _entryIdentity.AccountName;
            }
        }

        public string ServiceAccountSid => SharedServiceAccountValue(
            static evidence => evidence.UserSid,
            "account SID");

        public string EntryAgentServiceSid => StartedServiceSid(
            _entryProcess,
            _entryIdentity,
            "Entry");

        public string DownstreamAgentServiceSid => StartedServiceSid(
            _downstreamProcess,
            _downstreamIdentity,
            "Downstream");

        public StudioRabbitMqCleanupEvidence RabbitMqCleanup => _rabbitCleanup;

        public StudioMaterialArrivalIpcIsolationEvidence MaterialArrivalIpcIsolation =>
            _materialArrivalIpcIsolation
            ?? throw new InvalidOperationException(
                "Cross-Station material-arrival IPC isolation has not been verified.");

        public StudioApiCredential OperatorCredential => Credentials.Single(
            credential => string.Equals(credential.Role, "Operator", StringComparison.Ordinal));

        public StudioApiCredential SafetyCredential => Credentials.Single(
            credential => string.Equals(credential.Role, "Safety", StringComparison.Ordinal));

        public IReadOnlyList<StudioCoordinatorDeployment> Deployments =>
        [
            new(
                Fixture.ProjectId,
                Fixture.ApplicationId,
                EntryAgent.StationSystemId,
                EntryAgent.AgentId,
                EntryAgent.StationId),
            new(
                Fixture.ProjectId,
                Fixture.ApplicationId,
                DownstreamAgent.StationSystemId,
                DownstreamAgent.AgentId,
                DownstreamAgent.StationId)
        ];

        public string AgentExecutablePath =>
            RequiredDirectFile(_agentBundleRoot, "OpenLineOps.Agent.exe");

        public string AgentExecutableSha256 => _expectedAgentExecutableSha256;

        public static async Task<StudioTwoAgentExternalProcessHarness> PrepareAsync(
            StudioProductionFixture fixture,
            string agentBundleRoot,
            string expectedAgentExecutableSha256,
            Uri brokerUri,
            string serviceScope,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(fixture);
            ArgumentNullException.ThrowIfNull(brokerUri);
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException(
                    "The staged two-Agent process harness requires Windows.");
            }

            var canonicalBundleRoot = RequiredDirectory(
                agentBundleRoot,
                "staged Agent bundle root");
            var canonicalExpectedAgentSha256 = RequireStudioSha256(
                expectedAgentExecutableSha256,
                nameof(expectedAgentExecutableSha256));
            foreach (var fileName in new[]
                     {
                         "OpenLineOps.Agent.exe",
                         "OpenLineOps.StationRuntime.exe",
                         "OpenLineOps.PluginHost.exe",
                         "OpenLineOps.ScriptWorker.exe",
                         "OpenLineOps.LeastPrivilegeLauncher.exe"
                     })
            {
                _ = RequiredDirectFile(canonicalBundleRoot, fileName);
            }
            var agentExecutablePath = RequiredDirectFile(
                canonicalBundleRoot,
                "OpenLineOps.Agent.exe");
            if (!string.Equals(
                    StudioSha256File(agentExecutablePath),
                    canonicalExpectedAgentSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The staged Agent executable differs from the release-manifest-bound SHA-256.");
            }

            if (brokerUri.Scheme is not ("amqp" or "amqps"))
            {
                throw new InvalidDataException(
                    "Studio two-Agent broker URI must use amqp or amqps.");
            }

            if (brokerUri.Scheme == "amqp" && !brokerUri.IsLoopback)
            {
                throw new InvalidDataException(
                    "Studio two-Agent plaintext RabbitMQ is restricted to loopback; remote brokers require amqps.");
            }

            var scope = serviceScope;
            if (scope.Length != 32
                || scope.Any(static character =>
                    character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            {
                throw new InvalidDataException(
                    "Studio two-Agent service scope must be 32 lowercase hexadecimal characters.");
            }
            var cleanupContract = ReadRequiredAgentServiceCleanupContract(
                expectedKind: "studio-two-agent",
                expectedScope: scope);
            if (cleanupContract.Entries.Any(entry => !string.Equals(
                    entry.ExecutableSha256,
                    canonicalExpectedAgentSha256,
                    StringComparison.Ordinal)))
            {
                throw new InvalidDataException(
                    "Studio cleanup manifest executable SHA-256 differs from release attestation.");
            }

            var ownedAgentBundleRoot = Path.GetDirectoryName(
                cleanupContract.Entries[0].ExecutablePath)
                ?? throw new InvalidDataException(
                    "Studio cleanup manifest Agent bundle path has no parent.");
            var entryIdentity = RestrictedAgentIdentity.CreateRequired(
                AgentServiceScopedSuffix("entry-service", scope));
            RestrictedAgentIdentity? downstreamIdentity = null;
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "Temp",
                $"olo-studio-two-agent-{scope}");
            if (!string.Equals(
                    root,
                    cleanupContract.Entries[0].OwnedRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "Studio cleanup manifest owned root differs from the harness root.");
            }
            var distribution = Path.Combine(root, "station-packages");
            var catalogs = Path.Combine(root, "deployment-catalog");
            var trust = Path.Combine(root, "trust");
            var observer = default(StudioVendorProcessStartObserver);
            var probe = default(StudioRabbitMqConsumerProbe);
            try
            {
                downstreamIdentity = RestrictedAgentIdentity.CreateRequired(
                    AgentServiceScopedSuffix("downstream-service", scope));
                if (string.Equals(
                        entryIdentity.Sid,
                        downstreamIdentity.Sid,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The production closure requires two distinct restricted service SIDs.");
                }

                Directory.CreateDirectory(root);
                ProtectStudioHarnessRoot(root);
                CopyFrozenAgentBundle(canonicalBundleRoot, ownedAgentBundleRoot);
                ProtectFrozenBundleRoot(ownedAgentBundleRoot);
                foreach (var entry in cleanupContract.Entries)
                {
                    VerifyCleanupExecutable(entry);
                }
                Directory.CreateDirectory(distribution);
                Directory.CreateDirectory(catalogs);
                Directory.CreateDirectory(trust);

                foreach (var station in new[] { fixture.EntryStation, fixture.DownstreamStation })
                {
                    var packageDestination = Path.Combine(
                        distribution,
                        $"{station.PackageContentSha256}.olopkg");
                    File.Copy(station.PackagePath, packageDestination, overwrite: false);
                    if (!string.Equals(
                            StudioSha256File(packageDestination),
                            station.PackageFileSha256,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            $"Copied Station package for {station.StationSystemId} changed.");
                    }

                    var catalogDestination = Path.Combine(
                        catalogs,
                        Path.GetFileName(station.DeploymentCatalogPath));
                    File.Copy(
                        station.DeploymentCatalogPath,
                        catalogDestination,
                        overwrite: false);
                    if (!string.Equals(
                            StudioSha256File(catalogDestination),
                            station.DeploymentCatalogSha256,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            $"Copied deployment catalog for {station.StationSystemId} changed.");
                    }
                }

                var trustedKey = Path.Combine(trust, "release-signing-public.pem");
                File.Copy(fixture.SigningPublicKeyPath, trustedKey, overwrite: false);
                if (!string.Equals(
                        StudioSha256File(trustedKey),
                        fixture.SigningPublicKeySha256,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Copied Studio signing public key changed.");
                }

                var safetyExecutable = PrepareSafetyNoOp(root);
                var entryAgent = CreateEndpoint(
                    fixture.EntryStation,
                    "preparation",
                    scope,
                    root);
                var downstreamAgent = CreateEndpoint(
                    fixture.DownstreamStation,
                    "vendor",
                    scope,
                    root);
                CreateStudioAgentWritableTree(entryAgent);
                CreateStudioAgentWritableTree(downstreamAgent);

                GrantStudioAgentAccess(
                    entryIdentity,
                    root,
                    ownedAgentBundleRoot,
                    distribution,
                    catalogs,
                    trust,
                    Path.GetDirectoryName(safetyExecutable)!,
                    entryAgent.RootPath);
                GrantStudioAgentAccess(
                    downstreamIdentity,
                    root,
                    ownedAgentBundleRoot,
                    distribution,
                    catalogs,
                    trust,
                    Path.GetDirectoryName(safetyExecutable)!,
                    downstreamAgent.RootPath);

                observer = await StudioVendorProcessStartObserver.StartAsync(
                    Path.Combine(root, "vendor-process-observer"),
                    cancellationToken);
                var coordinatorId = $"coordinator-studio-{scope}";
                probe = await StudioRabbitMqConsumerProbe.CreateAsync(
                    brokerUri,
                    coordinatorId,
                    cancellationToken);
                var operatorCredential = CreateCredential(
                    $"studio-operator-{scope}",
                    $"studio.operator.{scope}",
                    "Operator",
                    stationId: null);
                var safetyCredential = CreateCredential(
                    $"studio-safety-{scope}",
                    $"studio.safety.{scope}",
                    "Safety",
                    stationId: null);
                var entryCredential = CreateCredential(
                    $"studio-agent-entry-{scope}",
                    entryAgent.AgentId,
                    "StationAgent",
                    entryAgent.StationId,
                    entryAgent.Token);
                var downstreamCredential = CreateCredential(
                    $"studio-agent-downstream-{scope}",
                    downstreamAgent.AgentId,
                    "StationAgent",
                    downstreamAgent.StationId,
                    downstreamAgent.Token);
                return new StudioTwoAgentExternalProcessHarness(
                    fixture,
                    ownedAgentBundleRoot,
                    brokerUri,
                    root,
                    distribution,
                    catalogs,
                    trustedKey,
                    coordinatorId,
                    entryIdentity,
                    downstreamIdentity,
                    canonicalExpectedAgentSha256,
                    safetyExecutable,
                    observer,
                    probe,
                    entryAgent,
                    downstreamAgent,
                    [
                        operatorCredential,
                        safetyCredential,
                        entryCredential,
                        downstreamCredential
                    ]);
            }
            catch (Exception primaryFailure)
            {
                var cleanupFailures = new List<Exception> { primaryFailure };
                if (probe is not null)
                {
                    try
                    {
                        await probe.DisposeAsync();
                    }
                    catch (Exception exception)
                    {
                        cleanupFailures.Add(exception);
                    }
                }

                if (observer is not null)
                {
                    try
                    {
                        await observer.DisposeAsync();
                    }
                    catch (Exception exception)
                    {
                        cleanupFailures.Add(exception);
                    }
                }

                var failuresBeforeIdentityCleanup = cleanupFailures.Count;
                var allowedIdentitySids = new HashSet<string>(StringComparer.Ordinal)
                {
                    entryIdentity.Sid
                };
                if (downstreamIdentity is not null)
                {
                    allowedIdentitySids.Add(downstreamIdentity.Sid);
                }

                try
                {
                    VerifyRunScopedOwnedRootForIdentityCleanup(
                        root,
                        allowedIdentitySids);
                }
                catch (Exception exception)
                {
                    cleanupFailures.Add(exception);
                }

                if (cleanupFailures.Count == failuresBeforeIdentityCleanup)
                {
                    if (downstreamIdentity is not null)
                    {
                        try
                        {
                            downstreamIdentity.Dispose();
                        }
                        catch (Exception exception)
                        {
                            cleanupFailures.Add(exception);
                        }
                    }

                    try
                    {
                        entryIdentity.Dispose();
                    }
                    catch (Exception exception)
                    {
                        cleanupFailures.Add(exception);
                    }
                }

                if (cleanupFailures.Count == failuresBeforeIdentityCleanup
                    && Directory.Exists(root))
                {
                    try
                    {
                        DeleteRunScopedOwnedRoot(
                            "studio-two-agent",
                            root,
                            cleanupContract.Entries.ToDictionary(
                                static entry => entry.Role,
                                static entry => new RunScopedPackageCacheBinding(
                                    entry.ServiceSid,
                                    PackageCacheRootForCleanupEntry(entry)),
                                StringComparer.Ordinal));
                    }
                    catch (Exception exception)
                    {
                        cleanupFailures.Add(exception);
                    }
                }

                if (cleanupFailures.Count == 1)
                {
                    ExceptionDispatchInfo.Capture(primaryFailure).Throw();
                }

                throw new AggregateException(
                    "Studio two-Agent preparation failed and bounded cleanup was incomplete.",
                    cleanupFailures);
            }
        }

        public async Task StartAgentsAsync(
            Uri coordinatorBaseUri,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entryProcess is not null || _downstreamProcess is not null)
            {
                throw new InvalidOperationException("Studio Agents have already started.");
            }

            var entryEnvironment = CreateStudioAgentEnvironment(
                EntryAgent,
                Fixture.EntryStation,
                coordinatorBaseUri);
            var downstreamEnvironment = CreateStudioAgentEnvironment(
                DownstreamAgent,
                Fixture.DownstreamStation,
                coordinatorBaseUri);
            try
            {
                VerifyAgentExecutableHash();
                _entryService = WindowsAgentService.Install(
                    AgentExecutablePath,
                    _agentBundleRoot,
                    entryEnvironment,
                    _entryIdentity,
                    AgentServiceScopedSuffix("entry-service", EntryAgent.AgentId[^32..]),
                    EntryAgent.PackageCachePath);
                ProvisionPackageCache(
                    EntryAgent.PackageCachePath,
                    _entryService.ServiceName,
                    _entryIdentity.Sid);
                _entryProcess = _entryService.Start();
                VerifyStartedAgentProcess(_entryProcess, "entry");
                VerifyAgentExecutableHash();
                _downstreamService = WindowsAgentService.Install(
                    AgentExecutablePath,
                    _agentBundleRoot,
                    downstreamEnvironment,
                    _downstreamIdentity,
                    AgentServiceScopedSuffix(
                        "downstream-service",
                        DownstreamAgent.AgentId[^32..]),
                    DownstreamAgent.PackageCachePath);
                ProvisionPackageCache(
                    DownstreamAgent.PackageCachePath,
                    _downstreamService.ServiceName,
                    _downstreamIdentity.Sid);
                _downstreamProcess = _downstreamService.Start();
                VerifyStartedAgentProcess(_downstreamProcess, "downstream");
                if (string.Equals(
                        _entryIdentity.Sid,
                        _downstreamIdentity.Sid,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The two Station services resolved to the same restricted service SID.");
                }

                VerifyAgentExecutableHash();
                await Task.WhenAll(
                    _consumerProbe.WaitForConsumerAsync(
                        EntryAgent,
                        _entryProcess,
                        TimeSpan.FromSeconds(30),
                        cancellationToken),
                    _consumerProbe.WaitForConsumerAsync(
                        DownstreamAgent,
                        _downstreamProcess,
                        TimeSpan.FromSeconds(30),
                        cancellationToken));
                _materialArrivalIpcIsolation =
                    VerifyCrossStationMaterialArrivalIpcIsolation(cancellationToken);
            }
            catch (Exception primaryFailure)
            {
                var cleanupFailures = new List<Exception> { primaryFailure };
                if (_entryProcess is not null
                    && TryTerminateAndDisposeAgent(_entryProcess, "entry", cleanupFailures))
                {
                    _entryProcess = null;
                }

                if (_downstreamProcess is not null
                    && TryTerminateAndDisposeAgent(
                        _downstreamProcess,
                        "downstream",
                        cleanupFailures))
                {
                    _downstreamProcess = null;
                }

                if (_entryService is not null
                    && TryDisposeAgentService(
                        _entryService,
                        "entry",
                        cleanupFailures))
                {
                    _entryService = null;
                }

                if (_downstreamService is not null
                    && TryDisposeAgentService(
                        _downstreamService,
                        "downstream",
                        cleanupFailures))
                {
                    _downstreamService = null;
                }

                if (cleanupFailures.Count == 1)
                {
                    ExceptionDispatchInfo.Capture(primaryFailure).Throw();
                }

                throw new AggregateException(
                    "Studio Agents failed to start and bounded process-tree cleanup was incomplete.",
                    cleanupFailures);
            }
        }

        public IReadOnlyList<StudioVendorProcessStart> ReadVendorProcessStarts() =>
            _vendorObserver.ReadEntries();

        public async Task<IReadOnlyList<StudioVendorProcessStart>> WaitForVendorProcessStartsAsync(
            int minimumCount,
            TimeSpan timeout,
            CancellationToken cancellationToken = default) =>
            await _vendorObserver.WaitForCountAsync(minimumCount, timeout, cancellationToken);

        public async Task<StudioVendorProcessLedgerEvidence> FreezeVendorProcessLedgerAsync()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await _vendorObserver.DisposeAsync();
            return _vendorObserver.ReadFrozenEvidence();
        }

        public Task<IReadOnlyDictionary<string, (uint Messages, uint Consumers)>>
            ReadQueueStateAsync() =>
            _consumerProbe.ReadQueueStateAsync(EntryAgent, DownstreamAgent);

        public async Task<(int EntryExitCode, int DownstreamExitCode)> StopAgentsAsync()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var entry = _entryProcess;
            var downstream = _downstreamProcess;
            if (entry is null || downstream is null)
            {
                throw new InvalidOperationException("Both Studio Agents must be running.");
            }

            var failures = new List<Exception>();
            int? entryExitCode = null;
            int? downstreamExitCode = null;
            var entryStop = StopOneAgentAsync(
                entry,
                "entry",
                code => entryExitCode = code,
                failures);
            var downstreamStop = StopOneAgentAsync(
                downstream,
                "downstream",
                code => downstreamExitCode = code,
                failures);
            try
            {
                await Task.WhenAll(entryStop, downstreamStop);
            }
            finally
            {
                if (TryTerminateAndDisposeAgent(entry, "entry", failures))
                {
                    _entryProcess = null;
                }

                if (TryTerminateAndDisposeAgent(downstream, "downstream", failures))
                {
                    _downstreamProcess = null;
                }
            }

            VerifyAgentExecutableHash();
            if (failures.Count == 1)
            {
                ExceptionDispatchInfo.Capture(failures[0]).Throw();
            }

            if (failures.Count > 1)
            {
                throw new AggregateException(
                    "Studio Agents did not stop without requiring bounded process-tree cleanup.",
                    failures);
            }

            return (
                entryExitCode
                ?? throw new InvalidOperationException("Entry Agent produced no exit code."),
                downstreamExitCode
                ?? throw new InvalidOperationException("Downstream Agent produced no exit code."));
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            var failures = new List<Exception>();
            if (_entryProcess is not null
                && TryTerminateAndDisposeAgent(_entryProcess, "entry", failures))
            {
                _entryProcess = null;
            }

            if (_downstreamProcess is not null
                && TryTerminateAndDisposeAgent(_downstreamProcess, "downstream", failures))
            {
                _downstreamProcess = null;
            }

            if (_entryService is not null
                && TryDisposeAgentService(_entryService, "entry", failures))
            {
                _entryService = null;
            }

            if (_downstreamService is not null
                && TryDisposeAgentService(_downstreamService, "downstream", failures))
            {
                _downstreamService = null;
            }

            try
            {
                await _vendorObserver.DisposeAsync();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }

            if (!_rabbitProbeDisposed)
            {
                try
                {
                    _rabbitCleanup = new(
                        Attempted: true,
                        Succeeded: true,
                        QueueCount: await _consumerProbe.DeleteQueuesAsync(
                            EntryAgent,
                            DownstreamAgent));
                }
                catch (Exception exception)
                {
                    _rabbitCleanup = new(true, false, 0);
                    failures.Add(exception);
                }

                try
                {
                    await _consumerProbe.DisposeAsync();
                    _rabbitProbeDisposed = true;
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }

            var nativeResidue = _entryProcess is not null
                                || _downstreamProcess is not null
                                || _entryService is not null
                                || _downstreamService is not null
                                || _entryIdentity.HasUnprovenServiceInstallation
                                || _downstreamIdentity.HasUnprovenServiceInstallation;
            if (nativeResidue)
            {
                failures.Add(new InvalidOperationException(
                    "Studio Agent process or SCM service residue prevents safe service-SID ACL and owned-root cleanup."));
            }
            else
            {
                var failuresBeforeIdentityCleanup = failures.Count;
                try
                {
                    VerifyRunScopedOwnedRootForIdentityCleanup(
                        RootPath,
                        new HashSet<string>(StringComparer.Ordinal)
                        {
                            _entryIdentity.Sid,
                            _downstreamIdentity.Sid
                        });
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }

                if (failures.Count == failuresBeforeIdentityCleanup)
                {
                    try
                    {
                        _downstreamIdentity.Dispose();
                    }
                    catch (Exception exception)
                    {
                        failures.Add(exception);
                    }

                    try
                    {
                        _entryIdentity.Dispose();
                    }
                    catch (Exception exception)
                    {
                        failures.Add(exception);
                    }
                }

                if (failures.Count == failuresBeforeIdentityCleanup)
                {
                    try
                    {
                        DeleteRunScopedOwnedRoot(
                            "studio-two-agent",
                            RootPath,
                            new Dictionary<string, RunScopedPackageCacheBinding>(StringComparer.Ordinal)
                            {
                                ["entry"] = new(
                                    _entryIdentity.Sid,
                                    EntryAgent.PackageCachePath),
                                ["downstream"] = new(
                                    _downstreamIdentity.Sid,
                                    DownstreamAgent.PackageCachePath)
                            });
                    }
                    catch (Exception exception)
                    {
                        failures.Add(exception);
                    }
                }
            }

            _disposed = failures.Count == 0;
            if (failures.Count == 1)
            {
                ExceptionDispatchInfo.Capture(failures[0]).Throw();
            }

            if (failures.Count > 1)
            {
                throw new AggregateException(
                    "Studio two-Agent external process harness cleanup failed.",
                    failures);
            }
        }

        private static async Task StopOneAgentAsync(
            WindowsAgentProcess process,
            string role,
            Action<int> captureExitCode,
            List<Exception> failures)
        {
            try
            {
                captureExitCode(await process.StopCleanlyAsync(TimeSpan.FromSeconds(30)));
            }
            catch (Exception exception)
            {
                lock (failures)
                {
                    failures.Add(new InvalidOperationException(
                        $"The {role} Studio Agent did not stop cleanly.",
                        exception));
                }
            }
        }

        private static bool TryTerminateAndDisposeAgent(
            WindowsAgentProcess process,
            string role,
            List<Exception> failures)
        {
            try
            {
                process.Kill();
            }
            catch (Exception exception)
            {
                failures.Add(new InvalidOperationException(
                    $"The {role} Studio Agent process tree could not be terminated.",
                    exception));
            }

            bool exited;
            try
            {
                exited = process.HasExited;
            }
            catch (Exception exception)
            {
                failures.Add(new InvalidOperationException(
                    $"The {role} Studio Agent exit state could not be confirmed.",
                    exception));
                return false;
            }

            if (!exited)
            {
                failures.Add(new InvalidOperationException(
                    $"The {role} Studio Agent remains alive after bounded process-tree cleanup."));
                return false;
            }

            try
            {
                process.Dispose();
            }
            catch (Exception exception)
            {
                failures.Add(new InvalidOperationException(
                    $"The {role} Studio Agent process handle could not be disposed.",
                    exception));
                return false;
            }

            return true;
        }

        private static bool TryDisposeAgentService(
            WindowsAgentService service,
            string role,
            List<Exception> failures)
        {
            try
            {
                service.Dispose();
            }
            catch (Exception exception)
            {
                failures.Add(new InvalidOperationException(
                    $"The {role} Studio Agent SCM service could not be deleted with proof.",
                    exception));
                return service.DeletionProven;
            }

            return service.DeletionProven;
        }

        private void VerifyAgentExecutableHash()
        {
            var actual = StudioSha256File(AgentExecutablePath);
            if (!string.Equals(
                    actual,
                    _expectedAgentExecutableSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The staged Agent executable changed after release attestation.");
            }
        }

        private StudioMaterialArrivalIpcIsolationEvidence
            VerifyCrossStationMaterialArrivalIpcIsolation(
                CancellationToken cancellationToken)
        {
            var entryProcess = _entryProcess
                               ?? throw new InvalidOperationException(
                                   "Entry Agent has not started.");
            var downstreamProcess = _downstreamProcess
                                    ?? throw new InvalidOperationException(
                                        "Downstream Agent has not started.");
            var entryService = _entryService
                               ?? throw new InvalidOperationException(
                                   "Entry Agent service has not started.");
            var downstreamService = _downstreamService
                                    ?? throw new InvalidOperationException(
                                        "Downstream Agent service has not started.");
            if (entryProcess.HasExited || downstreamProcess.HasExited)
            {
                throw new InvalidOperationException(
                    "Both Station Agents must remain alive during local IPC isolation verification.");
            }

            RequireElevatedTestProcess();
            var entryServiceSid = EntryAgentServiceSid;
            var downstreamServiceSid = DownstreamAgentServiceSid;
            if (string.Equals(
                    entryServiceSid,
                    downstreamServiceSid,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Cross-Station local IPC isolation requires distinct restricted service SIDs.");
            }

            var entryPipeName = StationMaterialArrivalLocalIpcOptions.DerivePipeName(
                entryServiceSid);
            using var entryToken = DuplicateServiceProcessImpersonationToken(
                entryProcess.Id,
                "entry");
            var entryImpersonationEntered = false;
            var entryServiceTokenConnected = false;
            var entryPipeExactAclVerified = false;
            WindowsIdentity.RunImpersonated(entryToken, () =>
            {
                entryImpersonationEntered = true;
                cancellationToken.ThrowIfCancellationRequested();
                using var pipe = new NamedPipeClientStream(
                    ".",
                    entryPipeName,
                    PipeDirection.InOut,
                    PipeOptions.None);
                pipe.Connect(checked((int)MaterialArrivalPipeConnectTimeout.TotalMilliseconds));
                entryServiceTokenConnected = pipe.IsConnected;
                WindowsIdentityBoundNamedPipe.Verify(pipe, entryServiceSid);
                entryPipeExactAclVerified = true;
            });
            if (!entryImpersonationEntered
                || !entryServiceTokenConnected
                || !entryPipeExactAclVerified)
            {
                throw new InvalidOperationException(
                    "The entry Station process token did not connect to and verify the exact material-arrival pipe ACL.");
            }

            using var downstreamToken = DuplicateServiceProcessImpersonationToken(
                downstreamProcess.Id,
                "downstream");
            var downstreamImpersonationEntered = false;
            var crossStationFailure = WindowsIdentity.RunImpersonated(
                downstreamToken,
                () =>
                {
                    downstreamImpersonationEntered = true;
                    return ProbeCrossStationPipeUntilTerminalResult(
                        entryPipeName,
                        MaterialArrivalPipeConnectTimeout,
                        cancellationToken);
                });
            if (!downstreamImpersonationEntered)
            {
                throw new InvalidOperationException(
                    "The downstream Station process token was not applied to the cross-Station pipe probe.");
            }

            if (crossStationFailure is null)
            {
                throw new UnauthorizedAccessException(
                    "The downstream Station service token connected to the entry Station material-arrival pipe.");
            }

            if (!IsExplicitAccessDenied(crossStationFailure))
            {
                var nativeError = WindowsErrorCode(crossStationFailure);
                var reason = nativeError switch
                {
                    ErrorFileNotFound => "the entry pipe was absent",
                    ErrorPipeBusy => "the entry pipe was busy",
                    _ when crossStationFailure is TimeoutException =>
                        "the cross-Station connection timed out",
                    _ => "the connection failed without an explicit access-denied result"
                };
                throw new InvalidOperationException(
                    $"Cross-Station material-arrival isolation was not proven because {reason}.",
                    crossStationFailure);
            }

            VerifyServiceStillRunning(
                entryService.ServiceName,
                entryProcess.Id,
                "entry");
            VerifyServiceStillRunning(
                downstreamService.ServiceName,
                downstreamProcess.Id,
                "downstream");
            if (entryProcess.HasExited || downstreamProcess.HasExited)
            {
                throw new InvalidOperationException(
                    "A Station Agent exited during local IPC isolation verification.");
            }

            return new StudioMaterialArrivalIpcIsolationEvidence(
                entryServiceTokenConnected,
                entryPipeExactAclVerified,
                DownstreamServiceTokenExplicitAccessDenied: true,
                BothServicesRunningOnOriginalPids: true);
        }

        internal static bool IsExplicitAccessDenied(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            return WindowsErrorCode(exception) == ErrorAccessDenied;
        }

        internal static bool IsRetryablePipeAvailabilityFailure(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            return WindowsErrorCode(exception) is ErrorFileNotFound or ErrorPipeBusy;
        }

        private static Exception? ProbeCrossStationPipeUntilTerminalResult(
            string pipeName,
            TimeSpan totalTimeout,
            CancellationToken cancellationToken)
        {
            var elapsed = Stopwatch.StartNew();
            Exception? lastAvailabilityFailure = null;
            while (elapsed.Elapsed < totalTimeout)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var pipe = new NamedPipeClientStream(
                    ".",
                    pipeName,
                    PipeDirection.InOut,
                    PipeOptions.None);
                var remaining = totalTimeout - elapsed.Elapsed;
                try
                {
                    pipe.Connect(Math.Max(
                        1,
                        checked((int)Math.Ceiling(remaining.TotalMilliseconds))));
                    return null;
                }
                catch (Exception exception) when (exception is IOException
                                                   or UnauthorizedAccessException
                                                   or Win32Exception
                                                   or TimeoutException)
                {
                    if (!IsRetryablePipeAvailabilityFailure(exception))
                    {
                        return exception;
                    }

                    lastAvailabilityFailure = exception;
                }

                remaining = totalTimeout - elapsed.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                Thread.Sleep(remaining < TimeSpan.FromMilliseconds(25)
                    ? remaining
                    : TimeSpan.FromMilliseconds(25));
            }

            return new TimeoutException(
                "The cross-Station material-arrival pipe did not become available before its total probe deadline.",
                lastAvailabilityFailure);
        }

        private static int? WindowsErrorCode(Exception exception) => exception switch
        {
            Win32Exception win32Exception => win32Exception.NativeErrorCode,
            IOException ioException => ioException.HResult & 0xFFFF,
            UnauthorizedAccessException unauthorizedAccessException =>
                unauthorizedAccessException.HResult & 0xFFFF,
            _ => null
        };

        private static void RequireElevatedTestProcess()
        {
            using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            var principal = new WindowsPrincipal(identity);
            if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
            {
                throw new InvalidOperationException(
                    "The formal two-Agent SCM gate must run in an elevated test process before it can duplicate Station service tokens.");
            }
        }

        private static SafeAccessTokenHandle DuplicateServiceProcessImpersonationToken(
            int processId,
            string role)
        {
            using var process = OpenProcess(
                ProcessQueryLimitedInformation,
                inheritHandle: false,
                checked((uint)processId));
            if (process.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                throw new Win32Exception(
                    error,
                    $"Could not open the {role} Station Agent process for token duplication.");
            }

            if (!OpenProcessToken(
                    process,
                    TokenQuery | TokenDuplicate,
                    out var sourceToken))
            {
                var error = Marshal.GetLastWin32Error();
                throw new Win32Exception(
                    error,
                    $"Could not open the {role} Station Agent token with TOKEN_QUERY | TOKEN_DUPLICATE.");
            }

            using (sourceToken)
            {
                if (!DuplicateTokenEx(
                        sourceToken,
                        TokenQuery | TokenImpersonate,
                        IntPtr.Zero,
                        NativeSecurityImpersonationLevel.SecurityImpersonation,
                        NativeTokenType.TokenImpersonation,
                        out var impersonationToken))
                {
                    var error = Marshal.GetLastWin32Error();
                    throw new Win32Exception(
                        error,
                        $"Could not create the {role} Station Agent SecurityImpersonation TokenImpersonation token.");
                }

                return impersonationToken;
            }
        }

        private static void VerifyServiceStillRunning(
            string serviceName,
            int expectedProcessId,
            string role)
        {
            using var manager = OpenSCManager(
                machineName: null,
                databaseName: null,
                ScManagerConnect);
            if (manager.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                throw new Win32Exception(
                    error,
                    "Could not open the Service Control Manager while verifying local IPC isolation.");
            }

            using var service = OpenService(
                manager,
                serviceName,
                ServiceQueryStatus);
            if (service.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                throw new Win32Exception(
                    error,
                    $"Could not open the {role} Station service after local IPC isolation verification.");
            }

            var status = new NativeServiceStatusProcess();
            if (!QueryServiceStatusEx(
                    service,
                    ScStatusProcessInfo,
                    ref status,
                    checked((uint)Marshal.SizeOf<NativeServiceStatusProcess>()),
                    out _))
            {
                var error = Marshal.GetLastWin32Error();
                throw new Win32Exception(
                    error,
                    $"Could not query the {role} Station service after local IPC isolation verification.");
            }

            if (status.CurrentState != ServiceRunning
                || status.ProcessId != checked((uint)expectedProcessId))
            {
                throw new InvalidOperationException(
                    $"The {role} Station service was not still Running on its original PID after local IPC isolation verification.");
            }
        }

        private void VerifyStartedAgentProcess(
            WindowsAgentProcess process,
            string role)
        {
            if (!string.Equals(
                    process.ExecutablePath,
                    AgentExecutablePath,
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    process.ExecutableSha256,
                    _expectedAgentExecutableSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"The started {role} Agent process does not match the release-manifest-bound executable.");
            }
        }

        private Dictionary<string, string> CreateStudioAgentEnvironment(
            StudioAgentEndpoint endpoint,
            StudioStationFixture station,
            Uri coordinatorBaseUri)
        {
            var environment = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var name in SafeAgentEnvironmentVariables)
            {
                var value = Environment.GetEnvironmentVariable(name);
                if (!string.IsNullOrEmpty(value))
                {
                    environment.Add(name, value);
                }
            }

            var privateTemp = Path.Combine(endpoint.RootPath, "private-temp");
            Directory.CreateDirectory(privateTemp);
            environment.Add("TEMP", privateTemp);
            environment.Add("TMP", privateTemp);

            Set("AgentId", endpoint.AgentId);
            Set("StationId", endpoint.StationId);
            Set("StationSystemId", endpoint.StationSystemId);
            Set("HeartbeatInterval", "00:00:01");
            Set("DataDirectory", endpoint.DataPath);
            Set("BrokerUri", _brokerUri.AbsoluteUri);
            Set("RequireBrokerTls", IsTls(_brokerUri).ToString());
            Set("PrefetchCount", "1");
            Set("MaximumConcurrentJobs", "1");
            Set("PackageDistributionDirectory", PackageDistributionPath);
            Set("PackageCacheDirectory", endpoint.PackageCachePath);
            Set("MaterialArrivalPackageContentSha256", endpoint.PackageContentSha256);
            Set(
                $"TrustedPackagePublicKeyFiles__{station.SigningKeyId}",
                TrustedSigningKeyPath);
            Set("RuntimeExecutablePath", "OpenLineOps.StationRuntime.exe");
            Set("PluginHostExecutablePath", "OpenLineOps.PluginHost.exe");
            Set("PythonScript__WorkerExecutablePath", "OpenLineOps.ScriptWorker.exe");
            Set("PythonScript__HostPythonRuntimeDllPath", RequiredHostPythonRuntimeDllPath());
            Set("PythonScript__Sandbox__RequireLeastPrivilegeExecution", "true");
            Set("PythonScript__Sandbox__IsolationMode", "LeastPrivilegeIdentity");
            Set("PythonScript__Sandbox__LeastPrivilegeIdentity", "PerExecutionAppContainer");
            Set(
                "PythonScript__Sandbox__LeastPrivilegeLauncherExecutable",
                "OpenLineOps.LeastPrivilegeLauncher.exe");
            Set("PythonScript__Sandbox__LeastPrivilegeNoInteractivePrompt", "true");
            Set("PythonScript__Sandbox__LeastPrivilegeArgumentsTemplate", string.Empty);
            Set("RuntimeWorkingDirectory", endpoint.RuntimeWorkPath);
            Set("ArtifactDirectory", Path.Combine(endpoint.RootPath, "artifacts"));
            Set("CoordinatorBaseUri", coordinatorBaseUri.AbsoluteUri);
            Set("ArtifactUploadBearerToken", endpoint.Token);
            Set("ArtifactUploadTimeout", "00:01:00");
            Set("RuntimeTimeout", "00:01:30");
            Set(
                "MaximumRuntimeOutputBytes",
                (2 * 1024 * 1024).ToString(CultureInfo.InvariantCulture));
            Set(
                "ExternalProgramAppContainerProfileNamespace",
                $"OpenLineOps.Studio.{endpoint.AgentId[^20..]}");
            Set("SafetyExecutablePath", _safetyExecutable);
            Set("SafetyWorkingDirectory", Path.Combine(endpoint.RootPath, "safety-work"));
            Set("SafetyTimeout", "00:00:05");
            environment["Logging__LogLevel__Default"] = "Information";
            environment["Logging__LogLevel__Microsoft.Hosting.Lifetime"] = "Information";
            return environment;

            void Set(string key, string value) =>
                environment[$"OpenLineOps__Agent__{key}"] = value;
        }

        private string SharedServiceAccountValue(
            Func<AgentHostTokenEvidence, string> selector,
            string description)
        {
            var entryEvidence = _entryProcess?.TokenEvidence
                                ?? throw new InvalidOperationException(
                                    "Entry Agent has not started.");
            var downstreamEvidence = _downstreamProcess?.TokenEvidence
                                     ?? throw new InvalidOperationException(
                                         "Downstream Agent has not started.");
            var entryValue = selector(entryEvidence);
            var downstreamValue = selector(downstreamEvidence);
            if (!string.Equals(entryValue, downstreamValue, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Studio Agent LocalService {description} differs across Station services.");
            }

            if (!string.Equals(
                    entryValue,
                    _entryIdentity.ServiceAccountSid,
                    StringComparison.Ordinal)
                || !string.Equals(
                    downstreamValue,
                    _downstreamIdentity.ServiceAccountSid,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Studio Agent token {description} is not the configured LocalService identity.");
            }

            return entryValue;
        }

        private static string StartedServiceSid(
            WindowsAgentProcess? process,
            RestrictedAgentIdentity identity,
            string role)
        {
            if (process is null)
            {
                throw new InvalidOperationException($"{role} Agent has not started.");
            }

            if (!process.TokenEvidence.ExactServiceSidPresent
                || !process.TokenEvidence.ExactServiceSidEnabled
                || !process.TokenEvidence.ExactServiceSidRestricted)
            {
                throw new InvalidOperationException(
                    $"{role} Agent token does not carry its enabled restricted service SID.");
            }

            return identity.Sid;
        }

        private static StudioAgentEndpoint CreateEndpoint(
            StudioStationFixture station,
            string role,
            string suffix,
            string root)
        {
            var agentRoot = Path.Combine(root, $"{role}-agent");
            var serviceRole = string.Equals(role, "preparation", StringComparison.Ordinal)
                ? "entry"
                : "downstream";
            var serviceSuffix = AgentServiceScopedSuffix($"{serviceRole}-service", suffix);
            var cacheAnchor = Path.Combine(
                Path.GetDirectoryName(root)
                ?? throw new InvalidDataException("Studio harness root has no parent."),
                $"olo-studio-two-agent-{serviceRole}-content-{serviceSuffix}");
            return new StudioAgentEndpoint(
                $"agent.studio.{role}.{suffix}",
                station.StationId,
                station.StationSystemId,
                station.PackageContentSha256,
                StudioToken($"agent-{role}-{suffix}"),
                agentRoot,
                Path.Combine(agentRoot, "data"),
                Path.Combine(cacheAnchor, "content"),
                Path.Combine(agentRoot, "runtime-work"));
        }

        private static void ProvisionPackageCache(
            string packageCacheRoot,
            string serviceName,
            string serviceSid)
        {
            new ImmutableContentProtector().ProvisionCacheNamespace(
                packageCacheRoot,
                serviceName,
                new ImmutableContentProtectionPolicy(
                    WindowsAppContainerIdentity.EnsureCapabilitySid(
                        WindowsAppContainerIdentity.ExternalProgramContentCapabilityName),
                    serviceSid));
        }

        private static void CreateStudioAgentWritableTree(StudioAgentEndpoint endpoint)
        {
            foreach (var path in new[]
                     {
                         endpoint.RootPath,
                         endpoint.DataPath,
                         endpoint.RuntimeWorkPath,
                         Path.Combine(endpoint.RootPath, "artifacts"),
                         Path.Combine(endpoint.RootPath, "private-temp"),
                         Path.Combine(endpoint.RootPath, "safety-work")
                     })
            {
                Directory.CreateDirectory(path);
            }
        }

        private static void GrantStudioAgentAccess(
            RestrictedAgentIdentity identity,
            string root,
            string bundleRoot,
            string distribution,
            string catalogs,
            string trust,
            string safetyRoot,
            string writableAgentRoot)
        {
            identity.GrantDirectoryAccess(
                root,
                FileSystemRights.Traverse
                | FileSystemRights.ReadAttributes
                | FileSystemRights.Synchronize,
                InheritanceFlags.None);
            foreach (var immutableRoot in new[]
                     {
                         bundleRoot,
                         distribution,
                         catalogs,
                         trust,
                         safetyRoot
                     })
            {
                identity.GrantDirectoryAccess(
                    immutableRoot,
                    FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize);
            }

            identity.GrantDirectoryAccess(
                writableAgentRoot,
                FileSystemRights.Modify | FileSystemRights.Synchronize);
        }

        private static void ProtectStudioHarnessRoot(string root)
            => ProtectRunScopedRoot(root);

        private enum NativeSecurityImpersonationLevel
        {
            SecurityAnonymous,
            SecurityIdentification,
            SecurityImpersonation,
            SecurityDelegation
        }

        private enum NativeTokenType
        {
            TokenPrimary = 1,
            TokenImpersonation
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeServiceStatusProcess
        {
            public uint ServiceType;
            public uint CurrentState;
            public uint ControlsAccepted;
            public uint Win32ExitCode;
            public uint ServiceSpecificExitCode;
            public uint CheckPoint;
            public uint WaitHint;
            public uint ProcessId;
            public uint ServiceFlags;
        }

        private sealed class SafeNativeServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public SafeNativeServiceHandle()
                : base(ownsHandle: true)
            {
            }

            protected override bool ReleaseHandle() => CloseServiceHandle(handle);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeProcessHandle OpenProcess(
            uint desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
            uint processId);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenProcessToken(
            SafeProcessHandle processHandle,
            uint desiredAccess,
            out SafeAccessTokenHandle tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DuplicateTokenEx(
            SafeAccessTokenHandle existingToken,
            uint desiredAccess,
            IntPtr tokenAttributes,
            NativeSecurityImpersonationLevel impersonationLevel,
            NativeTokenType tokenType,
            out SafeAccessTokenHandle newToken);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeNativeServiceHandle OpenSCManager(
            string? machineName,
            string? databaseName,
            uint desiredAccess);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeNativeServiceHandle OpenService(
            SafeNativeServiceHandle serviceControlManager,
            string serviceName,
            uint desiredAccess);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryServiceStatusEx(
            SafeNativeServiceHandle service,
            uint infoLevel,
            ref NativeServiceStatusProcess serviceStatus,
            uint bufferSize,
            out uint bytesNeeded);

        [DllImport("advapi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseServiceHandle(IntPtr serviceHandle);

        private static string RequireStudioSha256(string value, string name) =>
            value.Length == 64
            && value.All(static character =>
                character is >= '0' and <= '9' or >= 'a' and <= 'f')
                ? value
                : throw new ArgumentException(
                    $"{name} must be a lowercase SHA-256.",
                    name);

        private static StudioApiCredential CreateCredential(
            string credentialId,
            string actorId,
            string role,
            string? stationId,
            string? token = null)
        {
            var plainToken = token ?? StudioToken($"{credentialId}-{actorId}-{role}");
            return new StudioApiCredential(
                credentialId,
                actorId,
                plainToken,
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(plainToken))),
                role,
                stationId);
        }

        private static string StudioToken(string purpose) => Convert.ToBase64String(
                SHA256.HashData(Encoding.UTF8.GetBytes(
                    $"OpenLineOps.StudioTwoAgent:{purpose}:{Guid.NewGuid():N}")))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    [SupportedOSPlatform("windows")]
    private sealed class StudioVendorProcessStartObserver : IAsyncDisposable
    {
        private const string LedgerSchema = "openlineops.vendor-process-start";
        private readonly Process _process;
        private readonly string _ledgerPath;
        private readonly string _stopPath;
        private readonly string _readyPath;
        private readonly Task<string> _standardOutput;
        private readonly Task<string> _standardError;
        private bool _disposed;

        private StudioVendorProcessStartObserver(
            Process process,
            string ledgerPath,
            string stopPath,
            string readyPath,
            Task<string> standardOutput,
            Task<string> standardError)
        {
            _process = process;
            _ledgerPath = ledgerPath;
            _stopPath = stopPath;
            _readyPath = readyPath;
            _standardOutput = standardOutput;
            _standardError = standardError;
        }

        public static async Task<StudioVendorProcessStartObserver> StartAsync(
            string root,
            CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(root);
            if (File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException(
                    "Vendor process observer root cannot be a reparse point.");
            }

            var ledgerPath = Path.Combine(root, "vendor-process-starts.jsonl");
            var stopPath = Path.Combine(root, "stop.request");
            var readyPath = Path.Combine(root, "ready");
            var script = """
                $ErrorActionPreference = 'Stop'
                $ledger = [Environment]::GetEnvironmentVariable('OLO_VENDOR_LEDGER_PATH')
                $ready = [Environment]::GetEnvironmentVariable('OLO_VENDOR_READY_PATH')
                $stop = [Environment]::GetEnvironmentVariable('OLO_VENDOR_STOP_PATH')
                $utf8 = [Text.UTF8Encoding]::new($false)
                $stream = [IO.File]::Open($ledger, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
                $writer = [IO.StreamWriter]::new($stream, $utf8)
                $writer.AutoFlush = $true
                $watcher = [System.Management.ManagementEventWatcher]::new(
                    "SELECT * FROM Win32_ProcessStartTrace WHERE ProcessName = 'OpenLineOps.VendorTestHelper.exe'")
                $watcher.Options.Timeout = [TimeSpan]::FromMilliseconds(500)
                $sequence = 0
                try {
                  $watcher.Start()
                  [IO.File]::WriteAllText($ready, 'ready', $utf8)
                  while (-not [IO.File]::Exists($stop)) {
                    try {
                      $event = $watcher.WaitForNextEvent()
                    } catch [System.Management.ManagementException] {
                      if ($_.Exception.ErrorCode -eq [System.Management.ManagementStatus]::Timedout) {
                        continue
                      }
                      throw
                    }
                    $sequence++
                    $ancestors = [Collections.Generic.List[int]]::new()
                    $cursor = [int]$event.ParentProcessID
                    for ($depth = 0; $depth -lt 16 -and $cursor -gt 0; $depth++) {
                      $ancestors.Add($cursor)
                      $parent = Get-CimInstance Win32_Process -Filter "ProcessId = $cursor" -ErrorAction SilentlyContinue
                      if ($null -eq $parent) { break }
                      $next = [int]$parent.ParentProcessId
                      if ($next -le 0 -or $ancestors.Contains($next)) { break }
                      $cursor = $next
                    }
                    $entry = [ordered]@{
                      schema = 'openlineops.vendor-process-start'
                      schemaVersion = 1
                      sequence = $sequence
                      processId = [int]$event.ProcessID
                      parentProcessId = [int]$event.ParentProcessID
                      startedAtUtc = [DateTime]::FromFileTimeUtc([long]$event.TIME_CREATED).ToString('O')
                      ancestorProcessIds = $ancestors.ToArray()
                    }
                    $writer.WriteLine(($entry | ConvertTo-Json -Compress))
                    $writer.Flush()
                    $stream.Flush($true)
                  }
                } finally {
                  try { $watcher.Stop() } catch {}
                  $watcher.Dispose()
                  $writer.Dispose()
                  $stream.Dispose()
                }
                """;
            var encoded = Convert.ToBase64String(
                Encoding.Unicode.GetBytes(script));
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = root
            };
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-EncodedCommand");
            startInfo.ArgumentList.Add(encoded);
            startInfo.Environment.Clear();
            CopyEnvironment(startInfo.Environment, "SystemRoot");
            CopyEnvironment(startInfo.Environment, "WINDIR");
            CopyEnvironment(startInfo.Environment, "PATH");
            startInfo.Environment["OLO_VENDOR_LEDGER_PATH"] = ledgerPath;
            startInfo.Environment["OLO_VENDOR_READY_PATH"] = readyPath;
            startInfo.Environment["OLO_VENDOR_STOP_PATH"] = stopPath;
            var process = Process.Start(startInfo)
                          ?? throw new InvalidOperationException(
                              "Vendor process start observer did not start.");
            var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
            var observer = new StudioVendorProcessStartObserver(
                process,
                ledgerPath,
                stopPath,
                readyPath,
                stdout,
                stderr);
            try
            {
                await observer.WaitForReadyAsync(
                    TimeSpan.FromSeconds(15),
                    cancellationToken);
                return observer;
            }
            catch
            {
                await observer.DisposeAsync();
                throw;
            }
        }

        public List<StudioVendorProcessStart> ReadEntries()
        {
            if (!File.Exists(_ledgerPath))
            {
                return [];
            }

            var result = new List<StudioVendorProcessStart>();
            var expectedSequence = 1L;
            foreach (var line in File.ReadLines(_ledgerPath, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    throw new InvalidDataException(
                        "Vendor process ledger contains a blank entry.");
                }

                using var document = JsonDocument.Parse(
                    line,
                    new JsonDocumentOptions
                    {
                        AllowTrailingCommas = false,
                        CommentHandling = JsonCommentHandling.Disallow,
                        MaxDepth = 8
                    });
                StudioRejectDuplicateProperties(
                    document.RootElement,
                    "vendor process ledger entry");
                StudioRequireExactProperties(
                    document.RootElement,
                    "vendor process ledger entry",
                    "schema",
                    "schemaVersion",
                    "sequence",
                    "processId",
                    "parentProcessId",
                    "startedAtUtc",
                    "ancestorProcessIds");
                StudioRequireExactString(
                    document.RootElement,
                    "schema",
                    LedgerSchema);
                if (StudioRequiredInt64(document.RootElement, "schemaVersion") != 1
                    || StudioRequiredInt64(document.RootElement, "sequence") != expectedSequence)
                {
                    throw new InvalidDataException(
                        "Vendor process ledger schema or append sequence is invalid.");
                }

                var parentProcessId = StudioRequiredNonNegativeInt32(
                    document.RootElement,
                    "parentProcessId");
                var ancestorElement = StudioRequiredProperty(
                    document.RootElement,
                    "ancestorProcessIds");
                if (ancestorElement.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidDataException(
                        "Vendor process ancestorProcessIds must be an array.");
                }

                var ancestors = ancestorElement.EnumerateArray()
                    .Select(static ancestor => ancestor.ValueKind == JsonValueKind.Number
                                               && ancestor.TryGetInt32(out var value)
                                               && value > 0
                        ? value
                        : throw new InvalidDataException(
                            "Vendor process ancestorProcessIds contains an invalid PID."))
                    .ToArray();
                if (ancestors.Length is < 1 or > 16
                    || ancestors[0] != parentProcessId
                    || ancestors.Distinct().Count() != ancestors.Length)
                {
                    throw new InvalidDataException(
                        "Vendor process ancestor chain is empty, cyclic, or does not begin at parentProcessId.");
                }

                result.Add(new StudioVendorProcessStart(
                    expectedSequence,
                    StudioRequiredPositiveInt32(document.RootElement, "processId"),
                    parentProcessId,
                    StudioRequiredUtcTimestamp(
                        document.RootElement,
                        "startedAtUtc"),
                    ancestors));
                expectedSequence++;
            }

            return result;
        }

        public StudioVendorProcessLedgerEvidence ReadFrozenEvidence()
        {
            if (!_disposed || !_process.HasExited)
            {
                throw new InvalidOperationException(
                    "Vendor process ledger evidence may be read only after the observer is stopped.");
            }

            var bytes = File.ReadAllBytes(_ledgerPath);
            var entries = ReadEntries();
            return new StudioVendorProcessLedgerEvidence(
                entries,
                bytes.LongLength,
                Convert.ToHexStringLower(SHA256.HashData(bytes)),
                Convert.ToBase64String(bytes));
        }

        public async Task<IReadOnlyList<StudioVendorProcessStart>> WaitForCountAsync(
            int minimumCount,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(minimumCount);
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < timeout)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_process.HasExited)
                {
                    throw new InvalidOperationException(
                        $"Vendor process observer exited with code {_process.ExitCode}. "
                        + $"stdout={await _standardOutput} stderr={await _standardError}");
                }

                var entries = ReadEntries();
                if (entries.Count >= minimumCount)
                {
                    return entries;
                }

                await Task.Delay(100, cancellationToken);
            }

            throw new TimeoutException(
                $"Vendor process observer did not record {minimumCount} starts within {timeout}.");
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (!_process.HasExited)
            {
                await File.WriteAllTextAsync(
                    _stopPath,
                    "stop",
                    StudioUtf8WithoutBom);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    await _process.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(10_000);
                    throw new TimeoutException(
                        "Vendor process observer did not stop after its append ledger was flushed.");
                }
            }

            var exitCode = _process.ExitCode;
            var stdout = await _standardOutput;
            var stderr = await _standardError;
            _process.Dispose();
            if (exitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Vendor process observer exited with code {exitCode}. stdout={stdout} stderr={stderr}");
            }
        }

        private async Task WaitForReadyAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < timeout)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(_readyPath) && File.Exists(_ledgerPath))
                {
                    return;
                }

                if (_process.HasExited)
                {
                    throw new InvalidOperationException(
                        $"Vendor process observer exited before readiness with code {_process.ExitCode}. "
                        + $"stdout={await _standardOutput} stderr={await _standardError}");
                }

                await Task.Delay(100, cancellationToken);
            }

            throw new TimeoutException("Vendor process observer did not become ready.");
        }

        private static void CopyEnvironment(
            IDictionary<string, string?> environment,
            string key)
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (value is not null)
            {
                environment[key] = value;
            }
        }
    }

    private sealed class StudioRabbitMqConsumerProbe : IAsyncDisposable
    {
        private readonly IConnection _connection;
        private readonly string _coordinatorId;

        private StudioRabbitMqConsumerProbe(
            IConnection connection,
            string coordinatorId)
        {
            _connection = connection;
            _coordinatorId = coordinatorId;
        }

        public static async Task<StudioRabbitMqConsumerProbe> CreateAsync(
            Uri brokerUri,
            string coordinatorId,
            CancellationToken cancellationToken)
        {
            var factory = new ConnectionFactory
            {
                Uri = brokerUri,
                ClientProvidedName = $"openlineops-studio-probe-{coordinatorId}",
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = true,
                RequestedConnectionTimeout = TimeSpan.FromSeconds(5),
                HandshakeContinuationTimeout = TimeSpan.FromSeconds(5),
                ContinuationTimeout = TimeSpan.FromSeconds(5),
                SocketReadTimeout = TimeSpan.FromSeconds(5),
                SocketWriteTimeout = TimeSpan.FromSeconds(5)
            };
            var connection = await factory.CreateConnectionAsync(cancellationToken);
            return new StudioRabbitMqConsumerProbe(connection, coordinatorId);
        }

        [SupportedOSPlatform("windows")]
        public async Task WaitForConsumerAsync(
            StudioAgentEndpoint endpoint,
            WindowsAgentProcess process,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var queue = StationTransportRoute.JobQueue(endpoint.AgentId, endpoint.StationId);
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < timeout)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (process.HasExited)
                {
                    throw new InvalidOperationException(
                        $"Staged Agent {endpoint.AgentId} exited with code {process.ExitCode}.");
                }

                try
                {
                    using var operationTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken);
                    operationTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                    await using var channel = await _connection.CreateChannelAsync(
                        cancellationToken: operationTimeout.Token);
                    var state = await channel.QueueDeclarePassiveAsync(
                        queue,
                        operationTimeout.Token);
                    if (state.ConsumerCount == 1)
                    {
                        return;
                    }
                }
                catch (RabbitMQ.Client.Exceptions.OperationInterruptedException exception)
                    when (exception.ShutdownReason?.ReplyCode == 404)
                {
                }

                await Task.Delay(100, cancellationToken);
            }

            throw new TimeoutException(
                $"Staged Agent {endpoint.AgentId} did not attach to RabbitMQ queue {queue}.");
        }

        public async Task<IReadOnlyDictionary<string, (uint Messages, uint Consumers)>>
            ReadQueueStateAsync(params StudioAgentEndpoint[] endpoints)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var result = new Dictionary<string, (uint Messages, uint Consumers)>(
                StringComparer.Ordinal);
            foreach (var endpoint in endpoints)
            {
                var queue = StationTransportRoute.JobQueue(endpoint.AgentId, endpoint.StationId);
                await using var channel = await _connection.CreateChannelAsync(
                    cancellationToken: timeout.Token);
                var state = await channel.QueueDeclarePassiveAsync(queue, timeout.Token);
                result.Add(queue, (state.MessageCount, state.ConsumerCount));
            }

            var resultQueue =
                $"openlineops.coordinator.{_coordinatorId}.station-results";
            await using var resultChannel = await _connection.CreateChannelAsync(
                cancellationToken: timeout.Token);
            var resultState = await resultChannel.QueueDeclarePassiveAsync(
                resultQueue,
                timeout.Token);
            result.Add(resultQueue, (resultState.MessageCount, resultState.ConsumerCount));
            return result;
        }

        public async Task<int> DeleteQueuesAsync(params StudioAgentEndpoint[] endpoints)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            if (!_connection.IsOpen)
            {
                throw new InvalidOperationException(
                    "RabbitMQ cleanup cannot prove queue deletion because the probe connection is closed.");
            }

            var deletedOrAbsent = 0;
            foreach (var endpoint in endpoints)
            {
                var queues = new[]
                {
                    StationTransportRoute.JobQueue(endpoint.AgentId, endpoint.StationId),
                    StationTransportRoute.SafetyQueue(
                        endpoint.AgentId,
                        endpoint.StationId,
                        "emergency-stop"),
                    StationTransportRoute.SafetyQueue(
                        endpoint.AgentId,
                        endpoint.StationId,
                        "safe-stop"),
                    StationTransportRoute.SafetyQueue(
                        endpoint.AgentId,
                        endpoint.StationId,
                        "job-cancel")
                };
                foreach (var queue in queues)
                {
                    await DeleteQueueAsync(queue, timeout.Token);
                    deletedOrAbsent++;
                }
            }

            await DeleteQueueAsync(
                $"openlineops.coordinator.{_coordinatorId}.station-results",
                timeout.Token);
            deletedOrAbsent++;
            return deletedOrAbsent;
        }

        public async ValueTask DisposeAsync()
        {
            await _connection.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        }

        private async Task DeleteQueueAsync(string queue, CancellationToken cancellationToken)
        {
            try
            {
                await using var channel = await _connection.CreateChannelAsync(
                    cancellationToken: cancellationToken);
                await channel.QueueDeleteAsync(
                    queue,
                    ifUnused: false,
                    ifEmpty: false,
                    cancellationToken: cancellationToken);
            }
            catch (RabbitMQ.Client.Exceptions.OperationInterruptedException exception)
                when (exception.ShutdownReason?.ReplyCode == 404)
            {
            }
        }
    }

    private static int StudioRequiredPositiveInt32(
        JsonElement element,
        string propertyName)
    {
        var value = StudioRequiredProperty(element, propertyName);
        return value.ValueKind == JsonValueKind.Number
               && value.TryGetInt32(out var result)
               && result > 0
            ? result
            : throw new InvalidDataException($"'{propertyName}' must be a positive Int32.");
    }

    private static int StudioRequiredNonNegativeInt32(
        JsonElement element,
        string propertyName)
    {
        var value = StudioRequiredProperty(element, propertyName);
        return value.ValueKind == JsonValueKind.Number
               && value.TryGetInt32(out var result)
               && result >= 0
            ? result
            : throw new InvalidDataException($"'{propertyName}' must be a non-negative Int32.");
    }

    [SupportedOSPlatform("windows")]
    private static void DeleteStudioAgentHarnessRoot(
        string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        var canonicalRoot = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var allowedBases = new[]
        {
            Path.GetFullPath(Path.GetTempPath()).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "Temp").TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar)
        };
        var leaf = Path.GetFileName(canonicalRoot);
        if (!allowedBases.Any(allowedBase => string.Equals(
                Path.GetDirectoryName(canonicalRoot),
                allowedBase,
                StringComparison.OrdinalIgnoreCase))
            || !leaf.StartsWith("olo-studio-two-agent-", StringComparison.Ordinal)
            || leaf.Length != "olo-studio-two-agent-".Length + 32
            || leaf["olo-studio-two-agent-".Length..].Any(static character =>
                character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new InvalidDataException(
                "Refusing to delete a non-canonical Studio Agent harness root.");
        }

        RejectStudioExistingPathAndAncestorsReparsePoints(
            canonicalRoot,
            "Studio Agent harness root");
        RejectStudioTreeReparsePoints(canonicalRoot, "Studio Agent harness root");

        foreach (var agentDirectory in new[]
                 {
                     "preparation-agent",
                     "vendor-agent"
                 })
        {
            var agentRoot = Path.Combine(root, agentDirectory);
            DeleteWorkRoot(agentRoot);
        }

        foreach (var path in Directory.EnumerateFileSystemEntries(
                     root,
                     "*",
                     SearchOption.AllDirectories))
        {
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        }

        File.SetAttributes(root, File.GetAttributes(root) & ~FileAttributes.ReadOnly);
        Directory.Delete(root, recursive: true);
    }
}
