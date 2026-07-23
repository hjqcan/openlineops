namespace OpenLineOps.WindowsServiceToken.TestRelay;

internal static class Program
{
    private const int InvalidInvocationExitCode = 64;
    private const int OperationFailureExitCode = 70;

    public static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            return InvalidInvocationExitCode;
        }

        string requestPath;
        try
        {
            requestPath = RelayProtocol.ParseInvocation(args);
        }
        catch (Exception exception) when (exception is InvalidDataException
                                           or ArgumentException
                                           or IOException
                                           or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(exception.Message);
            return InvalidInvocationExitCode;
        }

        try
        {
            return await SourceTokenRelayOperation.ExecuteAsync(requestPath)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return OperationFailureExitCode;
        }
    }
}
