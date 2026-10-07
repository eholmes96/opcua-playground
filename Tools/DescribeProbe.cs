using System.Buffers.Binary;
using System.Net;
using System.Text;
using OpcUaPlayground.Services.Cip;

namespace OpcUaPlayground.Tools;

/// <summary>
/// Asks a controller for one tag's description every way this app knows how, and prints exactly
/// what comes back: general status, extended status and raw bytes.
///
/// It exists because the monitor deliberately swallows a failed description read, so the table just
/// shows an empty column and there is no way to tell a controller that has no documentation from a
/// request the controller never understood. That distinction is the whole question.
///
/// Everything here is a read. Get_Attributes_All, Get_Attribute_Single and Read Tag are all on the
/// read-only allowlist, so this cannot write no matter what is typed at it.
/// </summary>
public static class DescribeProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>Symbol Object attributes worth asking for one at a time. 1, 2 and 8 are the ones
    /// the browser already uses; the rest are here to see what else the controller will part with.</summary>
    private static readonly ushort[] Attributes = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };

    public static async Task<bool> RunAsync(string[] args)
    {
        int index = Array.FindIndex(args, a => string.Equals(a, "--describe", StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 2 >= args.Length || !IPAddress.TryParse(args[index + 1], out var address))
        {
            Console.WriteLine("usage: OpcUaPlayground --describe <ipv4> <slot> <tag name>");
            return false;
        }

        int slot = int.TryParse(args[index + 2], out int parsed) ? parsed : 0;
        string tagName = args[index + 3];

        Console.WriteLine($"asking {address} slot {slot} about \"{tagName}\"");
        Console.WriteLine();

        await using var session = await EipSession
            .ConnectAsync(address, null, Timeout, CancellationToken.None).ConfigureAwait(false);
        var route = CipRoutePath.ToSlot(slot);

        // 1. The value, so a failure below cannot be blamed on the tag name.
        await ShowAsync(session, route, "value: read " + tagName, ReadTag.Build(tagName)).ConfigureAwait(false);

        // 2. What the app actually sends today.
        await ShowAsync(session, route, "description: read " + tagName + ".@Description",
            ReadTag.BuildDescription(tagName)).ConfigureAwait(false);

        // 3. A second extended property. If @Max answers and @Description does not, the syntax is
        //    fine and only the description is unavailable. If neither answers, the whole idea of
        //    reading an extended property over CIP is wrong.
        await ShowAsync(session, route, "extended property: read " + tagName + ".@Max",
            ReadTag.Build(tagName + ".@Max")).ConfigureAwait(false);

        // 4. Find the tag in the Symbol Object, then ask that instance for everything it has.
        uint instance = await FindInstanceAsync(session, route, tagName).ConfigureAwait(false);
        if (instance == 0)
        {
            Console.WriteLine($"could not find \"{tagName}\" in the controller's symbol list, so the");
            Console.WriteLine("symbol attribute sweep below was skipped.");
            return false;
        }

        Console.WriteLine($"\"{tagName}\" is Symbol Object instance {instance}");
        Console.WriteLine();

        await ShowAsync(session, route, $"symbol {instance}: Get_Attributes_All",
            BuildGetAttributesAll(instance)).ConfigureAwait(false);

        foreach (ushort attribute in Attributes)
        {
            await ShowAsync(session, route, $"symbol {instance}: attribute {attribute}",
                BuildGetAttributeSingle(instance, attribute)).ConfigureAwait(false);
        }

        return true;
    }

    // ===================== Sending and showing =====================

    private static async Task ShowAsync(EipSession session, byte[] route, string what, byte[] request)
    {
        var reply = await session
            .SendRRDataAsync(UnconnectedSend.Build(request, route), Timeout, CancellationToken.None)
            .ConfigureAwait(false);

        if (reply is not { } response)
        {
            Console.WriteLine($"{what,-52} no reply");
            return;
        }

        string status = response.IsSuccess
            ? "OK"
            : $"status 0x{response.GeneralStatus:X2}/0x{response.ExtendedStatus:X4}  {response.Message}";

        Console.WriteLine($"{what,-52} {status}");

        if (response.Data.Length > 0)
        {
            Console.WriteLine($"{"",-52} {Hex(response.Data)}");
            Console.WriteLine($"{"",-52} {Printable(response.Data)}");
        }

        Console.WriteLine();
    }

    /// <summary>Walks the controller's symbol list looking for the name, so the sweep can address
    /// the right instance.</summary>
    private static async Task<uint> FindInstanceAsync(EipSession session, byte[] route, string tagName)
    {
        uint start = 0;

        for (int request = 0; request < 2000; request++)
        {
            var reply = await session
                .SendRRDataAsync(
                    UnconnectedSend.Build(SymbolObject.BuildListTags(start), route), Timeout, CancellationToken.None)
                .ConfigureAwait(false);

            if (reply is not { } response) return 0;

            bool more = response.GeneralStatus == CipStatus.PartialTransfer;
            if (!response.IsSuccess && !more) return 0;
            if (!SymbolObject.TryParse(response.Data, out var page) || page.Count == 0) return 0;

            foreach (var symbol in page)
                if (symbol.Name.Equals(tagName, StringComparison.OrdinalIgnoreCase))
                    return symbol.InstanceId;

            if (!more) return 0;
            start = page[^1].InstanceId + 1;
        }

        return 0;
    }

    // ===================== Request building =====================

    private static byte[] BuildGetAttributesAll(uint instance)
    {
        var request = new List<byte> { CipServices.GetAttributesAll, 0x03, 0x20, SymbolObject.ClassSymbol, 0x25, 0x00 };
        AppendInstance(request, instance);
        return request.ToArray();
    }

    private static byte[] BuildGetAttributeSingle(uint instance, ushort attribute)
    {
        var request = new List<byte> { CipServices.GetAttributeSingle, 0x04, 0x20, SymbolObject.ClassSymbol, 0x25, 0x00 };
        AppendInstance(request, instance);
        request.Add(0x30);                      // 8-bit attribute segment
        request.Add((byte)attribute);
        return request.ToArray();
    }

    private static void AppendInstance(List<byte> request, uint instance)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)instance);
        request.AddRange(bytes);
    }

    // ===================== Formatting =====================

    private static string Hex(byte[] data) =>
        string.Join(' ', data.Take(64).Select(b => b.ToString("X2"))) + (data.Length > 64 ? " ..." : "");

    /// <summary>The same bytes as text, because a description would be unmistakable here.</summary>
    private static string Printable(byte[] data)
    {
        var text = new StringBuilder();
        foreach (byte b in data.Take(64)) text.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
        return text.ToString();
    }
}
