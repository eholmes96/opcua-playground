using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;

namespace OpcUaPlayground.Services;

/// <summary>
/// The only part of the app that talks to the internet, and only when the user presses the update
/// button: one request for the latest release, and, if they choose Update, one download of its zip.
/// Nothing is sent to a controller from here and nothing is sent anywhere at startup.
/// </summary>
public sealed class UpdateClient : IDisposable
{
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SelfTestTimeout = TimeSpan.FromMinutes(2);

    private readonly HttpClient _http;

    public UpdateClient()
    {
        // The system proxy settings apply, as they would in a browser. GitHub refuses requests
        // without a User-Agent.
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"OpcUaPlayground/{UpdateCheck.Text(UpdateCheck.CurrentVersion())}");
    }

    public void Dispose() => _http.Dispose();

    /// <summary>The latest release, or null with a reason worth showing.</summary>
    public async Task<(UpdateRelease? Release, string Problem)> GetLatestAsync(UpdateSource source)
    {
        using var cts = new CancellationTokenSource(LookupTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, source.LatestReleaseUrl);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return (null, "GitHub has no public release to check against");
            if (!response.IsSuccessStatusCode)
                return (null, $"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}");

            string json = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            var release = UpdateCheck.ParseLatest(json, source, out string problem);
            return (release, problem);
        }
        catch (OperationCanceledException)
        {
            return (null, "GitHub didn't answer in time");
        }
        catch (HttpRequestException ex)
        {
            return (null, ex.HttpRequestError switch
            {
                HttpRequestError.NameResolutionError => "no internet connection (GitHub's address couldn't be looked up)",
                HttpRequestError.ConnectionError => "no internet connection (GitHub couldn't be reached)",
                HttpRequestError.SecureConnectionError => "the secure connection to GitHub failed",
                HttpRequestError.ProxyTunnelError => "the proxy refused the connection to GitHub",
                _ => ex.Message,
            });
        }
    }

    /// <summary>
    /// Downloads the release into <paramref name="workFolder"/>, checks it against GitHub's SHA-256,
    /// unpacks it, and runs the new exe's own self test. Returns the verified exe, or null with the
    /// reason. Nothing outside the work folder is touched.
    /// </summary>
    public async Task<(string? Exe, string Problem)> DownloadAndVerifyAsync(
        UpdateRelease release, string workFolder, IProgress<double>? progress)
    {
        if (Directory.Exists(workFolder)) Directory.Delete(workFolder, recursive: true);
        Directory.CreateDirectory(workFolder);
        string zip = Path.Combine(workFolder, release.AssetName);

        using (var cts = new CancellationTokenSource(DownloadTimeout))
        {
            try
            {
                using var response = await _http.GetAsync(release.AssetUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return (null, $"the download failed: GitHub answered {(int)response.StatusCode}");

                long total = response.Content.Headers.ContentLength ?? release.AssetSize;
                await using var source = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
                await using var target = File.Create(zip);

                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cts.Token).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cts.Token).ConfigureAwait(false);
                    done += read;
                    if (total > 0) progress?.Report((double)done / total);
                }
            }
            catch (OperationCanceledException)
            {
                return (null, "the download took too long and was stopped");
            }
            catch (HttpRequestException ex)
            {
                return (null, $"the download failed: {ex.Message}");
            }
        }

        if (!UpdateCheck.HashMatches(zip, release.Sha256))
            return (null, "the download didn't match GitHub's SHA-256 checksum, so it was thrown away");

        string unpacked = Path.Combine(workFolder, "unpacked");
        try
        {
            ZipFile.ExtractToDirectory(zip, unpacked);    // refuses entries that would land outside it
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return (null, $"the download couldn't be unpacked: {ex.Message}");
        }

        string exe = Path.Combine(unpacked, UpdateInstaller.ExeName);
        if (!File.Exists(exe))
            return (null, $"the download has no {UpdateInstaller.ExeName} in it");

        string? stamped = FileVersionInfo.GetVersionInfo(exe).ProductVersion;
        if (!UpdateCheck.TryParseVersion(stamped, out var exeVersion) || exeVersion != release.Version)
            return (null, $"the downloaded exe says it is version {stamped ?? "unknown"}, not {UpdateCheck.Text(release.Version)}");

        return await RunsItsSelfTestAsync(exe).ConfigureAwait(false)
            ? (exe, "")
            : (null, "the downloaded version failed its own self test, so it wasn't installed");
    }

    /// <summary>A last check that the new exe runs on this PC before it replaces the working one.</summary>
    private static async Task<bool> RunsItsSelfTestAsync(string exe)
    {
        var start = new ProcessStartInfo(exe, "--selftest")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };

        using var process = Process.Start(start);
        if (process is null) return false;

        var drain = process.StandardOutput.ReadToEndAsync();
        using var cts = new CancellationTokenSource(SelfTestTimeout);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return false;
        }

        await drain.ConfigureAwait(false);
        return process.ExitCode == 0;
    }
}
