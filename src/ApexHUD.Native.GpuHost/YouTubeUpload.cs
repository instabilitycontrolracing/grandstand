using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ApexHUD.Native.GpuHost;

/// <summary>
/// 2026-11-06 (Angelo: "do all of those integrations"): the finished highlight reel goes up to YouTube by itself, with a
/// chapter per moment in the description, and the link is posted to Discord.
///
/// <para><b>Setup (once).</b> Google only lets an app upload with its own "OAuth client": the user makes a free Google
/// Cloud project, switches on "YouTube Data API v3", makes an OAuth client of type "Desktop app" and gives Grandstand
/// its JSON file (Integrations page). "Sign in with Google" then opens the browser; the user signs in there (Grandstand
/// never sees the password) and Google hands back a key that stays on this PC
/// (%LOCALAPPDATA%\Grandstand\youtube_token.json). Scope: youtube.upload only (upload, nothing else).</para>
///
/// <para><b>Private until audited.</b> YouTube keeps every video uploaded through an app it hasn't audited private,
/// whatever we ask for. The upload reports what YouTube actually set; the Discord post only goes out for a video people
/// can watch (unlisted / public).</para>
/// </summary>
internal static class YouTubeUpload
{
    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Grandstand");
    static readonly string ClientFile = Path.Combine(Dir, "youtube_client.json");
    static readonly string TokenFile = Path.Combine(Dir, "youtube_token.json");
    static readonly HttpClient http = new() { Timeout = Timeout.InfiniteTimeSpan };
    static Action<string> log = _ => { };

    public static string Status { get; private set; } = "";
    public static string LastVideo { get; private set; } = "";
    public static bool Busy => Volatile.Read(ref busy) == 1;
    static int busy;
    static string? accessToken; static DateTime accessUntil;

    public static void Init(Action<string> logger) => log = logger;
    public static bool HasClient => File.Exists(ClientFile);
    public static bool SignedIn => File.Exists(TokenFile);

    /// <summary>The Google "Desktop app" client JSON (from the Cloud console's Download JSON). Returns a problem or null.</summary>
    public static string? SaveClient(string json)
    {
        try
        {
            var n = JsonNode.Parse(json);
            var inst = n?["installed"] ?? n?["web"];
            if (inst?["client_id"] == null || inst["client_secret"] == null) return "That isn't a Google OAuth client file (it needs client_id and client_secret - pick \"Desktop app\").";
            Directory.CreateDirectory(Dir);
            File.WriteAllText(ClientFile, json);
            try { File.Delete(TokenFile); } catch { }
            accessToken = null; Status = "Client saved - now press Sign in with Google.";
            return null;
        }
        catch { return "That file isn't JSON."; }
    }

    public static void SignOut() { try { File.Delete(TokenFile); } catch { } accessToken = null; Status = "Signed out."; }

    static (string Id, string Secret) Client()
    {
        var n = JsonNode.Parse(File.ReadAllText(ClientFile));
        var inst = n?["installed"] ?? n?["web"];
        return (inst!["client_id"]!.ToString(), inst["client_secret"]!.ToString());
    }

    static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Opens Google's sign-in page in the normal browser and waits (up to 5 minutes) for it to come back.</summary>
    public static void BeginSignIn()
    {
        if (!HasClient) { Status = "Add the Google client file first."; return; }
        if (Interlocked.Exchange(ref busy, 1) == 1) { Status = "Busy - try again in a moment."; return; }
        _ = Task.Run(async () =>
        {
            HttpListener? listener = null;
            try
            {
                var (id, secret) = Client();
                int port;
                using (var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0)) { probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop(); }
                string redirect = $"http://127.0.0.1:{port}/";
                listener = new HttpListener(); listener.Prefixes.Add(redirect); listener.Start();
                string verifier = B64Url(RandomNumberGenerator.GetBytes(32));
                string challenge = B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
                string state = B64Url(RandomNumberGenerator.GetBytes(16));
                string url = "https://accounts.google.com/o/oauth2/v2/auth?response_type=code&access_type=offline&prompt=consent"
                    + "&client_id=" + Uri.EscapeDataString(id) + "&redirect_uri=" + Uri.EscapeDataString(redirect)
                    + "&scope=" + Uri.EscapeDataString("https://www.googleapis.com/auth/youtube.upload")
                    + "&code_challenge_method=S256&code_challenge=" + challenge + "&state=" + state;
                // explorer.exe opens the link in the user's own browser (not as administrator)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + url + "\"") { UseShellExecute = false });
                Status = "Finish signing in in your browser…";
                var getCtx = listener.GetContextAsync();
                if (await Task.WhenAny(getCtx, Task.Delay(TimeSpan.FromMinutes(5))) != getCtx) { Status = "Sign-in timed out - press Sign in again."; return; }
                var ctx = getCtx.Result;
                string? code = ctx.Request.QueryString["code"], back = ctx.Request.QueryString["state"], err = ctx.Request.QueryString["error"];
                byte[] page = Encoding.UTF8.GetBytes("<html><body style=\"font-family:sans-serif;background:#05070e;color:#ecf4f7;padding:40px\"><h2>Grandstand</h2><p>"
                    + (code != null && back == state ? "Signed in. You can close this tab." : "Sign-in didn't finish. Go back to Grandstand and try again.") + "</p></body></html>");
                ctx.Response.ContentType = "text/html; charset=utf-8"; ctx.Response.OutputStream.Write(page); ctx.Response.Close();
                if (err != null || code == null || back != state) { Status = "Sign-in cancelled" + (err != null ? " (" + err + ")" : "") + "."; return; }
                var form = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["code"] = code, ["client_id"] = id, ["client_secret"] = secret, ["redirect_uri"] = redirect,
                    ["grant_type"] = "authorization_code", ["code_verifier"] = verifier,
                });
                using var res = await http.PostAsync("https://oauth2.googleapis.com/token", form);
                var tok = JsonNode.Parse(await res.Content.ReadAsStringAsync());
                string? refresh = tok?["refresh_token"]?.ToString();
                if (!res.IsSuccessStatusCode || refresh == null) { Status = "Google didn't give a key back (" + (tok?["error_description"] ?? tok?["error"]) + ")."; return; }
                Directory.CreateDirectory(Dir);
                File.WriteAllText(TokenFile, new JsonObject { ["refresh_token"] = refresh }.ToJsonString());
                accessToken = tok?["access_token"]?.ToString(); accessUntil = DateTime.UtcNow.AddSeconds((tok?["expires_in"]?.GetValue<int>() ?? 3000) - 120);
                Status = "Signed in. Highlight videos will upload by themselves.";
                log("youtube: signed in");
            }
            catch (Exception ex) { Status = "Sign-in failed: " + ex.Message; log("youtube: sign-in failed " + ex.Message); }
            finally { try { listener?.Stop(); } catch { } Interlocked.Exchange(ref busy, 0); }
        });
    }

    static async Task<string> Access()
    {
        if (accessToken != null && DateTime.UtcNow < accessUntil) return accessToken;
        var (id, secret) = Client();
        string refresh = JsonNode.Parse(File.ReadAllText(TokenFile))?["refresh_token"]?.ToString() ?? throw new InvalidOperationException("Not signed in");
        var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["client_id"] = id, ["client_secret"] = secret, ["refresh_token"] = refresh, ["grant_type"] = "refresh_token" });
        using var res = await http.PostAsync("https://oauth2.googleapis.com/token", form);
        var tok = JsonNode.Parse(await res.Content.ReadAsStringAsync());
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException("Google sign-in has expired - press Sign in with Google again (" + tok?["error"] + ")");
        accessToken = tok!["access_token"]!.ToString();
        accessUntil = DateTime.UtcNow.AddSeconds((tok["expires_in"]?.GetValue<int>() ?? 3000) - 120);
        return accessToken;
    }

    /// <summary>Uploads one video (resumable upload, one request for the file). Returns the watch link and the privacy
    /// YouTube actually gave it.</summary>
    public static async Task<(string Url, string Privacy)> Upload(string path, string title, string description, string privacy)
    {
        // OBS may still be writing the end of the file: wait until its size stops changing
        long last = -1;
        for (int i = 0; i < 30; i++) { long len = new FileInfo(path).Length; if (len > 0 && len == last) break; last = len; await Task.Delay(2000); }
        string token = await Access();
        var meta = new JsonObject
        {
            ["snippet"] = new JsonObject { ["title"] = title.Length > 100 ? title[..100] : title, ["description"] = description.Length > 4900 ? description[..4900] : description, ["categoryId"] = "20", ["tags"] = new JsonArray("iRacing", "sim racing", "Grandstand") },
            ["status"] = new JsonObject { ["privacyStatus"] = privacy, ["selfDeclaredMadeForKids"] = false },
        };
        long size = new FileInfo(path).Length;
        string type = path.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) ? "video/x-matroska" : path.EndsWith(".mov", StringComparison.OrdinalIgnoreCase) ? "video/quicktime" : "video/mp4";
        using var start = new HttpRequestMessage(HttpMethod.Post, "https://www.googleapis.com/upload/youtube/v3/videos?uploadType=resumable&part=snippet,status")
        { Content = new StringContent(meta.ToJsonString(), Encoding.UTF8, "application/json") };
        start.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        start.Headers.Add("X-Upload-Content-Length", size.ToString());
        start.Headers.Add("X-Upload-Content-Type", type);
        using var sres = await http.SendAsync(start);
        if (!sres.IsSuccessStatusCode) throw new InvalidOperationException(YtError(await sres.Content.ReadAsStringAsync(), (int)sres.StatusCode));
        var loc = sres.Headers.Location ?? throw new InvalidOperationException("YouTube didn't give an upload address");
        await using var fs = File.OpenRead(path);
        using var put = new HttpRequestMessage(HttpMethod.Put, loc) { Content = new StreamContent(fs, 1 << 20) };
        put.Content.Headers.ContentType = new MediaTypeHeaderValue(type);
        put.Content.Headers.ContentLength = size;
        put.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var pres = await http.SendAsync(put);
        string body = await pres.Content.ReadAsStringAsync();
        if (!pres.IsSuccessStatusCode) throw new InvalidOperationException(YtError(body, (int)pres.StatusCode));
        var v = JsonNode.Parse(body);
        string vid = v?["id"]?.ToString() ?? "";
        return ("https://youtu.be/" + vid, v?["status"]?["privacyStatus"]?.ToString() ?? privacy);
    }

    static string YtError(string body, int code)
    {
        try { var m = JsonNode.Parse(body)?["error"]?["message"]?.ToString(); if (!string.IsNullOrEmpty(m)) return $"YouTube: {m}"; } catch { }
        return $"YouTube answered {code}";
    }

    /// <summary>Called when the highlight reel has finished recording (HighlightReelRunner).</summary>
    public static void ReelFinished(string videoPath, string title, string chapters)
    {
        var s = Integrations.Current;
        if (!s.YouTubeAuto || !HasClient || !SignedIn || !File.Exists(videoPath)) return;
        if (Interlocked.Exchange(ref busy, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                Status = "Uploading the highlights video…";
                string desc = title + "\n\n" + chapters + "\n\nBroadcast graphics: Grandstand (free) - https://github.com/instabilitycontrolracing/grandstand";
                var (url, privacy) = await Upload(videoPath, title, desc, s.YouTubePrivacy);
                LastVideo = url;
                Status = $"Uploaded {DateTime.Now:HH:mm}: {url} ({privacy})" + (privacy == "private" && s.YouTubePrivacy != "private" ? " - YouTube keeps videos from apps it hasn't audited private" : "");
                log("youtube: " + Status);
                if (s.DiscordOnHighlights && privacy != "private") await Integrations.PostHighlights(title, url);
            }
            catch (Exception ex) { Status = "Upload failed: " + ex.Message; log("youtube: " + Status); }
            finally { Interlocked.Exchange(ref busy, 0); }
        });
    }
}
