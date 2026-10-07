using System.Buffers.Binary;

namespace OpcUaPlayground.Services.Cip;

/// <summary>
/// The Connection Manager's cumulative counters, read from class 0x06 instance 1.
///
/// These are the right thing to watch for connection pressure, because they only ever increment.
/// An instantaneous "connections in use" gauge misses a burst that is over in a few hundred
/// milliseconds; a counter records it permanently, so sampling before and after an action gives an
/// exact cost even when the sampling is slow.
///
/// <see cref="OpenResourceRejects"/> is the one that means the module ran out. If it moves, some
/// client asked for a connection and did not get one.
///
/// The field order is the documented ODVA Volume 1 layout for this object's attributes 1 to 8. It
/// has NOT been checked against real Rockwell firmware, and some modules return more attributes
/// than these, so <see cref="Raw"/> is kept and reported alongside the named values rather than
/// asking anyone to trust the labels blindly.
/// </summary>
public sealed record ConnectionManagerCounters(
    ushort OpenRequests,
    ushort OpenFormatRejects,
    ushort OpenResourceRejects,
    ushort OpenOtherRejects,
    ushort CloseRequests,
    ushort CloseFormatRequests,
    ushort CloseOtherRequests,
    ushort ConnectionTimeouts,
    byte[] Raw)
{
    private const int NamedFieldCount = 8;

    public static bool TryParse(ReadOnlySpan<byte> data, out ConnectionManagerCounters? counters)
    {
        counters = null;
        if (data.Length < NamedFieldCount * 2) return false;

        // Read the words up front: a span cannot be captured by a local function.
        var words = new ushort[NamedFieldCount];
        for (int i = 0; i < NamedFieldCount; i++)
            words[i] = BinaryPrimitives.ReadUInt16LittleEndian(data[(i * 2)..]);

        counters = new ConnectionManagerCounters(
            words[0], words[1], words[2], words[3],
            words[4], words[5], words[6], words[7], data.ToArray());
        return true;
    }

    public IEnumerable<(string Name, int Value)> Named()
    {
        yield return ("open requests", OpenRequests);
        yield return ("open format rejects", OpenFormatRejects);
        yield return ("open resource rejects", OpenResourceRejects);
        yield return ("open other rejects", OpenOtherRejects);
        yield return ("close requests", CloseRequests);
        yield return ("close format requests", CloseFormatRequests);
        yield return ("close other requests", CloseOtherRequests);
        yield return ("connection timeouts", ConnectionTimeouts);
    }

    /// <summary>
    /// What changed between two samples. Counters are 16-bit and wrap, so a decrease is reported as
    /// a wrap rather than as a negative, which would otherwise read as the module losing count.
    /// </summary>
    public IEnumerable<(string Name, int Before, int After, int Delta, bool Wrapped)> DeltaFrom(ConnectionManagerCounters before)
    {
        var b = before.Named().ToList();
        var a = Named().ToList();
        for (int i = 0; i < a.Count; i++)
        {
            bool wrapped = a[i].Value < b[i].Value;
            int delta = wrapped ? a[i].Value + ushort.MaxValue + 1 - b[i].Value : a[i].Value - b[i].Value;
            yield return (a[i].Name, b[i].Value, a[i].Value, delta, wrapped);
        }
    }
}
