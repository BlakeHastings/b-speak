using PortAudioSharp;

namespace BSpeak.Infrastructure.Audio;

public sealed record AudioDevice(int Index, string Name, int InputChannels, int OutputChannels, double DefaultSampleRate, bool IsDefaultInput, bool IsDefaultOutput);

/// <summary>Process-wide PortAudio setup and device lookup.</summary>
public static class PortAudioSystem
{
    private static readonly object Gate = new();
    private static bool _initialized;

    public static void EnsureInitialized()
    {
        lock (Gate)
        {
            if (_initialized) return;
            try { PortAudio.LoadNativeLibrary(); } catch { /* already loaded, or resolved by the runtime */ }
            PortAudio.Initialize();
            _initialized = true;
        }
    }

    public static IReadOnlyList<AudioDevice> List()
    {
        EnsureInitialized();
        var devices = new List<AudioDevice>();
        for (var i = 0; i < PortAudio.DeviceCount; i++)
        {
            var d = PortAudio.GetDeviceInfo(i);
            devices.Add(new AudioDevice(i, d.name, d.maxInputChannels, d.maxOutputChannels, d.defaultSampleRate,
                i == PortAudio.DefaultInputDevice, i == PortAudio.DefaultOutputDevice));
        }
        return devices;
    }

    /// <summary>Resolves "default", an index, or a name fragment to a device index.</summary>
    public static int Resolve(string device, bool output)
    {
        EnsureInitialized();
        var fallback = output ? PortAudio.DefaultOutputDevice : PortAudio.DefaultInputDevice;
        if (string.IsNullOrWhiteSpace(device) || device.Equals("default", StringComparison.OrdinalIgnoreCase)) return fallback;
        if (int.TryParse(device, out var index)) return index;
        for (var i = 0; i < PortAudio.DeviceCount; i++)
        {
            var info = PortAudio.GetDeviceInfo(i);
            var channels = output ? info.maxOutputChannels : info.maxInputChannels;
            if (channels > 0 && info.name?.Contains(device, StringComparison.OrdinalIgnoreCase) == true) return i;
        }
        throw new InvalidOperationException($"No {(output ? "output" : "input")} device matches '{device}'. Run: bspeak device list");
    }
}
