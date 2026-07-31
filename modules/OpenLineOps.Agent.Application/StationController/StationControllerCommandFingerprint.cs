using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace OpenLineOps.Agent.Application.StationController;

public static class StationControllerCommandFingerprint
{
    public const int CurrentVersion = 1;

    public static string Compute(StationControllerCommandEnvelope command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.ContractVersion != CurrentVersion)
        {
            throw new InvalidDataException(
                $"Unsupported Station controller command contract version "
                + $"{command.ContractVersion}.");
        }

        var canonical = new StringBuilder();
        Append(canonical, "contractVersion", command.ContractVersion.ToString(
            CultureInfo.InvariantCulture));
        Append(canonical, "stationId", command.StationId);
        Append(canonical, "ownerAgentId", command.OwnerAgentId);
        Append(canonical, "ownerInstanceId", command.OwnerInstanceId);
        Append(canonical, "commandId", command.CommandId);
        Append(canonical, "controllerSessionId", command.ControllerSessionId);
        Append(canonical, "commandSequence", command.CommandSequence.ToString(
            CultureInfo.InvariantCulture));
        Append(canonical, "fencingToken", command.FencingToken.ToString(
            CultureInfo.InvariantCulture));
        Append(canonical, "trigger", command.Trigger);
        Append(canonical, "expectedMode", command.ExpectedMode);
        Append(
            canonical,
            "expectedCompletionState",
            command.ExpectedCompletionState);
        Append(canonical, "idempotency", command.Idempotency.ToString());
        Append(canonical, "safetyClass", command.SafetyClass);
        AppendNullable(canonical, "confirmedRecipeId", command.ConfirmedRecipeId);
        AppendNullable(
            canonical,
            "confirmedRecipeVersion",
            command.ConfirmedRecipeVersion);
        Append(canonical, "issuedAtUtc", Utc(command.IssuedAtUtc));
        Append(canonical, "deadlineUtc", Utc(command.DeadlineUtc));
        Append(
            canonical,
            "issuedOperationalEpoch",
            command.IssuedOperationalEpoch.ToString(CultureInfo.InvariantCulture));
        AppendNullable(
            canonical,
            "recipeAssignmentId",
            command.RecipeAssignmentId);
        AppendNullable(
            canonical,
            "recipeDeploymentId",
            command.RecipeDeploymentId);
        AppendNullable(
            canonical,
            "recipeConfigurationSha256",
            command.RecipeConfigurationSha256);
        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static string Utc(DateTimeOffset value) =>
        value != default && value.Offset == TimeSpan.Zero
            ? value.ToString("O", CultureInfo.InvariantCulture)
            : throw new ArgumentException(
                "Station controller command timestamps must use UTC offset zero.");

    private static void Append(
        StringBuilder destination,
        string name,
        string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        destination
            .Append(name)
            .Append(':')
            .Append(Encoding.UTF8.GetByteCount(value).ToString(
                CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value)
            .Append('\n');
    }

    private static void AppendNullable(
        StringBuilder destination,
        string name,
        string? value)
    {
        if (value is null)
        {
            destination.Append(name).Append(":null\n");
            return;
        }

        Append(destination, name, value);
    }
}
