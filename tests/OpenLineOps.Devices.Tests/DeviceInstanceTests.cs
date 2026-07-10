using OpenLineOps.Devices.Domain.Identifiers;
using OpenLineOps.Devices.Domain.Instances;

namespace OpenLineOps.Devices.Tests;

public sealed class DeviceInstanceTests
{
    private static readonly DateTimeOffset RegisteredAtUtc = new(2026, 6, 29, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DeviceConnectionLifecycleUpdatesStatus()
    {
        var device = CreateDeviceInstance();

        var request = device.RequestConnection();
        var connected = device.ConfirmConnected(RegisteredAtUtc.AddSeconds(2));
        var disconnected = device.Disconnect(RegisteredAtUtc.AddSeconds(3), "operator disconnect");

        Assert.True(request.Succeeded, request.Message);
        Assert.True(connected.Succeeded, connected.Message);
        Assert.True(disconnected.Succeeded, disconnected.Message);
        Assert.Equal(DeviceConnectionStatus.Disconnected, device.Status);
        Assert.Null(device.ConnectedAtUtc);
        Assert.Equal(RegisteredAtUtc.AddSeconds(3), device.LastDisconnectedAtUtc);

        Assert.Empty(device.DomainEvents);
    }

    [Fact]
    public void FaultedDeviceMustBeResetBeforeReconnect()
    {
        var device = CreateDeviceInstance();
        device.RequestConnection();
        device.ConfirmConnected(RegisteredAtUtc.AddSeconds(2));

        var faulted = device.MarkFaulted("camera offline");
        var reconnect = device.RequestConnection();
        var faultReason = device.FaultReason;
        var reset = device.ResetFault();
        var reconnectAfterReset = device.RequestConnection();

        Assert.True(faulted.Succeeded, faulted.Message);
        Assert.False(reconnect.Succeeded);
        Assert.Equal("Devices.DeviceFaulted", reconnect.Code);
        Assert.Equal("camera offline", faultReason);
        Assert.True(reset.Succeeded, reset.Message);
        Assert.Null(device.FaultReason);
        Assert.True(reconnectAfterReset.Succeeded, reconnectAfterReset.Message);
        Assert.Equal(DeviceConnectionStatus.Connecting, device.Status);
    }

    [Fact]
    public void ConfirmConnectedWithoutRequestIsRejected()
    {
        var device = CreateDeviceInstance();

        var result = device.ConfirmConnected(RegisteredAtUtc.AddSeconds(1));

        Assert.False(result.Succeeded);
        Assert.Equal("Devices.ConnectionNotRequested", result.Code);
        Assert.Equal(DeviceConnectionStatus.Disconnected, device.Status);
    }

    private static DeviceInstance CreateDeviceInstance()
    {
        return DeviceInstance.Register(
            new DeviceInstanceId("camera-01"),
            new DeviceDefinitionId("camera-vision"),
            "station-eol",
            "Camera 01",
            new DeviceEndpoint("tcp", "192.168.1.10:9000"),
            RegisteredAtUtc);
    }
}
