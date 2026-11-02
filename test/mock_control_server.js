// Mock of the Grandstand control server (src/ApexHUD.Native.GpuHost/ControlServer.cs) for testing
// the control page without Windows / iRacing. Same routes, same JSON shapes, in-memory state.
// Usage: node test/mock_control_server.js [port]   (default 8790)
"use strict";
const http = require("http");
const fs = require("fs");
const path = require("path");

let viewerNames = 27;
let camTested = false;   // #611
const viewerStatus = () => ({ cards: true, waiting: 0, fund: { current: 0 }, tierLooks: true, names: { count: viewerNames, supporters: Math.min(viewerNames, 6), started: "2026-10-30 19:02" }, rollPlaying: false });
const PORT = +(process.argv[2] || 8790);
const WEB = path.join(__dirname, "..", "src", "ApexHUD.Native.GpuHost", "control");
const FONTS = path.join(__dirname, "..", "assets", "fonts");
const CLOCK = path.join(__dirname, "..", "assets", "clock");
// a stand-in for Documents\iRacing\Grandstand\dials: test/fixtures/dials (pictures the test drops in)
const DIALS = path.join(__dirname, "fixtures", "dials");

const DEF_SETTINGS = { mode: "broadcast", onAir: true, elements: {}, storyCardSeconds: 15, storyGapMinutes: 3, performance: "auto", director: false, directorReplays: true, customCards: true, transitions: "auto" };
let settings = JSON.parse(JSON.stringify(DEF_SETTINGS));
const SLOTS = ["RaceSoFar", "MoveOfRace", "WeatherWatch", "FastestLaps", "Limits", "CarContact", "Cleanest", "YourRace", "PitStops", "Halfway", "DriverOfDay", "UnderPressure", "WeatherBar", "PreRacePanelAB", "PreRacePanelStorm", "CustomCards"];
const DEF_PRESENTS = { RaceSoFar: "AB", MoveOfRace: "AB", WeatherWatch: "Storm", FastestLaps: "GoFast", Limits: "GoFast", CarContact: "TradingPaints", Cleanest: "TradingPaints", YourRace: "none", PitStops: "none", Halfway: "none", DriverOfDay: "none", UnderPressure: "AB", WeatherBar: "Storm", PreRacePanelAB: "AB", PreRacePanelStorm: "Storm", CustomCards: "none" };
const defSponsors = () => ({
  _about: "Grandstand partners (mock)",
  partners: { AB: { on: true, logo: "AB_GC_Logo.png", word: "" }, Storm: { on: true, logo: "", word: "" }, GoFast: { on: true, logo: "gofast_logo.png", word: "GO FAST" }, TradingPaints: { on: true, logo: "tradingpaints_logo.png", word: "TRADING PAINTS" }, Custom: { on: false, logo: "", word: "YOUR TEAM" } },
  presents: { ...DEF_PRESENTS },
});
let sponsors = defSponsors();
const cards = new Map();   // id -> raw JSON text
const packs = new Map();
const docs = new Map();
const testLog = [];
const SAFE = /^[A-Za-z0-9_-]{1,40}$/;
const REEL_SESSIONS = [
  { file: "2026-10-28_Road-Atlanta_race2.json", label: "2026-10-28 · Road Atlanta · race 2", markers: 23, moments: 14, seconds: 140, written: "2026-10-28 21:10" },
  { file: "2026-10-27_Spa-Francorchamps_race2.json", label: "2026-10-27 · Spa Francorchamps · race 2", markers: 9, moments: 7, seconds: 68, written: "2026-10-27 20:02" },
];
let reelRun = null;
function reelState() {
  if (!reelRun) return { running: false, state: "Idle", why: "Pick a session and press Start while iRacing shows its replay.", file: "", moment: 0, total: 0, label: "", saved: "" };
  const i = Math.floor((Date.now() - reelRun.at) / 3000);
  if (reelRun.stopped) return { running: false, state: "Stopped", why: "Stopped by you.", file: reelRun.file, moment: Math.min(i + 1, reelRun.total), total: reelRun.total, label: "", saved: "" };
  if (i >= reelRun.total) return { running: false, state: "Done", why: `All ${reelRun.total} moments played. Saved: C:\\Videos\\reel.mp4`, file: reelRun.file, moment: reelRun.total, total: reelRun.total, label: "", saved: "C:\\Videos\\reel.mp4" };
  return { running: true, state: "Playing", why: `Moment ${i + 1} of ${reelRun.total}: L${i + 2} Up to P${5 - (i % 4)}`, file: reelRun.file, moment: i + 1, total: reelRun.total, label: "", saved: "" };
}

// ---- broadcast clock (ClockDial.cs) ----
const BUILTIN_DIALS = [["homage_black", "Black"], ["homage_green", "Green"], ["homage_ivory", "Ivory"], ["homage_navy", "Navy"], ["homage_panda", "Panda"]];
const DIAL_EXT = /\.(png|jpe?g|webp)$/i;
let clock = { dial: "auto", subdialHands: null };
const customDialName = (n) => (typeof n === "string" && n.length <= 120 && n === path.basename(n) && !n.includes("..") && !n.startsWith(".") && !/[\\/]/.test(n) && DIAL_EXT.test(n) && fs.existsSync(path.join(DIALS, n))) ? n : null;
const hasClassic = () => { try { return fs.statSync(path.join(CLOCK, "dial.png")).size > 2000; } catch { return false; } };   // the real check reads the pixel size (> 64 px)
function dialsState() {
  const custom = fs.existsSync(DIALS) ? fs.readdirSync(DIALS).filter((n) => customDialName(n)).sort() : [];
  const valid = (c) => c === "auto" || (c === "classic" && hasClassic()) || BUILTIN_DIALS.some(([id]) => id === c) || (c.startsWith("file:") && customDialName(c.slice(5)));
  if (!valid(clock.dial)) clock = { dial: "auto", subdialHands: clock.subdialHands };
  const active = clock.dial === "auto" ? (hasClassic() ? "classic" : "homage_ivory") : clock.dial;
  const showing = clock.subdialHands ?? !active.startsWith("file:");
  const builtIns = (hasClassic() ? [{ id: "classic", name: "Classic", thumb: "/api/dials/thumb/classic" }] : [])
    .concat(BUILTIN_DIALS.map(([id, name]) => ({ id, name, thumb: "/api/dials/thumb/builtin/" + id })));
  return { choice: clock.dial, active, subdialHands: clock.subdialHands, showingSubdialHands: showing, defaultDial: hasClassic() ? "classic" : "homage_ivory",
    folder: "C:\\Users\\Angelo\\Documents\\iRacing\\Grandstand\\dials", builtIns,
    custom: custom.map((n) => ({ id: "file:" + n, name: n, thumb: "/api/dials/thumb/file/" + encodeURIComponent(n) })) };
}

const ELEMENTS = ["tower", "splash", "featured", "ticker", "battle", "grid", "precards", "spotlight", "fastest", "pit", "radio", "outro", "story", "custom", "sessionbar"];
// class transitions (2026-10-24): a user with GT3 rollers in their folder, no personal footage
let rescans = 0;
function transState() {
  const mode = settings.transitions || "auto";
  const user = { gt3: 150 };
  const classes = ["gt3", "gtp", "lmp2", "gtd", "overall"].map((key) => {
    const has = !!user[key] && (mode === "auto" || mode === "folder");
    return { key, source: mode === "off" ? "off" : has ? "user" : "built-in", frames: has ? user[key] : 0, userFrames: user[key] || 0 };
  });
  return { mode, folder: "C:\\Users\\you\\Documents\\iRacing\\Grandstand\\transitions", personalInstalled: false, classes };
}
function state() {
  const t = Date.now() / 1000;
  const lean = settings.mode === "lean";
  const els = ELEMENTS.map((n) => {
    const e = settings.elements[n] || { enabled: true };
    const allowed = settings.onAir && (!lean || ["tower", "splash", "sessionbar"].includes(n)) && e.enabled !== false && (n !== "custom" || settings.customCards);
    const naturally = ["tower", "sessionbar", "featured"].includes(n) || (n === "battle" && Math.floor(t / 6) % 2 === 0);
    return { name: n, allowed, showing: allowed && naturally };
  });
  return { connected: true, live: true, demo: false, track: "Spa-Francorchamps", session: "RACE", phase: "Broadcast", cars: 38, classes: 2, story: "None", presents: Math.floor(t * 20), canvas: { w: 3840, h: 2160 }, elements: els };
}
function telemetry() {
  const t = Date.now() / 1000;
  const v = [
    ["Speed", "GPS vehicle speed", "m/s", 1, (62 + 8 * Math.sin(t)).toFixed(3)],
    ["RPM", "Engine rpm", "revs/min", 1, (7200 + 600 * Math.sin(t * 2)).toFixed(0)],
    ["FuelLevel", "Liters of fuel remaining", "l", 1, (48.2 - (t % 100) / 50).toFixed(3)],
    ["Lap", "Laps started count", "", 1, "14"],
    ["SessionTime", "Seconds since session start", "s", 1, (3120 + t % 1000).toFixed(3)],
    ["SessionFlags", "Session flags", "irsdk_Flags", 1, "268435456"],
    ["AirTemp", "Temperature of air at start/finish line", "C", 1, "21.4"],
    ["TrackTempCrew", "Temperature of track measured by crew around track", "C", 1, "31.8"],
    ["CamCarIdx", "Active camera's focus car index", "", 1, "12"],
    ["CamGroupNumber", "Active camera group number", "", 1, "11"],
    ["IsReplayPlaying", "0=replay not playing  1=replay playing", "", 1, "yes"],
    ["IsOnTrack", "1=Car on track physics running with player in car", "", 1, "no"],
    ["ReplaySessionTime", "Seconds since replay session start", "s", 1, (3120 + t % 1000).toFixed(3)],
    ["CarIdxPosition", "Cars position in race by car index", "", 64, "[0, 4, 1, 7, 3, 2, ...]"],
    ["CarIdxClassPosition", "Cars class position in race by car index", "", 64, "[0, 2, 1, 4, 1, 2, ...]"],
    ["CarIdxLapDistPct", "Percentage distance around lap by car index", "%", 64, "[0.112, 0.551, 0.553, 0.901, ...]"],
    ["CarIdxBestLapTime", "Cars best lap time", "s", 64, "[-1, 137.214, 137.009, 138.115, ...]"],
    ["LFtempCM", "LF tire middle surface temperature", "C", 1, "88.2"],
    ["dpWingFront", "Pitstop front flap adjustment", "", 1, "0"],
  ];
  return v.map(([name, desc, unit, count, value]) => ({ name, desc, unit, count, value })).sort((a, b) => a.name.localeCompare(b.name));
}
const SESSIONINFO = [
  ["WeekendInfo.TrackName", "spa up"], ["WeekendInfo.TrackDisplayName", "Circuit de Spa-Francorchamps"], ["WeekendInfo.TrackLength", "6.93 km"],
  ["WeekendInfo.SimMode", "replay"], ["WeekendInfo.WeekendOptions.NumStarters", "38"],
  ["SessionInfo.Sessions[0].SessionType", "Practice"], ["SessionInfo.Sessions[2].SessionType", "Race"], ["SessionInfo.Sessions[2].SessionLaps", "unlimited"],
  ["CameraInfo.Groups[0].GroupName", "Nose"], ["CameraInfo.Groups[9].GroupName", "TV1"], ["CameraInfo.Groups[10].GroupName", "TV2"], ["CameraInfo.Groups[11].GroupName", "TV3"], ["CameraInfo.Groups[14].GroupName", "Blimp"], ["CameraInfo.Groups[15].GroupName", "Chopper"],
  ["DriverInfo.DriverCarIdx", "12"], ["DriverInfo.Drivers[0].UserName", "Pace Car"], ["DriverInfo.Drivers[12].UserName", "Angel Morell"], ["DriverInfo.Drivers[12].CarNumber", "\"72\""],
].map(([name, value]) => ({ name, value }));

function validateCard(text) {
  const c = JSON.parse(text);
  if (!c || typeof c !== "object" || Array.isArray(c)) throw new Error("not a card (expected a JSON object)");
  return c;
}
function exportPack(name) {
  const s = { ...settings }; delete s.director;
  const cs = {}; for (const [k, v] of cards) cs[k] = JSON.parse(v);
  return { grandstandPack: 1, name, saved: new Date().toISOString().slice(0, 16).replace("T", " "), settings: s, sponsors, cards: cs };
}
function applyPack(text) {
  const p = JSON.parse(text);
  if (!p || p.grandstandPack == null) throw new Error("not a Grandstand pack (no grandstandPack field)");
  const problems = [];
  if (p.settings) settings = { ...DEF_SETTINGS, ...p.settings, director: settings.director };
  if (p.sponsors) sponsors = p.sponsors;
  if (p.cards) for (const [k, v] of Object.entries(p.cards)) { if (!SAFE.test(k)) { problems.push(`card '${k}': bad name`); continue; } cards.set(k, JSON.stringify(v, null, 2)); }
  return problems;
}

function send(res, status, type, body) { res.writeHead(status, { "Content-Type": type, "Cache-Control": "no-store" }); res.end(body); }
const json = (res, o, status = 200) => send(res, status, "application/json", JSON.stringify(o));
const text = (res, status, msg) => send(res, status, "text/plain", msg);

http.createServer((req, res) => {
  let body = "";
  req.on("data", (c) => { body += c; if (body.length > 2_000_000) req.destroy(); });
  req.on("end", () => {
    const u = new URL(req.url, "http://x");
    const p = u.pathname, m = req.method;
    try {
      if (m !== "GET" && req.headers["x-grandstand"] !== "1") return text(res, 403, "forbidden");
      if (p === "/api/state" && m === "GET") return json(res, state());
      if (p === "/api/settings" && m === "GET") return json(res, settings);
      if (p === "/api/settings" && m === "POST") {
        const s = JSON.parse(body);
        s.mode = ["lean", "broadcast", "studio"].includes(s.mode) ? s.mode : "broadcast";
        settings = { ...DEF_SETTINGS, ...s, elements: s.elements || {} };
        return json(res, settings);
      }
      // #611: the cam placer's status and test
      if (p === "/api/cams" && m === "GET") return json(res, { on: settings.cams?.keepClear !== false, linked: true, scene: "Racing", note: "", cams: [{ name: "Face cam (VDO.Ninja)", role: "face", enabled: true, raised: false, at: camTested ? "away" : "home", x: 2992, y: 1630, w: 800, h: 450 }, { name: "Pedal cam (S10 USB)", role: "pedal", enabled: false, raised: false, at: "home", x: 3280, y: 1310, w: 512, h: 288 }], collectMs: 0.0003, collectMaxMs: 0.002, calls: 12, moves: camTested ? 1 : 0, raises: 0, last: camTested ? "20:41:07 Face cam (VDO.Ninja): moved clear" : "" });
      if (p === "/api/cams/test" && m === "POST") { camTested = true; testLog.push("cams:test"); return json(res, { result: "covering Face cam (VDO.Ninja) in \"Racing\" for 6 s" }); }
      if (p === "/api/test" && m === "POST") { testLog.push(u.searchParams.get("card")); return json(res, { queued: u.searchParams.get("card") }); }
      if (p === "/api/telemetry" && m === "GET") return json(res, telemetry());
      if (p === "/api/sessioninfo" && m === "GET") return json(res, SESSIONINFO);
      // highlight reel (#9): a pretend run that walks through its moments, 3 s each
      if (p === "/api/themes" && m === "GET") return json(res, { builtIns: [{ id: "default", name: "Default", builtIn: true }, { id: "halloween", name: "Halloween", builtIn: true }, { id: "team", name: "Team colours", builtIn: true, editable: true }], current: settings.theme || "default", byDate: settings.themeByDate === true });   // #601
      if (p === "/api/theme/preview" && m === "POST") return json(res, { previewing: true });
      if (p === "/api/theme/preview/stop" && m === "POST") return json(res, { previewing: false });
      if (p === "/api/viewer" && m === "GET") return json(res, viewerStatus());   // #494
      // #603: tier samples / stream test, the thank-you roll and "New stream"
      if (p === "/api/viewer/test" && m === "POST") { const t = JSON.parse(body || "{}").type || ""; testLog.push("viewer:" + t); return json(res, { accepted: true, onScreen: true, why: "queued" }); }
      if (p === "/api/viewer/thanks" && m === "POST") { const a = JSON.parse(body || "{}").action || "play"; testLog.push("thanks:" + a); return json(res, viewerStatus()); }
      if (p === "/api/viewer/names/clear" && m === "POST") { viewerNames = 0; testLog.push("names:clear"); return json(res, viewerStatus()); }
      if (p === "/api/reel" && m === "GET") return json(res, { reel: reelState(), sessions: u.searchParams.get("sessions") === "1" ? REEL_SESSIONS : null });
      if (p === "/api/reel/start" && m === "POST") {
        let file = ""; try { file = JSON.parse(body || "{}").file || ""; } catch { }
        if (reelRun && reelState().running) return json(res, { started: false, msg: "The reel is already playing.", reel: reelState() });
        const s = REEL_SESSIONS.find((x) => x.file === file) || REEL_SESSIONS[0];
        reelRun = { file: s.file, total: s.moments, at: Date.now(), stopped: false };
        testLog.push("reel:" + s.file);
        return json(res, { started: true, msg: `${s.moments} moments`, reel: reelState() });
      }
      if (p === "/api/reel/stop" && m === "POST") { if (reelRun) reelRun.stopped = true; testLog.push("reel:stop"); return json(res, { stopped: true, reel: reelState() }); }
      if (p === "/api/director" && m === "GET") {
        if (!settings.director) return json(res, { enabled: false, status: "Off", why: "Switch the director on in the control page to let Grandstand pick the shots.", shot: "", car: -1, group: "", groups: ["Nose", "TV1", "TV2", "TV3", "Blimp", "Chopper"], replaying: false });
        const shots = [["battle", "TV2"], ["leader", "TV1"], ["pass", "TV1"], ["incident replay", "TV1"]];
        const [shot, group] = shots[Math.floor(Date.now() / 5000) % shots.length];
        return json(res, { enabled: true, status: "On", why: "Picking the shots.", shot, car: 12, group, groups: ["Nose", "TV1", "TV2", "TV3", "Blimp", "Chopper"], replaying: shot === "incident replay" });
      }
      if (p === "/api/sponsors" && m === "GET") return json(res, { pack: sponsors, slots: SLOTS, file: "C:\\Users\\Angelo\\Documents\\iRacing\\Grandstand\\sponsors.json" });
      if (p === "/api/sponsors" && (m === "PUT" || m === "POST")) { const s = JSON.parse(body); if (!s || typeof s !== "object") return text(res, 400, "bad sponsor pack"); sponsors = { ...defSponsors(), ...s }; return json(res, sponsors); }
      if (p === "/api/sponsors/reset" && m === "POST") { sponsors = defSponsors(); return json(res, sponsors); }
      if (p === "/api/cards" && m === "GET") {
        const list = [...cards.keys()].sort().map((id) => { try { return { id, card: validateCard(cards.get(id)), error: null }; } catch (e) { return { id, card: null, error: e.message }; } });
        return json(res, { folder: "C:\\Users\\Angelo\\Documents\\iRacing\\Grandstand\\cards", cards: list, fields: ["PlacesGained", "Position"], loaded: list.filter((c) => c.card).length });
      }
      if (p.startsWith("/api/cards/")) {
        let rest = decodeURIComponent(p.slice("/api/cards/".length));
        const play = rest.endsWith("/play"); if (play) rest = rest.slice(0, -5);
        if (!SAFE.test(rest)) return text(res, 400, "card names use letters, numbers, - and _ (up to 40)");
        if (play && m === "POST") { testLog.push("custom:" + rest); return json(res, { playing: rest }); }
        if (m === "PUT" || m === "POST") { try { validateCard(body); } catch (e) { return text(res, 400, "bad card: " + e.message); } cards.set(rest, body); return json(res, { saved: rest, problems: [] }); }
        if (m === "DELETE") { cards.delete(rest); return json(res, { deleted: rest, problems: [] }); }
        if (m === "GET" && cards.has(rest)) return send(res, 200, "application/json", cards.get(rest));
        return text(res, 404, "no such card");
      }
      if (p === "/api/packs" && m === "GET") return json(res, { folder: "packs", packs: [...packs.keys()].sort() });
      if (p === "/api/packs/current" && m === "GET") return json(res, exportPack("current"));
      if (p === "/api/packs/import" && m === "POST") { try { return json(res, { applied: true, problems: applyPack(body), settings }); } catch (e) { return text(res, 400, e.message); } }
      if (p.startsWith("/api/packs/")) {
        let rest = decodeURIComponent(p.slice("/api/packs/".length));
        const apply = rest.endsWith("/apply"); if (apply) rest = rest.slice(0, -6);
        if (!SAFE.test(rest)) return text(res, 400, "pack names use letters, numbers, - and _ (up to 40)");
        if (apply && m === "POST") { if (!packs.has(rest)) return text(res, 404, "no such pack"); return json(res, { applied: rest, problems: applyPack(packs.get(rest)), settings }); }
        if (m === "PUT" || m === "POST") { packs.set(rest, JSON.stringify(exportPack(rest), null, 2)); return json(res, { saved: rest }); }
        if (m === "DELETE") { packs.delete(rest); return json(res, { deleted: rest }); }
        if (m === "GET" && packs.has(rest)) return send(res, 200, "application/json", packs.get(rest));
        return text(res, 404, "no such pack");
      }
      if (p.startsWith("/api/doc/")) {
        const n = p.slice(9);
        if (!["eventpack", "cards", "transitions"].includes(n)) return text(res, 404, "unknown document");
        if (m === "GET") return send(res, 200, "application/json", docs.get(n) || "null");
        JSON.parse(body); docs.set(n, body); return json(res, { saved: n });
      }
      if (p === "/api/dials" && m === "GET") return json(res, dialsState());
      if (p === "/api/transitions" && m === "GET") return json(res, transState());
      if (p === "/api/transitions/rescan" && m === "POST") { rescans++; return json(res, transState()); }
      if (p === "/api/dial" && m === "POST") {
        const b = JSON.parse(body || "{}");
        const dial = typeof b.dial === "string" ? b.dial : "auto";
        if (dial.length > 140) return text(res, 400, "bad dial name");
        clock = { dial, subdialHands: typeof b.subdialHands === "boolean" ? b.subdialHands : null };
        return json(res, dialsState());
      }
      if (p.startsWith("/api/dials/thumb/") && m === "GET") {
        const rest = p.slice("/api/dials/thumb/".length);
        let file = null;
        if (rest === "classic" && hasClassic()) file = path.join(CLOCK, "dial.png");
        else if (rest.startsWith("builtin/") && BUILTIN_DIALS.some(([id]) => id === rest.slice(8))) file = path.join(CLOCK, rest.slice(8) + ".png");
        else if (rest.startsWith("file/")) { let n = null; try { n = customDialName(decodeURIComponent(rest.slice(5))); } catch { } if (n) file = path.join(DIALS, n); }
        if (!file || !fs.existsSync(file)) return text(res, 404, "no such dial");
        return send(res, 200, { ".png": "image/png", ".jpg": "image/jpeg", ".jpeg": "image/jpeg", ".webp": "image/webp" }[path.extname(file).toLowerCase()], fs.readFileSync(file));
      }
      if (p === "/__test/log") return json(res, { tests: testLog, settings, sponsors, clock, cards: [...cards.keys()], packs: [...packs.keys()], rescans });
      // static
      if (m !== "GET") return text(res, 405, "");
      let root = WEB, rel = p === "/" ? "index.html" : p.slice(1);
      if (p.startsWith("/assets/fonts/")) { root = FONTS; rel = p.slice("/assets/fonts/".length); }
      const full = path.resolve(root, rel);
      if (!full.startsWith(path.resolve(root)) || !fs.existsSync(full)) return text(res, 404, "not found");
      const type = { ".html": "text/html; charset=utf-8", ".css": "text/css; charset=utf-8", ".js": "text/javascript; charset=utf-8", ".json": "application/json", ".ttf": "font/ttf" }[path.extname(full)] || "application/octet-stream";
      return send(res, 200, type, fs.readFileSync(full));
    } catch (e) { return text(res, 500, e.message); }
  });
}).listen(PORT, "127.0.0.1", () => console.log("mock Grandstand control server on http://127.0.0.1:" + PORT));
