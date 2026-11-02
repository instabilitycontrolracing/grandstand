// Grandstand viewer bridge for Streamer.bot (#494, #603 2026-10-30).
// Paste this whole file into one action's "Execute C# Code" sub-action (docs_viewer_setup.md, step 3) and add the triggers
// listed in step 4. It sends every YouTube / Twitch / StreamElements event to Grandstand's POST /api/viewer on this PC.
// Written for Streamer.bot's C# compiler (.NET Framework 4.8, C# 7.3): no newer language features.
//
// What it sends (all optional except type): type, name, amount, currency, message, platform, gifts, months, id, recipient,
// partOfGift. Grandstand picks the look (chat card / tier 3 / tier 2 / tier 1) from the type and the amount; it drops
// duplicates (same id) and gift-bomb singles, filters words and links, and remembers names for the thank-you roll.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;

public class CPHInline
{
    static readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    const string Url = "http://127.0.0.1:8790/api/viewer";

    string Arg(params string[] names)
    {
        foreach (var n in names)
        {
            object v;
            if (args.TryGetValue(n, out v) && v != null && v.ToString().Length > 0) return v.ToString();
        }
        return "";
    }

    bool Flag(params string[] names)
    {
        foreach (var n in names)
        {
            object v;
            if (args.TryGetValue(n, out v) && v != null)
            {
                if (v is bool) return (bool)v;
                if (string.Equals(v.ToString(), "true", StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }

    static string Q(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s ?? "")
        {
            if (c == '"' || c == '\\') sb.Append('\\').Append(c);
            else if (c < ' ') sb.Append(' ');
            else sb.Append(c);
        }
        return sb.Append('"').ToString();
    }

    // which kind of event, from the trigger's name (Streamer.bot's __source, e.g. "YouTubeSuperChat", "TwitchGiftBomb")
    static string TypeOf(string s)
    {
        if (s.Contains("giftbomb")) return "giftbomb";
        if (s.Contains("giftmembershipreceived")) return "giftreceived";
        if (s.Contains("membershipgift") || s.Contains("giftmembership")) return "giftmember";
        if (s.Contains("milestone")) return "milestone";
        if (s.Contains("superchat") || s.Contains("supersticker")) return "superchat";
        if (s.Contains("sponsor")) return "member";
        if (s.Contains("giftsub")) return "giftsub";
        if (s.Contains("resub")) return "resub";
        if (s.Contains("newsubscriber")) return "follow";            // a YouTube channel subscribe: like a follow, never shown
        if (s.Contains("twitchsub") || s.EndsWith("sub")) return "sub";
        if (s.Contains("cheer")) return "bits";
        if (s.Contains("tip")) return "tip";
        if (s.Contains("presentviewers")) return "present";
        if (s.Contains("firstwords")) return "skip";                 // the same message also comes through "Message"
        if (s.Contains("whisper")) return "skip";
        if (s.Contains("chatmessage") || s == "youtubemessage" || s.EndsWith("message")) return "comment";
        if (s.Contains("follow")) return "follow";
        return "";
    }

    public bool Execute()
    {
        string source = Arg("__source").ToLowerInvariant().Replace(" ", "");
        string type = Arg("gsType").ToLowerInvariant();
        if (type == "") type = TypeOf(source);
        if (type == "skip") return true;
        if (type == "") { CPH.LogWarn("Grandstand: unknown event " + source + " - add a Set Argument gsType"); return true; }

        bool yt = source.Contains("youtube"), tw = source.Contains("twitch");
        string platform = type == "tip" ? "tip" : yt ? "youtube" : tw ? "twitch"
            : (type == "superchat" || type == "member" || type == "milestone" || type == "giftmember") ? "youtube"
            : (type == "bits" || type == "sub" || type == "resub" || type == "giftsub" || type == "giftbomb") ? "twitch" : "tip";

        // Present Viewers: everyone in chat right now goes on the thank-you roll (never on screen)
        if (type == "present") { SendPresent(platform); return true; }

        // chat: never your own messages
        if (type == "comment" && (Flag("isOwner", "isBroadcaster") || (tw && Arg("user") != "" && Arg("user") == Arg("broadcastUser"))))
            return true;

        string name = type == "tip" ? Arg("tipUsername", "user", "userName") : Arg("user", "userName", "displayName");
        if (Flag("anonymous")) name = "Anonymous";
        string recipient = type == "giftsub" ? Arg("recipientUser", "recipientUserName") : "";
        if (type == "giftreceived") { name = Arg("recipientUser", "user", "userName"); }
        string message = type == "tip" ? Arg("tipMessage", "message") : tw ? Arg("messageStripped", "message") : Arg("message");

        string amount = "", currency = "", gifts = "", months = "";
        if (type == "superchat") { amount = Arg("amount"); currency = Arg("currencyCode"); }
        else if (type == "bits") amount = Arg("bits");
        else if (type == "tip")
        {
            currency = Arg("tipCurrency").ToUpperInvariant();
            string val = Arg("tipAmount");
            amount = currency == "" || currency == "USD" ? "$" + val : currency == "EUR" ? "€" + val : currency == "GBP" ? "£" + val : val + " " + currency;
        }
        else if (type == "giftbomb") gifts = Arg("gifts", "totalGifts");
        else if (type == "giftmember") gifts = Arg("count", "gifts");
        else if (type == "giftsub") gifts = "1";
        if (type == "milestone") months = Arg("months");
        else if (type == "resub") months = Arg("cumulative", "cumulativeMonths", "monthStreak");
        string id = Arg("messageId", "msgId", "tipId", "id", "eventId");
        bool partOfGift = type == "giftsub" && Flag("fromGiftBomb", "fromSubBomb");

        var json = new StringBuilder("{");
        json.Append("\"type\":").Append(Q(type)).Append(",\"name\":").Append(Q(name)).Append(",\"amount\":").Append(Q(amount))
            .Append(",\"currency\":").Append(Q(currency)).Append(",\"message\":").Append(Q(message)).Append(",\"platform\":").Append(Q(platform))
            .Append(",\"id\":").Append(Q(id)).Append(",\"recipient\":").Append(Q(recipient));
        if (gifts != "") json.Append(",\"gifts\":").Append(Q(gifts));
        if (months != "") json.Append(",\"months\":").Append(Q(months));
        if (partOfGift) json.Append(",\"partOfGift\":true");
        json.Append("}");
        Post(json.ToString(), type + " from " + name);
        return true;
    }

    void SendPresent(string platform)
    {
        object v;
        if (!args.TryGetValue("users", out v) || !(v is IEnumerable)) return;
        int n = 0;
        foreach (var u in (IEnumerable)v)
        {
            if (++n > 300) break;
            string who = "";
            var d = u as IDictionary<string, object>;
            if (d != null)
            {
                object x;
                if (d.TryGetValue("display", out x) && x != null) who = x.ToString();
                else if (d.TryGetValue("userName", out x) && x != null) who = x.ToString();
                else if (d.TryGetValue("user", out x) && x != null) who = x.ToString();
            }
            else if (u != null) who = u.ToString();
            if (who.Length == 0) continue;
            Post("{\"type\":\"join\",\"name\":" + Q(who) + ",\"platform\":" + Q(platform) + "}", null);
        }
    }

    void Post(string json, string what)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Post, Url);
            req.Headers.Add("X-Grandstand", "1");
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            var res = http.SendAsync(req).Result;
            if (what != null) CPH.LogInfo("Grandstand: " + what + " -> " + (int)res.StatusCode);
        }
        catch (Exception ex) { CPH.LogWarn("Grandstand not reachable: " + ex.Message); }
    }
}
