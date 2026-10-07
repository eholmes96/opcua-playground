using System.IO;
using System.Text;

namespace OpcUaPlayground.Services;

/// <summary>
/// Reads tag descriptions out of a Studio 5000 / RSLogix 5000 CSV tag export.
///
/// The format, which is what this whole file hangs on:
///
///   remark,"CSV-Import-Export"
///   remark,"Version = RSLogix 5000 v20.01"
///   remark,"Export Options = Decorated Data, Tag Comments, Alias Comments"
///   TYPE,SCOPE,NAME,DESCRIPTION,DATATYPE,SPECIFIER,ATTRIBUTES
///   TAG,,MotorSpeed,"Conveyor speed feedback",DINT,,"(RADIX := Decimal, ...)"
///   TAG,MainProgram,Step,"Sequence step",DINT,,"(...)"
///   COMMENT,,Recipe,"Setpoint in degrees",,".SetPoint",
///   COMMENT,,Counts,"Count for bin 3",,"[3]",
///   ALIAS,,Motor,"Alias to the drive",,MotorSpeed,"(...)"
///
/// Three row types carry descriptions. A TAG row describes a whole tag. A COMMENT row describes one
/// member or one element, with the sub-path in SPECIFIER, which is how UDT members and array
/// elements are covered. An ALIAS row describes the alias, and its SPECIFIER is the alias TARGET
/// rather than a sub-path, so it must never be appended.
///
/// An export that includes logic comments ends with a second section: its own header row,
/// TYPE,SCOPE,ROUTINE,COMMENT,OWNING_ELEMENT,LOCATION, then RCOMMENT (rung) and TEXTBOX (function
/// block and SFC) rows. None of them describe a tag, and in a large project they can be most of
/// the file, so reading stops where that section starts. See <see cref="CommentSectionStart"/>.
///
/// Studio 5000 escapes text the way its string literals do: $N or $R$N for a line break, $' and $"
/// (or $Q) for quotes, $$ for a dollar. A quote inside a description is therefore $" and not a
/// doubled quote, which matters to the CSV reader as well as to the text. Both forms come from
/// Rockwell 1756-RM014, chapter 21 and appendix A.
///
/// Columns are located by the header row rather than by position, so a different Studio 5000
/// version that reorders them still reads. A file with no Logix header at all is treated as a plain
/// CSV with a name column and a description column, because a hand-made list is a reasonable thing
/// to point this at.
///
/// **Known limit:** a tag CSV export carries descriptions attached to TAGS. Member descriptions
/// that live on a user-defined type definition are part of the data type, not the tag, and are not
/// in this file. Where they are missing, a member falls back to its parent's description, which is
/// what Logix itself shows when pass-through is on.
/// </summary>
public static class RsLogixTagCsv
{
    public const string DialogFilter = "Tag export (*.csv)|*.csv|All files (*.*)|*.*";

    private static readonly string[] NameHeaders = { "name", "tag", "tagname", "tag name", "symbol" };
    private static readonly string[] DescriptionHeaders = { "description", "comment", "desc", "tag description" };

    public static TagCsvImport Load(string path) => Parse(Decode(File.ReadAllBytes(path)));

    public static TagCsvImport Parse(string text)
    {
        // Only a Studio 5000 export gets its conventions applied. A hand made list is plain CSV,
        // where "$5" is five dollars.
        bool logix = IsLogixExport(text);
        int end = logix ? CommentSectionStart(text) : text.Length;

        var rows = ReadRows(text, end, logix).Where(r => !IsRemark(r)).ToList();

        int header = rows.FindIndex(HasUsableHeader);
        int type = -1, scope = -1, name = -1, description = -1, specifier = -1;
        string format;

        if (header >= 0)
        {
            var columns = rows[header];
            type = IndexOf(columns, "type");
            scope = IndexOf(columns, "scope");
            specifier = IndexOf(columns, "specifier");
            name = IndexOfAny(columns, NameHeaders);
            description = IndexOfAny(columns, DescriptionHeaders);
            format = type >= 0 && specifier >= 0 ? "Studio 5000 tag export" : "CSV with a header row";
        }
        else
        {
            // No header anywhere. The most forgiving reading of a bare file is name then description.
            name = 0;
            description = 1;
            format = "plain CSV, first column the tag and second the description";
        }

        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int considered = 0;

        foreach (var row in rows.Skip(header + 1))
        {
            considered++;

            // A Logix export holds rows this has no business reading, such as alarm messages, and
            // rung comments when the section check had to read on. Anything with an unrecognised
            // TYPE is left alone, and is checked first so its text is never tidied for nothing.
            string kind = Field(row, type).Trim().ToUpperInvariant();
            if (type >= 0 && kind.Length > 0 && kind is not ("TAG" or "ALIAS" or "COMMENT")) continue;

            string tagName = Field(row, name).Trim();
            string raw = Field(row, description);
            string text2 = Tidy(logix ? Unescape(raw) : raw);
            if (tagName.Length == 0 || text2.Length == 0) continue;

            string path = Qualify(Field(row, scope).Trim(), tagName);

            // Only a COMMENT row's specifier is a sub-path. An alias target appended here would
            // invent a tag that does not exist.
            if (kind == "COMMENT")
            {
                string sub = Field(row, specifier).Trim();
                if (sub.Length > 0) path = Join(path, sub);
            }

            found[path] = text2;
        }

        return new TagCsvImport(new TagDescriptionLookup(found), considered, found.Count, format);
    }

    // ===================== Paths =====================

    /// <summary>
    /// Controller scope is a bare name. A program-scoped tag is addressed by this app as
    /// "Program:MainProgram.Step", which is how the controller wants it too.
    /// </summary>
    private static string Qualify(string scope, string name) =>
        scope.Length == 0 ? name : $"Program:{scope}.{name}";

    /// <summary>A specifier already carries its own separator when it is a member or an element.</summary>
    private static string Join(string path, string specifier) =>
        specifier[0] is '.' or '[' ? path + specifier : $"{path}.{specifier}";

    // ===================== The logic comment section =====================

    /// <summary>
    /// A Studio 5000 export opens with a CSV-Import-Export remark and carries the
    /// TYPE,SCOPE,NAME header near the top. Either one is enough.
    /// </summary>
    private static bool IsLogixExport(string text)
    {
        string top = text.Length > 65536 ? text[..65536] : text;
        return top.Contains("CSV-Import-Export", StringComparison.OrdinalIgnoreCase)
               || top.Contains("TYPE,SCOPE,NAME", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Where reading can stop: the first line of the logic comment section, or the end of the text
    /// when there is no such section or when stopping would lose something.
    ///
    /// The section opens with its own header row, but Rockwell's second example starts it with a
    /// TEXTBOX rather than an RCOMMENT, so any of the three marks the start.
    ///
    /// Rockwell's examples always put every tag row first. The manual never promises that order,
    /// though, so one search of the rest checks it: a TAG, ALIAS or COMMENT row anywhere after the
    /// section starts means the whole file is read. A false alarm, say a hand edited description
    /// holding a line that begins "TAG,", only costs the time this would have saved.
    /// </summary>
    internal static int CommentSectionStart(string text)
    {
        int start = FirstLine(text, 0, "TYPE,SCOPE,ROUTINE", "RCOMMENT,", "TEXTBOX,");
        if (start < 0) return text.Length;

        return FirstLine(text, start, "TAG,", "ALIAS,", "COMMENT,") >= 0 ? text.Length : start;
    }

    /// <summary>
    /// The start of the first line at or after <paramref name="from"/> that begins with any of
    /// the prefixes, or -1. Every record starts on a new line, so a record type always follows a
    /// line feed, and "COMMENT," can never match inside "RCOMMENT,".
    /// </summary>
    private static int FirstLine(string text, int from, params string[] prefixes)
    {
        int first = -1;
        foreach (string prefix in prefixes)
        {
            int at = text.IndexOf("\n" + prefix, from, StringComparison.OrdinalIgnoreCase);
            if (at >= 0 && (first < 0 || at + 1 < first)) first = at + 1;
        }
        return first;
    }

    /// <summary>
    /// Undoes Studio 5000's escaping: $N, $L and $R$N line breaks, $T, $' and $" or $Q quotes, $$
    /// for a dollar and $hh for a character by its hex code. A $ followed by anything else is kept,
    /// since it is not something the exporter writes.
    /// </summary>
    internal static string Unescape(string text)
    {
        int dollar = text.IndexOf('$');
        if (dollar < 0) return text;

        var plain = new StringBuilder(text.Length);
        plain.Append(text, 0, dollar);

        for (int i = dollar; i < text.Length; i++)
        {
            char c = text[i];
            if (c != '$' || i + 1 >= text.Length) { plain.Append(c); continue; }

            char code = char.ToUpperInvariant(text[i + 1]);
            char? replacement = code switch
            {
                '$' => '$',
                '\'' => '\'',
                '"' or 'Q' => '"',
                'N' or 'L' => '\n',
                'R' => '\r',
                'T' => '\t',
                'P' => '\f',
                _ => null,
            };

            if (replacement is { } one)
            {
                plain.Append(one);
                i++;
            }
            else if (i + 2 < text.Length && char.IsAsciiHexDigit(text[i + 1]) && char.IsAsciiHexDigit(text[i + 2]))
            {
                plain.Append((char)Convert.ToInt32(text.Substring(i + 1, 2), 16));
                i += 2;
            }
            else
            {
                plain.Append(c);
            }
        }

        return plain.ToString();
    }

    // ===================== Reading the file =====================

    /// <summary>
    /// A header is usable when it names something to match on and something to show. The Logix
    /// header row is TYPE,SCOPE,NAME,DESCRIPTION,DATATYPE,SPECIFIER,ATTRIBUTES.
    /// </summary>
    private static bool HasUsableHeader(List<string> row) =>
        IndexOfAny(row, NameHeaders) >= 0 && IndexOfAny(row, DescriptionHeaders) >= 0;

    private static bool IsRemark(List<string> row) =>
        row.Count > 0 && row[0].Trim().Equals("remark", StringComparison.OrdinalIgnoreCase);

    private static int IndexOf(List<string> row, string header) =>
        row.FindIndex(c => c.Trim().Equals(header, StringComparison.OrdinalIgnoreCase));

    private static int IndexOfAny(List<string> row, string[] headers) =>
        row.FindIndex(c => headers.Contains(c.Trim(), StringComparer.OrdinalIgnoreCase));

    private static string Field(List<string> row, int column) =>
        column >= 0 && column < row.Count ? row[column] : "";

    /// <summary>
    /// A Logix description is often several lines. A table cell is one line, so the breaks become
    /// spaces and runs of whitespace collapse.
    /// </summary>
    private static string Tidy(string text)
    {
        var tidy = new StringBuilder(text.Length);
        bool space = false;

        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c)) { space = true; continue; }
            if (space && tidy.Length > 0) tidy.Append(' ');
            space = false;
            tidy.Append(c);
        }

        return tidy.ToString();
    }

    /// <summary>
    /// RFC 4180. Quoted fields may hold commas, doubled quotes and line breaks. Reads the first
    /// <paramref name="length"/> characters.
    ///
    /// With <paramref name="dollarEscapes"/>, a $ inside a quoted field takes the next character
    /// with it, because Studio 5000 writes a quote in a description as $" and that quote must not
    /// end the field. The pair is kept as written for <see cref="Unescape"/> to turn into text.
    /// </summary>
    internal static IEnumerable<List<string>> ReadRows(string text, int length, bool dollarEscapes)
    {
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;

        for (int i = 0; i < length; i++)
        {
            char c = text[i];

            if (quoted)
            {
                if (dollarEscapes && c == '$' && i + 1 < length)
                {
                    field.Append(c).Append(text[i + 1]);
                    i++;
                    continue;
                }

                if (c != '"') { field.Append(c); continue; }

                // A doubled quote inside a quoted field is one literal quote. Studio 5000 does not
                // write these, but a file saved back out of Excel does.
                if (i + 1 < length && text[i + 1] == '"') { field.Append('"'); i++; }
                else quoted = false;
                continue;
            }

            switch (c)
            {
                case '"':
                    quoted = true;
                    break;

                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    break;

                case '\r':
                    break;

                case '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    yield return row;
                    row = new List<string>();
                    break;

                default:
                    field.Append(c);
                    break;
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            yield return row;
        }
    }

    /// <summary>
    /// Studio 5000 writes the workstation's ANSI code page, not UTF-8, so a degree sign or a dash
    /// in a description arrives as one high byte. Valid UTF-8 is taken as UTF-8; anything else is
    /// read as Latin-1, which at least never throws away a byte.
    /// </summary>
    private static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (ArgumentException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }
}

/// <summary>What one import produced, for reporting back to the person who asked for it.</summary>
public sealed record TagCsvImport(TagDescriptionLookup Lookup, int RowsRead, int Descriptions, string Format);

/// <summary>
/// Descriptions by tag path, and the rules for finding one for an address the file does not name
/// outright.
///
/// Three attempts, in order. The exact path. The path with array indexes removed, so one comment
/// written against a member covers every element. Then the nearest ancestor, so an element inherits
/// its array's description and a member inherits its structure's, which is what Logix shows when
/// pass-through is enabled.
/// </summary>
public sealed class TagDescriptionLookup
{
    private readonly Dictionary<string, string> _byPath;

    public TagDescriptionLookup(Dictionary<string, string> byPath) => _byPath = byPath;

    public static TagDescriptionLookup Empty { get; } = new(new Dictionary<string, string>());

    public int Count => _byPath.Count;

    public bool TryFind(string address, out string description, out bool inherited)
    {
        if (TryDirect(address, out description, out bool viaStrip))
        {
            // Dropping indexes off "Stations[0].Id" reaches "Stations.Id", the same member written
            // without an element, so that is the member's own description. Dropping them off
            // "Counts[7]" reaches "Counts", the array itself, which is a parent. The difference is
            // whether the path ended in an element.
            inherited = viaStrip && address.EndsWith(']');
            return true;
        }

        string path = address;
        while (TryTrim(path, out string parent))
        {
            path = parent;
            if (TryDirect(path, out description, out _))
            {
                inherited = true;
                return true;
            }
        }

        inherited = false;
        return false;
    }

    private bool TryDirect(string path, out string description, out bool viaStrip)
    {
        viaStrip = false;
        if (_byPath.TryGetValue(path, out description!)) return true;

        string flat = StripIndexes(path);
        if (string.Equals(flat, path, StringComparison.Ordinal)) return false;

        viaStrip = _byPath.TryGetValue(flat, out description!);
        return viaStrip;
    }

    /// <summary>
    /// Drops the last thing on the path: a trailing element index, or a trailing member. False once
    /// only the tag itself is left, and never past the "Program:Name" prefix, which is a scope
    /// rather than a tag.
    /// </summary>
    internal static bool TryTrim(string path, out string parent)
    {
        parent = "";

        if (path.EndsWith(']'))
        {
            int open = path.LastIndexOf('[');
            if (open <= 0) return false;
            parent = path[..open];
            return true;
        }

        int dot = path.LastIndexOf('.');
        if (dot <= 0) return false;

        parent = path[..dot];

        // "Program:MainProgram" is where a program-scoped tag runs out of ancestors.
        return !parent.StartsWith("Program:", StringComparison.OrdinalIgnoreCase)
               || parent.Contains('.');
    }

    /// <summary>"Stations[0].Faults[3]" becomes "Stations.Faults".</summary>
    internal static string StripIndexes(string path)
    {
        if (!path.Contains('[')) return path;

        var stripped = new StringBuilder(path.Length);
        int depth = 0;

        foreach (char c in path)
        {
            if (c == '[') depth++;
            else if (c == ']') { if (depth > 0) depth--; }
            else if (depth == 0) stripped.Append(c);
        }

        return stripped.ToString();
    }
}
