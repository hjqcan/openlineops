using OpenLineOps.Runner;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

return await RunnerEntrypoint.RunAsync(
    args,
    Directory.GetCurrentDirectory(),
    Console.Out,
    cancellation.Token);
