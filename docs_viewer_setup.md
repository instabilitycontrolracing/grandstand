# Connecting Streamer.bot to Grandstand's viewer cards

Grandstand shows Super Chats, tips, bits, new members and new subs on the broadcast. It never logs in to YouTube or
Twitch itself. **Streamer.bot** (running on your PC) watches your channels and tells Grandstand about each event by
sending a small message to `http://127.0.0.1:8790/api/viewer`. You set this up once.

What viewers see depends on **Settings → Viewers & themes → Tier looks**.

**Tier looks ON** (the stream look, #576 / #603):

| What happened | Look | How long |
|---|---|---|
| Chat message (with **Chat comments** on) | A small chat card under the right half of the session bar | about 7 s |
| Super Chat / tip / bits **under $10** (the "Tier 2 from" amount), a new member or sub, a membership milestone or resub | **Tier 3**: a gold-edged card under the session bar with the amount on a gold chip | about 8.5 s |
| **$10 up to $49.99**, or 5 or more gifted subs / memberships at once | **Tier 2**: the session bar is wrapped for a moment - name and amount on the left, the message on the right | 8 s or more (long messages scroll) |
| **$50 and up** (the "Tier 1 from" amount) | **Tier 1**: a full-screen gold moment, then a THANK YOU band on top of the timing tower | 7.6 s, then the band for 20 s |
| After the race (with **Thank-you roll after the race** on) | The credits: everyone who chatted, joined, subscribed or supported, then the supporters by tier | about 10-20 s per page |

Bits count as 100 bits = $1. Other currencies are compared at a rough rate only to pick the look (a ¥1,000 Super Chat is a
few dollars, so it gets the tier 3 card). The amount on screen is always exactly what the viewer sent.

**Tier looks OFF** (the older look):

- **Super Chat, tip or bits:** a strip slides down under the session bar and the viewer's name, amount and message
  scroll through once. From $20 (or 2000 bits) a banner also plays on top of the timing tower. You can change both
  amounts in the control app.
- **New member (YouTube) or new sub (Twitch):** a card under the session bar with "NEW MEMBER · name".
- **Follows:** nothing on screen.

Cards wait their turn, one at a time. They never cover the race start (lap 1), a battle, an onboard, a replay, a story
card or the finish (from the white flag); one that gets interrupted early plays again later. Messages are cut to 140
characters, links and YouTube emote codes are removed, and a word filter runs on every message. A message with a
blocked word isn't shown at all. The name and amount still are. Chat bots (Nightbot, StreamElements ...), `!commands` and
your own messages never show. The same event sent twice (same id, or the same thing from the same person moments apart)
shows once, and a gift bomb shows once for the gifter (not once per gifted sub).

---

## 1. Check that Grandstand is ready

1. Start Grandstand and open the control app (`http://127.0.0.1:8790`).
2. Go to **Settings → Viewers & themes**. **Viewer cards** should be ON (it is by default).
3. Get in the car so the timing tower is showing (past lap 1 in a race). Then press **Test → Super Chat** (or, with
   **Tier looks** on, **Stream test**). A sample should show under the session bar. If nothing shows, it is probably lap
   1, a battle, an onboard or a replay. It will play when that ends.

## 2. Connect your channels in Streamer.bot

1. Install Streamer.bot from streamer.bot and open it.
2. **Platforms → YouTube:** log in with your YouTube account (the channel you stream on).
3. **Platforms → Twitch:** log in with your broadcaster account.
4. For tips, go to **Integrations → StreamElements**. Paste your StreamElements JWT token (on streamelements.com:
   your avatar → Account → Channels → Show secrets) and connect.
   (StreamElements runs on the internet and can't reach your PC directly, so its tips have to come through Streamer.bot.)

## 3. Make one action that sends events to Grandstand

1. Open the **Actions** tab. Right-click in the Actions list → **Add**, and name it `Grandstand viewer card`.
2. Select the new action. In the **Sub-Actions** box, right-click → **Core → C# → Execute C# Code**.
3. Delete everything in the code window and paste in the code below. (The same code is in the Grandstand folder as
   `tools\streamerbot\GrandstandViewerBridge.cs`.)
4. Click **Compile**. It should say "Compiled successfully". Then click **Save and Compile**.

If you set this up before 30 October 2026, paste the new code over the old one: it adds chat messages, gifted
memberships, milestones, gift bombs and the currency, and it fixes gifted subs (they used to show the person who
*received* the sub instead of the one who gave it).

```csharp
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
```

## 4. Tell the action which events to listen to

With the action still selected, right-click in the **Triggers** box and add each of these:

| Where in the Triggers menu | What it shows |
|---|---|
| **YouTube → Chat → Message** | Chat card (only when **Chat comments** is on); the name goes on the thank-you roll either way |
| **YouTube → Chat → Super Chat** | Tier 3 / 2 / 1 by amount |
| **YouTube → Chat → Super Sticker** | Same as a Super Chat |
| **YouTube → General → New Sponsor** (new member) | Tier 3 card: NEW MEMBER |
| **YouTube → Membership → Gift** | GIFTED MEMBERSHIPS (tier 2 from 5 at once, otherwise tier 3) |
| **YouTube → Membership → Gift Membership Received** | Nothing on screen; the person who received it goes on the roll |
| **YouTube → Membership → Member Milestone** | Tier 3 card with the months and their message |
| **YouTube → General → Present Viewers** (optional) | Nothing on screen; everyone in chat goes on the roll |
| **Twitch → Chat → Chat Message** | Chat card (only when **Chat comments** is on) |
| **Twitch → Chat → Cheer** | Tier 3 / 2 / 1 by bits (100 bits = $1) |
| **Twitch → Subscriptions → Subscription**, **Resubscription** | Tier 3 card: NEW SUBSCRIBER |
| **Twitch → Subscriptions → Gift Subscription**, **Gift Bomb** | GIFTED SUBS, once per gift (tier 2 from 5 at once) |
| **Integrations → StreamElements → Tip** | Tier 3 / 2 / 1 by amount |

You don't need to add follows or YouTube "New Subscriber" (a channel subscribe). They are never shown.

If one kind of event doesn't come through, give it its own copy of the action. Right-click the action →
**Duplicate**, keep only that trigger, and add a sub-action **Core → Arguments → Set Argument** *above* the C# code:
Variable `gsType`, Value one of `superchat`, `tip`, `bits`, `member`, `milestone`, `giftmember`, `sub`, `resub`,
`giftsub`, `giftbomb` or `comment`.

## 5. Test it from Streamer.bot

1. Right-click a trigger in the Triggers box → **Test Trigger** (on versions that have it), or send yourself a small
   real Super Chat or cheer.
2. Open the **Action History** / **Log** tab. You should see a line like `Grandstand: superchat from ... -> 200`.
   - `200`: Grandstand took it. It plays when the screen is clear.
   - `403`: the `X-Grandstand` line is missing from the code. Paste the code again.
   - "not reachable": Grandstand isn't running, or something else is using port 8790.

You can also test without Streamer.bot. Open **PowerShell** and paste:

```powershell
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:8790/api/viewer -Headers @{ "X-Grandstand" = "1" } -ContentType "application/json" -Body '{"type":"superchat","name":"Test Viewer","amount":"$5.00","message":"Hello from PowerShell","platform":"youtube"}'
```

The control app's **Test** buttons send samples without any of this. Samples never count towards the track fund.

## 6. Optional extras

- **Your own blocked words:** create `Documents\iRacing\Grandstand\viewer_blocklist.txt` with one word or phrase per
  line. Lines starting with `#` are notes. Grandstand picks up changes within a few seconds, with no restart needed.
- **Track fund bar:** in **Settings → Viewers & themes**, switch on **Track fund goal bar** and type a goal (for example
  "Le Mans entry") and a target in dollars. Real Super Chats and tips in dollars add to it. Other currencies don't count,
  because Grandstand never converts money. The running total is kept in `Documents\iRacing\Grandstand\fund.json`.
  **Reset to $0** starts it again.
- **Themes:** pick **Default** or **Halloween** in the same panel, or switch on **Theme by date** to get Halloween
  automatically from 24 to 31 October. A theme only changes the colours and texture of the tower, session bar and viewer
  cards. Nothing moves.

## A new stream, the thank-you roll and the stream test

- **Names this stream** (Settings → Viewers & themes) shows how many names the thank-you roll has. Names are kept in
  `%LOCALAPPDATA%\Grandstand\viewer_names.json`, so a restart mid-stream keeps them. Press **New stream** before you go
  live to start an empty list (it also starts fresh by itself when the list hasn't changed for 8 hours).
- **Thank-you roll after the race** plays the roll by itself after the post-race outro. **Play / Sample / Stop** play this
  stream's roll now, a sample with made-up names, or stop it.
- **Stream test** (Settings, or the Test cards screen) plays one of each look through the real queue: a chat card, a tier
  3 card, gifted subs, a tier 2 wrap and a tier 1 moment. Turn **Tier looks** on first. They wait for a clear screen like
  real events (on track, past lap 1, no battle, no onboard or replay). Samples never go on the roll or the fund.
- **Tier stingers**: tier 3 plays the light lower-third cue, tiers 2 and 1 the Grandstand string swell. Grandstand's sound
  goes to the TV / stream, never your headset. Chat cards are silent.

## What Grandstand accepts (for reference)

`POST http://127.0.0.1:8790/api/viewer` with the header `X-Grandstand: 1` and a JSON body:

```json
{ "type": "superchat", "name": "Viewer name", "amount": "$5.00", "message": "Their message", "platform": "youtube" }
```

- `type`: `superchat`, `tip`, `bits`, `member`, `milestone`, `giftmember`, `sub`, `resub`, `giftsub`, `giftbomb`,
  `comment`, `join` (roll only), `giftreceived` (roll only) or `follow`.
- `platform`: `youtube`, `twitch` or `tip`.
- `amount`: shown exactly as sent, so send it formatted (`"$5.00"`). For bits, send just the number (`"500"`) and it
  shows as "500 BITS".
- Optional: `currency` (ISO code like `"JPY"`, used to pick the tier), `gifts` (how many subs / memberships were gifted),
  `months` (milestone / resub), `id` (the platform's event id - the same id twice shows once), `recipient` (who received
  a gifted sub - goes on the roll), `partOfGift` (`true` for one sub out of a gift bomb that was already announced).

Other calls:

- `POST /api/viewer/test` with `{"type":"superchat"}` plays a sample (also `bigsuperchat`, `tip`, `bits`, `member`, `sub`,
  `tier3`, `tier2`, `tier1`, `giftsub`, `milestone`, and `sequence` for the whole stream test).
- `POST /api/viewer/thanks` with `{"action":"play"}` (or `sample`, `stop`) plays the thank-you roll.
- `POST /api/viewer/names/clear` starts a new list.
- `POST /api/viewer/fund` with `{"current": 0}` sets the fund total.
- `GET /api/viewer` shows the status (including how many names the roll has).
