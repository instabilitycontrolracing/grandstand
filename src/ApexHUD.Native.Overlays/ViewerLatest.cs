using System.Globalization;
using System.IO;
using System.Text.Json;
using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// #608 (2026-10-30, Angelo: "when starting out there won't be many people donating or even watching, so leave the most
/// recent donation or comment up until the next one shows"): the last viewer message that played on screen (a chat card,
/// a tier 3 / 2 / 1 look, or the older strip / member card), kept for the "latest" plate. A message counts once its look
/// has started; the plate shows it after that look is over, until the next one plays.
/// <para>control.json viewer.keepLatest (ON by default) and viewer.latestPaidOnly (OFF by default = paid + chat). Kept in
/// %LOCALAPPDATA%\Grandstand\viewer_latest.json so a restart mid-stream keeps it; a file older than
/// <see cref="ViewerRoll.StaleHours"/> hours is the last stream's and is ignored; the control app's "New stream" clears it.
/// Test events (control app samples) show but are never saved.</para>
/// </summary>
public static class ViewerLatest
{
    /// <summary>One remembered message: what is drawn, already cleaned by <see cref="ViewerFeed"/>.</summary>
    public sealed record Entry(ViewerKind Kind, string Name, string Amount, string Message, string Platform, int Gifts = 0, int Months = 0, bool Test = false)
    {
        public bool Paid => Kind is ViewerKind.SuperChat or ViewerKind.Tip or ViewerKind.Bits or ViewerKind.Member or ViewerKind.Sub;
    }

    public static volatile bool KeepOn = true;
    public static volatile bool PaidOnly;
    public static Action<string>? Log;

    static readonly object gate = new();
    static Entry? any, paid;
    static bool loaded;
    static int version;
    /// <summary>Bumped on every change (the plate re-lays out only then).</summary>
    public static int Version { get { lock (gate) { EnsureLoaded(); return version; } } }

    /// <summary>What the plate shows now (the paid-only switch picks which).</summary>
    public static Entry? Current { get { lock (gate) { EnsureLoaded(); return PaidOnly ? paid : any; } } }

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Grandstand", "viewer_latest.json");

    /// <summary>A look started playing this event (render thread).</summary>
    public static void Note(ViewerEvent e)
    {
        if (e.Kind is ViewerKind.Follow or ViewerKind.Join) return;
        var en = new Entry(e.Kind, e.Name, e.Amount, e.Message, e.Platform, e.Gifts, e.Months, e.Test);
        lock (gate)
        {
            EnsureLoaded();
            any = en;
            if (en.Paid) paid = en;
            version++;
        }
        if (!e.Test) SaveSoon();
    }

    /// <summary>A sample for the control app's test button (not saved).</summary>
    public static void Sample()
    {
        lock (gate)
        {
            EnsureLoaded();
            any = paid = new Entry(ViewerKind.SuperChat, "Lap Chart Larry", "$5.00", "Great overtake into the Esses! Keep it clean through the Bus Stop, sending good vibes from Ohio", "youtube", Test: true);
            version++;
        }
    }

    /// <summary>A new stream (control app "New stream"): nothing is kept.</summary>
    public static void Clear()
    {
        lock (gate) { loaded = true; any = paid = null; version++; }
        Task.Run(() => { try { File.Delete(FilePath); } catch { } });
    }

    sealed class FileModel
    {
        public string Saved { get; set; } = "";
        public Entry? Any { get; set; }
        public Entry? Paid { get; set; }
    }

    static int saving;
    static void SaveSoon()
    {
        if (Interlocked.Exchange(ref saving, 1) == 1) return;
        Task.Run(async () =>
        {
            await Task.Delay(400);
            Interlocked.Exchange(ref saving, 0);
            FileModel m;
            lock (gate) m = new FileModel { Saved = DateTime.Now.ToString("o", CultureInfo.InvariantCulture), Any = any is { Test: false } ? any : null, Paid = paid is { Test: false } ? paid : null };
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(m));
            }
            catch (Exception ex) { Log?.Invoke("latest message: could not save: " + ex.Message); }
        });
    }

    static void EnsureLoaded()
    {
        if (loaded) return;
        loaded = true;
        try
        {
            if (!File.Exists(FilePath)) return;
            var m = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(FilePath));
            if (m == null) return;
            DateTime.TryParse(m.Saved, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at);
            if (at == default || (DateTime.Now - at).TotalHours > ViewerRoll.StaleHours) return;   // the last stream's
            any = m.Any; paid = m.Paid;
            version++;
            Log?.Invoke($"latest message: kept from earlier in this stream ({any?.Name ?? paid?.Name})");
        }
        catch (Exception ex) { Log?.Invoke("latest message: could not read viewer_latest.json: " + ex.Message); }
    }
}

/// <summary>
/// #608: the "latest" plate - a compact plate in the top-right corner, level with the session bar's top edge, beside it
/// (the race-control bar owns the band under the session bar; the viewer looks own the bar and the band under it).
/// The eyebrow says what it was (LATEST SUPER CHAT / TIP / BITS / MEMBER / SUBSCRIBER / MESSAGE) with the platform's dot,
/// the amount at the right in gold when it was paid, the name, and the first ~60 characters of the message.
/// Shown only while the screen is clear exactly like the viewer cards (never on lap 1, in a battle, an onboard, a replay,
/// a story card, the finish, the splash or the outro) and while no viewer look is playing; it fades away for those and
/// back afterwards. A new message cross-fades.
/// <para>Cost: laid out once per message (fitted name, cut message, widths); per frame: kept paints and shaders in the
/// plate's own coordinates, cached text blobs; no layers.</para>
/// </summary>
public sealed class ViewerLatestPlate : IDisposable
{
    public const float W = 400f, H = 74f, Right = 1896f, Top = 18f;
    const int MaxChars = 60;

    ViewerLatest.Entry? shown;
    int shownVersion = -1;
    float k; double lastMs = -1, clearSince = -1;
    string eyebrow = "", name = "", amount = "", message = "";
    float eyebrowW, nameSize = 21f, msgSize = 13f;
    SKTypeface nameFace = Fonts.BarlowCondensed.Bold, msgFace = Fonts.Inter.Medium;
    SKColor dot;

    /// <summary>Drawn (or fading) right now.</summary>
    public bool Active => k > 0.002f;
    /// <summary>Mid fade (keeps the compositor presenting).</summary>
    public bool Fading => k > 0.002f && k < 0.998f;
    /// <summary>Where the plate is (1080 units) for the given session bar rect.</summary>
    public static SKRect RectFor(SKRect bar) { float l = Math.Max(Right - W, bar.Right + 16f); return new SKRect(l, Top, Right, Top + H); }
    public SKRect LastRect { get; private set; }

    readonly SKPaint eyeP = new() { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 10f };
    readonly SKPaint nameP = new() { IsAntialias = true, Typeface = Fonts.BarlowCondensed.Bold, TextSize = 21f };
    readonly SKPaint amtP = new() { IsAntialias = true, Typeface = Fonts.BarlowCondensed.Bold, TextSize = 21f, TextAlign = SKTextAlign.Right };
    readonly SKPaint msgP = new() { IsAntialias = true, Typeface = Fonts.Inter.Medium, TextSize = 13f };
    readonly SKPaint fill = new() { IsAntialias = true };
    readonly SKPaint shaded = new() { IsAntialias = false };
    readonly SKPaint measureP = new() { IsAntialias = true };
    readonly ShaderSlot glassSh = new(), lineSh = new(), hairSh = new(), bloomSh = new();

    static SKColor GoldHi => !Theme.Applies(ThemeSurface.Card) ? new SKColor(0xFF, 0xD8, 0x76) : Theme.Current.Accent;
    static SKColor GoldMid => !Theme.Applies(ThemeSurface.Card) ? new SKColor(0xC9, 0xA9, 0x61) : Theme.Current.Accent;
    static SKColor GoldLo => !Theme.Applies(ThemeSurface.Card) ? new SKColor(0xB8, 0x96, 0x4C) : Theme.Current.Accent;
    static readonly float[] StopsLine = { 0f, 0.55f, 1f }, StopsHair = { 0f, 0.2f, 0.8f, 1f };
    static readonly SKColor Ivory = new(252, 250, 245), Txt2 = new(0xD3, 0xD9, 0xE3);

    /// <summary>Advances the fade. <paramref name="wanted"/> = the gates say it may show; it waits 0.9 s of that before
    /// fading in (a look that just ended gets its exit first).</summary>
    public void Tick(bool wanted, double nowMs)
    {
        double dt = lastMs < 0 ? 16 : Math.Clamp(nowMs - lastMs, 0, 100);
        lastMs = nowMs;
        var cur = ViewerLatest.KeepOn ? ViewerLatest.Current : null;
        int ver = ViewerLatest.Version;
        if (!wanted || cur == null) clearSince = -1; else if (clearSince < 0) clearSince = nowMs;
        bool changed = ver != shownVersion && !ReferenceEquals(cur, shown);
        bool up = cur != null && wanted && nowMs - clearSince >= 900 && !(changed && shown != null);
        if (changed && (shown == null || k <= 0.002f)) { Layout(cur); shownVersion = ver; up = cur != null && wanted && nowMs - clearSince >= 900; }
        else if (ver != shownVersion && ReferenceEquals(cur, shown)) shownVersion = ver;
        float target = up ? 1f : 0f;
        k += (target > k ? 1f : -1f) * (float)(dt / (target > k ? 450.0 : 350.0));
        k = Math.Clamp(k, 0f, 1f);
    }

    void Layout(ViewerLatest.Entry? e)
    {
        shown = e;
        if (e == null) return;
        eyebrow = "LATEST " + e.Kind switch
        {
            ViewerKind.SuperChat => "SUPER CHAT", ViewerKind.Tip => "TIP", ViewerKind.Bits => "BITS",
            ViewerKind.Member => e.Gifts > 0 ? "GIFTED MEMBERSHIPS" : "MEMBER", ViewerKind.Sub => e.Gifts > 0 ? "GIFTED SUBS" : "SUBSCRIBER",
            _ => "MESSAGE",
        };
        eyebrowW = TextBlobs.TrackedWidth(eyebrow, eyeP, 2f);
        dot = e.Platform switch { "youtube" => new SKColor(0xFF, 0x1F, 0x3D), "twitch" => new SKColor(0x91, 0x46, 0xFF), _ => new SKColor(0x2E, 0xC4, 0x7A) };
        amount = e.Kind is ViewerKind.SuperChat or ViewerKind.Tip or ViewerKind.Bits ? ViewerTiers.Drawable(e.Amount, Fonts.BarlowCondensed.Bold).ToUpperInvariant()
               : e.Gifts > 0 ? "×" + e.Gifts.ToString(CultureInfo.InvariantCulture)
               : e.Months > 0 ? e.Months.ToString(CultureInfo.InvariantCulture) + " MO" : "";
        if (amount.Length > 14) amount = amount[..14];
        float amtW = amount.Length > 0 ? Measure(amount, Fonts.BarlowCondensed.Bold, 21f) + 14f : 0f;
        // the name (a face that has its letters), fitted beside the amount
        nameFace = ViewerTierRenderer.NameFace(e.Name, Fonts.BarlowCondensed.Bold, out name);
        if (name.Length == 0) name = "A viewer";
        float maxName = W - 32f - amtW;
        nameSize = 21f;
        float nw = Measure(name, nameFace, nameSize);
        if (nw > maxName) { nameSize = Math.Max(15f, 21f * maxName / nw); nw = Measure(name, nameFace, nameSize); }
        if (nw > maxName) name = Cut(name, nameFace, nameSize, maxName);
        // the message: its first ~60 characters, on one line
        msgFace = ViewerTierRenderer.NameFace(e.Message, Fonts.Inter.Medium, out string msg);
        msg = msg.Replace('\n', ' ').Trim();
        if (msg.Length > MaxChars)
        {
            int cut = msg.LastIndexOf(' ', MaxChars);
            msg = msg[..(cut > MaxChars / 2 ? cut : MaxChars)].TrimEnd(',', '.', ' ', ';', ':') + "…";
        }
        msgSize = 13f;
        float maxMsg = W - 32f;
        float mw = Measure(msg, msgFace, msgSize);
        if (mw > maxMsg) { msgSize = Math.Max(11.5f, 13f * maxMsg / mw); mw = Measure(msg, msgFace, msgSize); }
        if (mw > maxMsg) msg = Cut(msg.TrimEnd('…'), msgFace, msgSize, maxMsg);
        message = msg;
        nameP.Typeface = nameFace; nameP.TextSize = nameSize;
        msgP.Typeface = msgFace; msgP.TextSize = msgSize;
    }

    string Cut(string s, SKTypeface tf, float size, float max)
    {
        for (int n = s.Length - 1; n > 1; n--)
        {
            string t = s[..n].TrimEnd() + "…";
            if (Measure(t, tf, size) <= max) return t;
        }
        return s;
    }

    float Measure(string s, SKTypeface tf, float size) { measureP.Typeface = tf; measureP.TextSize = size; return measureP.MeasureText(s); }

    public void Draw(SKCanvas c, SKRect bar, double nowMs)
    {
        if (k <= 0.002f || shown == null) { LastRect = SKRect.Empty; return; }
        float e = k * k * (3f - 2f * k);
        var r = RectFor(bar);
        float w = r.Width;
        if (w < 260f) { LastRect = SKRect.Empty; return; }
        LastRect = r;
        byte A(float v) => (byte)Math.Clamp(255f * v * e, 0f, 255f);
        int save = c.Save();
        c.Translate(r.Left, r.Top - (1f - e) * 8f);
        // the session bar's deep glass, with a soft gold bloom from the top-left
        shaded.Shader = glassSh.Linear(new SKPoint(0, 0), new SKPoint(0, H), [new SKColor(14, 20, 34, 226), new SKColor(8, 12, 22, 236)], default);
        shaded.Color = new SKColor(0, 0, 0, A(1f));
        c.DrawRect(new SKRect(0, 0, w, H), shaded);
        shaded.Shader = bloomSh.Radial(new SKPoint(0, 0), 220f, [GoldMid.WithAlpha(34), GoldMid.WithAlpha(0)], default);
        c.DrawRect(new SKRect(0, 0, w, H), shaded);
        shaded.Shader = null;
        Theme.Skin(c, new SKRect(0, 0, w, H), ThemeSurface.Card, nowMs, e);   // #601 (nothing on Default)
        // a slim gold line along the top (the bar's progress line), and the faint bottom hairline
        shaded.Shader = lineSh.Linear(new SKPoint(0, 0), new SKPoint(w, 0), [GoldLo, GoldHi, GoldHi.WithAlpha(0)], StopsLine);
        c.DrawRect(new SKRect(0, 0, w, 2f), shaded);
        shaded.Shader = hairSh.Linear(new SKPoint(0, 0), new SKPoint(w, 0), [GoldMid.WithAlpha(0), GoldMid.WithAlpha(110), GoldMid.WithAlpha(110), GoldMid.WithAlpha(0)], StopsHair);
        c.DrawRect(new SKRect(0, H - 1f, w, H), shaded);
        shaded.Shader = null;
        // eyebrow with the platform dot
        fill.Color = dot.WithAlpha(A(1f));
        c.DrawCircle(19f, 18.5f, 3.5f, fill);
        eyeP.Color = GoldHi.WithAlpha(A(0.95f));
        TextBlobs.DrawTracked(c, eyebrow, 30f, 22f, 2f, eyeP);
        // amount (gold) at the right of the name row
        float ny = message.Length > 0 ? 46f : 54f;   // no message (a member / sub): the name sits in the middle of the room
        if (amount.Length > 0)
        {
            amtP.Color = GoldHi.WithAlpha(A(1f));
            TextBlobs.Draw(c, amount, w - 16f, ny, amtP);
        }
        nameP.Color = Ivory.WithAlpha(A(1f));
        TextBlobs.Draw(c, name, 16f, ny, nameP);
        if (message.Length > 0)
        {
            msgP.Color = Txt2.WithAlpha(A(0.95f));
            TextBlobs.Draw(c, message, 16f, 64f, msgP);
        }
        c.RestoreToCount(save);
    }

    public void Dispose()
    {
        eyeP.Dispose(); nameP.Dispose(); amtP.Dispose(); msgP.Dispose(); fill.Dispose(); shaded.Dispose(); measureP.Dispose();
        glassSh.Dispose(); lineSh.Dispose(); hairSh.Dispose(); bloomSh.Dispose();
    }
}
