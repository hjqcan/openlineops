using OpenLineOps.Recipes.Domain.Assignments;
using OpenLineOps.Recipes.Domain.Recipes;

namespace OpenLineOps.Recipes.Domain.Deployments;

public enum RecipeDeploymentStatus
{
    AwaitingReadback = 0,
    Verified = 1,
    Rejected = 2
}

public sealed record DeploymentCommand
{
    public DeploymentCommand(
        string commandId,
        long fencingToken,
        DateTimeOffset deadlineUtc)
    {
        CommandId = RecipeValueGuard.Required(commandId, nameof(commandId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fencingToken);

        FencingToken = fencingToken;
        DeadlineUtc = RecipeValueGuard.Utc(deadlineUtc, nameof(deadlineUtc));
    }

    public string CommandId { get; }

    public long FencingToken { get; }

    public DateTimeOffset DeadlineUtc { get; }
}

public sealed record RecipeReadbackParameter
{
    public RecipeReadbackParameter(
        string key,
        RecipeParameterValueType type,
        string? unit,
        string value)
    {
        Key = RecipeValueGuard.Required(key, nameof(key));
        if (!Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(nameof(type));
        }

        Type = type;
        Unit = RecipeValueGuard.Optional(unit);
        Value = RecipeValueGuard.CanonicalParameterValue(
            value,
            type,
            nameof(value),
            allowEmpty: true);
    }

    public string Key { get; }

    public RecipeParameterValueType Type { get; }

    public string? Unit { get; }

    public string Value { get; }
}

public sealed record RecipeParameterVerification(
    string Key,
    bool TypeMatches,
    bool UnitMatches,
    bool ValueMatches,
    string ExpectedType,
    string ActualType,
    string? ExpectedUnit,
    string? ActualUnit,
    string ExpectedValue,
    string ActualValue)
{
    public bool Matches => TypeMatches && UnitMatches && ValueMatches;
}

public sealed record RecipeVerificationFact(
    Guid VerificationId,
    DateTimeOffset VerifiedAtUtc,
    string VerifiedBy,
    bool Succeeded,
    IReadOnlyCollection<RecipeParameterVerification> Parameters,
    string ReadbackSha256);

public sealed record RecipeDeployment
{
    public RecipeDeployment(
        Guid deploymentId,
        RecipeAssignment assignment,
        RecipeRevisionSnapshot snapshot,
        DeploymentCommand command,
        DateTimeOffset createdAtUtc,
        string createdBy,
        long revision = 1,
        RecipeDeploymentStatus status = RecipeDeploymentStatus.AwaitingReadback,
        RecipeVerificationFact? verification = null)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(command);
        if (deploymentId == Guid.Empty)
        {
            throw new ArgumentException("Deployment id is required.", nameof(deploymentId));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(revision);

        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        if (!string.Equals(assignment.RecipeId, snapshot.RecipeId, StringComparison.Ordinal)
            || !string.Equals(assignment.VersionId, snapshot.VersionId, StringComparison.Ordinal)
            || !string.Equals(
                assignment.ConfigurationSha256,
                snapshot.ConfigurationSha256,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Deployment snapshot does not match the immutable assignment.",
                nameof(snapshot));
        }

        if ((status == RecipeDeploymentStatus.AwaitingReadback) != (verification is null))
        {
            throw new ArgumentException(
                "Deployment status and verification evidence are inconsistent.",
                nameof(verification));
        }

        if (verification is not null
            && (status == RecipeDeploymentStatus.Verified) != verification.Succeeded)
        {
            throw new ArgumentException(
                "Deployment verification outcome is inconsistent.",
                nameof(verification));
        }

        DeploymentId = deploymentId;
        Assignment = assignment;
        AssignmentId = assignment.AssignmentId;
        RecipeId = assignment.RecipeId;
        VersionId = assignment.VersionId;
        ProductModelId = assignment.ProductModelId;
        StationId = assignment.StationId;
        Snapshot = snapshot;
        Command = command;
        CreatedAtUtc = RecipeValueGuard.Utc(createdAtUtc, nameof(createdAtUtc));
        if (Command.DeadlineUtc <= CreatedAtUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(command),
                "Deployment deadline must be after creation.");
        }

        CreatedBy = RecipeValueGuard.Required(createdBy, nameof(createdBy));
        Revision = revision;
        Status = status;
        Verification = verification;
        ValidateRestoredVerification();
    }

    public Guid DeploymentId { get; }

    public RecipeAssignment Assignment { get; }

    public Guid AssignmentId { get; }

    public string RecipeId { get; }

    public string VersionId { get; }

    public string ProductModelId { get; }

    public string StationId { get; }

    public RecipeRevisionSnapshot Snapshot { get; }

    public DeploymentCommand Command { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public string CreatedBy { get; }

    public long Revision { get; init; }

    public RecipeDeploymentStatus Status { get; init; }

    public RecipeVerificationFact? Verification { get; init; }

    public RecipeDeployment Verify(
        Guid verificationId,
        IEnumerable<RecipeReadbackParameter> readback,
        string readbackSha256,
        DateTimeOffset verifiedAtUtc,
        string verifiedBy)
    {
        ArgumentNullException.ThrowIfNull(readback);
        if (Status != RecipeDeploymentStatus.AwaitingReadback)
        {
            throw new InvalidOperationException(
                "A recipe deployment can be verified only once.");
        }

        if (verificationId == Guid.Empty)
        {
            throw new ArgumentException("Verification id is required.", nameof(verificationId));
        }

        var canonicalVerifiedAtUtc = RecipeValueGuard.Utc(
            verifiedAtUtc,
            nameof(verifiedAtUtc));
        if (canonicalVerifiedAtUtc < CreatedAtUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(verifiedAtUtc),
                "Verification time cannot precede deployment creation.");
        }

        if (canonicalVerifiedAtUtc > Command.DeadlineUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(verifiedAtUtc),
                "Verification time cannot exceed deployment deadline.");
        }

        var readbackArray = readback.ToArray();
        if (readbackArray.Select(static parameter => parameter.Key)
            .Distinct(StringComparer.Ordinal).Count() != readbackArray.Length)
        {
            throw new ArgumentException(
                "Readback parameter keys must be unique.",
                nameof(readback));
        }

        var actualByKey = readbackArray.ToDictionary(
            static parameter => parameter.Key,
            StringComparer.Ordinal);
        var comparisons = Snapshot.Parameters
            .Select(expected =>
            {
                actualByKey.TryGetValue(expected.Key, out var actual);
                return new RecipeParameterVerification(
                    expected.Key,
                    actual?.Type == expected.Type,
                    string.Equals(expected.Unit, actual?.Unit, StringComparison.Ordinal),
                    string.Equals(
                        expected.CanonicalValue,
                        actual?.Value,
                        StringComparison.Ordinal),
                    expected.Type.ToString(),
                    actual?.Type.ToString() ?? string.Empty,
                    expected.Unit,
                    actual?.Unit,
                    expected.CanonicalValue,
                    actual?.Value ?? string.Empty);
            })
            .Concat(actualByKey.Values
                .Where(actual => !Snapshot.Parameters.Any(expected =>
                    string.Equals(
                        expected.Key,
                        actual.Key,
                        StringComparison.Ordinal)))
                .Select(actual => new RecipeParameterVerification(
                    actual.Key,
                    TypeMatches: false,
                    UnitMatches: false,
                    ValueMatches: false,
                    ExpectedType: string.Empty,
                    ActualType: actual.Type.ToString(),
                    ExpectedUnit: null,
                    ActualUnit: actual.Unit,
                    ExpectedValue: string.Empty,
                    ActualValue: actual.Value)))
            .OrderBy(static comparison => comparison.Key, StringComparer.Ordinal)
            .ToArray();
        var allExpectedMatch = comparisons.All(static comparison => comparison.Matches);
        var succeeded = allExpectedMatch
            && actualByKey.Count == Snapshot.Parameters.Count;
        var fact = new RecipeVerificationFact(
            verificationId,
            canonicalVerifiedAtUtc,
            RecipeValueGuard.Required(verifiedBy, nameof(verifiedBy)),
            succeeded,
            comparisons,
            RecipeValueGuard.Sha256(readbackSha256, nameof(readbackSha256)));

        return this with
        {
            Revision = checked(Revision + 1),
            Status = succeeded
                ? RecipeDeploymentStatus.Verified
                : RecipeDeploymentStatus.Rejected,
            Verification = fact
        };
    }

    private void ValidateRestoredVerification()
    {
        if (Verification is null)
        {
            if (Revision != 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(Revision),
                    "Unverified deployment must remain at revision one.");
            }

            return;
        }

        if (Revision != 2
            || Verification.VerificationId == Guid.Empty
            || Verification.VerifiedAtUtc.Offset != TimeSpan.Zero
            || Verification.VerifiedAtUtc < CreatedAtUtc
            || Verification.VerifiedAtUtc > Command.DeadlineUtc
            || string.IsNullOrWhiteSpace(Verification.VerifiedBy)
            || Verification.Parameters.Count == 0)
        {
            throw new ArgumentException(
                "Persisted deployment verification evidence is invalid.");
        }

        RecipeValueGuard.Sha256(
            Verification.ReadbackSha256,
            nameof(Verification.ReadbackSha256));
        if (Verification.Parameters.Select(static parameter => parameter.Key)
            .Distinct(StringComparer.Ordinal).Count() != Verification.Parameters.Count)
        {
            throw new ArgumentException(
                "Persisted deployment verification keys are duplicated.");
        }

        var expectedKeys = Snapshot.Parameters
            .Select(static parameter => parameter.Key)
            .ToHashSet(StringComparer.Ordinal);
        if (!expectedKeys.IsSubsetOf(
                Verification.Parameters.Select(static parameter => parameter.Key))
            || (Verification.Succeeded
                && (Verification.Parameters.Count != expectedKeys.Count
                    || Verification.Parameters.Any(static parameter => !parameter.Matches)))
            || (!Verification.Succeeded
                && Verification.Parameters.All(static parameter => parameter.Matches)))
        {
            throw new ArgumentException(
                "Persisted deployment verification outcome is inconsistent.");
        }
    }
}
