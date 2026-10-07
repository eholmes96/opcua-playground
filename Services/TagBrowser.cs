using OpcUaPlayground.Models;
using OpcUaPlayground.Services.Cip;

namespace OpcUaPlayground.Services;

/// <summary>
/// Builds the tag tree for a connected controller.
///
/// Shaped after Ignition's OPC browser, which most people here already know: a Global folder for
/// controller-scoped tags, then one folder per program. Arrays and structures are folders too, so a
/// UDT instance opens into its members and an array opens into its elements.
///
/// Two passes over the Symbol Object. The first lists controller scope, which also reveals the
/// programs, because each appears there as a symbol named "Program:MainProgram". The second lists
/// each program's own tags. Type definitions are fetched once per template and cached, since a
/// controller with two hundred instances of one UDT should read that definition once.
///
/// The cache lives for one browse and no longer. Template instance ids mean something only inside
/// one controller and one download of its project: template 0x0123 on the next controller, or on
/// this one after a download, is a different type. Each browse therefore gets its own
/// <see cref="Run"/>, which also keeps two overlapping browses from sharing anything.
/// </summary>
public sealed class TagBrowser
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Guards against a malformed reply turning the listing loop into an endless one. A controller
    /// returns dozens of symbols per reply, so this is far above any real tag count, but if it is
    /// ever reached the caller is told rather than being handed a quietly truncated list.
    /// </summary>
    private const int MaxListRequests = 2000;

    /// <summary>
    /// Array element indexes, capped per dimension. A DINT[100000] would otherwise build a hundred
    /// thousand nodes the moment its parent folder is opened, which helps nobody and freezes the
    /// window.
    /// </summary>
    private const int MaxArrayElementsShown = 512;

    public Task<TagBrowseResult> BrowseAsync(
        PlcEndpoint endpoint, IProgress<string>? progress, CancellationToken ct) =>
        new Run().BrowseAsync(endpoint, progress, ct);

    /// <summary>Hides the tags Logix keeps for itself, matching what Ignition shows.</summary>
    private static bool IsBrowsable(LogixSymbol symbol) =>
        !symbol.IsProgramContainer && !symbol.Type.IsSystem && !LogixTypes.IsInternalName(symbol.Name);

    private static IEnumerable<uint> ElementIndexes(uint length) =>
        Enumerable.Range(0, (int)Math.Min(length, MaxArrayElementsShown)).Select(i => (uint)i);

    private static int CountLeaves(TagNode node) =>
        node.Children.Count == 0 ? 1 : node.Children.Sum(CountLeaves);

    /// <summary>One browse: its session, its template cache, and whether its listing was cut short.</summary>
    private sealed class Run
    {
        private readonly Dictionary<ushort, LogixTemplate> _templates = new();
        private bool _truncated;

        private EipSession _session = null!;
        private byte[] _routePath = Array.Empty<byte>();

        public async Task<TagBrowseResult> BrowseAsync(
            PlcEndpoint endpoint, IProgress<string>? progress, CancellationToken ct)
        {
            await using var session = await EipSession
                .ConnectAsync(endpoint.Address, endpoint.SourceAddress, RequestTimeout, ct).ConfigureAwait(false);

            _session = session;
            _routePath = CipRoutePath.ToSlot(endpoint.Slot);

            progress?.Report("listing controller tags...");
            var controllerSymbols = await ListAsync(null, ct).ConfigureAwait(false);

            var programs = controllerSymbols.Where(s => s.IsProgramContainer).Select(s => s.ProgramName).ToList();
            var folders = new List<TagNode>();

            var global = new TagNode("Global", "", TagNodeKind.Folder, "");
            foreach (var symbol in controllerSymbols.Where(IsBrowsable).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
                global.Children.Add(await BuildNodeAsync(symbol.Name, symbol.Name, symbol.Type, symbol.Extents, ct).ConfigureAwait(false));
            folders.Add(global);

            foreach (var program in programs.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                progress?.Report($"listing tags in {program}...");
                var symbols = await ListAsync(program, ct).ConfigureAwait(false);

                var folder = new TagNode($"Program:{program}", "", TagNodeKind.Folder, "");
                foreach (var symbol in symbols.Where(IsBrowsable).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
                {
                    string address = $"Program:{program}.{symbol.Name}";
                    folder.Children.Add(await BuildNodeAsync(symbol.Name, address, symbol.Type, symbol.Extents, ct).ConfigureAwait(false));
                }
                folders.Add(folder);
            }

            int tagCount = folders.Sum(CountLeaves);
            return new TagBrowseResult(folders, tagCount, _templates.Count, _truncated);
        }

        // ===================== Symbol enumeration =====================

        private async Task<List<LogixSymbol>> ListAsync(string? programName, CancellationToken ct)
        {
            var all = new List<LogixSymbol>();
            uint instance = 0;

            for (int request = 0; ; request++)
            {
                if (request >= MaxListRequests)
                {
                    _truncated = true;
                    break;
                }

                ct.ThrowIfCancellationRequested();

                var cip = UnconnectedSend.Build(SymbolObject.BuildListTags(instance, programName), _routePath);
                var response = await _session.SendRRDataAsync(cip, RequestTimeout, ct).ConfigureAwait(false);
                if (response is not { } reply) break;

                // Partial transfer is the normal answer for all but the last page.
                bool more = reply.GeneralStatus == CipStatus.PartialTransfer;
                if (!reply.IsSuccess && !more) break;

                if (!SymbolObject.TryParse(reply.Data, out var page) || page.Count == 0) break;
                all.AddRange(page);

                if (!more) break;
                instance = page[^1].InstanceId + 1;
            }

            return all;
        }

        // ===================== Tree building =====================

        private async Task<TagNode> BuildNodeAsync(
            string name, string address, LogixSymbolType type, IReadOnlyList<uint> extents, CancellationToken ct)
        {
            // A zero extent means that dimension is not in use, so only the leading non-zero ones count.
            var dimensions = extents.TakeWhile(e => e > 0).ToArray();
            if (dimensions.Length > 0)
            {
                // Resolve the element type before labelling, so an array of a UDT reads "Station[3]"
                // rather than the useless "structure[3]".
                string elementType = type.IsStructure
                    ? (await GetTemplateAsync(type.TemplateInstanceId, ct).ConfigureAwait(false))?.Name
                      ?? $"structure {type.TemplateInstanceId}"
                    : LogixTypes.NameOf(type.AtomicCode);

                return await BuildArrayLevelAsync(name, address, type, elementType, dimensions, Array.Empty<uint>(), ct)
                    .ConfigureAwait(false);
            }

            if (!type.IsStructure)
                return new TagNode(name, LogixTypes.NameOf(type.AtomicCode), TagNodeKind.Tag, address);

            var template = await GetTemplateAsync(type.TemplateInstanceId, ct).ConfigureAwait(false);
            if (template is null)
                return new TagNode(name, $"structure (template {type.TemplateInstanceId})", TagNodeKind.Tag, address);

            var node = new TagNode(name, template.Name, TagNodeKind.Structure, address);
            foreach (var member in template.Members.Where(m => !m.IsInternal))
            {
                var memberExtents = member.ArrayLength > 0 ? new uint[] { member.ArrayLength } : Array.Empty<uint>();
                node.Children.Add(await BuildNodeAsync(
                    member.Name, $"{address}.{member.Name}", member.Type, memberExtents, ct).ConfigureAwait(false));
            }
            return node;
        }

        /// <summary>
        /// One dimension of an array, as a folder. Logix writes every index of an element inside one
        /// bracket, Grid[2,5], so the indexes chosen on the way down are carried to the innermost
        /// dimension and only that one produces elements. A folder for a partial index, Grid[2], is
        /// not something a controller can read, so it has no address.
        /// </summary>
        private async Task<TagNode> BuildArrayLevelAsync(
            string name, string address, LogixSymbolType type, string elementType,
            IReadOnlyList<uint> extents, uint[] chosen, CancellationToken ct)
        {
            var remaining = extents.Skip(chosen.Length);
            var folder = chosen.Length == 0
                ? new TagNode(name, $"{elementType}[{string.Join(',', extents)}]", TagNodeKind.Array, address)
                : new TagNode($"{name}[{string.Join(',', chosen)}]", $"{elementType}[{string.Join(',', remaining)}]",
                              TagNodeKind.Array, "");

            foreach (uint index in ElementIndexes(extents[chosen.Length]))
            {
                uint[] next = [.. chosen, index];

                if (next.Length < extents.Count)
                {
                    folder.Children.Add(await BuildArrayLevelAsync(
                        name, address, type, elementType, extents, next, ct).ConfigureAwait(false));
                    continue;
                }

                string indexes = string.Join(',', next);
                folder.Children.Add(await BuildNodeAsync(
                    $"{name}[{indexes}]", $"{address}[{indexes}]", type, Array.Empty<uint>(), ct).ConfigureAwait(false));
            }

            return folder;
        }

        // ===================== Templates =====================

        private async Task<LogixTemplate?> GetTemplateAsync(ushort instanceId, CancellationToken ct)
        {
            if (_templates.TryGetValue(instanceId, out var cached)) return cached;

            var headerCip = UnconnectedSend.Build(TemplateObject.BuildGetHeader(instanceId), _routePath);
            var headerReply = await _session.SendRRDataAsync(headerCip, RequestTimeout, ct).ConfigureAwait(false);
            if (headerReply is not { IsSuccess: true } h) return null;
            if (!TemplateObject.TryParseHeader(h.Data, out var header) || header is null) return null;
            if (header.DefinitionBytes <= 0) return null;

            var blob = new List<byte>(header.DefinitionBytes);
            uint offset = 0;
            while (blob.Count < header.DefinitionBytes)
            {
                ct.ThrowIfCancellationRequested();

                int remaining = header.DefinitionBytes - blob.Count;
                var readCip = UnconnectedSend.Build(
                    TemplateObject.BuildReadDefinition(instanceId, offset, (ushort)Math.Min(remaining, ushort.MaxValue)),
                    _routePath);
                var reply = await _session.SendRRDataAsync(readCip, RequestTimeout, ct).ConfigureAwait(false);
                if (reply is not { } r) return null;
                if (!r.IsSuccess && r.GeneralStatus != CipStatus.PartialTransfer) return null;
                if (r.Data.Length == 0) break;

                blob.AddRange(r.Data);
                offset += (uint)r.Data.Length;
                if (r.IsSuccess) break;
            }

            if (!TemplateObject.TryParseDefinition(blob.ToArray(), instanceId, header, out var template) || template is null)
                return null;

            _templates[instanceId] = template;
            return template;
        }
    }
}

/// <summary>What one browse produced.</summary>
public sealed record TagBrowseResult(
    IReadOnlyList<TagNode> Folders, int TagCount, int TemplateCount, bool Truncated);
