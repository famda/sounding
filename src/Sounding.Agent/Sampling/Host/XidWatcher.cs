using System.Runtime.InteropServices;

namespace Sounding.Agent.Sampling;

// Xid errors, the driver's reports of GPU faults (bad memory, fallen off the bus, a hung context), from NVML's
// event API. Not every platform delivers them (WSL2 doesn't); then it stays quiet.
public sealed class XidWatcher(ILogger logger)
{
    private const ulong XidCriticalError = 0x8;
    private const uint WaitMs = 1000;

    private readonly Lock _lock = new();
    private readonly List<AgentEvent> _events = [];
    private volatile bool _running, _unsupported;

    [StructLayout(LayoutKind.Sequential)]
    private struct EventData
    {
        public nint Device;
        public ulong EventType;
        public ulong Data;
        public uint GpuInstanceId;
        public uint ComputeInstanceId;
    }

    public void Start()
    {
        if (_running || _unsupported)
        {
            return;
        }

        _running = true;
        new Thread(Watch) { IsBackground = true, Name = "gpu-xid" }.Start();
    }

    public void Stop() => _running = false;

    public IReadOnlyList<AgentEvent> TakeEvents()
    {
        lock (_lock)
        {
            if (_events.Count == 0)
            {
                return [];
            }

            var events = _events.ToList();
            _events.Clear();
            return events;
        }
    }

    private void Watch()
    {
        try
        {
            if (nvmlInit_v2() != 0 || nvmlEventSetCreate(out var set) != 0 || nvmlDeviceGetCount_v2(out var count) != 0)
            {
                _unsupported = true;
                return;
            }

            var registered = 0;
            for (uint i = 0; i < count; i++)
            {
                if (nvmlDeviceGetHandleByIndex_v2(i, out var device) == 0 && nvmlDeviceRegisterEvents(device, XidCriticalError, set) == 0)
                {
                    registered++;
                }
            }

            if (registered == 0)
            {
                logger.LogDebug("This platform reports no GPU Xid events");
                _unsupported = true;
                nvmlEventSetFree(set);
                return;
            }

            while (_running)
            {
                if (nvmlEventSetWait_v2(set, out var data, WaitMs) == 0 && data.EventType == XidCriticalError)
                {
                    var index = nvmlDeviceGetIndex(data.Device, out var gpu) == 0 ? $"GPU {gpu}" : "A GPU";
                    lock (_lock)
                    {
                        _events.Add(new AgentEvent("gpu", "error", $"{index} reported Xid {data.Data}"));
                    }
                }
            }

            nvmlEventSetFree(set);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            logger.LogDebug("NVML not available: {Message}", ex.Message);
            _unsupported = true;
        }
        finally
        {
            _running = false;
        }
    }

    private const string Nvml = "libnvidia-ml.so.1";

    [DllImport(Nvml)] private static extern int nvmlInit_v2();
    [DllImport(Nvml)] private static extern int nvmlDeviceGetCount_v2(out uint count);
    [DllImport(Nvml)] private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out nint device);
    [DllImport(Nvml)] private static extern int nvmlDeviceGetIndex(nint device, out uint index);
    [DllImport(Nvml)] private static extern int nvmlEventSetCreate(out nint set);
    [DllImport(Nvml)] private static extern int nvmlDeviceRegisterEvents(nint device, ulong eventTypes, nint set);
    [DllImport(Nvml)] private static extern int nvmlEventSetWait_v2(nint set, out EventData data, uint timeoutMs);
    [DllImport(Nvml)] private static extern int nvmlEventSetFree(nint set);
}
