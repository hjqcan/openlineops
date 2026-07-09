using OpenLineOps.Plugins.Infrastructure.Lifecycle;

return await ExternalPluginHostProgram.RunAsync(
    args,
    Console.In,
    Console.Out,
    Console.Error);
