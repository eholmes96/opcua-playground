using System.Diagnostics;
using System.IO;
using System.Windows;
using OpcUaPlayground.Dialogs;
using OpcUaPlayground.Services;

namespace OpcUaPlayground;

/// <summary>
/// The update button at the right of the menu bar. Nothing is checked until it is pressed. Update
/// downloads the release, checks it, then closes the window the normal way (so unsaved changes are
/// asked about) and installs while closing.
/// </summary>
public partial class MainWindow
{
    /// <summary>The downloaded, verified exe to install when the window closes.</summary>
    private string? _updateReadyExe;

    private bool _updateBusy;

    private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_updateBusy) return;
        _updateBusy = true;
        CheckUpdatesButton.IsEnabled = false;

        try
        {
            using var client = new UpdateClient();
            var current = UpdateCheck.CurrentVersion();

            StatusText.Text = "checking GitHub for a newer version";
            var (release, problem) = await client.GetLatestAsync(App.UpdateFeed).ConfigureAwait(true);

            if (release is null)
            {
                StatusText.Text = $"couldn't check for updates: {problem}";
                return;
            }

            if (!UpdateCheck.IsNewer(release, current))
            {
                StatusText.Text = $"you have the latest version ({UpdateCheck.Text(current)})";
                return;
            }

            string latest = UpdateCheck.Text(release.Version);
            var answer = DarkMessageBox.Confirm(
                this,
                $"Version {latest} is available. You have {UpdateCheck.Text(current)}."
                + Environment.NewLine + Environment.NewLine
                + $"Update downloads it from GitHub ({UpdateCheck.Megabytes(release.AssetSize)}), checks it, "
                + "and restarts OPC UA Playground. You'll be asked about unsaved changes first.",
                "Update",
                "Not now",
                cancel: null);

            if (answer != DarkDialogResult.Yes)
            {
                StatusText.Text = $"version {latest} is available and was not installed";
                return;
            }

            if (UpdateInstaller.WhyNotInstallable() is { } reason)
            {
                OfferReleasePage(release, $"This copy can't update itself: {reason}.");
                return;
            }

            if (_recorder.IsRecording)
            {
                StatusText.Text = "stop the recording before updating";
                return;
            }

            var progress = new Progress<double>(share =>
                StatusText.Text = $"downloading version {latest}: {share:P0}");
            string workFolder = UpdateInstaller.WorkFolder(release.Version);
            var (exe, failure) = await client
                .DownloadAndVerifyAsync(release, workFolder, progress)
                .ConfigureAwait(true);

            if (exe is null)
            {
                UpdateInstaller.DiscardDownload(workFolder);
                StatusText.Text = $"version {latest} wasn't installed: {failure}";
                return;
            }

            // A recording may have been started while the download ran.
            if (_recorder.IsRecording)
            {
                UpdateInstaller.DiscardDownload(workFolder);
                StatusText.Text = $"version {latest} wasn't installed: stop the recording, then press update again";
                return;
            }

            StatusText.Text = $"version {latest} is downloaded and checked; installing";
            _updateReadyExe = exe;
            Close();

            // Still here means Cancel was chosen at the unsaved-changes question, or installing failed.
            // Once installed, the new copy removes the download itself.
            if (_updateReadyExe is not null)
            {
                _updateReadyExe = null;
                UpdateInstaller.DiscardDownload(workFolder);
                StatusText.Text = $"version {latest} wasn't installed; nothing was changed";
            }
        }
        finally
        {
            _updateBusy = false;
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Called from the window's Closing handler, after unsaved changes have been dealt with. True
    /// lets the window close; false keeps it open, with the old exe still in place.
    /// </summary>
    private bool InstallPendingUpdate()
    {
        if (_updateReadyExe is not { } newExe) return true;
        string current = Environment.ProcessPath!;

        try
        {
            UpdateInstaller.Replace(current, newExe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DarkMessageBox.Show(this, $"The update couldn't be installed, so nothing was changed.{Environment.NewLine}{Environment.NewLine}{ex.Message}");
            return false;
        }

        _updateReadyExe = null;     // installed: from here on, closing is closing
        try
        {
            Process.Start(new ProcessStartInfo(current) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(current)! });
        }
        catch (Exception ex)
        {
            DarkMessageBox.Show(this, $"The update is installed, but the new version didn't start. Open it again from your shortcut.{Environment.NewLine}{Environment.NewLine}{ex.Message}");
        }

        return true;
    }

    private void OfferReleasePage(UpdateRelease release, string why)
    {
        var answer = DarkMessageBox.Confirm(
            this,
            why + Environment.NewLine + Environment.NewLine + "Open the release page to download it yourself?",
            "Open page",
            "Not now",
            cancel: null);

        if (answer == DarkDialogResult.Yes && Uri.TryCreate(release.PageUrl, UriKind.Absolute, out var page)
            && page.Scheme == Uri.UriSchemeHttps)
            Process.Start(new ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true });

        StatusText.Text = $"version {UpdateCheck.Text(release.Version)} is available and was not installed";
    }
}
