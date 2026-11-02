using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace ApexHUD.Native.Common;

/// <summary>
/// 2026-10-27 — a small reader for iRacing's disk telemetry (.ibt), so Grandstand can look back at a session without
/// the Race Engineer app. The format: a 112-byte header (version, tick rate, session-info offset/length, variable
/// count, variable-header offset, row length, buffer offset), a 32-byte disk sub-header (start date, start/end time,
/// lap count, record count), the variable headers (144 bytes each: type, offset, count, name, description, unit), the
/// session info YAML, then one fixed-length row per tick. Only the channels asked for are kept; rows are streamed in
/// blocks, so a 250 MB file never sits in memory.
/// </summary>
public sealed class IbtFile
{
    public enum VarType { Char = 0, Bool = 1, Int = 2, BitField = 3, Float = 4, Double = 5 }

    public readonly record struct Var(string Name, VarType Type, int Offset, int Count, string Unit);

    public required string Path { get; init; }
    public int TickRate { get; private init; }
    public int RowLength { get; private init; }
    public long DataOffset { get; private init; }
    /// <summary>Rows in the file (the disk header's count, or worked out from the file length while that is 0).</summary>
    public int RecordCount { get; private init; }
    /// <summary>True when the disk header carried a record count (iRacing writes it when it closes the file).</summary>
    public bool Finished { get; private init; }
    public DateTime StartLocal { get; private init; }
    public double StartTime { get; private init; }
    public double EndTime { get; private init; }
    public int LapCount { get; private init; }
    public string SessionInfo { get; private init; } = "";
    public IReadOnlyDictionary<string, Var> Vars { get; private init; } = new Dictionary<string, Var>();

    /// <summary>Reads the headers and the session info. Throws on anything that isn't an .ibt.</summary>
    public static IbtFile Open(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        Span<byte> h = stackalloc byte[144];
        if (fs.Read(h) != 144) throw new InvalidDataException("too short");
        int ver = I32(h, 0), tick = I32(h, 8), siLen = I32(h, 16), siOff = I32(h, 20), nVars = I32(h, 24), vhOff = I32(h, 28),
            nBuf = I32(h, 32), bufLen = I32(h, 36);
        int bufOff = I32(h, 48 + 4);                         // varBuf[0] = { tickCount, bufOffset, pad, pad }
        if (ver < 1 || ver > 9 || tick <= 0 || tick > 1000 || nVars <= 0 || nVars > 4096 || bufLen <= 0 || bufLen > 1 << 20 || nBuf < 1
            || siOff <= 0 || siLen < 0 || bufOff <= 0)
            throw new InvalidDataException("not an iRacing telemetry file");
        long start = BinaryPrimitives.ReadInt64LittleEndian(h[112..]);
        double t0 = BitConverter.ToDouble(h[120..128]), t1 = BitConverter.ToDouble(h[128..136]);
        int laps = I32(h, 136), recs = I32(h, 140);
        long fileRows = Math.Max(0, (fs.Length - bufOff) / bufLen);
        bool finished = recs > 0;
        if (recs <= 0 || recs > fileRows) recs = (int)Math.Min(int.MaxValue, fileRows);

        var vars = new Dictionary<string, Var>(StringComparer.Ordinal);
        var vh = new byte[144 * nVars];
        fs.Position = vhOff;
        fs.ReadExactly(vh);
        for (int i = 0; i < nVars; i++)
        {
            var v = vh.AsSpan(i * 144, 144);
            string name = Str(v.Slice(16, 32));
            if (name.Length == 0) continue;
            vars[name] = new Var(name, (VarType)I32(v, 0), I32(v, 4), Math.Max(1, I32(v, 8)), Str(v.Slice(112, 32)));
        }
        var si = new byte[siLen];
        fs.Position = siOff;
        fs.ReadExactly(si);
        int end = Array.IndexOf(si, (byte)0); if (end < 0) end = si.Length;
        return new IbtFile
        {
            Path = path, TickRate = tick, RowLength = bufLen, DataOffset = bufOff, RecordCount = recs, Finished = finished,
            StartLocal = start > 0 ? DateTimeOffset.FromUnixTimeSeconds(start).LocalDateTime : DateTime.MinValue,
            StartTime = t0, EndTime = t1, LapCount = laps, SessionInfo = Encoding.Latin1.GetString(si, 0, end), Vars = vars,
        };
    }

    /// <summary>Every row of the named channels (first element of array channels), as doubles. Channels the file
    /// doesn't have are left out of the result.</summary>
    public Dictionary<string, double[]> Read(IEnumerable<string> names, CancellationToken ct = default)
    {
        var want = names.Where(Vars.ContainsKey).Distinct().Select(n => Vars[n]).ToArray();
        var outp = want.ToDictionary(v => v.Name, _ => new double[RecordCount], StringComparer.Ordinal);
        var cols = want.Select(v => outp[v.Name]).ToArray();
        const int Block = 2048;
        var buf = new byte[RowLength * Block];
        using var fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.SequentialScan);
        fs.Position = DataOffset;
        int row = 0;
        while (row < RecordCount)
        {
            ct.ThrowIfCancellationRequested();
            int n = Math.Min(Block, RecordCount - row);
            int got = 0, need = n * RowLength;
            while (got < need) { int r = fs.Read(buf, got, need - got); if (r <= 0) break; got += r; }
            n = got / RowLength;
            if (n == 0) break;
            for (int i = 0; i < n; i++)
            {
                var rs = buf.AsSpan(i * RowLength, RowLength);
                for (int k = 0; k < want.Length; k++) cols[k][row + i] = Value(rs, want[k]);
            }
            row += n;
            if (n < Block && got < need) break;
        }
        if (row < RecordCount)                       // file shorter than promised: trim
            foreach (var key in outp.Keys.ToArray()) outp[key] = outp[key][..row];
        return outp;
    }

    static double Value(ReadOnlySpan<byte> row, Var v)
    {
        var s = row[v.Offset..];
        return v.Type switch
        {
            VarType.Char => s[0],
            VarType.Bool => s[0] != 0 ? 1 : 0,
            VarType.Int or VarType.BitField => BinaryPrimitives.ReadInt32LittleEndian(s),
            VarType.Float => BinaryPrimitives.ReadSingleLittleEndian(s),
            VarType.Double => BinaryPrimitives.ReadDoubleLittleEndian(s),
            _ => double.NaN,
        };
    }

    static int I32(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadInt32LittleEndian(b[at..]);
    static string Str(ReadOnlySpan<byte> b)
    {
        int z = b.IndexOf((byte)0); if (z < 0) z = b.Length;
        return Encoding.Latin1.GetString(b[..z]).Trim();
    }

    // ------------------------------------------------------------------ session info (YAML) lookups
    /// <summary>A value from a top-level section ("WeekendInfo", "TrackName") - the first match, trimmed; "" if absent.</summary>
    public static string Yaml(string yaml, string section, string key)
    {
        int s = SectionStart(yaml, section);
        if (s < 0) return "";
        foreach (var line in Lines(yaml, s))
        {
            if (line.Length > 0 && line[0] != ' ') break;            // next top-level section
            string t = line.TrimStart();
            if (t.StartsWith(key + ":", StringComparison.Ordinal)) return Clean(t[(key.Length + 1)..]);
        }
        return "";
    }

    /// <summary>The items of a list inside a section (e.g. "SessionInfo" / "Sessions", "DriverInfo" / "Drivers") as
    /// key/value maps of their own scalar fields (nested lists inside an item are skipped).</summary>
    public static List<Dictionary<string, string>> YamlList(string yaml, string section, string list)
    {
        var items = new List<Dictionary<string, string>>();
        int s = SectionStart(yaml, section);
        if (s < 0) return items;
        int listIndent = -1, itemIndent = -1;
        Dictionary<string, string>? cur = null;
        foreach (var line in Lines(yaml, s))
        {
            if (line.Length > 0 && line[0] != ' ') break;
            int ind = line.Length - line.TrimStart().Length;
            string t = line.Trim();
            if (t.Length == 0) continue;
            if (listIndent < 0)
            {
                if (t == list + ":") listIndent = ind;
                continue;
            }
            if (ind <= listIndent && !t.StartsWith("- ", StringComparison.Ordinal)) break;   // list ended
            if (t.StartsWith("- ", StringComparison.Ordinal) && (itemIndent < 0 || ind == itemIndent))
            {
                if (ind < listIndent) break;
                itemIndent = ind;
                cur = new Dictionary<string, string>(StringComparer.Ordinal);
                items.Add(cur);
                t = t[2..];
                ind += 2;
            }
            if (cur == null) continue;
            if (ind != itemIndent + 2) continue;                       // a nested item's field
            int c = t.IndexOf(':');
            if (c <= 0) continue;
            cur[t[..c]] = Clean(t[(c + 1)..]);
        }
        return items;
    }

    static int SectionStart(string yaml, string section)
    {
        int at = 0;
        while (true)
        {
            int i = yaml.IndexOf(section + ":", at, StringComparison.Ordinal);
            if (i < 0) return -1;
            if (i == 0 || yaml[i - 1] == '\n') { int nl = yaml.IndexOf('\n', i); return nl < 0 ? -1 : nl + 1; }
            at = i + 1;
        }
    }

    static IEnumerable<string> Lines(string yaml, int from)
    {
        int i = from;
        while (i < yaml.Length)
        {
            int nl = yaml.IndexOf('\n', i);
            if (nl < 0) nl = yaml.Length;
            yield return yaml[i..nl].TrimEnd('\r');
            i = nl + 1;
        }
    }

    static string Clean(string v)
    {
        v = v.Trim();
        if (v.Length >= 2 && v[0] == '"' && v[^1] == '"') v = v[1..^1];
        return v;
    }
}
