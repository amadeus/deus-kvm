using System.Text;
using System.Text.Json;

namespace DeusKVM.Companion.Core;

/// <summary>
/// One request from the tray to the service over the control pipe: a JSON object on one line.
/// Commands: <c>status</c>; <c>watch</c> (the service then streams a status line whenever it
/// changes, and at least every few seconds, until the client disconnects); <c>configure</c>
/// with <see cref="Mac"/> (what Add Mac saves); <c>startup</c> and <c>tray-startup</c> with
/// <see cref="Enabled"/>; <c>stop</c>. One request per connection.
/// </summary>
public sealed record ServiceControlRequest(string Command, CompanionSettings? Mac = null, bool? Enabled = null);

/// <summary>The service's answer: <see cref="Ok"/>, or a message to show; status for status/watch.</summary>
public sealed record ServiceControlResponse(bool Ok, string? Error = null, ServiceSnapshot? Status = null);

/// <summary>What the pipe's commands change; the service implements it, tests fake it.</summary>
public interface IServiceControlTarget
{
    ServiceSnapshot? Status();
    void Configure(CompanionSettings mac);
    void SetAutomaticStartup(bool automatic);
    void SetTrayStartup(bool enabled);
    void StopService();
}

public static class ServiceControlProtocol
{
    /// <summary>Local only (the pipe denies network clients); interactive users may connect.</summary>
    public const string PipeName = "DeusKVM.Control";
    public const int MaximumRequest = 16 * 1024, MaximumResponse = 256 * 1024;
    public const string Status = "status", Watch = "watch", Configure = "configure",
        Startup = "startup", TrayStartup = "tray-startup", Stop = "stop";
    private static readonly string[] Commands = [Status, Watch, Configure, Startup, TrayStartup, Stop];

    /// <summary>Parses and validates one request line; throws <see cref="ArgumentException"/> if unusable.</summary>
    public static ServiceControlRequest Parse(string line)
    {
        if (Encoding.UTF8.GetByteCount(line) > MaximumRequest) throw new ArgumentException("The request is too large.");
        ServiceControlRequest? request;
        try { request = JsonSerializer.Deserialize<ServiceControlRequest>(line); }
        catch (JsonException) { throw new ArgumentException("The request is not valid JSON."); }
        if (request?.Command is not { } command || Array.IndexOf(Commands, command) < 0)
            throw new ArgumentException("Unknown service command.");
        switch (command)
        {
            case Configure:
                (request.Mac ?? throw new ArgumentException("Choose one Mac.")).Validate();
                break;
            case Startup or TrayStartup when request.Enabled is null:
                throw new ArgumentException("The setting needs a value.");
        }
        return request;
    }

    public static string Serialize(ServiceControlRequest request) => JsonSerializer.Serialize(request);

    public static string Serialize(ServiceControlResponse response)
    {
        var line = JsonSerializer.Serialize(response);
        // A status too large to send says so instead of breaking the client's line limit.
        return Encoding.UTF8.GetByteCount(line) <= MaximumResponse ? line
            : JsonSerializer.Serialize(new ServiceControlResponse(false, "The service status is too large to send."));
    }

    public static ServiceControlResponse ParseResponse(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<ServiceControlResponse>(line)
                ?? throw new InvalidDataException("Empty service response.");
        }
        catch (JsonException) { throw new InvalidDataException("The service sent an invalid response."); }
    }

    /// <summary>
    /// Handles every command except watch (a stream the server runs itself). Failures the user
    /// can act on come back as messages; anything else is the server's to log.
    /// </summary>
    public static ServiceControlResponse Handle(ServiceControlRequest request, IServiceControlTarget target)
    {
        try
        {
            switch (request.Command)
            {
                case Status: return new(true, Status: target.Status());
                case Configure: target.Configure(request.Mac!); break;
                case Startup: target.SetAutomaticStartup(request.Enabled!.Value); break;
                case TrayStartup: target.SetTrayStartup(request.Enabled!.Value); break;
                case Stop: target.StopService(); break;
                default: return new(false, "Unknown service command.");
            }
            return new(true);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return new(false, error.Message);
        }
    }

    /// <summary>
    /// Reads one newline-terminated UTF-8 line of at most <paramref name="limit"/> bytes. Null at
    /// end of stream before any byte; <see cref="InvalidDataException"/> past the limit.
    /// </summary>
    public static async Task<string?> ReadLineAsync(Stream stream, int limit, CancellationToken cancellation)
    {
        var line = new MemoryStream();
        var buffer = new byte[1];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, 0, 1, cancellation).ConfigureAwait(false);
            if (count == 0) return line.Length == 0 ? null : throw new InvalidDataException("The line ended early.");
            if (buffer[0] == (byte)'\n') break;
            if (line.Length >= limit) throw new InvalidDataException("The line is too long.");
            line.WriteByte(buffer[0]);
        }
        var text = Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length);
        return text.EndsWith("\r", StringComparison.Ordinal) ? text.Substring(0, text.Length - 1) : text;
    }

    public static async Task WriteLineAsync(Stream stream, string line, CancellationToken cancellation)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        await stream.WriteAsync(bytes, 0, bytes.Length, cancellation).ConfigureAwait(false);
        await stream.FlushAsync(cancellation).ConfigureAwait(false);
    }
}
