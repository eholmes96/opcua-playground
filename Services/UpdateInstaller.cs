using System.IO;

namespace OpcUaPlayground.Services;

/// <summary>
/// Puts a verified new exe in place of the running one. Windows won't let a running exe be
/// overwritten, but it will let it be renamed, so the running copy steps aside as ".old", the new
/// one takes its name, and the new copy deletes ".old" once it has started.
/// </summary>
public static class UpdateInstaller
{
    public const string ExeName = "OpcUaPlayground.exe";
    public const string OldSuffix = ".old";
    public const string NewSuffix = ".new";

    /// <summary>Holds each download while it is unpacked and checked.</summary>
    public static string DownloadsRoot => Path.Combine(Path.GetTempPath(), "OpcUaPlayground-update");

    public static string WorkFolder(Version version) => Path.Combine(DownloadsRoot, UpdateCheck.Text(version));

    /// <summary>Removes a download that won't be installed. It is ~130 MB unpacked, too much to leave in Temp.</summary>
    public static void DiscardDownload(string workFolder)
    {
        try
        {
            if (Directory.Exists(workFolder)) Directory.Delete(workFolder, recursive: true);

            // And the app's own download folder, once nothing else is in it.
            string? parent = Path.GetDirectoryName(workFolder);
            if (string.Equals(parent, DownloadsRoot, StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                Directory.Delete(parent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for Windows' own temp clean-up; nothing in it can run by itself.
        }
    }

    /// <summary>
    /// Why this copy can't replace itself, or null if it can: only the published single-file exe
    /// can (a development build is an exe beside a folder of DLLs), and only where it may write.
    /// </summary>
    public static string? WhyNotInstallable()
    {
        string? exe = Environment.ProcessPath;
        if (exe is null) return "this copy couldn't find its own exe";

        // A single-file app's assemblies have no file of their own, so their Location is empty. That
        // emptiness is the test here, which is what the IL3000 warning is about.
#pragma warning disable IL3000
        if (!string.IsNullOrEmpty(typeof(UpdateInstaller).Assembly.Location))
            return "this is a development build, not the packaged single-file app";
#pragma warning restore IL3000

        string folder = Path.GetDirectoryName(exe)!;
        string probe = Path.Combine(folder, $".update-probe-{Environment.ProcessId}");
        try
        {
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Windows won't let this app write to its own folder ({folder})";
        }
    }

    /// <summary>
    /// Copies the new exe in beside the current one, then swaps the names. If the second rename
    /// fails, the current exe is put back, so a failed update leaves a working app.
    /// </summary>
    /// <param name="beforeFinalMove">Self-test seam: runs between the two renames.</param>
    public static void Replace(string currentPath, string newExe, Action? beforeFinalMove = null)
    {
        string staged = currentPath + NewSuffix;
        string old = currentPath + OldSuffix;

        File.Copy(newExe, staged, overwrite: true);
        try
        {
            if (File.Exists(old)) File.Delete(old);     // left by an earlier update
            File.Move(currentPath, old);
        }
        catch
        {
            TryDelete(staged);
            throw;
        }

        try
        {
            beforeFinalMove?.Invoke();
            File.Move(staged, currentPath);
        }
        catch
        {
            File.Move(old, currentPath);
            TryDelete(staged);
            throw;
        }
    }

    /// <summary>
    /// Run by the new copy at startup: removes the old exe and the download. The old copy may
    /// still be closing for a moment, so this tries a few times before giving up quietly.
    /// </summary>
    /// <param name="downloads">The download folder to remove; <see cref="DownloadsRoot"/> unless a test names its own.</param>
    public static async Task CleanUpAfterUpdateAsync(string? currentPath, string? downloads = null)
    {
        if (currentPath is null) return;
        string old = currentPath + OldSuffix;
        string staged = currentPath + NewSuffix;
        downloads ??= DownloadsRoot;

        // Nothing to do unless this copy has just been installed. Checking first also keeps a second
        // window from deleting a download another one is still busy with.
        if (!File.Exists(old) && !File.Exists(staged)) return;

        for (int attempt = 0; attempt < 10; attempt++)
        {
            bool left = false;
            left |= !TryDelete(old);
            left |= !TryDelete(staged);
            try { if (Directory.Exists(downloads)) Directory.Delete(downloads, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { left = true; }

            if (!left) return;
            await Task.Delay(1000).ConfigureAwait(false);
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
