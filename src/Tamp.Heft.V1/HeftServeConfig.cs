using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Tamp.Heft.V1;

/// <summary>
/// Where a Heft dev server will listen, as the SharePoint Framework rig works it out from the project's
/// <c>config/serve.json</c>: <c>hostname</c> (else <c>ipAddress</c>, else <c>localhost</c>), <c>port</c>
/// (default 4321) and <c>https</c> (default false). The manifests file the SharePoint debug loader asks for
/// is served at <c>temp/build/manifests.js</c> under that base URL.
/// </summary>
public sealed record HeftServeConfig
{
    /// <summary>The SPFx rig's default port when <c>serve.json</c> doesn't set one.</summary>
    public const int DefaultPort = 4321;

    /// <summary>Path of the manifests file under the server root, as the SPFx rig serves it.</summary>
    public const string DefaultManifestsPath = "temp/build/manifests.js";

    /// <summary>The server root, e.g. <c>https://localhost:4321/</c>. Always ends with <c>/</c>.</summary>
    public required Uri BaseUrl { get; init; }

    /// <summary>The port in <see cref="BaseUrl"/>.</summary>
    public int Port => BaseUrl.Port;

    /// <summary>True when <see cref="BaseUrl"/> uses <c>https</c>.</summary>
    public bool Https => BaseUrl.Scheme == Uri.UriSchemeHttps;

    /// <summary>The <c>serve.json</c> the values came from, or null when defaults or an explicit URL were used.</summary>
    public string? SourcePath { get; init; }

    /// <summary>
    /// Read <paramref name="serveJsonPath"/>. A missing file gives the rig's defaults
    /// (<c>http://localhost:4321/</c>); a file that can't be parsed throws.
    /// </summary>
    public static HeftServeConfig Load(string serveJsonPath)
    {
        if (string.IsNullOrWhiteSpace(serveJsonPath)) throw new ArgumentException("serveJsonPath is required.", nameof(serveJsonPath));
        if (!File.Exists(serveJsonPath)) return FromParts(null, DefaultPort, https: false, sourcePath: null);

        string? hostname = null;
        var port = DefaultPort;
        var https = false;
        try
        {
            // The rig reads serve.json with JSON-with-comments rules.
            using var doc = JsonDocument.Parse(File.ReadAllText(serveJsonPath), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidOperationException($"{serveJsonPath} is not a JSON object.");

            if (root.TryGetProperty("ipAddress", out var ip) && ip.ValueKind == JsonValueKind.String) hostname = ip.GetString();
            if (root.TryGetProperty("hostname", out var host) && host.ValueKind == JsonValueKind.String) hostname = host.GetString();
            if (root.TryGetProperty("port", out var p))
            {
                if (p.ValueKind != JsonValueKind.Number || !p.TryGetInt32(out port) || port is < 1 or > 65535)
                    throw new InvalidOperationException($"{serveJsonPath}: 'port' must be a number from 1 to 65535.");
            }
            if (root.TryGetProperty("https", out var h))
            {
                if (h.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new InvalidOperationException($"{serveJsonPath}: 'https' must be true or false.");
                https = h.GetBoolean();
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{serveJsonPath} could not be parsed: {ex.Message}", ex);
        }

        return FromParts(hostname, port, https, Path.GetFullPath(serveJsonPath));
    }

    /// <summary>Use an explicit server root instead of reading <c>serve.json</c> (for rigs other than SPFx's).</summary>
    public static HeftServeConfig FromUrl(Uri baseUrl)
    {
        if (baseUrl is null) throw new ArgumentNullException(nameof(baseUrl));
        if (!baseUrl.IsAbsoluteUri || (baseUrl.Scheme != Uri.UriSchemeHttps && baseUrl.Scheme != Uri.UriSchemeHttp))
            throw new ArgumentException("The dev server URL must be an absolute http or https URL.", nameof(baseUrl));
        var root = new UriBuilder(baseUrl.Scheme, baseUrl.Host, baseUrl.Port, baseUrl.AbsolutePath.TrimEnd('/') + "/").Uri;
        return new HeftServeConfig { BaseUrl = root };
    }

    private static HeftServeConfig FromParts(string? hostname, int port, bool https, string? sourcePath)
    {
        var host = string.IsNullOrWhiteSpace(hostname) ? "localhost" : hostname!.Trim();
        if (host.Contains('{'))
            throw new InvalidOperationException(
                $"serve.json's hostname '{host}' contains a placeholder (such as {{tenantDomain}}). Set the server URL " +
                "explicitly with SetServerUrl so Tamp knows where to probe.");
        // A wildcard bind address is where the server listens, not an address to browse to.
        if (host is "0.0.0.0" or "::" or "[::]") host = "localhost";

        var builder = new UriBuilder(https ? Uri.UriSchemeHttps : Uri.UriSchemeHttp, host, port, "/");
        return new HeftServeConfig { BaseUrl = builder.Uri, SourcePath = sourcePath };
    }

    /// <summary>True when <paramref name="host"/> is <c>localhost</c> or a loopback address.</summary>
    public static bool IsLoopback(string host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip);
    }

    /// <inheritdoc />
    public override string ToString() => BaseUrl.ToString() + (SourcePath is null ? "" : string.Create(CultureInfo.InvariantCulture, $" (from {SourcePath})"));
}
