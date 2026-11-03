using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tamp.Heft.V1;

/// <summary>
/// The ready-state file a running <see cref="HeftDevServer"/> writes, so browser automation (or anything else
/// outside the Tamp process) can find the server. Written atomically once the server is ready and deleted when
/// it is disposed. It holds only where the server is and which process serves it: no cookies, credentials or
/// debug query strings.
/// </summary>
/// <remarks>
/// A file can outlive its server if the build process is killed hard. Check <see cref="IsLive"/> before
/// trusting one: it requires the recorded PID to be running <em>and</em> to have the recorded start time, so a
/// recycled PID doesn't count.
/// </remarks>
public sealed record HeftDevServerState
{
    /// <summary>The current <see cref="SchemaVersion"/>.</summary>
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Format version of this file.</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>The server root, e.g. <c>https://localhost:4321/</c>.</summary>
    public required string Url { get; init; }

    /// <summary>The manifests file, e.g. <c>https://localhost:4321/temp/build/manifests.js</c>.</summary>
    public required string ManifestsUrl { get; init; }

    /// <summary>The listening port.</summary>
    public required int Port { get; init; }

    /// <summary>PID of the Heft process (the root of the server's process tree).</summary>
    public required int Pid { get; init; }

    /// <summary>When that process started (UTC). Distinguishes it from a later process that reuses the PID.</summary>
    public required DateTimeOffset ProcessStartTime { get; init; }

    /// <summary>When the server became ready (UTC).</summary>
    public required DateTimeOffset ReadyAt { get; init; }

    /// <summary>The installed <c>@rushstack/heft</c> version, when known.</summary>
    public string? HeftVersion { get; init; }

    /// <summary>The <c>Tamp.Heft.V1</c> version that wrote the file.</summary>
    public string? TampHeftVersion { get; init; }

    /// <summary>Read a state file; null when it doesn't exist or isn't a readable state file.</summary>
    public static HeftDevServerState? TryRead(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            var state = JsonSerializer.Deserialize<HeftDevServerState>(File.ReadAllText(path), Json);
            return state is { SchemaVersion: CurrentSchemaVersion } ? state : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>True when the recorded process is still running and is the same process (same start time).</summary>
    public bool IsLive()
    {
        try
        {
            using var p = Process.GetProcessById(Pid);
            if (p.HasExited) return false;
            var started = new DateTimeOffset(p.StartTime.ToUniversalTime(), TimeSpan.Zero);
            // Start times are recorded and re-read through the same API; allow for rounding only.
            return (started - ProcessStartTime).Duration() < TimeSpan.FromSeconds(1);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Write to a temporary file next to <paramref name="path"/>, then rename over it.</summary>
    internal void WriteAtomically(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
