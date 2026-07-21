using System.Runtime.Versioning;

namespace OpenLineOps.WindowsServiceToken.TestHelper;

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
            requestPath = TokenTransferProtocol.ParseRequestPath(args);
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
            return await RunWindowsServiceAsync(requestPath);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return OperationFailureExitCode;
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task<int> RunWindowsServiceAsync(string requestPath)
    {
        var request = TokenTransferProtocol.ReadRequest(requestPath);
        var builder = Host.CreateApplicationBuilder([]);
        builder.Logging.ClearProviders();
        builder.Services.AddWindowsService(options =>
        {
            options.ServiceName = request.HelperServiceName;
        });
        builder.Services.AddSingleton(request);
        builder.Services.AddSingleton<WindowsServiceTokenTransferOperation>();
        builder.Services.AddHostedService<OneShotWindowsServiceWorker>();

        using var host = builder.Build();
        Environment.ExitCode = 0;
        await host.RunAsync();
        return Environment.ExitCode;
    }
}
