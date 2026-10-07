using System.Collections.ObjectModel;
using System.Diagnostics;
using OpcUaPlayground.Models;
using OpcUaPlayground.Services.Cip;

namespace OpcUaPlayground.Services;

/// <summary>
/// Polls the tags in the monitor table, each on its own interval.
///
/// One loop wakes often, gathers only the tags whose interval has elapsed, packs them into as few
/// Multiple Service Packets as the message size allows, and sends those. A tag at 1000 ms and a
/// tag at 100 ms therefore share a message whenever their schedules coincide, and cost nothing when
/// they do not.
///
/// Everything the table shows is mutated on the UI thread through the dispatcher the caller
/// supplies, so the grid never sees a half-updated row. That hand-off is synchronous and costs a
/// round trip through the UI thread, so it happens once per message rather than once per tag, and
/// the loop reads the table from a copy kept up to date by the collection rather than asking the UI
/// thread for one on every tick.
/// </summary>
public sealed class TagMonitor
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How much of one message to fill, on the way out and on the way back. An unconnected message
    /// tops out a little over 500 bytes in either direction, so both stay well inside that rather
    /// than gambling on it. The reply side is the one that bites: short names on 8-byte types fit
    /// forty requests in the request budget and then come back larger than the limit.
    /// </summary>
    private const int RequestBudgetBytes = 440;
    private const int ReplyBudgetBytes = 440;

    /// <summary>How often the loop wakes to look for due tags. Finer than any sensible scan rate,
    /// coarse enough not to spin.</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(20);

    private readonly Action<Action> _toUiThread;
    private CancellationTokenSource? _running;

    /// <summary>
    /// The table as the loop sees it: a copy, because the collection is edited on the UI thread.
    /// Only refreshed after <see cref="_tableChanged"/> says a row came or went. Copying on every
    /// change instead would make a bulk add of N rows cost N squared.
    /// </summary>
    private volatile MonitoredTag[] _snapshot = Array.Empty<MonitoredTag>();
    private volatile bool _tableChanged = true;

    public TagMonitor(Action<Action> toUiThread)
    {
        _toUiThread = toUiThread;
        Tags.CollectionChanged += (_, _) => _tableChanged = true;
    }

    /// <summary>
    /// The current rows. The flag is cleared before copying, so an edit landing during the copy
    /// sets it again and is picked up on the next tick rather than lost.
    /// </summary>
    private MonitoredTag[] CurrentTags()
    {
        if (_tableChanged)
        {
            _tableChanged = false;
            _toUiThread(() => _snapshot = Tags.ToArray());
        }
        return _snapshot;
    }

    public ObservableCollection<MonitoredTag> Tags { get; } = new();

    public bool IsRunning => Volatile.Read(ref _running) is not null;

    /// <summary>Raised when polling stops on its own, e.g. the connection dropped.</summary>
    public event Action<string>? Stopped;

    public void Start(PlcEndpoint endpoint)
    {
        if (IsRunning) return;

        // What the rows show now is left over from before, however current it looks. A recording
        // started alongside this must wait for this run's readings. See TagRecorder.
        foreach (var tag in Tags) tag.ForgetReading();

        var cts = new CancellationTokenSource();
        Volatile.Write(ref _running, cts);
        _ = RunAsync(endpoint, cts);
    }

    public void Stop()
    {
        var running = Interlocked.Exchange(ref _running, null);
        if (running is null) return;

        // The loop disposes its own token when it finishes, which can land between the exchange
        // above and this call. It is stopping either way.
        try { running.Cancel(); } catch (ObjectDisposedException) { }
    }

    private async Task RunAsync(PlcEndpoint endpoint, CancellationTokenSource cts)
    {
        string reason = "stopped";
        bool failed = false;
        try
        {
            await using var session = await EipSession
                .ConnectAsync(endpoint.Address, endpoint.SourceAddress, RequestTimeout, cts.Token)
                .ConfigureAwait(false);

            var routePath = CipRoutePath.ToSlot(endpoint.Slot);
            var clock = Stopwatch.StartNew();

            // Everything is due immediately on the first pass, so the table fills at once.
            _tableChanged = true;
            foreach (var tag in CurrentTags()) tag.NextDueTicks = 0;

            while (!cts.IsCancellationRequested)
            {
                long now = clock.ElapsedMilliseconds;
                var due = CurrentTags().Where(t => t.NextDueTicks <= now).ToList();

                if (due.Count > 0)
                {
                    foreach (var tag in due) tag.NextDueTicks = now + tag.ScanRateMs;
                    await PollAsync(session, routePath, due, cts.Token).ConfigureAwait(false);
                    if (session.IsFaulted) { reason = "connection lost"; failed = true; break; }
                }

                try { await Task.Delay(Tick, cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Stop was pressed.
        }
        catch (OperationCanceledException)
        {
            // Nobody pressed Stop, so this is a request's own timeout firing: the controller went
            // quiet. That is the failure that most needs saying, not a normal stop.
            reason = $"the controller did not answer within {RequestTimeout.TotalSeconds:0} s";
            failed = true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            failed = true;
        }
        finally
        {
            // Only clear the slot if it is still ours. A Stop followed straight away by a Start
            // puts a new loop there, and clearing it would leave that loop running with nothing
            // able to stop it.
            bool selfStopped = Interlocked.CompareExchange(ref _running, null, cts) == cts;
            cts.Dispose();

            if (selfStopped)
            {
                var rows = _snapshot;
                _toUiThread(() =>
                {
                    // Values left standing after the loop has died look live and are not. Say so on
                    // every row, before anyone is told polling stopped, so a recording in progress
                    // captures the moment the connection went.
                    if (failed)
                        foreach (var tag in rows) tag.ApplyReading("", $"not polling: {reason}", null);

                    Stopped?.Invoke(reason);
                });
            }
        }
    }

    /// <summary>What one poll found for one row, carried to the UI thread with the rest of its message.</summary>
    private readonly record struct Reading(MonitoredTag Tag, string Value, string? Error, int? ByteCount)
    {
        public static Reading Failed(MonitoredTag tag, string error) => new(tag, "", error, null);
    }

    private sealed record Work(MonitoredTag Tag, byte[] Request, int? ReplyBytes);

    private async Task PollAsync(
        EipSession session, byte[] routePath, List<MonitoredTag> due, CancellationToken ct)
    {
        // Values only. This used to ask each tag for its description as well, which cost an extra
        // request per tag on the first poll and could never succeed: a controller answers 0x05,
        // path destination unknown, for a property segment. Descriptions come from a CSV export
        // now. See RsLogixTagCsv.
        var work = new List<Work>(due.Count);
        var unreadable = new List<Reading>();

        foreach (var tag in due)
        {
            // A name that cannot be turned into a path is that row's problem, not the loop's.
            try { work.Add(new Work(tag, ReadTag.Build(tag.Address), ExpectedReplyBytes(tag))); }
            catch (ArgumentException ex) { unreadable.Add(Reading.Failed(tag, $"not a valid tag address: {ex.Message}")); }
        }

        if (unreadable.Count > 0) Publish(unreadable);

        foreach (var chunk in ReadTag.Chunk(
                     work, w => w.Request.Length, w => w.ReplyBytes, RequestBudgetBytes, ReplyBudgetBytes))
        {
            ct.ThrowIfCancellationRequested();

            var cip = chunk.Count == 1
                ? chunk[0].Request
                : ReadTag.Batch(chunk.Select(w => w.Request).ToList());

            var response = await session
                .SendRRDataAsync(UnconnectedSend.Build(cip, routePath), RequestTimeout, ct)
                .ConfigureAwait(false);

            var readings = new List<Reading>(chunk.Count);

            if (response is not { } reply)
            {
                readings.AddRange(chunk.Select(w => Reading.Failed(w.Tag, "no reply")));
            }
            else if (chunk.Count == 1)
            {
                readings.Add(Decode(chunk[0].Tag, reply));
            }
            // A batch answers with one reply per request, in the order they were sent. A partial
            // status here means some member failed, not that the batch did.
            else if (!ReadTag.TryUnpack(reply.Data, out var replies) || replies.Count != chunk.Count)
            {
                string reason = reply.IsSuccess ? "unreadable batch reply" : reply.Message;
                readings.AddRange(chunk.Select(w => Reading.Failed(w.Tag, reason)));
            }
            else
            {
                for (int i = 0; i < chunk.Count; i++) readings.Add(Decode(chunk[i].Tag, replies[i]));
            }

            Publish(readings);
        }
    }

    /// <summary>
    /// What this row's reply should cost, from the value size last seen or estimated from its type.
    /// Null when nothing is known yet, which sends the row on its own once, after which its real
    /// size is known. Guessing instead could overflow a reply, and an overflowed read never
    /// reports the size that would have fixed the guess.
    /// </summary>
    private static int? ExpectedReplyBytes(MonitoredTag tag) =>
        tag.ByteCount > 0 ? ReadTag.ReplyOverheadBytes + tag.ByteCount : null;

    private static Reading Decode(MonitoredTag tag, CipResponse reply)
    {
        if (!reply.IsSuccess) return Reading.Failed(tag, reply.Message);

        if (!TagValueDecoder.TryDecode(reply.Data, tag.DataType, out var value) || value is null)
            return Reading.Failed(tag, "could not decode the value");

        return new Reading(tag, value.Display, null, value.ByteCount);
    }

    /// <summary>One trip to the UI thread for a whole message's worth of rows.</summary>
    private void Publish(List<Reading> readings) =>
        _toUiThread(() =>
        {
            foreach (var r in readings) r.Tag.ApplyReading(r.Value, r.Error, r.ByteCount);
        });
}
