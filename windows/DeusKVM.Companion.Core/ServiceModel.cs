using System.Text.Json;

namespace DeusKVM.Companion.Core;

// DeviceId is the Mac's control-link id (a Guid); older files' other fields are ignored.
public sealed record CompanionSettings(string DeviceId, string DeviceName)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DeviceId) || DeviceId.Length > 4096 || DeviceId.Any(char.IsControl))
            throw new ArgumentException("Choose one Mac.");
        if (string.IsNullOrWhiteSpace(DeviceName) || DeviceName.Length > 256 || DeviceName.Any(char.IsControl))
            throw new ArgumentException("The selected device name is invalid.");
    }
}

public sealed record WorkerStatus(string State, string Detail, DateTimeOffset At,
    string? DeviceName = null, DateTimeOffset? LastDiscovery = null,
    string? Identity = null, int ProcessSession = -1,
    string? DeviceId = null, string[]? WaitingMacs = null, string? DiscoveryEvent = null, string[]? PausedMacs = null);

public sealed record ServiceSnapshot(int ProcessId, string Identity, int ProcessSession,
    uint ConsoleSession, string ConsoleState, WorkerStatus Worker, DateTimeOffset UpdatedAt);

public static class ServicePolicy
{
    // intentional Stop never enters this retry path; SCM handles unexpected service exits.
    public static TimeSpan RetryDelay(int failures) => TimeSpan.FromSeconds(
        failures switch { <= 1 => 2, 2 => 5, 3 => 10, 4 => 20, _ => 30 });

    public static bool IsFresh(DateTimeOffset updated, DateTimeOffset now) =>
        updated <= now.AddSeconds(5) && now - updated < TimeSpan.FromSeconds(45);
}

public static class JsonFiles
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static T? Read<T>(string path)
    {
        if (!File.Exists(path)) return default;
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return JsonSerializer.Deserialize<T>(file);
    }

    public static void Write<T>(string path, T value)
    {
        var temporary = path + ".new";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, Options));
        RuntimeCompat.MoveReplace(temporary, path);
    }
}
