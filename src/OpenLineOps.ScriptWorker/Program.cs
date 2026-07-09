using OpenLineOps.ScriptWorker;

return await PythonScriptWorkerProgram
    .RunAsync(args, Console.In, Console.Out, Console.Error)
    .ConfigureAwait(false);
