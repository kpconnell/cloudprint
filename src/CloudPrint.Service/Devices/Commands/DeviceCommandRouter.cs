using System.Collections.Concurrent;

namespace CloudPrint.Service.Devices.Commands;

/// <summary>Command sink for one device — implemented by <see cref="DeviceForwardingService"/>.</summary>
public interface IDeviceCommandTarget
{
    string DeviceName { get; }
    /// <summary>Queues a raw command message for this device. Returns false if the target is not accepting commands.</summary>
    bool TryEnqueue(DeviceCommandMessage message);
}

/// <summary>Maps device names to their forwarding loops so an inbound command can be delivered.</summary>
public sealed class DeviceCommandRouter
{
    private readonly ConcurrentDictionary<string, IDeviceCommandTarget> _targets = new(StringComparer.OrdinalIgnoreCase);

    public void Register(IDeviceCommandTarget target) => _targets[target.DeviceName] = target;
    public void Unregister(IDeviceCommandTarget target) => _targets.TryRemove(new KeyValuePair<string, IDeviceCommandTarget>(target.DeviceName, target));

    public IReadOnlyCollection<string> DeviceNames => _targets.Keys.ToList();

    /// <summary>Delivers a command to its device. Returns false when no such device is registered.</summary>
    public bool TryRoute(DeviceCommandMessage message) =>
        _targets.TryGetValue(message.TargetDevice, out var target) && target.TryEnqueue(message);
}
