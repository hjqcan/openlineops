using OpenLineOps.StationRuntime;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

return await StationRuntimeEntrypoint.RunAsync(
    args,
    StationRuntimeHostOptions.LoadFromEnvironment(),
    cancellation.Token);
