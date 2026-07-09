# Loopback Device Sample

This sample plugin implements a minimal `IOpenLineOpsDeviceCommandPlugin`.

It declares one capability:

- `device.loopback`

It declares one command:

- `loopback.echo` / `Echo`

Build:

```powershell
dotnet build samples/plugins/OpenLineOps.SamplePlugins.LoopbackDevice/OpenLineOps.SamplePlugins.LoopbackDevice.csproj
```

The command returns a JSON payload containing the target device instance id and the echoed input payload.
