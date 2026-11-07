using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ApexHUD.Native.Overlays;

public enum ViewerKind { SuperChat, Tip, Bits, Member, Sub, Follow, Comment, Join }

/// <summary>One viewer event, already cleaned and filtered. Every value on screen comes from the event itself.</summary>
/// <remarks>#576: <paramref name="Gifts"/> = gifted subs / memberships in this event (0 = their own); <paramref name="Usd"/> = the
/// amount in rough dollars (bits / 100, other currencies at a rough rate) used to pick the tier look.
/// #603 (2026-10-30): <paramref name="Currency"/> = the ISO code when known (an amount symbol the font can't draw is shown as
/// the code instead); <paramref name="Months"/> = a membership milestone / resub's months; <paramref name="At"/> = when it
/// arrived (Environment.TickCount64; chat comments that waited too long are dropped).</remarks>
public sealed record ViewerEvent(ViewerKind Kind, string Name, string Amount, string Message, string Platform, bool Big, bool Test,
    int Gifts = 0, double? Usd = null, string Currency = "", int Months = 0, long At = 0)
{
    /// <summary>#576: which tiered look this event gets (only used when the tier looks are on).</summary>
    public ViewerTier Tier => ViewerTiers.TierOf(this);
    public bool Paid => Kind is ViewerKind.SuperChat or ViewerKind.Tip or ViewerKind.Bits;
    /// <summary>Rides the strip under the session bar (paid messages, and chat comments when they're switched on).</summary>
    public bool Strip => Paid || Kind == ViewerKind.Comment;
}

/// <summary>#603 (2026-10-30): everything one POST /api/viewer can carry. Only <see cref="Type"/> is required. The extra
/// fields come from the Streamer.bot bridge (docs_viewer_setup.md): <see cref="Currency"/> (ISO code), <see cref="Gifts"/>
/// (gifted subs / memberships in this event), <see cref="Months"/> (milestone / resub), <see cref="Id"/> (the platform's
/// event id - the same id twice is shown once), <see cref="Recipient"/> (who received a gifted sub: remembered for the
/// thank-you roll, never shown), <see cref="PartOfGift"/> (one sub out of a gift bomb that was already announced as a whole).</summary>
public sealed record ViewerInput(string? Type, string? Name, string? Amount, string? Message, string? Platform,
    string? Currency = null, int? Gifts = null, int? Months = null, string? Id = null, string? Recipient = null, bool PartOfGift = false);

/// <summary>
/// 2026-10-29 (#494): viewer interactions from the stream tools on this PC (Streamer.bot / StreamElements call
/// POST /api/viewer). Grandstand never logs in to YouTube or Twitch - it only shows what those tools hand it.
/// <para>Safety: only paid messages (Super Chat, tips, bits) and members / subs go on screen; follows never do. Names and
/// messages are cleaned (no control characters, no links), messages are cut to <see cref="MaxMessage"/> characters and
/// run through a small built-in word filter plus the user's own list (Documents\iRacing\Grandstand\viewer_blocklist.txt):
/// a message with a blocked word is not shown at all (the name and amount still are); a blocked name becomes
/// "A viewer". Events wait in a queue (<see cref="ViewerCardsRenderer"/> plays them one at a time, never over the race
/// start, a battle or the finish).</para>
/// </summary>
public static class ViewerFeed
{
    public const int MaxMessage = 140, MaxName = 32, MaxAmount = 16, MaxQueue = 40;

    /// <summary>Viewer cards on screen (control app "Viewer cards"). Off = events are dropped (the track fund still counts).</summary>
    public static volatile bool Enabled = true;
    /// <summary>#575 (2026-10-29): ordinary chat comments on screen too (control app "Chat comments", OFF by default). Kept
    /// rare on purpose: at most one every <see cref="CommentGapMs"/>, and never more than two waiting - the rest are skipped.</summary>
    public static volatile bool CommentsOn;
    public const long CommentGapMs = 20000;
    static long lastCommentMs = long.MinValue / 2;
    /// <summary>Super Chat / tip amount (in dollars, euros or pounds) from which the tower-top banner plays too.</summary>
    public static double BigAmount = 20;
    public static int BigBits = 2000;
    public static Action<string>? Log;

    static readonly ConcurrentQueue<ViewerEvent> incoming = new();
    /// <summary>Taken by the renderer on the render thread.</summary>
    public static bool TryTake(out ViewerEvent? e) { var ok = incoming.TryDequeue(out var x); e = x; return ok; }
    public static int Waiting => incoming.Count;

    public static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "iRacing", "Grandstand");
    public static string BlocklistPath => Path.Combine(Folder, "viewer_blocklist.txt");

    public sealed record Result(bool Accepted, bool OnScreen, string Why);

    public static bool TryKind(string? type, out ViewerKind kind)
    {
        kind = ViewerKind.Follow;
        switch (TypeKey(type))
        {
            case "superchat": case "supersticker": kind = ViewerKind.SuperChat; return true;
            case "tip": case "donation": kind = ViewerKind.Tip; return true;
            case "bits": case "cheer": kind = ViewerKind.Bits; return true;
            // #603: YouTube memberships (new, milestone, gifted) are Member; Twitch subs (new, resub, gifted, gift bomb) are Sub
            // (the gifted YouTube memberships used to come out as Twitch "GIFTED SUBS")
            case "member": case "membership": case "sponsor": case "newsponsor": case "milestone": case "membermilestone":
            case "giftmember": case "giftmembership": case "giftmemberships": case "membershipgift": kind = ViewerKind.Member; return true;
            case "sub": case "subscription": case "resub": case "giftsub": case "giftsubs": case "gift": case "giftbomb": case "giftedsubs":
            case "subbomb": kind = ViewerKind.Sub; return true;
            // #576: roll-only (never on screen): someone in chat (Streamer.bot "Present Viewers" / "User Joined"), and #603 the
            // person who RECEIVED a gifted sub / membership (they're on the roll as a member; the gifter gets the card)
            case "join": case "joined": case "present": case "presentviewer": case "viewer":
            case "giftreceived": case "giftmembershipreceived": case "giftrecipient": kind = ViewerKind.Join; return true;
            case "follow": case "newsubscriber": kind = ViewerKind.Follow; return true;   // a YouTube channel subscribe is a follow
            case "comment": case "chat": case "chatmessage": case "message": kind = ViewerKind.Comment; return true;
            default: return false;
        }
    }

    static string TypeKey(string? type) => (type ?? "").Trim().ToLowerInvariant().Replace("_", "").Replace("-", "").Replace(" ", "");

    /// <summary>Takes one event from the control server (any thread). Cleans, filters, counts tips towards the track fund
    /// and queues it for the screen.</summary>
    public static Result Accept(string? type, string? name, string? amount, string? message, string? platform, bool test = false) =>
        Accept(new ViewerInput(type, name, amount, message, platform), test);

    /// <summary>Chat bots and their commands never go on screen or on the roll.</summary>
    static readonly HashSet<string> Bots = new(StringComparer.OrdinalIgnoreCase)
    {
        "nightbot", "streamelements", "streamlabs", "moobot", "fossabot", "streamer.bot", "streamerbot", "wizebot", "botrix",
        "sery_bot", "soundalerts", "kofistreambot", "commanderroot", "pokemoncommunitygame", "own3d", "blerp", "tangiabot",
    };

    // ---- #603 duplicates: the same event id, or the same thing from the same person moments apart, is shown once
    static readonly Dictionary<string, long> seen = new();
    static readonly object seenGate = new();
    const long IdWindowMs = 15 * 60_000, PaidWindowMs = 20_000, ChatWindowMs = 60_000;

    static bool Duplicate(string key, long now, long windowMs)
    {
        lock (seenGate)
        {
            if (seen.TryGetValue(key, out long until) && until > now) return true;
            if (seen.Count > 4000)
            {
                var old = new List<string>();
                foreach (var kv in seen) if (kv.Value <= now) old.Add(kv.Key);
                foreach (var k in old) seen.Remove(k);
                if (seen.Count > 4000) seen.Clear();
            }
            seen[key] = now + windowMs;
            return false;
        }
    }

    // ---- #603 gift bombs: Twitch sends the bomb ("5 gifted") AND one gift-sub event per recipient; YouTube sends the gift
    // AND one "gift membership received" per recipient. The singles from the same gifter right after are not shown again.
    static readonly Dictionary<string, (int Left, long Until)> bombs = new(StringComparer.OrdinalIgnoreCase);

    static bool SwallowedByBomb(string gifter, long now)
    {
        lock (seenGate)
        {
            if (!bombs.TryGetValue(gifter, out var b) || b.Until < now || b.Left <= 0) return false;
            bombs[gifter] = (b.Left - 1, b.Until);
            return true;
        }
    }

    static void RememberBomb(string gifter, int gifts, long now)
    {
        lock (seenGate)
        {
            if (bombs.Count > 200) bombs.Clear();
            bombs[gifter] = (gifts, now + 120_000);
        }
    }

    /// <summary>Resets the duplicate memory and the chat rate limit (a new stream; the preview tools).</summary>
    public static void ForgetSeen() { lock (seenGate) { seen.Clear(); bombs.Clear(); } lastCommentMs = long.MinValue / 2; }

    /// <summary>Takes one event (any thread): cleans and filters it, drops duplicates, remembers the name for the thank-you
    /// roll, counts tips towards the track fund and queues it for the screen.</summary>
    public static Result Accept(ViewerInput input, bool test = false)
    {
        if (!TryKind(input.Type, out var kind)) return new(false, false, "unknown type (superchat, tip, bits, member, sub, giftsub, giftbomb, milestone, follow, comment or join)");
        string typeKey = TypeKey(input.Type);
        bool giftType = typeKey.StartsWith("gift", StringComparison.Ordinal) || typeKey is "membershipgift" or "subbomb";
        bool milestone = typeKey is "milestone" or "membermilestone" or "resub";
        long now = Environment.TickCount64;
        string plat = (input.Platform ?? "").Trim().ToLowerInvariant() switch
        {
            "youtube" or "yt" => "youtube",
            "twitch" => "twitch",
            "tip" or "streamelements" or "streamlabs" or "kofi" or "ko-fi" or "paypal" => "tip",
            _ => kind switch { ViewerKind.Bits or ViewerKind.Sub => "twitch", ViewerKind.SuperChat or ViewerKind.Member => "youtube", _ => "tip" },
        };
        string nm = Clean(input.Name, MaxName);
        bool anonymous = nm.Equals("AnAnonymousGifter", StringComparison.OrdinalIgnoreCase) || nm.Equals("Anonymous", StringComparison.OrdinalIgnoreCase);
        if (anonymous) nm = "";
        bool nameBlocked = nm.Length > 0 && Blocked(nm);
        bool bot = nm.Length > 0 && Bots.Contains(nm);
        if (nm.Length == 0 || nameBlocked) nm = anonymous ? "Anonymous gifter" : "A viewer";
        string amt = Clean(input.Amount, MaxAmount);
        string msg = StripShortcodes(Clean(input.Message, 1000));
        bool command = kind == ViewerKind.Comment && msg.StartsWith('!');
        bool msgBlocked = false;
        if (msg.Length > 0 && (Blocked(msg) || HasLink(msg))) { msg = ""; msgBlocked = true; }
        if (msg.Length > MaxMessage) msg = msg[..(MaxMessage - 1)].TrimEnd() + "…";

        // #603 duplicates (samples skip the look-alike check so a test can be pressed twice; never the id check)
        string id = Clean(input.Id, 120);
        if (id.Length > 0 && Duplicate("id:" + plat + ":" + id, now, IdWindowMs)) return new(true, false, "duplicate event (same id) - shown once");
        if (!test && kind != ViewerKind.Join && Duplicate(string.Join("|", kind, nm.ToLowerInvariant(), amt, msg.ToLowerInvariant(), input.Gifts ?? 0, input.Months ?? 0, (input.Recipient ?? "").ToLowerInvariant()),
                now, kind == ViewerKind.Comment ? ChatWindowMs : PaidWindowMs))
            return new(true, false, "duplicate event (same person, same thing, moments apart) - shown once");

        // #576 / #603: gifted subs / memberships - the count from "gifts", or riding in the amount ("5", "5 gifted")
        int gifts = 0;
        if (kind is ViewerKind.Member or ViewerKind.Sub)
        {
            if (input.Gifts is int g && g > 0) gifts = Math.Clamp(g, 1, 10000);
            else if ((giftType || amt.Contains("gift", StringComparison.OrdinalIgnoreCase)) && ParseMoney(amt) is { } gc) gifts = (int)Math.Clamp(gc.Value, 1, 10000);
            else if (giftType) gifts = 1;
        }
        int months = milestone || (input.Months is int mo0 && mo0 > 1) ? Math.Clamp(input.Months ?? 0, 0, 1200) : 0;
        string currency = ViewerTiers.CurrencyOf(amt, input.Currency);
        double? usd = kind is ViewerKind.SuperChat or ViewerKind.Tip or ViewerKind.Bits ? ViewerTiers.Usd(kind, amt, currency) : null;

        // the person who received a gifted sub / membership: on the roll as a member (never on screen)
        string recipient = Clean(input.Recipient, MaxName);
        if (!test && recipient.Length > 0 && !Blocked(recipient) && !Bots.Contains(recipient)) ViewerRoll.Record(ViewerKind.Member, recipient, "", null, 0);
        // one sub out of a gift bomb already announced as a whole: nothing more on screen (the gifter is already credited)
        if (kind is ViewerKind.Member or ViewerKind.Sub && giftType && ((gifts <= 1 && SwallowedByBomb(nm, now)) | input.PartOfGift))
            return new(true, false, "part of a gift already shown");
        if (gifts > 1 && !anonymous) RememberBomb(nm, gifts, now);

        // #576: the thank-you roll remembers the name BEFORE the switches, the message filter and the comment rate limit below
        // (a name that fails the word filter is left out; samples, bots and anonymous gifters never count)
        if (!test && !nameBlocked && !bot && !anonymous)
            ViewerRoll.Record(kind == ViewerKind.Join && typeKey.StartsWith("gift", StringComparison.Ordinal) ? ViewerKind.Member : kind, nm, amt, usd, gifts);
        if (kind == ViewerKind.Join) return new(true, false, "remembered for the thank-you roll (never shown on its own)");

        // the track fund counts real Super Chats and tips in dollars (test events and other currencies never count)
        if (!test && kind is ViewerKind.SuperChat or ViewerKind.Tip && ParseMoney(amt) is { Usd: true } m && m.Value > 0 && currency is "" or "USD")
            ViewerFund.Add(m.Value);

        if (kind == ViewerKind.Follow) { Log?.Invoke($"viewer: follow from {nm} (not shown)"); return new(true, false, "follows are not shown on screen"); }
        if (!Enabled) return new(true, false, "viewer cards are off in the control app");
        if (kind == ViewerKind.Comment)
        {
            if (bot) return new(true, false, "chat bot (not shown)");
            if (command) return new(true, false, "chat command (not shown)");
            if (!CommentsOn && !test) return new(true, false, "chat comments are off in the control app");
            if (msg.Length == 0) return new(true, false, msgBlocked ? "comment hidden by the word filter" : "empty comment (emotes only?)");
            int waitingComments = 0; foreach (var q in incoming) if (q.Kind == ViewerKind.Comment) waitingComments++;
            if (!test && (now - lastCommentMs < CommentGapMs || waitingComments >= 2)) return new(true, false, "comment skipped (one every 20 s on screen)");
            lastCommentMs = now;
            amt = "";
        }

        bool big = false;
        if (kind == ViewerKind.Bits) big = ParseMoney(amt) is { } b && b.Value >= BigBits;
        else if (kind is ViewerKind.SuperChat or ViewerKind.Tip) big = ParseMoney(amt) is { Comparable: true } p && p.Value >= BigAmount;
        if (kind == ViewerKind.Bits && amt.Length > 0 && amt.All(ch => char.IsDigit(ch) || ch == ',')) amt += " BITS";
        if (kind is ViewerKind.Member or ViewerKind.Sub) { amt = ""; if (!milestone) msg = ""; }   // members / subs: just the name (a milestone keeps its message)

        var e = new ViewerEvent(kind, nm, amt, msg, plat, big, test, gifts, usd, currency, months, now);
        incoming.Enqueue(e);
        while (incoming.Count > MaxQueue && incoming.TryDequeue(out var old)) Log?.Invoke($"viewer: queue full, dropped {old.Kind} from {old.Name}");
        Log?.Invoke($"viewer: {(test ? "TEST " : "")}{kind} from {nm} {amt}{(gifts > 0 ? $" ({gifts} gifted)" : "")}{(months > 0 ? $" ({months} months)" : "")}{(big ? " (big)" : "")}{(msgBlocked ? " - message hidden by the filter" : "")}{(nameBlocked ? " - name hidden by the filter" : "")}");
        return new(true, true, msgBlocked ? "queued (message hidden by the word filter)" : "queued");
    }

    /// <summary>The sample events behind the control app's Test buttons (POST /api/viewer/test).</summary>
    /// <summary>2026-11-06 (Angelo: every Live view button must work): when a Test button was pressed (TickCount64). For
    /// <see cref="TestBoostMs"/> after it the viewer cards don't wait for a clear screen (a battle, a story card...), so the
    /// sample you asked for plays now. Real viewer events still wait their turn.</summary>
    public static long TestAtTicks = long.MinValue / 2;
    public const long TestBoostMs = 30000;
    public static bool TestBoost => Environment.TickCount64 - Interlocked.Read(ref TestAtTicks) < TestBoostMs;

    public static Result Test(string? type)
    {
        Interlocked.Exchange(ref TestAtTicks, Environment.TickCount64);
        string t = (type ?? "superchat").Trim().ToLowerInvariant();
        return t switch
        {
            "superchat" => Accept("superchat", "Sample Viewer", "$5.00", "Sample message: this is how a Super Chat rides the ticker. Good luck in the race!", "youtube", true),
            "bigsuperchat" or "superchat_big" or "big" => Accept("superchat", "Sample Viewer", "$50.00", "Sample message: a big Super Chat also gets the banner on top of the timing tower.", "youtube", true),
            "tip" => Accept("tip", "Sample Viewer", "$10.00", "Sample tip message from StreamElements.", "tip", true),
            "bits" => Accept("bits", "SampleViewer", "500", "Sample cheer message.", "twitch", true),
            "member" => Accept("member", "Sample Viewer", "", "", "youtube", true),
            "sub" => Accept("sub", "SampleViewer", "", "", "twitch", true),
            "follow" => Accept("follow", "SampleViewer", "", "", "twitch", true),
            "comment" or "chat" => Accept("comment", "Sample Viewer", "", "Sample chat comment: what a save into Arnage!", "youtube", true),
            // #576: one sample per tier look (the amounts follow the control app's thresholds)
            "tier3" => Accept("superchat", "Sample Viewer", "$" + Math.Max(1, Math.Floor(ViewerTiers.Tier2From / 2)).ToString("0.00", CultureInfo.InvariantCulture),
                "Sample tier 3: a gold card under the session bar. Great pace tonight!", "youtube", true),
            "tier2" => Accept("tip", "Sample Viewer", "$" + Math.Max(ViewerTiers.Tier2From, Math.Round((ViewerTiers.Tier2From + ViewerTiers.Tier1From) / 2)).ToString("0.00", CultureInfo.InvariantCulture),
                "Sample tier 2: this one wraps the session bar. Send it into the Porsche curves!", "tip", true),
            "tier1" => Accept("superchat", "Sample Viewer", "$" + Math.Max(ViewerTiers.Tier1From, 1).ToString("0.00", CultureInfo.InvariantCulture),
                "Sample tier 1: the full-screen moment, then a thank-you on the tower. Bring it home!", "youtube", true),
            "giftsub" or "gift" => Accept(new ViewerInput("giftbomb", "SampleViewer", "", "", "twitch", Gifts: ViewerTiers.GiftTier2), true),
            "milestone" => Accept(new ViewerInput("milestone", "Sample Viewer", "", "Sample milestone message: a year of race nights!", "youtube", Months: 12), true),
            "sequence" or "streamtest" or "all" => StreamTest(),
            _ => new(false, false, "unknown test type"),
        };
    }

    /// <summary>#603 (2026-10-30): the control app's "Stream test" - one of each look through the real pipeline, in the order
    /// they'd escalate: a chat comment, a tier 3 Super Chat, a gifted-subs bomb (tier 2), a tier 2 tip and a tier 1 Super Chat.
    /// Samples: labelled SAMPLE names, never on the roll, never in the fund. They play one after another when the screen is
    /// clear (with the tier looks on; with them off the strip / member card play instead).</summary>
    public static Result StreamTest()
    {
        var parts = new[] { "comment", "tier3", "giftsub", "tier2", "tier1" };
        int queued = 0;
        foreach (var p in parts) if (Test(p).OnScreen) queued++;
        return new(true, queued > 0, $"stream test: {queued} of {parts.Length} samples queued (chat, tier 3, gifted subs, tier 2, tier 1)");
    }

    // ---------------------------------------------------------------- cleaning

    /// <summary>Single line, no control / zero-width characters, collapsed spaces, at most <paramref name="max"/> characters.</summary>
    public static string Clean(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var sb = new StringBuilder(Math.Min(s.Length, max + 8));
        bool space = false;
        foreach (char ch in s)
        {
            var cat = char.GetUnicodeCategory(ch);
            if (char.IsWhiteSpace(ch) || char.IsControl(ch)) { space = sb.Length > 0; continue; }
            if (cat is UnicodeCategory.Format or UnicodeCategory.OtherNotAssigned or UnicodeCategory.PrivateUse) continue;
            if (space) { sb.Append(' '); space = false; }
            sb.Append(ch);
            if (sb.Length >= max) break;
        }
        return sb.ToString().Trim();
    }

    static readonly Regex ShortcodeRx = new(@":(?=[^:\s]*[A-Za-z])[A-Za-z0-9_\-]{2,40}:", RegexOptions.Compiled);

    /// <summary>#603: YouTube custom emoji / emoji shortcodes (":face-blue-smiling:", ":chillwdog:") come through as text - they're
    /// taken out (spaces collapsed). A message of only emotes ends up empty.</summary>
    public static string StripShortcodes(string s)
    {
        if (s.Length == 0 || s.IndexOf(':') < 0) return s;
        string r = ShortcodeRx.Replace(s, " ");
        return Clean(r, r.Length + 1);
    }

    static readonly Regex LinkRx = new(@"(https?://|www\.|\b[a-z0-9-]+\.(com|net|org|gg|tv|io|ly|me|co|xyz|ru|info|link|shop)\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    public static bool HasLink(string s) => LinkRx.IsMatch(s);

    // a small built-in list (whole words; the longer ones also catch "-ing", "-er" ...). Add your own in viewer_blocklist.txt.
    static readonly string[] BuiltIn =
    {
        "fuck", "shit", "cunt", "bitch", "bastard", "asshole", "dick", "cock", "pussy", "whore", "slut", "wank", "twat",
        "fag", "faggot", "nigger", "nigga", "retard", "rape", "nazi", "kys", "kill yourself", "porn", "nude", "nudes",
    };
    static string[] userWords = Array.Empty<string>();
    static DateTime userStamp = DateTime.MinValue;
    static long userCheckedAt = long.MinValue;
    static readonly object listGate = new();

    static string[] UserWords()
    {
        long now = Environment.TickCount64;
        lock (listGate)
        {
            if (now - userCheckedAt < 5000) return userWords;
            userCheckedAt = now;
            try
            {
                var f = BlocklistPath;
                var stamp = File.Exists(f) ? File.GetLastWriteTimeUtc(f) : DateTime.MinValue;
                if (stamp != userStamp)
                {
                    userStamp = stamp;
                    userWords = stamp == DateTime.MinValue ? Array.Empty<string>()
                        : File.ReadAllLines(f).Select(l => Normalize(l.Split('#')[0])).Where(l => l.Length > 0).Distinct().Take(2000).ToArray();
                    Log?.Invoke($"viewer: word filter - {userWords.Length} of your own words from viewer_blocklist.txt");
                }
            }
            catch (Exception ex) { Log?.Invoke("viewer: could not read viewer_blocklist.txt: " + ex.Message); }
            return userWords;
        }
    }

    /// <summary>True when the text contains a built-in or user word (case, l33t spellings and s.p.a.c.i.n.g ignored).</summary>
    public static bool Blocked(string text)
    {
        string norm = Normalize(text);
        if (norm.Length == 0) return false;
        string squashed = norm.Replace(" ", "");
        var words = norm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var list in new[] { BuiltIn, UserWords() })
            foreach (var w in list)
            {
                if (w.Contains(' ')) { if ((" " + norm + " ").Contains(" " + w + " ")) return true; continue; }
                foreach (var word in words)
                    if (word == w || (w.Length >= 4 && word.StartsWith(w, StringComparison.Ordinal))) return true;
                if (w.Length >= 5 && squashed.Contains(w, StringComparison.Ordinal)) return true;   // "f u c k e r", "s.h.i.t"
            }
        return false;
    }

    static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char ch0 in s.ToLowerInvariant().Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch0) == UnicodeCategory.NonSpacingMark) continue;
            char ch = ch0 switch { '0' => 'o', '1' or '!' or '|' => 'i', '3' => 'e', '4' or '@' => 'a', '5' or '$' => 's', '7' => 't', _ => ch0 };
            if (char.IsLetter(ch)) sb.Append(ch);
            else if (char.IsWhiteSpace(ch) && sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
            // other punctuation is dropped: "s.h.i.t" -> "shit"
        }
        return sb.ToString().Trim();
    }

    /// <summary>The number in an amount ("$5.00", "€4,99", "500", "CA$10"). Usd: plain dollars (or no currency);
    /// Comparable: dollars, euros or pounds (close enough to judge a "big" message - nothing is converted or shown).</summary>
    public readonly record struct Money(double Value, bool Usd, bool Comparable);

    public static Money? ParseMoney(string? amount)
    {
        if (string.IsNullOrWhiteSpace(amount)) return null;
        var m = Regex.Match(amount, @"\d[\d.,]*");
        if (!m.Success) return null;
        string num = m.Value.TrimEnd('.', ',');
        // "1,234.56" / "1.234,56" / "4,99"
        int lastDot = num.LastIndexOf('.'), lastComma = num.LastIndexOf(',');
        if (lastComma > lastDot && num.Length - lastComma - 1 is 1 or 2) num = num.Replace(".", "").Replace(',', '.');
        else num = num.Replace(",", "");
        if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || v < 0 || v > 1e7) return null;
        string rest = (amount[..m.Index] + amount[(m.Index + m.Length)..]).Trim().ToUpperInvariant();
        bool usd = rest is "" or "$" or "USD" or "US$" or "$USD" or "USD$";
        bool comparable = usd || rest.Contains('€') || rest.Contains('£') || rest is "EUR" or "GBP";
        return new Money(v, usd, comparable);
    }
}

/// <summary>
/// 2026-10-29 (#494): the "Track fund" goal. The goal text and target come from the control app; the current amount is
/// the sum of real Super Chats and tips in dollars, kept in Documents\iRacing\Grandstand\fund.json (the control app can
/// set or reset it). Shown as a slim bar under the session bar only while switched on.
/// </summary>
public static class ViewerFund
{
    public static volatile bool Enabled;
    public static string Goal = "";
    public static double Target;
    static double current;
    static bool loaded;
    static readonly object gate = new();
    public static string FilePath => Path.Combine(ViewerFeed.Folder, "fund.json");

    public static double Current { get { lock (gate) { EnsureLoaded(); return current; } } }

    static void EnsureLoaded()
    {
        if (loaded) return;
        loaded = true;
        try
        {
            if (File.Exists(FilePath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
                if (doc.RootElement.TryGetProperty("current", out var c) && c.TryGetDouble(out double v) && v >= 0 && v < 1e9) current = v;
            }
        }
        catch (Exception ex) { ViewerFeed.Log?.Invoke("track fund: could not read fund.json: " + ex.Message); }
    }

    public static void Add(double amount)
    {
        if (amount <= 0 || amount > 1e6) return;
        lock (gate) { EnsureLoaded(); current = Math.Round(current + amount, 2); Save(); }
    }

    public static void Set(double value)
    {
        lock (gate) { loaded = true; current = Math.Round(Math.Clamp(value, 0, 1e9), 2); Save(); }
    }

    static void Save()
    {
        try
        {
            Directory.CreateDirectory(ViewerFeed.Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new { current, updated = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") }));
        }
        catch (Exception ex) { ViewerFeed.Log?.Invoke("track fund: could not save fund.json: " + ex.Message); }
    }

    /// <summary>"$12" / "$12.50".</summary>
    public static string Money(double v) => v % 1 == 0
        ? "$" + v.ToString("N0", CultureInfo.InvariantCulture)
        : "$" + v.ToString("N2", CultureInfo.InvariantCulture);
}
