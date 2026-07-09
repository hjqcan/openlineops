namespace OpenLineOps.Devices.Domain.Operations;

public sealed record DeviceOperationResult(bool Succeeded, string Code, string Message)
{
    public static DeviceOperationResult Accepted(string message = "Accepted")
    {
        return new DeviceOperationResult(true, "Devices.Accepted", message);
    }

    public static DeviceOperationResult Rejected(string code, string message)
    {
        return new DeviceOperationResult(false, code, message);
    }
}
