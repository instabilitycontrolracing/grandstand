using System.IO.MemoryMappedFiles;
using System.Text;

namespace ApexHUD.Native.GpuHost;

/// <summary>
/// #777 (2026-11-05): CrewChief V4 -> Grandstand's radio card, for people who use CrewChief instead of our race engineer.
/// CrewChief publishes what it says in shared memory when "Enable Shared Memory" is ticked in its properties (then
/// restart CrewChief). Layout (CrewChiefV4/SharedMemory.cs, MIT): map "Local\CrewChiefV4"; header ver, numVars,
/// varHeaderOffset, bufOffset (4 x int32); 400-byte var headers (type, typeSize, offset, count, name = UTF-16 at +16, 64
/// bytes); data at bufOffset + offset (+ i * typeSize). Phrases: a ring of 10 - lastPhraseIndex, phraseFileTimes (int64),
/// phraseVoiceNames / phrasePhrases (UTF-16 strings), phrasesVoiceType (0 chief, 1 spotter, 2 you).
/// Read only, never written; off unless the control page's "CrewChief on the radio card" switch is on.
/// </summary>
internal static class CrewChiefFeed
{
    const string MapName = @"Local\CrewChiefV4";
    static Thread? thread;
    static volatile bool started;
    public static string State { get; private set; } = "off";

    sealed record Var(int Type, int TypeSize, int Offset, int Count);

    public static void Start()
    {
        if (started) return;
        started = true;
        thread = new Thread(Loop) { IsBackground = true, Name = "Grandstand-CrewChief", Priority = ThreadPriority.BelowNormal };
        thread.Start();
    }

    static void Loop()
    {
        long lastTime = 0; bool primed = false;
        while (true)
        {
            var s = ControlState.Current;
            if (!s.CrewChiefRadio) { State = "off"; primed = false; Thread.Sleep(1000); continue; }
            MemoryMappedFile? mmf = null;
            try { mmf = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.Read); }
            catch { State = "waiting for CrewChief (tick 'Enable Shared Memory' in its properties, then restart it)"; Thread.Sleep(3000); continue; }
            try
            {
                using (mmf)
                using (var v = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read))
                {
                    int numVars = v.ReadInt32(4), varHdr = v.ReadInt32(8), buf = v.ReadInt32(12);
                    if (numVars <= 0 || numVars > 200) { State = "CrewChief shared memory not ready"; Thread.Sleep(2000); continue; }
                    var vars = new Dictionary<string, Var>();
                    var nb = new byte[64];
                    for (int i = 0; i < numVars; i++)
                    {
                        int p = varHdr + i * 400;
                        v.ReadArray(p + 16, nb, 0, 64);
                        string name = Encoding.Unicode.GetString(nb).TrimEnd('\0');
                        int z = name.IndexOf('\0'); if (z >= 0) name = name[..z];
                        vars[name] = new Var(v.ReadInt32(p), v.ReadInt32(p + 4), v.ReadInt32(p + 8), v.ReadInt32(p + 12));
                    }
                    if (!vars.TryGetValue("lastPhraseIndex", out var vIdx) || !vars.TryGetValue("phraseFileTimes", out var vTime)
                        || !vars.TryGetValue("phrasePhrases", out var vText)) { State = "CrewChief shared memory has no phrases"; Thread.Sleep(3000); continue; }
                    vars.TryGetValue("phrasesVoiceType", out var vType);
                    State = "connected";
                    Program.Log("crewchief: connected to CrewChief's shared memory");
                    while (ControlState.Current.CrewChiefRadio)
                    {
                        int idx = v.ReadInt32(buf + vIdx.Offset);
                        if (idx >= 0 && idx < vTime.Count)
                        {
                            long t = v.ReadInt64(buf + vTime.Offset + idx * 8);
                            if (t != 0 && t != lastTime)
                            {
                                lastTime = t;
                                if (primed)   // never replay what was already there when we connected
                                {
                                    string text = ReadString(v, buf + vText.Offset + idx * vText.TypeSize, vText.TypeSize);
                                    int type = vType != null ? v.ReadInt32(buf + vType.Offset + idx * 4) : 0;
                                    bool spotter = type == 1;
                                    if (type != 2 && !string.IsNullOrWhiteSpace(text) && (!spotter || ControlState.Current.CrewChiefSpotter))
                                        ApexHUD.Native.Overlays.RadioFeed.Push(spotter ? "SPOTTER" : "ENGINEER", text);
                                }
                            }
                        }
                        primed = true;
                        Thread.Sleep(150);
                    }
                }
            }
            catch (Exception ex) { State = "CrewChief closed"; Program.Log("crewchief: " + ex.Message); primed = false; Thread.Sleep(3000); }
        }
    }

    static string ReadString(MemoryMappedViewAccessor v, long pos, int bytes)
    {
        bytes = Math.Clamp(bytes, 2, 8192);
        var b = new byte[bytes];
        v.ReadArray(pos, b, 0, bytes);
        string s = Encoding.Unicode.GetString(b);
        int z = s.IndexOf('\0');
        return (z >= 0 ? s[..z] : s).Trim();
    }
}
