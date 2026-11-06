using Microsoft.Win32;

namespace DeusKVM.Companion;

/// <summary>
/// The signed-in user's precision touchpad values. The tray runs as that user, so no elevation
/// is involved; the key is created on the first write, as the Settings app would.
/// </summary>
internal sealed class TrackpadRegistry : ITrackpadStore
{
    public int? Read(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(TrackpadSettings.KeyPath);
        return key?.GetValue(name) is int value ? value : null;
    }

    public void Write(string name, int value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(TrackpadSettings.KeyPath, true);
        key.SetValue(name, value, RegistryValueKind.DWord);
    }

    public void Delete(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(TrackpadSettings.KeyPath, true);
        key?.DeleteValue(name, false);
    }
}
