using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace OpcUaPlayground.Services;

/// <summary>A published release this copy could update to.</summary>
public sealed record UpdateRelease(
    Version Version, string Tag, string PageUrl, string AssetName, string AssetUrl, long AssetSize, string Sha256);

/// <summary>
/// Where releases are looked up and downloaded from. Downloads are pinned to the repository's own
/// release download path, so a reply naming anywhere else is refused rather than followed.
/// </summary>
public sealed record UpdateSource(Uri LatestReleaseUrl, string DownloadPrefix)
{
    public static readonly UpdateSource GitHub = new(
        new Uri("https://api.github.com/repos/eholmes96/opcua-playground/releases/latest"),
        "https://github.com/eholmes96/opcua-playground/releases/download/");

    /// <summary>
    /// A test feed on this machine (<c>--update-feed</c>), so the whole update can be tried without
    /// the internet. Anything that isn't loopback is refused: the switch must not be a way to point
    /// the app at someone else's server.
    /// </summary>
    public static UpdateSource? Loopback(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/", UriKind.Absolute, out var root))
            return null;
        if (root.Scheme is not ("http" or "https") || !root.IsLoopback) return null;

        return new UpdateSource(new Uri(root, "releases/latest"), new Uri(root, "download/").AbsoluteUri);
    }
}

/// <summary>
/// Reading GitHub's latest-release reply and deciding whether it is an update. No network here,
/// so all of it is covered by the self test; <see cref="UpdateClient"/> does the fetching.
/// </summary>
public static class UpdateCheck
{
    /// <summary>The release zip's name for a version, as the release steps in the docs produce it.</summary>
    public static string AssetNameFor(Version version) => $"OpcUaPlayground-{Text(version)}-win-x64.zip";

    public static string Text(Version version) => version.ToString(3);

    /// <summary>This build's version, without the commit id that .NET appends after a '+'.</summary>
    public static Version CurrentVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(UpdateCheck).Assembly;
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (TryParseVersion(informational, out var version)) return version;
        return Normalize(assembly.GetName().Version ?? new Version(0, 0, 0));
    }

    /// <summary>
    /// Reads "v0.8.4", "0.8.4" or "0.8.4+abc123" as 0.8.4. Always three parts, so 1.0 and 1.0.0
    /// compare equal. A pre-release suffix ("-beta") is not a version this app publishes.
    /// </summary>
    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;

        string core = text.Trim();
        if (core.StartsWith('v') || core.StartsWith('V')) core = core[1..];
        int plus = core.IndexOf('+');
        if (plus >= 0) core = core[..plus];

        if (!Version.TryParse(core, out var parsed) || parsed.Revision > 0) return false;
        version = Normalize(parsed);
        return true;
    }

    public static bool IsNewer(UpdateRelease release, Version current) => release.Version > Normalize(current);

    /// <summary>
    /// The release in GitHub's reply, or null with the reason. Fails closed: a reply without the
    /// expected zip, without a SHA-256 for it, or pointing anywhere but this repository's release
    /// downloads is not an update.
    /// </summary>
    public static UpdateRelease? ParseLatest(string json, UpdateSource source, out string problem)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (Bool(root, "draft") || Bool(root, "prerelease"))
            {
                problem = "the latest release is a draft or pre-release";
                return null;
            }

            string tag = Str(root, "tag_name");
            if (!TryParseVersion(tag, out var version))
            {
                problem = $"the latest release's tag \"{tag}\" isn't a version number";
                return null;
            }

            string wanted = AssetNameFor(version);
            JsonElement? asset = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                foreach (var candidate in assets.EnumerateArray())
                    if (Str(candidate, "name").Equals(wanted, StringComparison.OrdinalIgnoreCase))
                        asset = candidate;

            if (asset is not { } found)
            {
                problem = $"release {tag} has no {wanted} to download";
                return null;
            }

            string url = Str(found, "browser_download_url");
            if (!url.StartsWith(source.DownloadPrefix, StringComparison.Ordinal))
            {
                problem = $"release {tag} points its download somewhere other than this project's releases";
                return null;
            }

            string digest = Str(found, "digest");
            if (!digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                || !IsSha256(digest["sha256:".Length..]))
            {
                problem = $"release {tag} has no SHA-256 checksum to check the download against";
                return null;
            }

            long size = found.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out long s) ? s : 0;

            problem = "";
            return new UpdateRelease(
                version, tag, Str(root, "html_url"), wanted, url, size,
                digest["sha256:".Length..].ToUpperInvariant());
        }
        catch (JsonException)
        {
            problem = "GitHub's reply couldn't be read";
            return null;
        }
    }

    /// <summary>Whether the file's SHA-256 is the expected one (hex, any case).</summary>
    public static bool HashMatches(string path, string expectedHex)
    {
        using var stream = File.OpenRead(path);
        string actual = Convert.ToHexString(SHA256.HashData(stream));
        return actual.Equals(expectedHex, StringComparison.OrdinalIgnoreCase);
    }

    private static Version Normalize(Version v) => new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));

    private static bool IsSha256(string hex) =>
        hex.Length == 64 && hex.All(c => char.IsAsciiHexDigit(c));

    private static string Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static bool Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    internal static string Megabytes(long bytes) =>
        (bytes / 1048576.0).ToString("0", CultureInfo.InvariantCulture) + " MB";
}
