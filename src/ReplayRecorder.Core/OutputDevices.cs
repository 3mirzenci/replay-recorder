using NAudio.CoreAudioApi;

namespace ReplayRecorder.Core;

/// <summary>Which real output "Automatic" records. One rule for the recorder and the settings meter.</summary>
public static class OutputDevices
{
    /// <summary>
    /// Virtual mixers (NGENUITY, Sonar, VB-Cable, Voicemeeter...) present a fake output and forward
    /// the mix to the real device in their own process; WASAPI loopback of the fake one is silent.
    /// </summary>
    public static bool IsVirtual(string name) =>
        new[] { "Virtual", "Sonar", "NGENUITY", "VB-Audio", "Voicemeeter" }.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase));

    /// <summary>The Windows default output, or the real device behind it when the default is virtual. Null when there is none.</summary>
    public static MMDevice? Automatic(MMDeviceEnumerator en)
    {
        if (!en.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) return null;
        var def = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        if (!IsVirtual(def.FriendlyName)) return def;
        var real = PhysicalOutputFor(en, def);
        if (real == null) return def;
        def.Dispose();
        return real;
    }

    /// <summary>Picks the physical output a virtual device most likely routes to.</summary>
    public static MMDevice? PhysicalOutputFor(MMDeviceEnumerator en, MMDevice virtualDev)
    {
        var candidates = en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Where(d => !IsVirtual(d.FriendlyName)).ToList();
        if (candidates.Count == 0) return null;

        // 1) Same brand as the virtual device, e.g. "HyperX Virtual Audio Device" -> "HyperX Cloud III".
        var brand = virtualDev.FriendlyName.Split('(', ')', ' ', '-')
            .FirstOrDefault(w => w.Length > 3 && candidates.Any(c => c.FriendlyName.Contains(w, StringComparison.OrdinalIgnoreCase))
                                 && !w.Equals("Audio", StringComparison.OrdinalIgnoreCase));
        var pick = brand == null ? null : candidates.First(c => c.FriendlyName.Contains(brand, StringComparison.OrdinalIgnoreCase));

        // 2) Otherwise a USB/Bluetooth headset, 3) otherwise anything that isn't a monitor's HDMI/DP audio.
        pick ??= candidates.FirstOrDefault(c => c.FriendlyName.Contains("Headset", StringComparison.OrdinalIgnoreCase)
                                                || c.FriendlyName.Contains("USB", StringComparison.OrdinalIgnoreCase));
        pick ??= candidates.FirstOrDefault(c => !c.FriendlyName.Contains("High Definition Audio", StringComparison.OrdinalIgnoreCase)
                                                && !c.FriendlyName.Contains("Display Audio", StringComparison.OrdinalIgnoreCase));
        pick ??= candidates[0];
        foreach (var c in candidates) if (c != pick) c.Dispose();
        return pick;
    }
}
