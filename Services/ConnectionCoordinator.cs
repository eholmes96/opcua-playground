using OpcUaPlayground.Models;

namespace OpcUaPlayground.Services;

/// <summary>
/// Owns one connection attempt at a time. Editing the address or the slot supersedes whatever is
/// in flight, so a half-finished scan of the previous address can never land on the light after
/// the user has moved on.
/// </summary>
public sealed class ConnectionCoordinator
{
    private readonly IPlcTransport _transport;
    private CancellationTokenSource? _inFlight;

    public ConnectionCoordinator(IPlcTransport transport) => _transport = transport;

    public void Cancel()
    {
        var previous = Interlocked.Exchange(ref _inFlight, null);
        if (previous is null) return;
        try { previous.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Runs ping, identify, chassis walk. <paramref name="progress"/> must be created on the UI
    /// thread so its callbacks marshal back there, which is what lets the handler touch controls
    /// directly without any locking.
    /// </summary>
    public async Task ConnectAsync(PlcEndpoint endpoint, IProgress<ConnectionStatus> progress)
    {
        Cancel();
        var cts = new CancellationTokenSource();
        _inFlight = cts;

        try
        {
            progress.Report(new ConnectionStatus(PlcConnectionState.Disconnected, $"pinging {endpoint.Address}..."));
            var status = await _transport.ConnectAsync(endpoint, progress, cts.Token).ConfigureAwait(true);
            if (!cts.IsCancellationRequested) progress.Report(status);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer attempt; the newer one owns the light now.
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested)
                progress.Report(new ConnectionStatus(PlcConnectionState.Disconnected, ex.Message));
        }
        finally
        {
            Interlocked.CompareExchange(ref _inFlight, null, cts);
            cts.Dispose();
        }
    }
}
