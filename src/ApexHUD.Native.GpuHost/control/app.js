/* Grandstand Control — talks to the overlay engine on this PC (ControlServer.cs).
   Plain JS, no libraries: it loads instantly on any screen. The Studio editors
   (Preview/Program, data browser, card builder) live in studio.js and are only
   loaded once Studio mode is switched on. */
(() => {
  "use strict";
  const $ = (s, r = document) => r.querySelector(s);
  const $$ = (s, r = document) => [...r.querySelectorAll(s)];
  const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

  // ---------------- the graphics Grandstand draws ----------------
  // rect = where it sits on a 1920x1080 screen (for the Preview / Program monitors)
  const ELEMENTS = [
    { id: "grid",          label: "Starting grid",            hint: "Every class, before the green", group: "before", rect: [410, 125, 1100, 830] },
    { id: "spotlight",     label: "Front-row hero cards",     hint: "Top 3 per class",               group: "before", rect: [410, 125, 1100, 830] },
    { id: "precards",      label: "Pre-race info cards",      hint: "Weather, pole, driver to watch", group: "before", rect: [560, 720, 800, 290] },
    { id: "tower",         label: "Timing tower",             hint: "Positions and gaps",            group: "during", rect: [16, 18, 400, 1046] },
    { id: "splash",        label: "Class transitions",        hint: "The wipe between classes",      group: "during", rect: [16, 18, 400, 1046] },
    { id: "sessionbar",    label: "Session bar and clock",    hint: "Track, time left, flag",        group: "during", rect: [472, 18, 976, 92] },
    { id: "featured",      label: "Featured driver card",     hint: "The car on camera",             group: "during", rect: [576, 853, 768, 211] },
    { id: "battle",        label: "Battle box",               hint: "When a fight is on",            group: "during", rect: [600, 762, 1000, 280] },
    { id: "ticker",        label: "Battle ticker",            hint: "Order strip during a fight",    group: "during", rect: [0, 110, 1920, 46] },
    { id: "story",         label: "Story cards",              hint: "In quiet moments",              group: "during", rect: [1010, 150, 880, 790] },
    { id: "custom",        label: "Your own cards",           hint: "From the card builder",         group: "during", rect: [1336, 150, 560, 252] },
    { id: "fastest",       label: "Fastest lap callout",      hint: "New best lap",                  group: "during", rect: [600, 580, 720, 130] },
    { id: "radio",         label: "Team radio captions",      hint: "Engineer and strategist",       group: "during", rect: [1392, 560, 504, 196] },
    { id: "viewer",        label: "Viewer cards",             hint: "Super Chats, tips, members",    group: "during", rect: [472, 110, 976, 120] },
    { id: "bets",          label: "Viewer bets",              hint: "Bet on the race (Integrations)", group: "during", rect: [1476, 104, 420, 128] },
    { id: "outro",         label: "Flag, podium and results", hint: "After the chequered flag",      group: "after",  rect: [0, 0, 1920, 1080] },
  ];
  const LEAN = new Set(["tower", "splash", "sessionbar"]);
  const PRESETS = {
    full: ELEMENTS.map((e) => e.id),
    racing: ["grid", "spotlight", "tower", "splash", "sessionbar", "battle", "ticker", "fastest", "radio", "outro"],
    minimal: ["tower", "splash", "sessionbar"],
  };
  const CARDS = [
    { id: "RaceSoFar",    label: "Race so far",     hint: "Position trace",     accent: "var(--gtp)" },
    { id: "YourRace",     label: "Your race",       hint: "Started vs now",     accent: "var(--gold)" },
    { id: "MoveOfRace",   label: "Move of the race", hint: "Places gained",     accent: "var(--gold)" },
    { id: "FastestLaps",  label: "Fastest laps",    hint: "Class bests",        accent: "var(--green)" },
    { id: "PitStops",     label: "Pit lane",        hint: "Quickest stops",     accent: "var(--amber)" },
    { id: "Halfway",      label: "Halfway",         hint: "Then and now",       accent: "var(--gtp)" },
    { id: "WeatherWatch", label: "Weather watch",   hint: "Track temperature",  accent: "var(--lmp2)" },
    { id: "CarContact",   label: "Car contact",     hint: "Incidents",          accent: "var(--lmp2)" },
    { id: "Limits",       label: "Off the track",   hint: "Track limits",       accent: "var(--green)" },
    { id: "Cleanest",     label: "Cleanest driver", hint: "Fewest incidents",   accent: "var(--green)" },
    { id: "DriverOfDay",  label: "Driver of the day", hint: "Most places gained", accent: "var(--gold)" },
  ];
  const STUDIO_SCREENS = ["studio", "timing", "data", "builder"];

  // ---------------- state ----------------
  let settings = null;          // what is on air (Program)
  let state = null;             // live engine status
  let director = null;          // replay director status
  let reel = null, reelSessions = null, reelPick = "";   // 2026-10-29 (#9): highlight reel status, the session picker, the chosen file
  let screen = "onair";
  let pollTimer = 0;
  const leaveHooks = [];        // run when leaving a screen (stops its polling)

  const api = {
    get: (p) => fetch(p, { cache: "no-store" }).then((r) => (r.ok ? r.json() : r.text().then((t) => Promise.reject(new Error(t))))),
    post: (p, body, method = "POST") => fetch(p, { method, headers: { "Content-Type": "application/json", "X-Grandstand": "1" }, body: body == null ? null : typeof body === "string" ? body : JSON.stringify(body) })
      .then((r) => (r.ok ? r.json() : r.text().then((t) => Promise.reject(new Error(t))))),
  };
  const clone = (o) => JSON.parse(JSON.stringify(o));
  const DEF_EL = { enabled: true, x: 0, y: 0, scale: 1, opacity: 1, image: "", imageMode: "glass", imageOpacity: 0.3 };
  const el = (id) => settings?.elements?.[id] ?? { ...DEF_EL };
  const moved = (e) => !!e && (e.x !== 0 || e.y !== 0 || e.scale !== 1);

  let saveT = 0, savePending = false;   // 2026-10-27: no settings sync while a save is on its way (it could undo the change)
  function save(msg) {
    clearTimeout(saveT);
    savePending = true;
    saveT = setTimeout(() => api.post("/api/settings", settings).then((s) => { settings = s; settings.elements = settings.elements || {}; if (msg) toast(msg); }).catch(() => toast("Couldn't reach Grandstand")).finally(() => { savePending = false; }), 120);
  }
  function toast(text) {
    const t = $("#toast"); t.textContent = text; t.classList.add("on");
    clearTimeout(t._h); t._h = setTimeout(() => t.classList.remove("on"), 2200);
  }
  function download(name, obj) {
    const a = document.createElement("a");
    a.href = URL.createObjectURL(new Blob([typeof obj === "string" ? obj : JSON.stringify(obj, null, 2)], { type: "application/json" }));
    a.download = name; document.body.appendChild(a); a.click(); setTimeout(() => { URL.revokeObjectURL(a.href); a.remove(); }, 500);
  }
  const readFile = (file) => new Promise((ok, bad) => { const r = new FileReader(); r.onload = () => ok(String(r.result)); r.onerror = bad; r.readAsText(file); });

  // ---------------- Studio editors load only in Studio mode ----------------
  let studioLoading = null;
  function loadStudio() {
    if (window.GS.studioReady) return Promise.resolve();
    if (studioLoading) return studioLoading;
    studioLoading = new Promise((ok, bad) => {
      const s = document.createElement("script");
      s.src = "studio.js"; s.onload = () => ok(); s.onerror = () => { studioLoading = null; bad(new Error("studio.js")); };
      document.head.appendChild(s);
    });
    return studioLoading;
  }

  // ---------------- top bar ----------------
  function paintBar() {
    if (!settings) return;
    $$("#modes button").forEach((b) => {
      const on = b.dataset.mode === settings.mode;
      b.setAttribute("aria-pressed", on);
      if (on) { const s = $("#modes .slider"); s.style.width = b.offsetWidth + "px"; s.style.transform = `translateX(${b.offsetLeft - 4}px)`; }
    });
    $("#onair").setAttribute("aria-pressed", settings.onAir);
    $("#studioGroup").classList.toggle("open", settings.mode === "studio");
  }
  function paintStatus() {
    const box = $("#status");
    if (!state) { box.className = "status"; $(".state", box).textContent = "Grandstand not running"; $(".where", box).textContent = ""; return; }
    const live = state.connected && state.live;
    box.className = "status " + (live ? "live" : state.demo || !state.connected ? "demo" : "");
    $(".state", box).textContent = live ? "iRacing live" : state.demo ? "Demo data" : "Waiting for iRacing";
    $(".where", box).textContent = live ? `${state.track || ""} · ${state.session || ""} · ${state.cars} cars` : "sample race shown";
  }
  function setMode(m) {
    if (!settings) return;
    settings.mode = m; paintBar(); save(m === "lean" ? "Lean mode: tower and session bar only" : m === "studio" ? "Studio open" : "Full broadcast");
    if (m === "studio") loadStudio().catch(() => toast("Couldn't load the Studio editors"));
    if (m !== "studio" && STUDIO_SCREENS.includes(screen)) go("onair");
    else render();
  }
  $("#modes").addEventListener("click", (e) => { const b = e.target.closest("button"); if (b) setMode(b.dataset.mode); });
  $("#onair").addEventListener("click", () => {
    if (!settings) return; settings.onAir = !settings.onAir; paintBar(); save(settings.onAir ? "On air" : "Off air: every graphic hidden"); render();
  });
  window.addEventListener("resize", paintBar);

  // ---------------- navigation with the broadcast wipe ----------------
  function go(name, push = true) {
    if (name === screen && $("#screen").childElementCount) return;
    while (leaveHooks.length) { try { leaveHooks.pop()(); } catch { } }
    screen = name;
    if (push) history.replaceState(null, "", "#" + name);
    $$("#rail a").forEach((a) => a.classList.toggle("on", a.dataset.screen === name));
    $("#screen").classList.remove("static"); $("#screen").dataset.sig = "";
    const w = $("#wipe"); w.classList.remove("go"); void w.offsetWidth; w.classList.add("go");
    const show = () => setTimeout(render, 230);     // swap the content while the panel covers it
    if (STUDIO_SCREENS.includes(name)) loadStudio().then(show, () => { toast("Couldn't load the Studio editors"); show(); });
    else show();
  }
  $("#rail").addEventListener("click", (e) => { const a = e.target.closest("a[data-screen]"); if (!a) return; e.preventDefault(); go(a.dataset.screen); });

  // ---------------- screens ----------------
  const SCREENS = {};
  const BIND = {};
  function render() {
    // a fresh node each time: listeners from the previous render never pile up
    const old = $("#screen"), host = old.cloneNode(false);
    old.replaceWith(host);
    const make = SCREENS[screen] || (STUDIO_SCREENS.includes(screen) ? () => head("Studio", "Loading…", "") : SCREENS.onair);
    host.innerHTML = make();
    $$("#screen > *").forEach((n, i) => n.style.setProperty("--i", i));
    $$(".panel", host).forEach((n, i) => n.style.setProperty("--i", i + 1));
    (BIND[screen] || (() => {}))(host);
  }
  const head = (kicker, title, lede, right = "") =>
    `<div class="head"><div><div class="kicker">${kicker}</div><h1>${title}</h1>${lede ? `<p class="lede">${lede}</p>` : ""}</div>${right}</div>`;
  const sw = (key, on, label) => `<label class="switch"><input type="checkbox" data-k="${key}" ${on ? "checked" : ""} aria-label="${esc(label)}"><span></span></label>`;
  const showing = (id) => !!state?.elements?.find((e) => e.name === id)?.showing;
  const leanOff = (id) => settings?.mode === "lean" && !LEAN.has(id);

  // ---- On Air ----
  function onairBig() {
    return ELEMENTS.map((e) => {
      const on = el(e.id).enabled, lean = leanOff(e.id);
      const st = lean ? "Off in Lean mode" : !settings.onAir ? "Off air" : on ? "On" : "Hidden";
      const x = el(e.id), extra = x.image ? `<span class="pic" title="Has its own picture"></span>` : "";
      return `<div class="bigwrap"><button type="button" class="big ${on && !lean ? "on" : ""} ${showing(e.id) ? "live" : ""}" data-big="${e.id}" aria-pressed="${on}" ${lean ? "disabled" : ""}>
        <span class="lt">LIVE</span><b>${esc(e.label)}</b><small>${esc(e.hint)}</small><span class="st">${extra}${st}</span></button>
        <button type="button" class="dots" data-dots="${e.id}" aria-label="${esc(e.label)} options" title="Options"><i></i><i></i><i></i></button></div>`;
    }).join("");
  }
  function directorPanel() {
    const d = director || {};
    const on = !!settings?.director;
    const cls = on ? (d.status === "On" ? "on" : d.status === "Paused" ? "paused" : "wait") : "";
    return `<div class="panel dir" style="--accent: var(--lmp2)" id="dirPanel">
      <div style="display:flex;align-items:center;gap:12px"><h2 style="margin:0;flex:1">Replay director</h2>${sw("director", on, "Replay director")}</div>
      <p class="muted" style="margin:8px 0 10px">Grandstand picks the camera like a TV director: wide shot at the start, battles, passes, incidents in slow motion, the leader to the flag. <b>Replays and spectating only</b> — it switches itself off when you get in the car.</p>
      <div class="dirstate ${cls}"><span class="dot"></span><b>${on ? esc(d.status || "Starting") : "Off"}</b><span>${esc(on ? d.why || "" : "Only one thing steers the camera: switch this on to let Grandstand do it.")}</span></div>
      ${on && d.shot ? `<div class="row"><span class="t">Current shot<small>${esc(d.shot)}${d.group ? " · " + esc(d.group) : ""}</small></span></div>` : ""}
      <div class="row"><span class="t">Broadcast mode<small>${settings?.directorField ? "Broadcast mode: covers the whole field like TV - battles in every class, passes, crashes, pit stops, missed-pass replays, every class winner at the flag" : "Streamer mode: stays on your car and only picks the camera (gearbox when someone's close behind, nose when you're closing on someone). Switch on for Broadcast mode"}</small></span>${sw("directorField", !!settings?.directorField, "Broadcast mode")}</div>
      <div class="row"><span class="t">Slow-motion replays of spins and crashes<small>Jumps back a few seconds, then returns</small></span>${sw("directorReplays", settings?.directorReplays !== false, "Slow-motion replays")}</div>
    </div>`;
  }
  // ---- highlight reel (2026-10-29, #9): plays a session's highlight markers in iRacing's replay while OBS records ----
  function reelStateHtml() {
    const r = reel || {};
    const cls = r.running ? "on" : r.state === "Stopped" ? "paused" : "";
    const head = esc(r.state || (r.running ? "Starting" : "Idle"));
    return `<div class="dirstate ${cls}"><span class="dot"></span><b>${head}</b><span>${esc(r.why || "Pick a session and press Start while iRacing shows its replay.")}</span></div>`;
  }
  function reelPanel() {
    const list = reelSessions || [];
    const opts = list.length ? list.map((x, i) => `<option value="${esc(x.file)}"${(reelPick ? reelPick === x.file : i === 0) ? " selected" : ""}>${esc(x.label)} · ${x.moments} moment${x.moments === 1 ? "" : "s"} · ~${Math.round(x.seconds)} s${i === 0 ? " (latest)" : ""}</option>`).join("")
      : `<option value="">${reelSessions ? "No highlight files yet" : "Loading…"}</option>`;
    const running = !!reel?.running;
    return `<div class="panel" style="--accent: var(--gold); grid-column: 1 / -1" id="reelPanel"><h2>Highlight reel</h2>
      <p class="muted" style="margin:-6px 0 10px">After a session: Grandstand plays its highlight markers back in iRacing's replay (6 s before each moment, 12 s for passes and battles, 8 s for the rest, TV cameras) while OBS records them into one video with a chapter per moment. <b>Replay only</b> — open the session's replay first; it never runs while you drive, and stops the moment you touch the camera or the replay. For a 4K video, watch the replay on the 4K screen and have OBS capture that screen.</p>
      <div style="display:flex;gap:10px;flex-wrap:wrap;align-items:flex-end;margin-bottom:10px"><label class="f" style="flex:1;min-width:260px">Session<select class="in" id="reelSession" ${running || !list.length ? "disabled" : ""}>${opts}</select></label>
        <button type="button" class="btn gold" id="reelStart" ${running || !list.length ? "disabled" : ""}>Start reel</button><button type="button" class="btn red" id="reelStop" ${running || reel?.armed ? "" : "disabled"}>Stop</button></div>
      <div class="row"><span class="t">Make the reel by itself after each race<small>When a race ends and you get out of the car (OBS open), the reel starts after a 15 s countdown and OBS records the highlights video. Stop skips it for that race. Never after practice or qualifying</small></span>${sw("autoReel", settings?.autoReel === true, "Highlight reel after the race")}</div>
      <div id="reelState">${reelStateHtml()}</div></div>`;
  }
  function paintReel() {
    const st = $("#reelState"); if (st) st.innerHTML = reelStateHtml();
    const running = !!reel?.running, has = !!reelSessions?.length;
    const a = $("#reelStart"), b = $("#reelStop"), sel = $("#reelSession");
    if (a) a.disabled = running || !has; if (b) b.disabled = !running && !reel?.armed; if (sel) sel.disabled = running || !has;
  }
  function loadReelSessions() {
    return api.get("/api/reel?sessions=1").then((r) => {
      reel = r.reel; reelSessions = r.sessions || [];
      if (reelPick && !reelSessions.some((x) => x.file === reelPick)) reelPick = "";
      const p = $("#reelPanel");
      if (p) { const n = document.createElement("div"); n.innerHTML = reelPanel(); const fresh = n.firstElementChild; fresh.style.animation = "none"; fresh.classList.add("nolay"); p.replaceWith(fresh); }
    }).catch(() => {});
  }
  function pollReel() { return api.get("/api/reel").then((r) => { const was = reel?.running; reel = r.reel; paintReel(); if (was && !reel?.running) loadReelSessions(); }).catch(() => {}); }
  SCREENS.onair = () => {
    const s = state || {};
    const res = s.canvas && s.canvas.w ? `${s.canvas.w} × ${s.canvas.h}` : "—";
    return head("On air", "Your broadcast", "Tap a graphic to show or hide it — it changes on the broadcast straight away. Minimise this window any time: the broadcast keeps going at full speed. Closing the Grandstand window quits Grandstand.",
      `<div class="seg" role="group" aria-label="Mode">${[["lean", "Lean"], ["broadcast", "Broadcast"], ["studio", "Studio"]].map(([k, l]) => `<button type="button" data-setmode="${k}" aria-pressed="${settings.mode === k}">${l}</button>`).join("")}</div>`) +
      `<div class="grid g4">
        <div class="panel stat" style="--accent: var(--green)"><span class="k">Source</span><span class="v">${state ? (s.connected && s.live ? "iRacing" : "Demo") : "—"}</span><span class="s">${esc(s.track || "Sample race")}</span></div>
        <div class="panel stat" style="--accent: var(--gtp)"><span class="k">Session</span><span class="v">${esc(s.phase || "—")}</span><span class="s">${s.cars || 0} cars · ${s.classes || 0} classes</span></div>
        <div class="panel stat" style="--accent: var(--lmp2)"><span class="k">Output</span><span class="v">${res}</span><span class="s">Scales itself to any screen, 720p to 8K</span></div>
        <div class="panel stat"><span class="k">Mode</span><span class="v">${esc((settings?.mode || "").toUpperCase())}</span><span class="s">${settings?.onAir ? "On air" : "Off air"} · story: ${s.story && s.story !== "None" ? esc(s.story.replace(/([a-z])([A-Z])/g, "$1 $2")) : "none"}</span></div>
      </div>
      <div class="kicker" style="margin:20px 0 10px">Graphics <span class="muted" style="letter-spacing:0;text-transform:none;font:400 13px var(--ui)">· green edge = on screen this second</span></div>
      <div class="bigs" id="bigs">${onairBig()}</div>
      <div class="grid g2" style="margin-top:16px">${directorPanel()}
        <div class="panel" style="--accent: var(--amber)"><h2>Quick styles</h2>
          <div class="seg" role="group" aria-label="Style"><button type="button" data-preset="full">Full broadcast</button><button type="button" data-preset="racing">Racing</button><button type="button" data-preset="minimal">Minimal</button></div>
          <p class="muted" style="margin:12px 0 0">Want it back how it was? <b>Settings → Presets &amp; packs</b> saves and restores whole setups, and every graphic has its own reset.</p>
          <div class="row"><span class="t">Open this page on another screen<small>Drag this window to any monitor, or press F11 for full screen</small></span></div></div>
        ${reelPanel()}
      </div>`;
  };
  function paintOnAirLive() {
    if (screen !== "onair") return;
    const bigs = $("#bigs");
    if (bigs) $$("[data-big]", bigs).forEach((b) => b.classList.toggle("live", showing(b.dataset.big)));
    const dp = $("#dirPanel");
    if (dp) { const n = document.createElement("div"); n.innerHTML = directorPanel(); const fresh = n.firstElementChild; fresh.style.animation = "none"; fresh.classList.add("nolay"); dp.replaceWith(fresh); }
  }
  function applyPreset(name) {
    const on = new Set(PRESETS[name]);
    ELEMENTS.forEach((x) => { settings.elements[x.id] = { ...el(x.id), enabled: on.has(x.id) }; });
  }
  BIND.onair = (host) => {
    loadReelSessions();
    host.addEventListener("change", (e) => {   // 2026-11-06: the reel by itself after a race
      if (e.target.dataset.k !== "autoReel") return;
      settings.autoReel = e.target.checked; save(e.target.checked ? "Highlight reel after each race: on" : "Highlight reel after each race: off");
    });
    host.addEventListener("click", (e) => {
      if (e.target.id === "reelStart") {
        const file = $("#reelSession")?.value || "";
        e.target.disabled = true;
        api.post("/api/reel/start", { file }).then((r) => { reel = r.reel; paintReel(); toast(r.started ? "Highlight reel started" : r.msg || "The reel couldn't start"); }).catch(() => { toast("Couldn't reach Grandstand"); paintReel(); });
        return;
      }
      if (e.target.id === "reelStop") { api.post("/api/reel/stop", null).then((r) => { reel = r.reel; paintReel(); toast("Highlight reel stopping"); }).catch(() => toast("Couldn't reach Grandstand")); return; }
      const dots = e.target.closest("[data-dots]"); if (dots) { openCardMenu(dots.dataset.dots, dots); return; }   // 2026-11-05
      const b = e.target.closest("[data-big]");
      if (b) {
        const id = b.dataset.big, on = !el(id).enabled;
        settings.elements[id] = { ...el(id), enabled: on };
        b.setAttribute("aria-pressed", on); b.classList.toggle("on", on); $(".st", b).textContent = !settings.onAir ? "Off air" : on ? "On" : "Hidden";
        save(`${ELEMENTS.find((x) => x.id === id)?.label}: ${on ? "on" : "hidden"}`);
        return;
      }
      const m = e.target.closest("[data-setmode]"); if (m) { setMode(m.dataset.setmode); return; }
      const p = e.target.closest("[data-preset]"); if (p) { applyPreset(p.dataset.preset); save(p.textContent + " style"); render(); }
    });
    host.addEventListener("change", (e) => {
      if (e.target.id === "reelSession") { reelPick = e.target.value; return; }
      const k = e.target.dataset.k;
      if (k === "director") { settings.director = e.target.checked; save(e.target.checked ? "Director on: Grandstand is steering the camera" : "Director off: the camera is yours"); setTimeout(pollDirector, 400); }
      if (k === "directorField") { settings.directorField = e.target.checked; save(e.target.checked ? "Director: whole field" : "Director: stays on your car"); render(); }
      if (k === "directorReplays") { settings.directorReplays = e.target.checked; save(e.target.checked ? "Slow-motion replays on" : "Slow-motion replays off"); }
    });
  };

  // ---------------- 2026-11-05: the "⋯" menu on every graphic + the card editor (Angelo) ----------------
  // Each graphic: on/off, position, size, see-through, its own picture (behind the glass or a corner logo), and
  // extras for some cards (the session bar's watch, the story cards' timing). Changes go on air as you make them.
  const labelOf = (id) => ELEMENTS.find((x) => x.id === id)?.label || id;
  function closeCardMenu() { $("#cardMenu")?.remove(); document.removeEventListener("pointerdown", menuAway, true); }
  function menuAway(e) { if (!e.target.closest("#cardMenu") && !e.target.closest("[data-dots]")) closeCardMenu(); }
  function openCardMenu(id, anchor) {
    const was = $("#cardMenu")?.dataset.id; closeCardMenu(); if (was === id) return;
    const x = el(id), r = anchor.getBoundingClientRect();
    const m = document.createElement("div");
    m.id = "cardMenu"; m.className = "cardmenu"; m.dataset.id = id; m.setAttribute("role", "menu");
    m.innerHTML = `<div class="cm-h">${esc(labelOf(id))}</div>
      <button type="button" role="menuitem" data-cm="edit"><span>Edit card</span><small>Position, size, picture, look</small></button>
      <button type="button" role="menuitem" data-cm="toggle"><span>${x.enabled ? "Hide on air" : "Show on air"}</span></button>
      ${x.image ? `<button type="button" role="menuitem" data-cm="nopic"><span>Remove its picture</span></button>` : ""}
      <button type="button" role="menuitem" data-cm="reset"><span>Reset to default</span><small>Place, size and look</small></button>`;
    document.body.appendChild(m);
    const w = m.offsetWidth;
    m.style.left = Math.max(8, Math.min(window.innerWidth - w - 8, r.right - w)) + "px";
    m.style.top = Math.min(window.innerHeight - m.offsetHeight - 8, r.bottom + 6) + "px";
    m.addEventListener("click", (e) => {
      const b = e.target.closest("[data-cm]"); if (!b) return;
      closeCardMenu();
      if (b.dataset.cm === "edit") openEditor(id);
      if (b.dataset.cm === "toggle") { settings.elements[id] = { ...el(id), enabled: !el(id).enabled }; save(`${labelOf(id)}: ${el(id).enabled ? "on" : "hidden"}`); render(); }
      if (b.dataset.cm === "nopic") removePicture(id);
      if (b.dataset.cm === "reset") { settings.elements[id] = { ...DEF_EL, enabled: el(id).enabled, image: el(id).image }; save(`${labelOf(id)}: back to default`); render(); }
    });
    setTimeout(() => document.addEventListener("pointerdown", menuAway, true), 0);
  }
  const pct = (v) => Math.round(v * 100) + "%";
  function edRow(label, hint, control) { return `<div class="row"><span class="t">${label}${hint ? `<small>${hint}</small>` : ""}</span>${control}</div>`; }
  function edRange(k, min, max, step, val, show) {
    return `<div class="edrange"><input type="range" min="${min}" max="${max}" step="${step}" value="${val}" data-ed="${k}"><output data-edv="${k}">${show}</output></div>`;
  }
  function editorBody(id) {
    const x = { ...DEF_EL, ...el(id) }, img = !!x.image;
    const extra =
      id === "sessionbar" ? `<div class="ed-sec">The watch</div>
        ${edRow("Show the watch", "Off: a small digital clock sits in the bar instead", sw("showWatch", settings.showWatch !== false, "Show the watch"))}
        ${edRow("Watch face", "Pick a face or upload your own picture", `<button type="button" class="btn sm" data-edgo="clock">Change face</button>`)}`
      : id === "story" ? `<div class="ed-sec">Timing</div>
        ${edRow("Time on screen", "", edRange("storyCardSeconds", 6, 40, 1, settings.storyCardSeconds ?? 15, (settings.storyCardSeconds ?? 15) + " s"))}
        ${edRow("Gap between cards", "", edRange("storyGapMinutes", 1, 15, 0.5, settings.storyGapMinutes ?? 3, (settings.storyGapMinutes ?? 3) + " min"))}`
      : "";
    return `<div class="ed-head"><div><div class="kicker">Card editor</div><h2>${esc(labelOf(id))}</h2></div><button type="button" class="ed-x" data-edclose aria-label="Close">×</button></div>
      <div class="ed-prev"><div class="ed-prevbox"><img id="edPrevImg" alt="Preview of ${esc(labelOf(id))}"><div class="ed-prevmsg" id="edPrevMsg">Drawing the preview…</div></div>
        <div class="ed-prevcap"><span id="edPrevSrc">Preview</span><button type="button" class="mini" data-edprev>Refresh</button></div></div>
      <div class="ed-body">
        <div class="ed-sec">On screen</div>
        ${edRow("Show on air", ELEMENTS.find((e) => e.id === id)?.hint || "", sw("ed:enabled", x.enabled, "Show on air"))}
        <div class="ed-sec">Place and size</div>
        ${edRow("Left / right", "", edRange("x", -960, 960, 2, x.x, Math.round(x.x) + " px"))}
        ${edRow("Up / down", "", edRange("y", -540, 540, 2, x.y, Math.round(x.y) + " px"))}
        ${edRow("Size", "", edRange("scale", 0.5, 1.6, 0.01, x.scale, pct(x.scale)))}
        ${edRow("Solid", "Lower = more see-through", edRange("opacity", 0.1, 1, 0.01, x.opacity ?? 1, pct(x.opacity ?? 1)))}
        <div class="ed-sec">Picture</div>
        <div class="ed-pic">
          <div class="ed-thumb">${img ? `<img src="/api/cardimage/${id}?v=${encodeURIComponent(x.image)}" alt="">` : `<span>No picture</span>`}</div>
          <div class="ed-picbtns"><label class="btn sm filebtn">${img ? "Replace picture…" : "Upload a picture…"}<input type="file" accept="image/png,image/jpeg,image/webp" data-edup="${id}"></label>
            ${img ? `<button type="button" class="btn sm" data-ednopic>Remove</button>` : ""}
            <small class="muted">PNG, JPG or WebP. Use pictures you have the rights to.</small></div>
        </div>
        ${img ? `${edRow("Style", "", `<div class="seg" role="group" aria-label="Picture style"><button type="button" data-edmode="glass" aria-pressed="${x.imageMode !== "badge"}">Behind the glass</button><button type="button" data-edmode="badge" aria-pressed="${x.imageMode === "badge"}">Corner logo</button></div>`)}
        ${edRow("Strength", x.imageMode === "badge" ? "How solid the logo is" : "Kept faint so the text stays easy to read", edRange("imageOpacity", 0, 1, 0.01, x.imageOpacity ?? 0.3, pct(x.imageOpacity ?? 0.3)))}` : ""}
        ${extra}
      </div>
      <div class="ed-foot"><button type="button" class="btn" data-edreset>Reset to default</button><button type="button" class="btn gold" data-edclose>Done</button></div>`;
  }
  function openEditor(id, opts = {}) {
    closeEditor(true);
    const fromLive = !!opts.fromLive;   // from the Live view: the big view IS the preview - no dimming, no second preview
    const bg = document.createElement("div"); bg.id = "edBg"; bg.className = "ed-bg" + (fromLive ? " clear" : "");
    const d = document.createElement("aside"); d.id = "editor"; d.className = "editor"; d.dataset.id = id; d.setAttribute("aria-label", "Card editor");
    d.innerHTML = editorBody(id);
    if (fromLive) { d.classList.add("fromlive"); document.body.classList.add("lv-editing"); }
    document.body.append(bg, d);
    requestAnimationFrame(() => { bg.classList.add("on"); d.classList.add("on"); });
    if (!fromLive) startPreview(id);
    $$(".lvbox").forEach((x) => x.classList.toggle("sel", x.dataset.lvbox === id));
    bg.addEventListener("click", () => closeEditor());
    d.addEventListener("input", (e) => {
      const k = e.target.dataset.ed; if (!k) return;
      const v = Number(e.target.value), out = $(`[data-edv="${k}"]`, d);
      if (k === "storyCardSeconds" || k === "storyGapMinutes") { settings[k] = v; if (out) out.textContent = v + (k === "storyCardSeconds" ? " s" : " min"); save(); return; }
      settings.elements[id] = { ...DEF_EL, ...el(id), [k]: v };
      if (out) out.textContent = k === "x" || k === "y" ? Math.round(v) + " px" : pct(v);
      save(); soonPreview();
    });
    d.addEventListener("change", (e) => {
      const t = e.target;
      if (t.dataset.k === "ed:enabled") { settings.elements[id] = { ...DEF_EL, ...el(id), enabled: t.checked }; save(`${labelOf(id)}: ${t.checked ? "on" : "hidden"}`); return; }
      if (t.dataset.k === "showWatch") { settings.showWatch = t.checked; save(t.checked ? "Watch on" : "Watch off: digital clock in the bar"); soonPreview(); return; }
      if (t.dataset.edup && t.files?.length) uploadPicture(id, t.files[0]);
      if (t.dataset.ed) toast("Saved");
    });
    d.addEventListener("click", (e) => {
      const t = e.target;
      if (t.closest("[data-edclose]")) { closeEditor(); return; }
      if (t.closest("[data-edprev]")) { loadPreview(); return; }
      if (t.closest("[data-ednopic]")) { removePicture(id); return; }
      const m = t.closest("[data-edmode]");
      if (m) { settings.elements[id] = { ...DEF_EL, ...el(id), imageMode: m.dataset.edmode, imageOpacity: m.dataset.edmode === "badge" ? 0.9 : 0.3 }; save(m.dataset.edmode === "badge" ? "Picture as a corner logo" : "Picture behind the glass"); refreshEditor(); return; }
      if (t.closest("[data-edreset]")) { settings.elements[id] = { ...DEF_EL, enabled: el(id).enabled, image: el(id).image, imageMode: el(id).imageMode, imageOpacity: el(id).imageOpacity }; save(`${labelOf(id)}: back to its default place, size and look`); refreshEditor(); return; }
      if (t.closest("[data-edgo]")) { closeEditor(); go("event"); setTimeout(() => $("#clockPanel")?.scrollIntoView({ behavior: "smooth", block: "start" }), 700); }
    });
    document.addEventListener("keydown", edKey);
  }
  function edKey(e) { if (e.key === "Escape") closeEditor(); }
  // ---- live preview: the engine draws the graphic with its real renderer and settings (GET /api/preview/{id}) ----
  let prev = { url: "", empty: false, live: false, busy: false, again: false, timer: 0, tick: 0, id: "" };
  function paintPreview() {
    const img = $("#edPrevImg"), msg = $("#edPrevMsg"), src = $("#edPrevSrc");
    if (!img) return;
    if (prev.url) { img.src = prev.url; img.classList.add("on"); }
    if (msg) {
      msg.textContent = !prev.url ? "Drawing the preview…" : prev.empty ? "Not on screen right now: this graphic only appears at certain moments (a battle, a new best lap, the start or the finish). Your changes still apply when it does." : "";
      msg.classList.toggle("on", !prev.url || prev.empty);
    }
    if (src) src.textContent = prev.url ? (prev.live ? "Live · from your race" : "Preview · Grandstand's sample race") : "Preview";
  }
  function loadPreview() {
    const id = prev.id; if (!id) return;
    if (prev.busy) { prev.again = true; return; }
    prev.busy = true;
    fetch(`/api/preview/${id}?t=${Date.now()}`, { cache: "no-store" })
      .then((r) => (r.ok ? r.blob().then((b) => ({ b, empty: r.headers.get("X-Preview-Empty") === "1", live: r.headers.get("X-Preview-Live") === "1" })) : Promise.reject(new Error(r.status))))
      .then(({ b, empty, live }) => {
        if (prev.id !== id) return;
        if (prev.url) URL.revokeObjectURL(prev.url);
        prev.url = URL.createObjectURL(b); prev.empty = empty; prev.live = live; paintPreview();
      })
      .catch(() => { const m = $("#edPrevMsg"); if (m && !prev.url) { m.textContent = "Preview not available right now"; m.classList.add("on"); } })
      .finally(() => { prev.busy = false; if (prev.again) { prev.again = false; setTimeout(loadPreview, 60); } });
  }
  function soonPreview(ms = 320) { clearTimeout(prev.timer); prev.timer = setTimeout(loadPreview, ms); }
  function startPreview(id) {
    stopPreview(); prev.id = id;
    loadPreview();
    prev.tick = setInterval(() => { if (!document.hidden) loadPreview(); }, 3000);   // live data and the clock keep moving
  }
  function stopPreview() { clearTimeout(prev.timer); clearInterval(prev.tick); if (prev.url) URL.revokeObjectURL(prev.url); prev = { url: "", empty: false, live: false, busy: false, again: false, timer: 0, tick: 0, id: "" }; }
  function refreshEditor() { const d = $("#editor"); if (d) { const sc = $(".ed-body", d)?.scrollTop || 0; d.innerHTML = editorBody(d.dataset.id); const b = $(".ed-body", d); if (b) b.scrollTop = sc; paintPreview(); soonPreview(250); } }
  function closeEditor(now) {
    const d = $("#editor"), bg = $("#edBg");
    document.removeEventListener("keydown", edKey);
    stopPreview();
    document.body.classList.remove("lv-editing"); $$(".lvbox.sel").forEach((x) => x.classList.remove("sel"));
    if (!d) return;
    if (now) { d.remove(); bg?.remove(); return; }
    d.classList.remove("on"); bg?.classList.remove("on");
    setTimeout(() => { d.remove(); bg?.remove(); if (screen === "onair" || screen === "show") render(); else if (screen === "live") { lv.sig = ""; $$("[data-k^=\"lv:\"]").forEach((x) => { x.checked = el(x.dataset.k.slice(3)).enabled; }); } }, 320);
  }
  function uploadPicture(id, file) {
    if (file.size > 25 * 1024 * 1024) { toast("That picture is too big (25 MB max)"); return; }
    toast("Uploading…");
    fetch(`/api/cardimage/${id}`, { method: "POST", headers: { "X-Grandstand": "1", "Content-Type": file.type || "application/octet-stream" }, body: file })
      .then((r) => (r.ok ? r.json() : r.text().then((t) => Promise.reject(new Error(t)))))
      .then((s) => { settings = s; settings.elements = settings.elements || {}; toast(`${labelOf(id)}: picture on air`); refreshEditor(); })
      .catch((er) => toast(er.message || "Upload failed"));
  }
  function removePicture(id) {
    fetch(`/api/cardimage/${id}`, { method: "DELETE", headers: { "X-Grandstand": "1" } })
      .then((r) => (r.ok ? r.json() : Promise.reject(new Error("failed"))))
      .then((s) => { settings = s; settings.elements = settings.elements || {}; toast(`${labelOf(id)}: picture removed`); refreshEditor(); if (!$("#editor")) render(); })
      .catch(() => toast("Couldn't remove it"));
  }

  // ---------------- 2026-11-05: LIVE VIEW (Angelo: "a viewer that can show live sample overlay all of it in a window
  // inside the app ... click any part of the overlay and open the settings for that component ... see the changes
  // instantly"). The engine draws the whole broadcast (GET /api/live/frame) - live data in a session, the sample race
  // otherwise - and says where each graphic sits (GET /api/live/boxes) so a click opens that graphic's editor.
  const MOMENTS = [
    ["Team radio", "live:radio", "Engineer message on the radio card"], ["Spotter call", "live:spotter", "Spotter line on the radio card"],
    ["Race control", "test:rcbar", "A spoken line, then a penalty"], ["Latest message", "test:latestplate", "The viewer message plate"],
    ["Flag &amp; podium", "test:outro", "The chequered flag, podiums and results"],
  ];
  let lv = { on: false, url: "", timer: 0, live: false, offAir: false, boxes: [], busy: false, sample: false, stage: "race" };
  const STAGE_NAME = { prerace: "Pre-race", race: "Race", finish: "Finish" };
  // 2026-11-06 (Angelo): every "Play a moment" section folds away; all folded the first time, then it remembers
  // which ones you opened (this PC only)
  const lvOpen = (() => { try { return new Set(JSON.parse(localStorage.getItem("gs.lvOpen") || "[]")); } catch { return new Set(); } })();
  function lvSec(key, title, inner) {
    return `<details class="lvsec" data-lvsec="${key}"${lvOpen.has(key) ? " open" : ""}><summary><span>${esc(title)}</span><i class="chev"></i></summary><div class="lvsec-body">${inner}</div></details>`;
  }
  function lvRememberOpen(d) {
    if (d.open) lvOpen.add(d.dataset.lvsec); else lvOpen.delete(d.dataset.lvsec);
    try { localStorage.setItem("gs.lvOpen", JSON.stringify([...lvOpen])); } catch { }
  }
  SCREENS.live = () => head("Live view", "Your broadcast, live", "This is the whole overlay as Grandstand draws it. <b>Click any graphic to edit it</b>, and your changes show here straight away. Play a moment from the right to watch its animation.",
      `<div class="lvstate" id="lvState"><span class="dot"></span><b>Starting…</b></div>`) +
    `<div class="lv">
      <div class="lvscreen" id="lvScreen" tabindex="0" aria-label="Live view of the broadcast">
        <img id="lvImg" alt="">
        <div class="lvboxes" id="lvBoxes"></div>
        <div class="lvwait" id="lvWait">Drawing your broadcast…</div>
      </div>
      <div class="lvside">
        <div class="panel"><h2>Play a moment</h2>
          ${lvSec("sample", "Sample race", `
          <div class="seg lvstage" role="group" aria-label="Sample race stage">${[["prerace", "Pre-race"], ["race", "Race"], ["finish", "Finish"]].map(([k, l]) => `<button type="button" data-lvstage="${k}" aria-pressed="${lv.stage === k}">${l}</button>`).join("")}</div>
          <div class="row" style="padding:8px 0 0;border:0"><span class="t">Sound<small>Music and stingers for the sample race, played here in the app (in a real race Grandstand's sound goes to your stream / TV)</small></span>${sw("lvsound", lv.sound !== false, "Sample race sound")}</div>
          <p class="muted" id="lvSampleNote" style="margin:8px 0 0;font-size:12px">With iRacing closed, Grandstand plays a sample race here so everything can run. When a race or replay is running, this shows your race instead.</p>`)}
          ${lvSec("before", "Before the race", `<div class="lvchips"><button type="button" class="chip gold" data-lvshow="prerace" title="The opener music, globe, logos, the starting grid roll, front-row hero cards and the pre-race info cards">Pre-race show, from the top</button><button type="button" class="chip" data-lvwatch="1" title="Three drivers to keep an eye on and why - the on-demand card (it also plays in the pre-race show)">Drivers to watch</button></div>`)}
          ${lvSec("story", "Story cards", `<div class="lvchips">${CARDS.map((c) => `<button type="button" class="chip" data-lvplay="test:${c.id}" title="${esc(c.hint)}">${esc(c.label)}</button>`).join("")}</div>`, CARDS.length)}
          ${lvSec("radio", "Radio and race control", `<div class="lvchips">${MOMENTS.filter(([, k]) => k !== "test:outro").map(([l, k, h]) => `<button type="button" class="chip" data-lvplay="${k}" title="${h}">${l}</button>`).join("")}</div>`)}
          ${lvSec("viewers", "Viewers", `<div class="lvchips">${VIEWER_TESTS.map(([k, l]) => `<button type="button" class="chip" data-lvviewer="${k}">${esc(l)}</button>`).join("")}${TIER_TESTS.filter(([k]) => k !== "comment").map(([k, l]) => `<button type="button" class="chip" data-lvviewer="${k}" title="Needs Tier looks on (Settings > Viewers)">${esc(l)}</button>`).join("")}<button type="button" class="chip gold" data-lvbet="1" title="A pretend viewer bet: open, locked, result">Viewer bet</button></div>`)}
          ${lvSec("mine", "Your own cards", `<div class="lvchips" id="lvMyCards"><span class="muted" style="font-size:12px">Reading your cards…</span></div>`)}
          ${lvSec("vp", "Extra graphics (visual pack)", `<div class="lvchips">${VP.filter(([id]) => id !== "W9" && id !== "V2").map(([id, k, name, desc]) => `<button type="button" class="chip" data-lvplay="test:vp:${id}" title="${esc(desc)}${vpOn(k) ? "" : " (switch it on in Settings first)"}" ${vpOn(k) ? "" : "data-off=\"1\""}>${esc(name)}</button>`).join("")}</div>`)}
          ${lvSec("finish", "The finish", `<div class="lvchips"><button type="button" class="chip gold" data-lvshow="finish" title="The chequered flag, podiums and results">Flag, podium and results</button></div>`)}
          <p class="muted" style="margin:12px 0 0;font-size:12px">Battles, class transitions and fastest laps happen by themselves as the race runs.</p>
        </div>
        <div class="panel" style="margin-top:14px"><h2>Graphics</h2>
          ${ELEMENTS.map((e) => `<div class="row lvrow" data-lvrow="${e.id}"><span class="t">${esc(e.label)}</span><button type="button" class="mini" data-edit="${e.id}">Edit</button>${sw("lv:" + e.id, el(e.id).enabled, e.label)}</div>`).join("")}
        </div>
      </div>
    </div>`;
  function lvPaintBoxes() {
    const host = $("#lvBoxes"); if (!host) return;
    const list = [...lv.boxes].filter((b) => ELEMENTS.some((e) => e.id === b.id)).sort((a, b) => (a.drawn - b.drawn) || (b.w * b.h - a.w * a.h));   // what is on screen above what isn't, then small ones on top
    host.innerHTML = list.map((b) => {
      const on = el(b.id).enabled, selId = $("#editor")?.dataset.id;
      return `<button type="button" class="lvbox ${b.drawn ? "drawn" : ""} ${on ? "" : "off"} ${b.id === selId ? "sel" : ""}" data-lvbox="${b.id}" style="left:${(b.x / 19.2).toFixed(3)}%;top:${(b.y / 10.8).toFixed(3)}%;width:${(b.w / 19.2).toFixed(3)}%;height:${(b.h / 10.8).toFixed(3)}%"><span>${esc(labelOf(b.id))}${on ? "" : " · hidden"}</span></button>`;
    }).join("");
    $$("[data-lvstage]").forEach((x) => { x.setAttribute("aria-pressed", x.dataset.lvstage === lv.stage); x.disabled = lv.live; });
    const sn = $("#lvSampleNote"); if (sn) sn.textContent = lv.live ? "iRacing is running: this is your race. The sample race comes back when iRacing closes." : "With iRacing closed, Grandstand plays a sample race here so everything can run. When a race or replay is running, this shows your race instead.";
    const st = $("#lvState");
    if (st) { st.className = "lvstate " + (lv.live ? "live" : "sample"); st.innerHTML = `<span class="dot"></span><b>${lv.live ? "Live · your race" : "Sample race · " + (STAGE_NAME[lv.stage] || "Race")}</b>${lv.offAir ? `<span class="offair">Off air</span>` : ""}`; }
  }
  function lvFrame() {
    if (!lv.on) return;
    const img = $("#lvImg"); if (!img) { lv.on = false; return; }
    // 2026-11-06 (lag): 1280 wide at most, the next picture asked for as soon as one arrives (the engine now draws it on the
    // graphics card); the clickable outlines and the sounds come on their own slower timer (lvSide)
    const w = Math.min(1280, Math.max(960, Math.round((img.parentElement.clientWidth || 1200) * (window.devicePixelRatio || 1))));
    lv.busy = true;
    fetch(`/api/live/frame?w=${w}&t=${Date.now()}`, { cache: "no-store" }).then((r) => (r.ok ? r.blob() : Promise.reject(new Error(r.status)))).then((blob) => {
      if (!lv.on) return;
      const u = URL.createObjectURL(blob);
      img.onload = () => { if (lv.url) URL.revokeObjectURL(lv.url); lv.url = u; img.classList.add("on"); $("#lvWait")?.classList.remove("on"); };
      img.src = u;
    }).catch(() => { const m = $("#lvWait"); if (m && !lv.url) { m.textContent = "Couldn't reach Grandstand - retrying…"; m.classList.add("on"); } })
      .finally(() => { lv.busy = false; if (lv.on) lv.timer = setTimeout(lvFrame, document.hidden ? 1500 : lv.live ? 200 : 0); });
  }
  // the outlines (300 ms) and the sample race's sound, played here in the app window (Grandstand's own sound goes to the TV)
  const lvAudio = { since: -1, music: null, cues: [] };
  function lvSoundStop() { try { lvAudio.music?.pause(); } catch { } lvAudio.music = null; lvAudio.cues.forEach((a) => { try { a.pause(); } catch { } }); lvAudio.cues = []; }
  function lvSide() {
    if (!lv.on) return;
    Promise.all([api.get("/api/live/boxes").catch(() => null), api.get(`/api/live/sounds?since=${lvAudio.since}`).catch(() => null)]).then(([bx, snd]) => {
      if (!lv.on) return;
      if (bx) {
        const wasSample = lv.sample, wasStage = lv.stage;
        const sig = JSON.stringify(bx.boxes) + bx.live + bx.offAir + bx.stage + bx.sample + bx.sound;
        if (sig !== lv.sig) { lv.sig = sig; lv.boxes = bx.boxes || []; lv.live = bx.live; lv.offAir = bx.offAir; lv.sample = !!bx.sample; lv.stage = bx.stage || lv.stage; lv.sound = bx.sound !== false; lvPaintBoxes(); }
        if ((wasSample && !lv.sample) || wasStage !== lv.stage || !lv.sound) lvSoundStop();
      }
      if (snd) {
        if (lvAudio.since < 0) lvAudio.since = snd.last;   // first look: don't replay what already played
        else {
          for (const it of snd.items || []) {
            lvAudio.since = Math.max(lvAudio.since, it.seq);
            if (!lv.sample || !lv.sound) continue;
            const a = new Audio(it.url);
            a.volume = it.kind === "music" ? 0.8 : 0.9;
            if (it.kind === "music") {
              lvSoundStop(); lvAudio.music = a;
              if (it.max > 0) setTimeout(() => { if (lvAudio.music === a) { let v = a.volume; const f = setInterval(() => { v -= 0.08; if (v <= 0 || lvAudio.music !== a) { clearInterval(f); a.pause(); } else a.volume = v; }, 80); } }, Math.max(0, it.max * 1000 - 1000));
            } else { lvAudio.cues.push(a); if (lvAudio.cues.length > 6) lvAudio.cues.shift(); }
            a.play().catch(() => {});
          }
          lvAudio.since = Math.max(lvAudio.since, snd.last);
        }
      }
    }).finally(() => { if (lv.on) lv.sideTimer = setTimeout(lvSide, document.hidden ? 1500 : 300); });
  }
  function lvSetStage(stage, restart) {
    lv.stage = stage; lv.sig = ""; $$("[data-lvstage]").forEach((x) => x.setAttribute("aria-pressed", x.dataset.lvstage === stage));
    return api.post(`/api/live/sample?stage=${stage}${restart ? "&restart=1" : ""}`, null).catch(() => toast("Couldn't reach Grandstand"));
  }
  function lvStop() { lv.on = false; clearTimeout(lv.timer); clearTimeout(lv.sideTimer); lvSoundStop(); lvAudio.since = -1; if (lv.url) URL.revokeObjectURL(lv.url); lv.url = ""; lv.sig = ""; document.body.classList.remove("lv-editing"); }
  BIND.live = (host) => {
    lvStop(); lv.on = true; $("#lvWait")?.classList.add("on"); lvFrame(); lvSide();
    host.addEventListener("toggle", (e) => { if (e.target.matches?.("details.lvsec")) lvRememberOpen(e.target); }, true);
    api.get("/api/cards").then((r) => {
      const box = $("#lvMyCards"); if (!box) return;
      const cards = (r.cards || []).filter((c) => c.card);
      box.innerHTML = cards.length ? cards.map((c) => `<button type="button" class="chip" data-lvcard="${esc(c.id)}">${esc(c.card.name || c.id)}</button>`).join("") : `<span class="muted" style="font-size:12px">None yet - make one in Studio > Card builder.</span>`;
    }).catch(() => { const box = $("#lvMyCards"); if (box) box.innerHTML = ""; });
    leaveHooks.push(lvStop);
    host.addEventListener("click", (e) => {
      const b = e.target.closest("[data-lvbox]");
      if (b) { openEditor(b.dataset.lvbox, { fromLive: true }); return; }
      if (e.target.closest("[data-lvbet]")) { api.post("/api/test?card=storyclear", null).catch(() => {}).then(() => api.post("/api/integrations/bets/test")).then(() => toast("Viewer bet: open 15 s, locked, then the result")); return; }   // 2026-11-06
      if (e.target.closest("[data-lvwatch]")) { api.post("/api/test?card=storyclear", null).catch(() => {}).then(() => api.post("/api/desk/show?what=watch")).then((r) => toast(r?.result || "Drivers to watch")).catch(() => toast("Couldn't reach Grandstand")); return; }   // 2026-11-06
      const ed = e.target.closest("[data-edit]"); if (ed) { openEditor(ed.dataset.edit, { fromLive: true }); return; }
      const fired = (x) => { x.classList.remove("fired"); void x.offsetWidth; x.classList.add("fired"); };
      const st = e.target.closest("[data-lvstage]");
      if (st) { lvSetStage(st.dataset.lvstage, false).then(() => toast("Sample race: " + STAGE_NAME[st.dataset.lvstage])); return; }
      const sh = e.target.closest("[data-lvshow]");
      if (sh) {
        fired(sh);
        if (lv.live) { if (sh.dataset.lvshow === "finish") api.post("/api/test?card=outro", null).then(() => toast("Playing: the finish")); else toast("iRacing is running - the pre-race show plays in your race's own pre-race"); return; }
        lvSetStage(sh.dataset.lvshow, true).then(() => {
          if (sh.dataset.lvshow === "finish") setTimeout(() => api.post("/api/test?card=outro", null).catch(() => {}), 700);
          toast(sh.dataset.lvshow === "prerace" ? "Pre-race show: music, globe, logos, the grid, hero cards and info cards" : "Playing: flag, podium and results");
        });
        return;
      }
      const raceFirst = () => (!lv.live && lv.stage !== "race" ? lvSetStage("race", false).then(() => new Promise((ok) => setTimeout(ok, 800))) : Promise.resolve());
      // 2026-11-06 (Angelo: "every button needs to work"): anything that shares the right side / the top band with the story
      // cards first sends a story card on screen on its way, so what you pressed shows straight away
      const clearWay = () => api.post("/api/test?card=storyclear", null).catch(() => {}).then(() => new Promise((ok) => setTimeout(ok, 350)));
      const p = e.target.closest("[data-lvplay]");
      if (p) {
        fired(p);
        const [kind, ...rest] = p.dataset.lvplay.split(":"); const what = rest.join(":");
        const story = kind === "test" && /^[A-Z]/.test(what);   // the story cards themselves replace whatever is up
        raceFirst().then(() => (story ? null : clearWay())).then(() => (kind === "live" ? api.post("/api/live/trigger?what=" + what, null) : api.post("/api/test?card=" + encodeURIComponent(what), null)))
          .then(() => toast("Playing: " + p.textContent + (p.dataset.off ? " (it's switched off in Settings, so it may not show)" : ""))).catch(() => toast("Couldn't reach Grandstand"));
        return;
      }
      const v = e.target.closest("[data-lvviewer]");
      if (v) {
        fired(v);
        raceFirst().then(clearWay).then(() => api.post("/api/viewer/test", { type: v.dataset.lvviewer }))
          .then((r) => toast(r?.onScreen === false && r.why ? r.why : "Playing: " + v.textContent)).catch(() => toast("Couldn't reach Grandstand"));
        return;
      }
      const mc = e.target.closest("[data-lvcard]");
      if (mc) { fired(mc); raceFirst().then(clearWay).then(() => api.post(`/api/cards/${encodeURIComponent(mc.dataset.lvcard)}/play`, null)).then(() => toast("Playing: " + mc.textContent)).catch(() => toast("Couldn't reach Grandstand")); return; }
    });
    host.addEventListener("mouseover", (e) => { const r = e.target.closest("[data-lvrow]"); $$(".lvbox", host).forEach((x) => x.classList.toggle("hl", !!r && x.dataset.lvbox === r.dataset.lvrow)); });
    host.addEventListener("change", (e) => {
      if (e.target.dataset.k === "lvsound") { lv.sound = e.target.checked; api.post(`/api/live/sample?sound=${lv.sound ? 1 : 0}`, null).then(() => toast(lv.sound ? "Sample race sound on" : "Sample race is silent")).catch(() => {}); return; }
      const k = e.target.dataset.k; if (!k?.startsWith("lv:")) return;
      const id = k.slice(3);
      settings.elements[id] = { ...DEF_EL, ...el(id), enabled: e.target.checked };
      save(`${labelOf(id)}: ${e.target.checked ? "on" : "hidden"}`); lv.sig = "";
    });
  };

  // ---- Show ----
  SCREENS.show = () => {
    const grp = (g, title, sub) => `<div class="panel"><h2>${title}</h2><p class="muted" style="margin:-6px 0 6px">${sub}</p>` +
      ELEMENTS.filter((e) => e.group === g).map((e) => {
        return `<div class="row"><span class="t">${esc(e.label)}<small>${leanOff(e.id) ? "Off in Lean mode" : esc(e.hint)}</small></span>${moved(settings.elements[e.id]) ? `<button type="button" class="mini" data-reset="${e.id}" title="Put it back where it started">Reset</button>` : ""}<button type="button" class="mini" data-edit="${e.id}" style="margin-right:4px">Edit</button><span class="live-tag ${showing(e.id) ? "on" : ""}" data-live="${e.id}">LIVE</span>${sw(e.id, el(e.id).enabled, e.label)}</div>`;
      }).join("") + `</div>`;
    return head("Show", "What goes on screen", "Switch any graphic on or off. It takes effect on the broadcast instantly.",
      `<div class="seg" role="group" aria-label="Style"><button type="button" data-preset="full">Full broadcast</button><button type="button" data-preset="racing">Racing</button><button type="button" data-preset="minimal">Minimal</button></div>`) +
      `<div class="grid g3">${grp("before", "Before the race", "From the grid to the green")}${grp("during", "During the race", "Always on, plus moments")}${grp("after", "After the flag", "The finish and the wrap-up")}</div>`;
  };
  BIND.show = (host) => {
    host.addEventListener("change", (e) => {
      const k = e.target.dataset.k; if (!k) return;
      settings.elements[k] = { ...el(k), enabled: e.target.checked };
      save(`${ELEMENTS.find((x) => x.id === k)?.label}: ${e.target.checked ? "on" : "off"}`);
    });
    host.addEventListener("click", (e) => {
      const p = e.target.closest("[data-preset]"); if (p) { applyPreset(p.dataset.preset); save(p.textContent + " style"); render(); return; }
      const ed = e.target.closest("[data-edit]"); if (ed) { openEditor(ed.dataset.edit); return; }   // 2026-11-05
      const r = e.target.closest("[data-reset]"); if (r) { const o = el(r.dataset.reset); settings.elements[r.dataset.reset] = { ...DEF_EL, enabled: o.enabled, image: o.image || "", imageMode: o.imageMode || "glass", imageOpacity: o.imageOpacity ?? 0.3 }; save("Back to its default place and size"); render(); }
    });
  };

  // ---- Event & sponsors (edits Documents\iRacing\Grandstand\sponsors.json) ----
  const PARTNERS = [["AB", "A&B General Contracting"], ["Storm", "StormExhibit"], ["GoFast", "GO FAST"], ["TradingPaints", "Trading Paints"], ["Custom", "Your own partner"]];
  const SLOT_LABEL = {
    RaceSoFar: "Race so far card", MoveOfRace: "Move of the race card", WeatherWatch: "Weather watch card", FastestLaps: "Fastest laps card",
    Limits: "Off the track card", CarContact: "Car contact card", Cleanest: "Cleanest driver card", YourRace: "Your race card", PitStops: "Pit lane card",
    Halfway: "Halfway card", DriverOfDay: "Driver of the day", UnderPressure: "Under pressure (battle box tag)", WeatherBar: "Weather tag on the session bar",
    PreRacePanelAB: "Pre-race partner panel (roof)", PreRacePanelStorm: "Pre-race partner panel (radar)", CustomCards: "Your own cards (card builder)",
    WeatherCard: "Weather card (V1)", StrategyBoard: "Strategy board - its look comes from this partner's logo file (V4)", ReplayBumper: "Replay bumper (V6)",
    AwardStint: "Award: longest stint (V7)", AwardLapsLed: "Award: most laps led (V7)", AwardConsistent: "Award: most consistent (V7)", AwardPhotoFinish: "Award: photo finish (V7)",
  };
  let sponsors = null, sponsorSlots = [], sponsorFile = "";
  SCREENS.event = () => {
    const pk = sponsors || { partners: {}, presents: {} };
    const partnerRows = PARTNERS.map(([k, name]) => {
      const p = pk.partners?.[k] || { on: false, logo: "", word: "" };
      return `<div class="prow" data-partner="${k}">
        <div class="pn"><b>${esc(name)}</b><small class="mono muted">${k}</small></div>
        ${sw("p_on_" + k, p.on, name + " on")}
        <label class="f">Logo file<input data-pf="logo" value="${esc(p.logo)}" placeholder="logo.png or C:\\full\\path\\logo.png"></label>
        <label class="f pick">&nbsp;<span class="btn">Pick…<input type="file" accept="image/png,image/jpeg" data-pick="${k}"></span></label>
        <label class="f">Wordmark text<input data-pf="word" value="${esc(p.word)}" placeholder="drawn when there's no logo" maxlength="40"></label>
      </div>`;
    }).join("");
    const opts = (cur) => `<option value="none" ${cur === "none" || !cur ? "selected" : ""}>Nobody (shows Grandstand)</option>` +
      PARTNERS.map(([k, n]) => `<option value="${k}" ${cur === k ? "selected" : ""}>${esc(n)}</option>`).join("");
    const slotRows = sponsorSlots.map((s) => `<div class="row"><span class="t">${esc(SLOT_LABEL[s] || s)}<small class="mono">${esc(s)}</small></span><select class="in" data-slot="${esc(s)}" style="width:250px">${opts(pk.presents?.[s])}</select></div>`).join("");
    return head("Event &amp; sponsors", "Make it your league's broadcast", `Who presents what, saved to <span class="mono">${esc(sponsorFile || "Documents\\iRacing\\Grandstand\\sponsors.json")}</span>. Changes go on air as soon as you save — no restart.`,
      `<div style="display:flex;gap:10px"><button type="button" class="btn" id="resetSponsors">Reset to defaults</button><button type="button" class="btn gold" id="saveSponsors">Save sponsors</button></div>`) +
      (sponsors ? "" : `<p class="muted">Loading the sponsor pack…</p>`) +
      `<div class="panel" style="--accent: var(--gold2)"><h2>Partners</h2><p class="muted" style="margin:-6px 0 8px">Switch a partner off to hide it everywhere. Logo: a file name in <span class="mono">Documents\\iRacing\\Grandstand</span> (or the bundled sponsors folder), or a full path. "Pick…" fills in the file name — put the file in that folder.</p>${partnerRows}</div>
      <div class="grid g2" style="margin-top:16px">
        <div class="panel" style="--accent: var(--gold)"><h2>Who presents each spot</h2>${slotRows || `<p class="muted">—</p>`}</div>
        <div class="panel"><h2>The event</h2>
          <div class="grid" style="gap:14px">
            <label class="f">Event name<input id="evName" placeholder="The Night Shift 6 Hours"></label>
            <label class="f">League<input id="evLeague" placeholder="Night Shift Endurance League"></label>
            <div class="grid g2"><label class="f">Main colour<input id="evC1" type="color" value="#0B1426"></label><label class="f">Accent colour<input id="evC2" type="color" value="#C5A572"></label></div>
            <button type="button" class="btn" id="saveEvent">Save event details</button>
            <p class="muted" style="margin:0">Event name and colours are kept with your pack; they don't change the graphics yet.</p>
          </div></div>
      </div>
      <div class="panel clockpanel" id="clockPanel" style="--accent: var(--gold2); margin-top:16px"><h2>Broadcast clock</h2>
        <p class="muted" style="margin:-6px 0 12px">The watch on the session bar. Click a face to put it on air — it changes live.</p>
        <div id="clockBody">${dialsBody()}</div>
      </div>`;
  };

  // ---- Broadcast clock (ClockDial.cs: Documents\iRacing\Grandstand\clock.json + dials\ folder) ----
  let dials = null;
  const isCustomDial = (id) => String(id || "").startsWith("file:");
  function dialPick(d, customFace) {
    const on = dials && (dials.choice === d.id || (dials.choice === "auto" && dials.active === d.id));
    return `<button type="button" class="dialpick" data-dial="${esc(d.id)}" aria-pressed="${on}" title="${esc(d.name)}">
      <span class="watchface ${customFace ? "custom" : "pm"}"><img src="${esc(d.thumb)}" alt="" loading="lazy"></span>
      <b>${esc(customFace ? d.name.replace(/\.[^.]+$/, "").replace(/[_-]+/g, " ") : d.name)}</b>${dials.choice === "auto" && dials.active === d.id ? `<small>default</small>` : ""}</button>`;
  }
  function dialsBody() {
    if (!dials) return `<p class="muted">Loading the clock faces…</p>`;
    const custom = dials.custom || [];
    const showing = isCustomDial(dials.choice);
    const handsOn = showing ? !!dials.showingSubdialHands : dials.subdialHands === true;
    return `<div class="row" style="margin:-4px 0 10px"><span class="t">Show the watch<small>Off: a small digital clock sits in the session bar instead</small></span>${sw("showWatch", settings.showWatch !== false, "Show the watch")}</div>
      <div class="row" style="margin:0 0 14px"><span class="t">Your own face<small>Any picture becomes the dial and goes on air straight away. Square pictures work best</small></span><label class="btn sm gold filebtn">Upload a face…<input type="file" accept="image/png,image/jpeg,image/webp" id="dialUpload"></label></div>
      <div class="kicker" style="margin:0 0 10px">Built in</div>
      <div class="dials">${(dials.builtIns || []).map((d) => dialPick(d, false)).join("")}</div>
      <div class="kicker" style="margin:18px 0 10px">Your dials</div>
      ${custom.length ? `<div class="dials">${custom.map((d) => dialPick(d, true)).join("")}</div>`
        : `<p class="muted" style="margin:0">No faces of your own yet. Use <b>Upload a face</b> above (or drop a square-ish PNG, JPG or WEBP in the dials folder and press Refresh).</p>`}
      <div class="row" style="margin-top:10px"><span class="t">Subdial hands on your dial<small>Only if your dial has three small registers at 9, 3 and 6 o'clock, like the built-in faces. Leave off for logos and artwork.</small></span>${sw("dialHands", handsOn, "Subdial hands on your dial")}</div>
      <div class="row"><span class="t">Open my dials folder<small class="mono">${esc(dials.folder || "Documents\\iRacing\\Grandstand\\dials")}</small></span><button type="button" class="btn sm" id="dialsRefresh">Refresh</button></div>
      <p class="muted" style="margin:10px 0 0">Use your own watch face, a partner's design or your league's artwork — use artwork you have the rights to.</p>`;
  }
  function paintDials() { const b = $("#clockBody"); if (b) { b.innerHTML = dialsBody(); const t = $('[data-k="dialHands"]', b); if (t) t.disabled = !isCustomDial(dials?.choice); } }
  function loadDials() { return api.get("/api/dials").then((r) => { dials = r; paintDials(); }); }
  function setDial(dial, subdialHands) {
    return api.post("/api/dial", { dial, subdialHands }).then((r) => { dials = r; paintDials(); return r; });
  }
  function readSponsorsForm(host) {
    const pk = clone(sponsors || { partners: {}, presents: {} });
    pk.partners = pk.partners || {}; pk.presents = pk.presents || {};
    $$("[data-partner]", host).forEach((r) => {
      const k = r.dataset.partner;
      pk.partners[k] = { on: $(`[data-k="p_on_${k}"]`, r).checked, logo: $('[data-pf="logo"]', r).value.trim(), word: $('[data-pf="word"]', r).value.trim() };
    });
    $$("[data-slot]", host).forEach((s) => { pk.presents[s.dataset.slot] = s.value; });
    return pk;
  }
  function loadSponsors() {
    return api.get("/api/sponsors").then((r) => { sponsors = r.pack; sponsorSlots = r.slots || Object.keys(r.pack?.presents || {}); sponsorFile = r.file || ""; });
  }
  BIND.event = (host) => {
    if (!sponsors) loadSponsors().then(() => { if (screen === "event") render(); }).catch(() => toast("Couldn't read the sponsor pack"));
    loadDials().catch(() => { const b = $("#clockBody"); if (b) b.innerHTML = `<p class="muted">Couldn't read the clock faces.</p>`; });
    host.addEventListener("click", (e) => {
      const d = e.target.closest("[data-dial]");
      if (d) {
        const id = d.dataset.dial, custom = isCustomDial(id);
        const hands = custom && $('[data-k="dialHands"]', host)?.checked ? true : null;
        setDial(id, hands).then((r) => toast(r.active === id ? `Clock face: ${d.title} — on air now` : "That picture couldn't be read — back to the default face")).catch((er) => toast("Not changed: " + er.message));
        return;
      }
      if (e.target.closest("#dialsRefresh")) loadDials().then(() => toast("Dials folder re-read")).catch(() => toast("Couldn't read the dials folder"));
    });
    host.addEventListener("change", (e) => {   // 2026-11-05: watch on/off + upload your own face
      const t = e.target;
      if (t.dataset?.k === "showWatch") { settings.showWatch = t.checked; save(t.checked ? "Watch on" : "Watch off: digital clock in the bar"); return; }
      if (t.id === "dialUpload" && t.files?.length) {
        const f = t.files[0];
        if (f.size > 40 * 1024 * 1024) { toast("That picture is too big (40 MB max)"); return; }
        toast("Uploading the face…");
        fetch("/api/dials/upload?name=" + encodeURIComponent(f.name), { method: "POST", headers: { "X-Grandstand": "1", "Content-Type": f.type || "application/octet-stream" }, body: f })
          .then((r) => (r.ok ? r.json() : r.text().then((x) => Promise.reject(new Error(x)))))
          .then((r) => { dials = r; paintDials(); toast("Your face is on air"); })
          .catch((er) => toast(er.message || "Upload failed"));
      }
    });
    host.addEventListener("change", (e) => {
      if (e.target.dataset?.k !== "dialHands" || !dials) return;
      setDial(dials.choice, e.target.checked).then(() => toast(e.target.checked ? "Subdial hands on" : "Subdial hands off")).catch(() => toast("Couldn't reach Grandstand"));
    });
    api.get("/api/doc/eventpack").then((p) => {
      if (!p) return;
      $("#evName").value = p.name || ""; $("#evLeague").value = p.league || ""; if (p.c1) $("#evC1").value = p.c1; if (p.c2) $("#evC2").value = p.c2;
    }).catch(() => {});
    host.addEventListener("change", (e) => {
      const f = e.target.closest("[data-pick]"); if (!f || !f.files?.length) return;
      $(`[data-partner="${f.dataset.pick}"] [data-pf="logo"]`, host).value = f.files[0].name;
      toast(`Logo set to ${f.files[0].name} — put the file in Documents\\iRacing\\Grandstand`);
    });
    $("#saveSponsors").addEventListener("click", () => {
      const pk = readSponsorsForm(host);
      api.post("/api/sponsors", pk, "PUT").then((saved) => { sponsors = saved; toast("Sponsors saved — on air now"); }).catch((e) => toast("Not saved: " + e.message));
    });
    $("#resetSponsors").addEventListener("click", () => {
      if (!confirm("Put every partner and spot back to the built-in defaults?")) return;
      api.post("/api/sponsors/reset", null).then((saved) => { sponsors = saved; toast("Sponsors reset to defaults"); render(); }).catch(() => toast("Couldn't reset"));
    });
    $("#saveEvent").addEventListener("click", () => {
      api.post("/api/doc/eventpack", { name: $("#evName").value, league: $("#evLeague").value, c1: $("#evC1").value, c2: $("#evC2").value }, "PUT").then(() => toast("Event details saved"));
    });
  };

  // ---- Test cards ----
  SCREENS.test = () => head("Test cards", "Play any graphic, any time", "Check your layout without waiting for the race to make it happen. Works with demo data when iRacing isn't running.") +
    `<div class="kicker" style="margin:4px 0 10px">Story cards</div><div class="grid g4">${CARDS.map((c) => `<button type="button" class="tile" data-card="${c.id}" style="--accent:${c.accent}"><b>${esc(c.label)}</b><small>${esc(c.hint)}</small></button>`).join("")}</div>
     <div class="kicker" style="margin:22px 0 10px">The finish</div><div class="grid g4"><button type="button" class="tile" data-card="outro" style="--accent: var(--ivory)"><b>Full outro</b><small>Flag, podiums, results</small></button></div>
     <div class="kicker" style="margin:22px 0 10px">Viewers (tier looks)</div><div class="grid g4">
       <button type="button" class="tile" data-vwtile="sequence" style="--accent: var(--gold)"><b>Stream test</b><small>One of each: chat, tier 3, gifted subs, tier 2, tier 1 - through the real queue</small></button>
       <button type="button" class="tile" data-vwtile="tier1" style="--accent: var(--gold)"><b>Tier 1</b><small>Full-screen moment + tower band</small></button>
       <button type="button" class="tile" data-vwtile="tier2" style="--accent: var(--gold)"><b>Tier 2</b><small>Wraps the session bar</small></button>
       <button type="button" class="tile" data-vwtile="tier3" style="--accent: var(--gold)"><b>Tier 3</b><small>Gold card under the bar</small></button>
       <button type="button" class="tile" data-vwtile="comment" style="--accent: var(--gtp)"><b>Chat</b><small>Chat card</small></button>
       <button type="button" class="tile" data-vwroll="sample" style="--accent: var(--ivory)"><b>Thank-you roll</b><small>Sample names</small></button></div>
     <div class="kicker" style="margin:22px 0 10px">Your own cards</div><div class="grid g4" id="myCards"><p class="muted">Cards you make in Studio → Card builder show up here.</p></div>`;
  BIND.test = (host) => {
    host.addEventListener("click", (e) => {
      // #603: the viewer tier samples and the stream test (they wait for a clear screen like real events)
      const v = e.target.closest("[data-vwtile],[data-vwroll]");
      if (v) {
        const p = v.dataset.vwtile ? api.post("/api/viewer/test", { type: v.dataset.vwtile }) : api.post("/api/viewer/thanks", { action: v.dataset.vwroll });
        p.then((r) => toast(v.dataset.vwroll ? "Sample roll playing" : r.onScreen ? "Queued: plays when the screen is clear (Tier looks must be on)" : r.why)).catch(() => toast("Couldn't reach Grandstand"));
        v.classList.remove("fired"); void v.offsetWidth; v.classList.add("fired");
        return;
      }
      const b = e.target.closest("[data-card],[data-mycard]"); if (!b) return;
      const p = b.dataset.card ? api.post("/api/test?card=" + encodeURIComponent(b.dataset.card), null) : api.post(`/api/cards/${encodeURIComponent(b.dataset.mycard)}/play`, null);
      p.then(() => toast("Playing: " + b.querySelector("b").textContent)).catch(() => toast("Couldn't reach Grandstand"));
      b.classList.remove("fired"); void b.offsetWidth; b.classList.add("fired");
    });
    api.get("/api/cards").then((r) => {
      const box = $("#myCards"); if (!box || !r.cards?.length) return;
      box.innerHTML = r.cards.filter((c) => c.card).map((c) => `<button type="button" class="tile" data-mycard="${esc(c.id)}" style="--accent:${esc(c.card.accent || "#34D27B")}"><b>${esc(c.card.name || c.id)}</b><small>${esc(c.card.template || "number")}</small></button>`).join("");
    }).catch(() => {});
  };

  // ---- Settings (+ presets & shareable packs) ----
  let packs = [];
  // story card layout (2026-10-27, #349 / #393): control.json cardLayout
  const CARD_LAYOUTS = [["mixed", "Mixed (default)"], ["column", "Column"], ["halves", "Two halves"], ["flip", "Flip"], ["corners", "Corners"], ["classic", "Classic"]];
  // class transitions (2026-10-24): Auto / Built-in animated / My rollers folder / Off
  let trans = null;
  const TRANS = [["auto", "Auto", "Your rollers where you have them, else the built-in animation"], ["builtin", "Built-in animated", "Grandstand's own animated transition, every class"],
    ["folder", "My rollers folder", "Your picture sequences (Documents\\iRacing\\Grandstand\\transitions)"], ["off", "Off", "The tower just changes board"]];
  const SRC = { personal: "your footage", user: "your rollers", "built-in": "built-in animated", off: "off" };
  function transBody() {
    const cur = settings.transitions || "auto";
    const rows = (trans?.classes || []).filter((c) => ["overall", "gtp", "lmp2", "gt3", "gtd"].includes(c.key))
      .map((c) => `<div class="row" data-tclass="${esc(c.key)}"><span class="t">${esc(c.key.toUpperCase())}<small>${c.userFrames ? `${c.userFrames} frames in your folder` : "no frames in your folder"}</small></span><span class="live-tag on" data-src="${esc(c.source)}">${esc((SRC[c.source] || c.source).toUpperCase())}</span><button type="button" class="mini" data-roller="${esc(c.key)}" title="Records the next 3 seconds of the iRacing replay as this class's roller">Record</button></div>`).join("");
    return `<div class="seg" role="group" aria-label="Class transition" style="flex-wrap:wrap">${TRANS.map(([k, l]) => `<button type="button" data-trans="${k}" aria-pressed="${cur === k}">${l}</button>`).join("")}</div>
      <p class="muted" style="margin:10px 0 6px" id="transHint">${esc(TRANS.find((x) => x[0] === cur)?.[2] || "")}</p>
      ${rows || `<p class="muted">Reading…</p>`}
      <div class="row"><span class="t">Your rollers folder<small>${esc(trans?.folder || "Documents\\iRacing\\Grandstand\\transitions")} — one folder per class, jpg/png frames at 30 fps (README inside)</small></span><button type="button" class="btn" id="transRescan">Rescan</button></div>`;
  }
  function paintTrans() { const b = $("#transBody"); if (b) b.innerHTML = transBody(); }
  function loadTrans() { return api.get("/api/transitions").then((r) => { trans = r; paintTrans(); }).catch(() => {}); }
  // 2026-10-27: visual review pack items: [id, settings key, name, description, has an elevated design]
  // round 2 (2026-10-27): V3 (battle radar) not approved - kept off and out of this list. Approved, on by default: W8, W11, W12, W13.
  const VP = [
    ["V1", "weatherCard", "Weather card (StormExhibit)", "\u201cRAIN AT TERTRE ROUGE\u201d presented by StormExhibit when rain starts, gets heavier, the track dries or the track temperature swings. Always below the session bar and ticker", true],
    ["V2", "nightPalette", "Day-to-night graphics", "As the sim's sun sets the whole package turns to a night palette: midnight-navy glass, warm ivory type, amber golds (Elevated adds a soft glow)", true],
    ["V4", "strategyBoard", "Strategy board (sponsored)", "Long races: who has pitted, laps on tyres, next stops, your fuel. With a partner for the StrategyBoard spot it booms out of their logo: the logo's own shape is the border, its colour the theme", true],
    ["V5", "liveTag", "BACK TO LIVE card", "As the LIVE wipe clears, a BACK TO LIVE plate in the REPLAY tag's corner (lap and time to go)", false],
    ["V6", "replayBumper", "Replay bumper", "\u201cREPLAY presented by\u201d your partner as a replay starts; Elevated: navy doors with your watch rewinding to the replayed moment. The session bar goes to its battle look while it plays (needs Replay wipes on)", true],
    ["V7", "statsCards", "Race awards", "After the flag, one at a time, each its own sponsored card: longest stint (fuel gauge), most laps led (LED board), most consistent (metronome), photo finish (film strip)", false],
    ["V8", "yellowMap", "Yellow on the 3D map", "Under a local yellow: the track card's 3D map flies from the whole lap down to the yellow sector, the car in trouble pulsing", false],
    ["W8", "breakCard", "Going to break", "Replay paused for 6 s: \u201cWe'll be right back\u201d with your watch showing the real time; the stinger when it plays again (Elevated: split screen)", true],
    ["W9", "lowerThirdSounds", "Lower-third sounds", "A light whoosh when the driver card changes or a team radio card comes in (replays only, like the other stingers)", false],
    ["W10", "pipOnboard", "PiP onboard", "During a battle, LIVE sessions only: a virtual onboard of the chasing car - wheel, lights, gear, gap. Stands down by itself if the car's data isn't coming through (Elevated: both drivers)", true],
    ["W11", "lensEffects", "Lens flare (lined up with the sun)", "On chase / nose / rear cameras: a flare exactly where the sim's sun is in the shot, glow when it's in front of the lens; nothing when the direction isn't known (Elevated: cinema flare, dust, heat haze)", true],
    ["W12", "penaltyCard", "Race control card", "Penalties, warnings, track limits and incident points (with \u201cRace control as a top bar\u201d on they come down in the bar; otherwise in the radio column above the team radio card - Elevated: message log)", true],
    ["W13", "purpleBeep", "Purple-sector beep", "The timing-screen beep when a class leader (or the car on camera) sets a purple sector (replays only, so TV only)", false],
  ];
  const VP_ON_BY_DEFAULT = new Set(["breakCard", "lensEffects", "penaltyCard", "purpleBeep", "lowerThirdSounds"]);
  const vpOn = (k) => { const v = settings.visuals || {}; return VP_ON_BY_DEFAULT.has(k) ? v[k] !== false : !!v[k]; };
  const vpElev = (id) => ((settings.visuals || {}).elevated || []).includes(id);
  // 2026-10-29 (#494): viewer cards (Streamer.bot / StreamElements -> POST /api/viewer), the track fund and themes
  const vw = () => settings.viewer || {};
  const VIEWER_TESTS = [["comment", "Chat"], ["superchat", "Super Chat"], ["bigsuperchat", "Big Super Chat"], ["tip", "Tip"], ["bits", "Bits"], ["member", "Member"], ["sub", "Sub"]];
  let viewerStatus = null;
  const money = (v) => "$" + (Number(v) || 0).toLocaleString("en-US", { minimumFractionDigits: (Number(v) || 0) % 1 ? 2 : 0, maximumFractionDigits: 2 });
  // #603 (2026-10-30): the tier looks - one sample per look, the "Stream test" sequence and this stream's thank-you list
  const TIER_TESTS = [["comment", "Chat"], ["tier3", "Tier 3"], ["milestone", "Milestone"], ["giftsub", "Gifted subs"], ["tier2", "Tier 2"], ["tier1", "Tier 1"]];
  const namesText = (r) => r && r.names ? `${r.names.count} ${r.names.count === 1 ? "name" : "names"} (${r.names.supporters} ${r.names.supporters === 1 ? "supporter" : "supporters"}) since ${r.names.started}` : "Reading…";
  function loadViewer() { return api.get("/api/viewer").then((r) => { viewerStatus = r; const c = $("#vwCur"); if (c) c.textContent = money(r.fund?.current) + " raised so far (fund.json)"; const n = $("#vwNames"); if (n) n.textContent = namesText(r); }).catch(() => {}); }
  // #611: what the cam placer sees in OBS (one line under "Keep cams clear of graphics")
  function loadCams() {
    return api.get("/api/cams").then((r) => {
      const el = $("#camStatus"); if (!el) return;
      if (!r.on) { el.textContent = ""; return; }
      if (!r.linked) { el.textContent = "Now: OBS not linked" + (r.note ? " (" + r.note + ")" : ""); return; }
      const on = (r.cams || []).filter((c) => c.enabled);
      el.textContent = `Now: scene \u201c${r.scene}\u201d, ` + (on.length ? on.map((c) => c.name + (c.raised ? " (over the graphics)" : c.at === "home" ? " at home" : " moved clear")).join(", ") : "no cam switched on") + (r.last ? ` · last: ${r.last}` : "");
    }).catch(() => {});
  }
  // ---------------- 2026-10-30 (#601): broadcast themes - picker, auto by date and the theme editor ----------------
  // Custom themes live in settings.customThemes (control.json) and are saved like every other setting: the WHOLE settings
  // object is read, changed and posted back (save()). The live preview (POST /api/theme/preview) is never saved.
  const TH_TEX = [["none", "None"], ["grain", "Film grain"], ["carbon", "Carbon weave"], ["brushed", "Brushed metal"], ["embers", "Embers glow"], ["snow", "Frost"], ["stars", "Star field"]];
  const TH_MOTIF = [["none", "None"], ["cobweb-bat", "Cobweb + bat"], ["snowflake", "Snowflakes"], ["flag-stripes", "Corner stripes"], ["chequered", "Chequered corner"], ["pinstripe", "Pinstripe along the foot"]];
  const TH_PART = [["auto", "Match the texture"], ["embers", "Rising embers"], ["snow", "Falling snow"], ["twinkle", "Twinkling stars"], ["glints", "Floating glints"]];
  const TH_TARGETS = [["tower", "Timing tower"], ["bar", "Session bar"], ["driver", "Driver card"], ["story", "Story cards"], ["battle", "Battle box"], ["splash", "Transitions, pre-race & results cards"], ["ticker", "Battle ticker"], ["popups", "Radio, fastest lap, pit & flag callouts"], ["viewer", "Viewer cards"]];
  let themes = null, thEdit = null, thDirty = false, thPreview = false, thPrevT = 0;
  const thAll = () => {
    if (!themes) return [];
    const cs = settings.customThemes || [];
    const list = themes.builtIns.map((b) => cs.find((c) => c.id === b.id) || b);
    cs.forEach((c) => { if (!themes.builtIns.some((b) => b.id === c.id)) list.push(c); });
    return list;
  };
  const thFind = (id) => thAll().find((t) => t.id === id);
  const thCanEdit = (t) => !!t && (!t.builtIn || t.editable);
  const thSlug = (s) => String(s || "").toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "").slice(0, 32);
  function loadThemes() {
    return api.get("/api/themes").then((r) => { themes = r; if (!thEdit) thEdit = clone(thFind(settings.theme || "default") || r.builtIns[0]); paintThemes(); }).catch(() => {});
  }
  function thSwatch(t) {
    const tx = { grain: "repeating-linear-gradient(45deg,rgba(255,255,255,.04) 0 1px,transparent 1px 3px)", carbon: "repeating-linear-gradient(45deg,rgba(255,255,255,.07) 0 4px,rgba(0,0,0,.2) 4px 8px)", brushed: "repeating-linear-gradient(0deg,rgba(255,255,255,.05) 0 1px,transparent 1px 3px)", stars: "radial-gradient(circle at 20% 30%,#fff 0 1px,transparent 2px),radial-gradient(circle at 70% 60%,#fff 0 1px,transparent 2px)", snow: "linear-gradient(180deg,rgba(255,255,255,.18),transparent 40%)" }[t.texture] || "none";
    const glass = `color-mix(in srgb, ${t.glass} ${Math.round((t.glassOpacity ?? 0.5) * 100)}%, #080A10)`;
    return `<div aria-hidden="true" style="height:64px;border-radius:8px;border:1px solid var(--line2);position:relative;overflow:hidden;background:${tx},linear-gradient(0deg, color-mix(in srgb, ${t.glow} ${Math.round((t.glowStrength ?? 0.3) * 45)}%, transparent), transparent 70%),${glass}">
      <div style="position:absolute;left:0;right:0;top:0;height:2px;background:${t.accent}"></div>
      <div style="position:absolute;left:14px;top:14px;font:700 13px var(--ui);letter-spacing:.12em;color:${t.accent}">${esc((t.name || "").toUpperCase())}</div>
      <div style="position:absolute;left:14px;top:34px;font:600 18px var(--ui);color:#FCFAF5">P1 · 3:30.041</div>
      ${t.motif === "pinstripe" ? `<div style="position:absolute;left:0;right:0;bottom:3px;height:2px;background:${t.accent};box-shadow:0 -3px 0 ${t.secondary},0 -6px 0 ${t.glow}"></div>` : ""}</div>`;
  }
  const thRange = (k, label, hint) => `<div class="row"><span class="t">${label}<small>${hint}</small></span><input type="range" min="0" max="100" step="1" data-th="${k}" value="${Math.round((thEdit[k] ?? 0) * 100)}" style="max-width:220px" ${thCanEdit(thEdit) ? "" : "disabled"}><span class="muted" data-thv="${k}" style="width:42px;text-align:right">${Math.round((thEdit[k] ?? 0) * 100)}%</span></div>`;
  const thColor = (k, label, hint) => `<div class="row"><span class="t">${label}<small>${hint}</small></span><input type="color" data-th="${k}" value="${esc((thEdit[k] || "#000000").toLowerCase())}" style="width:64px;height:40px;border:0;background:none;cursor:pointer" ${thCanEdit(thEdit) ? "" : "disabled"}></div>`;
  const thSelect = (k, label, hint, opts) => `<div class="row"><span class="t">${label}<small>${hint}</small></span><select class="in" data-th="${k}" style="width:230px" ${thCanEdit(thEdit) ? "" : "disabled"}>${opts.map(([v, l]) => `<option value="${v}"${thEdit[k] === v ? " selected" : ""}>${l}</option>`).join("")}</select></div>`;
  const thSwitch = (k, label, hint) => `<div class="row"><span class="t">${label}<small>${hint}</small></span><label class="switch"><input type="checkbox" data-th="${k}" ${thEdit[k] ? "checked" : ""} ${thCanEdit(thEdit) ? "" : "disabled"} aria-label="${esc(label)}"><span></span></label></div>`;
  function themeBody() {
    if (!themes || !thEdit) return `<p class="muted">Reading the themes…</p>`;
    const all = thAll(), d = settings.themeDates || {}, edit = thCanEdit(thEdit), custom = !thEdit.builtIn;
    const opt = (sel) => all.map((t) => `<option value="${esc(t.id)}"${t.id === sel ? " selected" : ""}>${esc(t.name)}${t.builtIn ? "" : " (yours)"}</option>`).join("");
    return `
      <div class="row"><span class="t">Theme on air<small>Tints the glass of the graphics, adds a texture, a small motif and (on some) drifting particles. The layout, sizes and timing never change. Default is the normal look</small></span><select class="in" id="thPick" style="width:230px">${opt(settings.theme || "default")}</select></div>
      <div class="row"><span class="t">Theme by date<small>On those dates the holiday theme takes over by itself; your pick the rest of the year</small></span>${sw("themeByDate", settings.themeByDate === true, "Theme by date")}</div>
      ${settings.themeByDate ? `<div class="row"><span class="t">Holidays<small>Halloween 24-31 Oct · Christmas 1-26 Dec · July 4th 1-5 Jul</small></span><div style="display:flex;gap:16px;flex-wrap:wrap;justify-content:flex-end">
        ${[["halloween", "Halloween"], ["christmas", "Christmas"], ["july4", "July 4th"]].map(([k, l]) => `<label style="display:flex;gap:8px;align-items:center">${l}${sw("thd:" + k, d[k] !== false, l)}</label>`).join("")}</div></div>` : ""}
      <h3 style="margin:18px 0 6px">Edit a theme</h3>
      <div class="row"><span class="t">Edit<small>${edit ? (custom ? "Your theme: change anything, then Save" : "Team colours: edit in place, then Save (Reset brings the preset back)") : "A built-in preset: Duplicate it to make your own version"}</small></span><select class="in" id="thEditPick" style="width:230px">${opt(thEdit.id)}</select></div>
      <div class="row"><span class="t">Preview on the overlay<small>Shows the theme you are editing on the overlay right now, without saving. Turns itself off when you leave this page (or after 10 minutes). Test a card from the On air page to see it on the cards</small></span><label class="switch"><input type="checkbox" id="thPrev" ${thPreview ? "checked" : ""} aria-label="Preview on the overlay"><span></span></label></div>
      <div style="margin:10px 0">${thSwatch(thEdit)}</div>
      <div style="display:flex;gap:8px;flex-wrap:wrap;margin:10px 0 4px">
        <button type="button" class="btn sm" id="thDup">Duplicate</button>
        ${edit ? `<button type="button" class="btn sm gold" id="thSave"${thDirty ? "" : " disabled"}>Save${thDirty ? "" : "d"}</button>` : ""}
        ${custom ? `<button type="button" class="btn sm" id="thDel">Delete</button>` : ""}
        ${thEdit.id === "team" && (settings.customThemes || []).some((c) => c.id === "team") ? `<button type="button" class="btn sm" id="thReset">Reset Team colours</button>` : ""}
        <button type="button" class="btn sm" id="thUse"${(settings.theme || "default") === thEdit.id ? " disabled" : ""}>Put on air</button></div>
      ${edit ? `<div class="row"><span class="t">Name</span><input class="in" data-th="name" maxlength="32" value="${esc(thEdit.name)}" style="width:230px"></div>` : ""}
      ${thColor("accent", "Accent", "Hairlines and eyebrows (gold on the normal look)")}
      ${thColor("secondary", "Second colour", "Particles, snowflakes, the middle stripe")}
      ${thColor("glow", "Glow", "Soft light rising from the bottom edge")}
      ${thRange("glowStrength", "Glow strength", "0 = no glow")}
      ${thColor("glass", "Glass tint", "The shade the dark glass is tinted towards")}
      ${thRange("glassOpacity", "Glass tint strength", "How strongly the glass takes that shade (the most is 90%: it always stays glass)")}
      ${thSelect("texture", "Texture", "Laid over the glass, very subtle", TH_TEX)}
      ${thRange("textureStrength", "Texture strength", "")}
      ${thSelect("motif", "Motif", "A small decoration in a quiet corner of each graphic", TH_MOTIF)}
      ${thSwitch("particles", "Particles", "Drifting embers, snow, stars or glints inside the glass (off in light mode)")}
      ${thSelect("particleStyle", "Particle style", "", TH_PART)}
      ${thRange("density", "Particle amount", "")}
      ${thSwitch("sweep", "Light sweep", "A soft sheen crosses each graphic every few seconds (off in light mode)")}
      <div class="row" style="align-items:flex-start"><span class="t">Applies to<small>Which graphics take the theme</small></span><div style="display:grid;grid-template-columns:repeat(auto-fill,minmax(220px,1fr));gap:6px 16px;flex:0 1 480px">
        ${TH_TARGETS.map(([k, l]) => `<label style="display:flex;gap:8px;align-items:center;font-size:14px"><input type="checkbox" data-thapply="${k}" ${(thEdit.appliesTo || []).includes(k) ? "checked" : ""} ${edit ? "" : "disabled"}>${l}</label>`).join("")}</div></div>`;
  }
  function paintThemes() { const b = $("#themeBody"); if (b) b.innerHTML = themeBody(); const g = $("#thGallery"); if (g) g.innerHTML = themeGallery(); }
  // 2026-11-05 (Angelo: "a section called themes and theme editor"): Themes is its own page - a gallery to pick from,
  // then the editor underneath
  function themeGallery() {
    if (!themes) return `<p class="muted">Reading the themes…</p>`;
    const cur = settings.theme || "default";
    return thAll().map((t) => `<button type="button" class="thcard" data-thon="${esc(t.id)}" aria-pressed="${t.id === cur}">${thSwatch(t)}
      <span class="thname"><b>${esc(t.name)}</b><small>${t.id === cur ? "On air" : t.builtIn ? "Built in" : "Yours"}</small></span></button>`).join("");
  }
  SCREENS.themes = () => head("Themes", "The look of your broadcast", "A theme tints the glass of every graphic and can add a texture, a small motif and drifting particles. The layout, sizes and timing never change. Click one to put it on air.") +
    `<div class="panel" style="--accent: var(--gold2)"><h2>Choose a theme</h2><div class="thgrid" id="thGallery">${themeGallery()}</div></div>
     <div class="panel" id="themePanel" style="margin-top:16px"><h2>Theme editor</h2><div id="themeBody">${themeBody()}</div></div>`;
  BIND.themes = (host) => {
    bindThemes(host);
    host.addEventListener("click", (e) => {
      const c = e.target.closest("[data-thon]"); if (!c) return;
      const t = thFind(c.dataset.thon); if (!t) return;
      settings.theme = t.id; save("Theme on air: " + t.name + (settings.themeByDate ? " (theme by date is on)" : ""));
      if (!thDirty) thEdit = clone(t);
      paintThemes();
    });
  };
  function thSendPreview() {
    clearTimeout(thPrevT);
    if (!thPreview) return;
    thPrevT = setTimeout(() => api.post("/api/theme/preview", thEdit).catch(() => toast("Couldn't reach Grandstand")), 150);
  }
  function thStopPreview() { clearTimeout(thPrevT); if (thPreview) { thPreview = false; api.post("/api/theme/preview/stop", null).catch(() => {}); } }
  function thSaveCustom(t, msg) {
    const cs = (settings.customThemes || []).filter((c) => c.id !== t.id);
    const keep = { ...t }; delete keep.builtIn; delete keep.editable;
    settings.customThemes = [...cs, keep];
    save(msg);
  }
  function bindThemes(host) {
    leaveHooks.push(thStopPreview);
    loadThemes();
    host.addEventListener("input", (e) => {
      const k = e.target.dataset?.th;
      if (!k || !thEdit || !thCanEdit(thEdit)) return;
      if (e.target.type === "range") { thEdit[k] = Number(e.target.value) / 100; const v = $(`[data-thv="${k}"]`, host); if (v) v.textContent = e.target.value + "%"; }
      else if (e.target.type === "checkbox") thEdit[k] = e.target.checked;
      else thEdit[k] = e.target.value;
      if (!thDirty) { thDirty = true; const s = $("#thSave", host); if (s) { s.disabled = false; s.textContent = "Save"; } }
      thSendPreview();
    });
    host.addEventListener("change", (e) => {
      const t = e.target;
      if (t.id === "thPick") { settings.theme = t.value; save("Theme on air: " + t.selectedOptions[0].textContent + (settings.themeByDate ? " (theme by date is on)" : "")); paintThemes(); return; }
      if (t.dataset?.k === "themeByDate") { settings.themeByDate = t.checked; save(t.checked ? "Theme follows the date" : "Theme: your pick"); paintThemes(); return; }
      if (t.dataset?.k?.startsWith("thd:")) { const k = t.dataset.k.slice(4); settings.themeDates = { ...(settings.themeDates || {}), [k]: t.checked }; save(t.closest("label").textContent.trim() + (t.checked ? " by date on" : " by date off")); return; }
      if (t.id === "thEditPick") {
        if (thDirty && !confirm("Leave your unsaved changes?")) { t.value = thEdit.id; return; }
        thEdit = clone(thFind(t.value)); thDirty = false; paintThemes(); thSendPreview(); return;
      }
      if (t.id === "thPrev") { if (t.checked) { thPreview = true; thSendPreview(); toast("Previewing " + thEdit.name + " on the overlay"); } else { thStopPreview(); toast("Preview off"); } return; }
      if (t.dataset?.thapply && thCanEdit(thEdit)) {
        const set = new Set(thEdit.appliesTo || []);
        if (t.checked) set.add(t.dataset.thapply); else set.delete(t.dataset.thapply);
        thEdit.appliesTo = TH_TARGETS.map(([k]) => k).filter((k) => set.has(k));
        thDirty = true; paintThemes(); thSendPreview();
      }
    });
    host.addEventListener("click", (e) => {
      const t = e.target;
      if (!thEdit) return;
      if (t.id === "thDup") {
        const base = thEdit.name.replace(/ copy( \d+)?$/, "");
        let name = base + " copy", n = 2;
        while (thAll().some((x) => x.name === name || x.id === thSlug(name))) name = `${base} copy ${n++}`;
        thEdit = { ...clone(thEdit), id: thSlug(name) || "custom", name, builtIn: false, editable: true };
        if (!thEdit.appliesTo?.length) thEdit.appliesTo = TH_TARGETS.map(([k]) => k);
        thSaveCustom(thEdit, "Made " + name + ": change it and Save"); thDirty = false;
        setTimeout(paintThemes, 200); thSendPreview(); return;
      }
      if (t.id === "thSave") {
        if (!thEdit.name?.trim()) { toast("Give the theme a name"); return; }
        thSaveCustom(thEdit, "Saved " + thEdit.name); thDirty = false; setTimeout(paintThemes, 200); return;
      }
      if (t.id === "thDel" && confirm(`Delete the theme "${thEdit.name}"?`)) {
        const id = thEdit.id;
        settings.customThemes = (settings.customThemes || []).filter((c) => c.id !== id);
        if (settings.theme === id) settings.theme = "default";
        save("Deleted " + thEdit.name); thEdit = clone(thFind(settings.theme || "default")); thDirty = false; thStopPreview(); paintThemes(); return;
      }
      if (t.id === "thReset" && confirm("Put Team colours back to the preset?")) {
        settings.customThemes = (settings.customThemes || []).filter((c) => c.id !== "team");
        save("Team colours reset"); thEdit = clone(themes.builtIns.find((b) => b.id === "team")); thDirty = false; paintThemes(); thSendPreview(); return;
      }
      if (t.id === "thUse") {
        if (thDirty && thCanEdit(thEdit)) thSaveCustom(thEdit, "");
        settings.theme = thEdit.id; thDirty = false; save("Theme on air: " + thEdit.name); setTimeout(paintThemes, 200);
      }
    });
  }
  // ---- Integrations (2026-11-05): everything Grandstand connects to, in one place ----
  let ints = null, hueRooms = null;
  const SD_KEYS = [
    ["onair", "ON AIR on / off", "Puts every graphic on or off air"],
    ["window", "Open Grandstand", "Brings this window to the front"],
    ["highlight", "Flag this moment", "A highlight marker (and an OBS chapter / clip when that's on)"],
    ["story:RaceSoFar", "Story card: Race so far", "Any story card: story:YourRace, story:FastestLaps, story:DriverOfDay …"],
    ["card:NAME", "One of your own cards", "Use the card's file name from the card builder"],
    ["toggle:tower", "Timing tower on / off", "Any graphic: toggle:sessionbar, toggle:featured, toggle:radio …"],
    ["racecontrol", "Race control test", "Plays the race control bar"],
    ["mark", "Mark a replay moment", "Producer desk: marks this moment for an instant replay"],
    ["replay-slow", "Replay the last moment (slow-mo)", "Producer desk: plays the newest marked moment in slow motion, then back to live (“replay” = normal speed)"],
    ["live", "Back to live", "Producer desk"],
    ["champ", "Live championship card", "Producer desk (also: pits, sectors, clear)"],
    ["results", "Flag, podium and results", "Plays the finish now"],
    ["clip", "Vertical clip", "The last 30 seconds as a 9:16 Short / TikTok / Reel (Integrations → Vertical clips)"],
    ["watch", "Drivers to watch card", "Who to keep an eye on and why (Garage 61 pace, grid, iRating)"],
  ];
  function intLight(ok, warn) { return `<span class="ilight ${ok ? "ok" : warn ? "warn" : ""}"></span>`; }
  function intTiles(st) {
    const t = (name, ok, warn, line, go) => `<div class="itile ${ok ? "ok" : warn ? "warn" : ""}">${intLight(ok, warn)}<div><b>${esc(name)}</b><small>${line}</small></div>${go || ""}</div>`;
    const g = st.garage61, d = st.discord, l = st.league;
    return `<div class="itiles">
      ${t("iRacing", st.iracing.connected, st.iracing.running, st.iracing.connected ? "Connected, live data flowing" : st.iracing.running ? "Running, waiting for a session" : "Not running")}
      ${t("Trading Paints", st.tradingPaints.installed, false, st.tradingPaints.installed ? (st.tradingPaints.running ? "Installed and running: real liveries on the cards" : "Installed (start it before the race for real liveries)") : "Not installed - some features need it (liveries on the grid and driver cards)", st.tradingPaints.installed ? "" : `<a class="mini" href="https://www.tradingpaints.com/install" target="_blank" rel="noopener">Get it</a>`)}
      ${t("OBS Studio", st.obs.running, false, st.obs.running ? `Running${st.obs.spout ? " · Spout2 output on" : " · Spout2 output off (Settings)"}${st.obs.director ? " · scene director on" : ""}` : "Not running")}
      ${t("Streamer.bot", st.streamerbot.running, false, st.streamerbot.running ? "Running: viewer cards, Super Chats and tips come in" : "Not running (needed for viewer cards)")}
      ${t("CrewChief", st.crewChief.on && st.crewChief.sharedMemory, st.crewChief.running, !st.crewChief.on ? "Off (Settings → CrewChief on the radio card)" : st.crewChief.sharedMemory ? "Connected: its calls show on the radio card" : st.crewChief.running ? "Running, but shared memory is off (CrewChief → Properties)" : "Not running")}
      ${t("SimHub", st.simhub.running, false, st.simhub.running ? (st.simhub.propertyServer ? "Running, property server on" : "Running (Grandstand reads iRacing directly, nothing needed)") : "Not running - not needed for iRacing")}
      ${t("Garage 61", g.key && g.drivingData, g.key, !g.key ? "No key yet - add one below" : g.drivingData ? "Connected: reference laps for your car and track" : esc(g.status || "Key saved - press Test"))}
      ${t("Discord", d.webhook, false, d.webhook ? `Posts ${[d.onGreen && "the green flag", d.onFlag && "the results"].filter(Boolean).join(" and ") || "nothing (both switched off)"}` : "Not set up")}
      ${t("League standings", (l.rows || []).length > 0, !!l.source, esc(l.status || "Not set up"))}
      ${t("YouTube", st.youtube.signedIn, st.youtube.client, st.youtube.signedIn ? (st.youtube.busy ? "Uploading…" : "Signed in: highlight reels upload by themselves") : st.youtube.client ? "Client added - sign in below" : "Not set up")}
      ${t("Hue lights", st.hue.paired && ints.settings.hueOn && !!ints.settings.hueRoom, st.hue.paired, st.hue.paired ? (ints.settings.hueOn ? (ints.settings.hueRoom ? "Following the flags" : "Pick a room below") : "Paired, switched off") : "Not set up")}
      ${t("Chat commands", ints.settings.chatOn !== false && st.streamerbot.running, ints.settings.chatOn !== false, ints.settings.chatOn === false ? "Off" : st.streamerbot.running ? "Ready (through Streamer.bot)" : "On - needs Streamer.bot running")}
    </div>`;
  }
  // 2026-11-06 (Angelo: "do the vertical clip stuff"): one button -> the last N seconds as a 9:16 clip
  let clipPrevAt = Date.now();   // the preview picture only refreshes when asked (the page repaints every 6 s)
  function clipPanel(s, st) {
    const c = st.clip || {};
    const ready = c.ffmpeg && c.obs && c.buffer;
    const need = !c.ffmpeg ? "ffmpeg isn't installed yet - press Get ffmpeg (once)" : !c.obs ? "Open OBS (its remote control must be on: Tools → WebSocket Server Settings)" : !c.buffer ? (s.clipKeepBuffer !== false ? "Starting OBS's Replay Buffer…" : "OBS's Replay Buffer is off - switch on “Keep the buffer running”") : "Ready: the last " + (s.clipSeconds || 30) + " seconds are always there";
    return `<div class="panel" style="--accent: var(--red); grid-column: 1 / -1"><h2>Vertical clips</h2>
      <p class="muted" style="margin:-6px 0 10px">One button turns the last ${esc(s.clipSeconds || 30)} seconds of your stream into a tall 9:16 video for <b>YouTube Shorts, TikTok and Instagram Reels</b>: the race picture across the middle (graphics and all) over a blurred copy of itself, with the track, lap and the moment above it and your name below. OBS keeps the last moments in its Replay Buffer; Grandstand sets that up for you. Clips go to <b>Videos\\Grandstand\\Clips</b>.</p>
      <div class="grid g2" style="gap:0 28px">
      <div>
        <div class="row"><span class="t">Clip it now<small id="clipStatus">${esc(c.status || need)}</small></span><button type="button" class="btn ${ready ? "gold" : ""} sm" id="clipNow" ${c.busy ? "disabled" : ""}>${c.busy ? "Making…" : "Clip it"}</button></div>
        <div class="row"><span class="t">Vertical clips</span>${sw("int:clipOn", s.clipOn !== false, "Vertical clips")}</div>
        <div class="row"><span class="t">Keep OBS's Replay Buffer running<small>While OBS is open. It keeps the last ~${esc((s.clipSeconds || 30) + 15)} s in memory (a few hundred MB)</small></span>${sw("int:clipKeepBuffer", s.clipKeepBuffer !== false, "Replay buffer")}</div>
        <div class="row"><span class="t">Clip length<small>Seconds (10 to 60 - Shorts are up to 60)</small></span><input class="in" type="number" min="10" max="60" id="clipSecs" value="${esc(s.clipSeconds || 30)}" style="width:80px"></div>
        <div class="row"><span class="t">Your name on the clip<small>e.g. @YourChannel - shown under the picture</small></span><input class="in" id="clipHandle" value="${esc(s.clipHandle || "")}" maxlength="40" placeholder="@YourChannel" style="width:200px"></div>
        <div class="row"><span class="t">Upload to YouTube as a Short<small>${st.youtube.signedIn ? "Uses your YouTube sign-in (above). Private until YouTube reviews the app" : "Sign in to YouTube above first"}</small></span>${sw("int:clipUpload", s.clipUpload === true, "Upload clips")}</div>
        <div class="row"><span class="t">Chat can make clips<small><b>!clip</b> in chat (Chat commands setup) - at most one a minute</small></span>${sw("int:clipChat", s.clipChat === true, "Chat clips")}</div>
        <div class="row"><span class="t">Keep OBS's full replay file too<small>Off: only the vertical clip is kept</small></span>${sw("int:clipKeepSource", s.clipKeepSource === true, "Keep the replay file")}</div>
        ${c.ffmpeg ? "" : `<div class="row"><span class="t">ffmpeg<small>The free video tool that makes the clip. Installs with Windows' own installer (winget)</small></span><button type="button" class="btn gold sm" id="clipFfmpeg">Get ffmpeg</button></div>`}
      </div>
      <div>
        <div class="row" style="align-items:flex-start"><span class="t">What a clip looks like<small>Made from what's on screen now. Your real clips show the stream as OBS sends it (the game picture and the graphics)</small><button type="button" class="mini" id="clipPrev" style="margin-top:8px">Refresh</button></span><img id="clipPrevImg" alt="Vertical clip preview" src="/api/clip/preview.png?t=${clipPrevAt}" style="width:150px;height:auto;border:1px solid var(--line2);border-radius:6px;background:#000"></div>
        <div class="row"><span class="t">Your clips<small>Newest first - click one to watch it</small></span><button type="button" class="mini" id="clipFolder">Open folder</button></div>
        ${(c.recent || []).length ? c.recent.map((f) => `<div class="row"><span class="t">${esc(f.name.replace(/^Clip /, "").replace(/\.mp4$/, ""))}<small>${esc(f.at)} · ${esc(f.mb)} MB</small></span><button type="button" class="mini" data-clipopen="${esc(f.name)}">Play</button></div>`).join("") : `<p class="muted" style="font-size:12px">No clips yet. Also on: the Producer page, Stream Deck <b>--do clip</b>.</p>`}
      </div></div></div>`;
  }
  function intBody() {
    if (!ints) return `<p class="muted">Reading…</p>`;
    const s = ints.settings, st = ints.status, g = st.garage61, exe = st.exe || "Grandstand.exe";
    return intTiles(st) + `<div class="grid g2" style="margin-top:18px">
      <div class="panel" style="--accent: var(--green)"><h2>Garage 61</h2>
        <p class="muted" style="margin:-6px 0 10px">Reference laps for the car and track you're on: the fastest lap you can see on Garage 61, who set it, your own best and the gap. Use them on your own cards (<b>G61.FastestLap</b>, <b>G61.GapToFastest</b> …). Garage 61 shows your laps and your teammates' laps.</p>
        <div class="row"><span class="t">Your Garage 61 key<small>${g.key ? (g.ownKey ? "Saved for Grandstand" : "Using the Race Engineer's key") + ". Paste a new one to replace it" : "Make one on garage61.net → Developer, tick the driving data permission, then paste it here"}</small></span>
          <div style="display:flex;gap:6px"><input class="in" type="password" id="g61key" placeholder="${g.key ? "••••••••••••" : "Paste your key"}" autocomplete="off" style="width:220px"><button type="button" class="btn gold" id="g61save">Save &amp; test</button></div></div>
        <div class="row"><span class="t">Status<small id="g61status">${esc(g.status || (g.key ? "Not tested yet" : "No key yet"))}</small></span><button type="button" class="mini" id="g61test" ${g.key ? "" : "disabled"}>Test</button></div>
        <div class="row"><span class="t">Use Garage 61 during races<small>Fetches once when a session starts, then every 15 minutes at most</small></span>${sw("int:garage61", s.garage61 !== false, "Garage 61")}</div>
        <div class="row"><span class="t">Make a key<small>Opens Garage 61's developer page</small></span><a class="mini" href="https://garage61.net/developer" target="_blank" rel="noopener">Open</a></div></div>
      <div class="panel" style="--accent: var(--lmp2)"><h2>Discord</h2>
        <p class="muted" style="margin:-6px 0 10px">Posts to your Discord channel by itself: "racing now" at the green flag (with your stream link) and the class podiums plus your finish at the chequered flag. Only real races, never the sample race or a replay.</p>
        <div class="row"><span class="t">Channel webhook<small>Discord: channel settings → Integrations → Webhooks → New webhook → Copy webhook URL</small></span><input class="in" type="password" id="dcHook" value="${esc(s.discordWebhook)}" placeholder="https://discord.com/api/webhooks/…" autocomplete="off" style="width:260px"></div>
        <div class="row"><span class="t">Stream link<small>Added to the green flag post (optional)</small></span><input class="in" id="dcLink" value="${esc(s.discordStreamLink)}" placeholder="https://youtube.com/@…" style="width:260px"></div>
        <div class="row"><span class="t">Post at the green flag</span>${sw("int:discordOnGreen", s.discordOnGreen !== false, "Green flag post")}</div>
        <div class="row"><span class="t">Post the results</span>${sw("int:discordOnFlag", s.discordOnFlag !== false, "Results post")}</div>
        <div class="row"><span class="t">Test<small id="dcStatus">Sends "Grandstand is connected" to the channel</small></span><button type="button" class="mini" id="dcTest" ${s.discordWebhook ? "" : "disabled"}>Send a test</button></div></div>
      <div class="panel" style="--accent: var(--gold)"><h2>League standings</h2>
        <p class="muted" style="margin:-6px 0 10px">Your championship table from a Google Sheet or a CSV file, with columns for <b>Driver</b> and <b>Points</b> (Pos, Car # and Class are used when they're there). On your own cards: <b>League.Position</b>, <b>League.Points</b>, <b>League.GapToLeader</b>; a Top 5 card on League.Points is the championship top 5.</p>
        <div class="row"><span class="t">League name<small>Shown in the Discord posts</small></span><input class="in" id="lgName" value="${esc(s.leagueName)}" maxlength="60" placeholder="Sunday GT League" style="width:260px"></div>
        <div class="row"><span class="t">Standings link or file<small>A Google Sheets link (Share → anyone with the link can view), a CSV link, or a file like C:\\League\\standings.csv</small></span><input class="in" id="lgSrc" value="${esc(s.leagueSource)}" placeholder="https://docs.google.com/spreadsheets/d/…" style="width:260px"></div>
        <div class="row"><span class="t">Status<small id="lgStatus">${esc(st.league.status)}</small></span><button type="button" class="mini" id="lgReload" ${s.leagueSource ? "" : "disabled"}>Reload</button></div>
        <div id="lgRows">${(st.league.rows || []).length ? `<table class="lgtable">${st.league.rows.map((r) => `<tr><td>P${esc(r.pos)}</td><td>${r.number ? "#" + esc(r.number) : ""}</td><td>${esc(r.name)}</td><td>${esc(r.points)}</td></tr>`).join("")}</table>` : ""}</div></div>
      <div class="panel" style="--accent: var(--gtp)"><h2>Stream Deck</h2>
        <p class="muted" style="margin:-6px 0 10px">Any key on a Stream Deck (or a button box, or a keyboard macro) can drive Grandstand. In the Stream Deck app drag <b>System → Open</b> onto a key and paste one of these. Copy puts it on your clipboard.</p>
        ${SD_KEYS.map(([cmd, name, hint]) => `<div class="row"><span class="t">${esc(name)}<small>${esc(hint)}</small></span><button type="button" class="mini" data-sdcopy="${esc(cmd)}">Copy</button></div>`).join("")}
        <p class="muted" style="margin:10px 0 0;font-size:12px">Each one is <code>"${esc(exe)}" --do</code> and the command.</p></div>
      <div class="panel" style="--accent: var(--red)"><h2>YouTube</h2>
        <p class="muted" style="margin:-6px 0 10px">When the highlight reel finishes recording, Grandstand uploads the video to your channel with a chapter for every moment, then posts the link to Discord. <b>Note:</b> YouTube keeps videos uploaded by an app it hasn't checked <b>private</b> (you can still watch them) until the app passes YouTube's free review (<a href="https://support.google.com/youtube/contact/yt_api_form" target="_blank" rel="noopener">request it here</a>). The Discord post only goes out for videos people can watch.</p>
        <div class="row"><span class="t">1 · Google client file<small>${st.youtube.client ? "Added. Add a new one to replace it" : "Once: console.cloud.google.com → new project → enable “YouTube Data API v3” → OAuth consent screen (External, publish it) → Credentials → Create OAuth client ID → Desktop app → Download JSON"}</small></span><label class="mini filebtn">Add file…<input type="file" accept=".json,application/json" id="ytClient"></label></div>
        <div class="row"><span class="t">2 · Sign in<small id="ytStatus">${esc(st.youtube.status || (st.youtube.signedIn ? "Signed in" : "Opens Google in your browser - you sign in there"))}</small></span><div style="display:flex;gap:6px">${st.youtube.signedIn ? `<button type="button" class="mini" id="ytOut">Sign out</button>` : ""}<button type="button" class="btn ${st.youtube.signedIn ? "" : "gold"} sm" id="ytIn" ${st.youtube.client ? "" : "disabled"}>${st.youtube.signedIn ? "Sign in again" : "Sign in with Google"}</button></div></div>
        <div class="row"><span class="t">Upload the highlight reel by itself</span>${sw("int:youTubeAuto", s.youTubeAuto !== false, "Upload to YouTube")}</div>
        <div class="row"><span class="t">Who can watch<small>What Grandstand asks YouTube for</small></span><select class="in" id="ytPrivacy" style="width:150px">${[["unlisted", "Unlisted"], ["public", "Public"], ["private", "Private"]].map(([v, n]) => `<option value="${v}"${(s.youTubePrivacy || "unlisted") === v ? " selected" : ""}>${n}</option>`).join("")}</select></div>
        <div class="row"><span class="t">Post the video to Discord</span>${sw("int:discordOnHighlights", s.discordOnHighlights !== false, "Highlights to Discord")}</div>
        ${st.youtube.last ? `<div class="row"><span class="t">Last video</span><a class="mini" href="${esc(st.youtube.last)}" target="_blank" rel="noopener">Open</a></div>` : ""}</div>
      <div class="panel" style="--accent: var(--amber, #e8a33a)"><h2>Hue lights</h2>
        <p class="muted" style="margin:-6px 0 10px">Your Philips Hue room lights follow the race: green at the start, amber under a full-course caution, red for a red flag, white on the last lap, flashing white at the chequered flag, a blue blink when you're being lapped. Between flags they go back to how you had them.</p>
        <div class="row"><span class="t">Bridge address<small>Find it, or type it (Hue app → Settings → Bridges → ⓘ)</small></span><div style="display:flex;gap:6px"><input class="in" id="hueIp" value="${esc(s.hueBridge)}" placeholder="192.168.1.20" style="width:150px"><button type="button" class="mini" id="hueFind">Find</button></div></div>
        <div class="row"><span class="t">Pair<small id="hueStatus">${esc(st.hue.status || (st.hue.paired ? "Paired" : "Press the round button on top of the bridge, then Pair"))}</small></span><button type="button" class="btn ${st.hue.paired ? "" : "gold"} sm" id="huePair" ${s.hueBridge ? "" : "disabled"}>${st.hue.paired ? "Pair again" : "Pair"}</button></div>
        <div class="row"><span class="t">Room</span><select class="in" id="hueRoom" style="width:190px" ${st.hue.paired ? "" : "disabled"}><option value="">${st.hue.paired ? "Pick a room…" : "Pair first"}</option>${(hueRooms || []).map((r) => `<option value="${esc(r.id)}"${s.hueRoom === r.id ? " selected" : ""}>${esc(r.name)} (${r.lights} lights)</option>`).join("")}</select></div>
        <div class="row"><span class="t">Brightness<small>For the flag colours</small></span><input class="in" type="number" min="30" max="254" step="1" id="hueBri" value="${esc(s.hueBrightness ?? 254)}" style="width:90px"></div>
        <div class="row"><span class="t">Follow the flags</span><button type="button" class="mini" id="hueTest" style="margin-right:10px" ${st.hue.paired && s.hueRoom ? "" : "disabled"}>Test</button>${sw("int:hueOn", s.hueOn === true, "Hue lights")}</div></div>
      <div class="panel" style="--accent: var(--gtp)"><h2>Chat commands</h2>
        <p class="muted" style="margin:-6px 0 10px">Viewers type a command in Twitch or YouTube chat and Grandstand answers from the live race. Streamer.bot passes the command on and sends the answer back.</p>
        <div class="row"><span class="t">Answer chat commands</span>${sw("int:chatOn", s.chatOn !== false, "Chat commands")}</div>
        <div class="row"><span class="t">Chat can play cards<small>!standings, !fastest and !weather also play the matching story card - at most one every 90 s</small></span>${sw("int:chatCards", s.chatCards === true, "Chat plays cards")}</div>
        <div class="row"><span class="t">Commands<small>${(st.chat || []).map((c) => `<b>!${esc(c.cmd)}</b> ${esc(c.what.toLowerCase())}`).join(" · ")}</small></span></div>
        <div class="row"><span class="t">Set it up in Streamer.bot (once)<small>Commands → Add: name “Grandstand”, commands = every line above (one per line), linked to a new action. In the action add <b>Core → Network → Fetch URL</b> with the address below, variable “reply”, then <b>Twitch/YouTube → Send Message</b> with <code>%reply%</code></small></span><button type="button" class="mini" id="chatCopy">Copy address</button></div></div>
      <div class="panel" style="--accent: var(--gold); grid-column: 1 / -1"><h2>Viewer bets</h2>
        <p class="muted" style="margin:-6px 0 10px">During a race Grandstand asks your viewers a yes / no question the telemetry settles, shows it on a card top right, takes bets, locks them and shows who called it. The questions: <b>Podium finish?</b> (before the green), <b>Next lap under 1:45.2?</b> (your best + 0.4 s), <b>Gain a place in the next 3 laps?</b>, <b>Pass #12 within 3 laps?</b> (when you're within a second of the car ahead). One at a time, never in the last laps, practice, replays or the sample race. A pit stop, a caution or an invalid lap cancels a lap bet and gives the points back.</p>
        <div class="grid g2" style="gap:0 28px">
        <div>
        <div class="row"><span class="t">Viewer bets<small>Switch on to start asking questions in races</small></span><button type="button" class="mini" id="betTest" style="margin-right:10px" title="A pretend bet runs through the card: open, locked, result">Test</button>${sw("int:betsOn", s.betsOn === true, "Viewer bets")}</div>
        <div class="row"><span class="t">Twitch channel points<small>A real Twitch Prediction for each question, through Streamer.bot (needs a Twitch affiliate or partner channel). Setup below</small></span>${sw("int:betsTwitch", s.betsTwitch === true, "Twitch predictions")}</div>
        <div class="row"><span class="t">Grandstand points<small>For YouTube (no channel points there) or any chat: <b>!bet 1 200</b>, <b>!bet 2 all</b>, <b>!points</b>, <b>!top</b>. Uses the Chat commands setup</small></span>${sw("int:betsOwnPoints", s.betsOwnPoints !== false, "Grandstand points")}</div>
        <div class="row"><span class="t">A question every<small>Minutes between bets</small></span><input class="in" type="number" min="2" max="60" id="betEvery" value="${esc(s.betsEveryMinutes ?? 6)}" style="width:80px"></div>
        <div class="row"><span class="t">Betting stays open<small>Seconds (Twitch's minimum is 30)</small></span><input class="in" type="number" min="30" max="120" id="betOpen" value="${esc(s.betsOpenSeconds ?? 45)}" style="width:80px"></div>
        <div class="row"><span class="t">Points a new viewer starts with</span><input class="in" type="number" min="100" max="100000" step="100" id="betStart" value="${esc(s.betsStartPoints ?? 1000)}" style="width:100px"></div>
        <div class="row"><span class="t">Now<small>${esc(st.bets?.status || "Nothing open")}${st.bets?.twitch ? " · " + esc(st.bets.twitch) : ""}</small></span></div>
        </div>
        <div>
        <div class="row"><span class="t">Twitch setup in Streamer.bot (once)<small>1 · <b>Servers/Clients → HTTP Server</b>: tick Auto Start, port ${esc(s.betsSbPort ?? 7474)}, Start.<br>2 · Make four actions, named exactly:<br><b>Grandstand Bet Open</b> → Core → C# → Execute C# Code, paste the code from Copy.<br><b>Grandstand Bet Lock</b> → Twitch → Predictions → Lock Last Prediction.<br><b>Grandstand Bet Resolve</b> → Twitch → Predictions → Resolve Last Prediction, Winning Index <code>%winner%</code>.<br><b>Grandstand Bet Cancel</b> → Twitch → Predictions → Cancel Last Prediction.</small></span><div style="display:flex;flex-direction:column;gap:6px"><button type="button" class="mini" id="betCode">Copy code</button><button type="button" class="mini" id="betTwitchTest">Test Twitch</button></div></div>
        <div class="row"><span class="t">Top bettors<small>${(st.betTop || []).length ? st.betTop.map((w, i) => `${i + 1}. ${esc(w.name)} ${Number(w.points).toLocaleString()}`).join(" · ") : "No bets yet"}</small></span><button type="button" class="mini" id="betReset">Reset points</button></div>
        </div></div></div>
      ${clipPanel(s, st)}
      <div class="panel" style="--accent: var(--lmp2)"><h2>Drivers to watch</h2>
        <p class="muted" style="margin:-6px 0 10px">Three drivers worth keeping an eye on, each with the reason - like the TV broadcasts do before the start. <b>Pace setter</b>: the fastest in your class. <b>The charger</b>: starts well behind where their pace says they should be. <b>Top rated</b>: the highest iRating left. Pace comes from Garage 61 (every driver's best lap in that car at this track), else qualifying, else this session's laps. You're never one of the picks - you have your own card.</p>
        <div class="row"><span class="t">In the pre-race cards<small>Plays right after the pole sitters</small></span>${sw("int:driversToWatch", s.driversToWatch !== false, "Drivers to watch")}</div>
        <div class="row"><span class="t">Pace data<small>${esc(st.watch?.g61 || st.watch?.source || "")}</small></span></div>
        <div class="row"><span class="t">Show it now<small>The on-demand card (also on the Producer page, Stream Deck <b>--do watch</b> and chat <b>!watch</b>)</small></span><button type="button" class="mini" id="watchShow">Show</button></div></div>
      <div class="panel" style="--accent: var(--gt3)"><h2>iRacing data service</h2>
        <p class="muted" style="margin:-6px 0 0">iRacing's own data service (iRating history, series standings, past results) needs a key from iRacing, and <b>iRacing has paused giving out new keys</b> while it reviews how outside apps use it. Grandstand already shows everything iRacing sends during a session (iRating, licence, team, car …). When iRacing opens keys again, this is ready to add.</p></div>
      <div class="panel" style="--accent: var(--gt3)"><h2>SimHub</h2>
        <p class="muted" style="margin:-6px 0 0">Not needed for iRacing: Grandstand reads iRacing directly, which is faster and gives more than SimHub passes on. SimHub matters for running Grandstand with other sims (Le Mans Ultimate, ACC …) - coming with other games.</p></div>
    </div>`;
  }
  SCREENS.integrations = () => head("Integrations", "Everything Grandstand connects to", "What's connected right now, and the outside services Grandstand can use: Garage 61 reference laps, Discord posts, your league's standings and Stream Deck keys.") +
    `<div id="intBody">${intBody()}</div>`;
  BIND.integrations = (host) => {
    const paint = () => { const b = $("#intBody", host); if (b && !host.contains(document.activeElement?.closest?.("input,select"))) b.innerHTML = intBody(); };
    const load = () => api.get("/api/integrations").then((r) => {
      ints = r; paint();
      if (r.status.hue.paired && r.settings.hueBridge && hueRooms === null) { hueRooms = []; api.get("/api/integrations/hue/rooms").then((x) => { hueRooms = x.rooms || []; paint(); }).catch(() => {}); }
    }).catch(() => {});
    load();
    const t = setInterval(load, 6000);
    leaveHooks.push(() => clearInterval(t));
    const saveInts = (msg) => api.post("/api/integrations", ints.settings).then(() => { if (msg) toast(msg); load(); }).catch(() => toast("Couldn't reach Grandstand"));
    host.addEventListener("change", (e) => {
      if (!ints) return;
      const k = e.target.dataset.k;
      if (k?.startsWith("int:")) { ints.settings[k.slice(4)] = e.target.checked; saveInts("Saved"); return; }
      const map = { dcHook: "discordWebhook", dcLink: "discordStreamLink", lgName: "leagueName", lgSrc: "leagueSource" };
      if (map[e.target.id]) { ints.settings[map[e.target.id]] = e.target.value.trim(); saveInts(e.target.id === "lgSrc" ? "Reading the standings…" : "Saved"); }
      // 2026-11-06: YouTube, Hue
      if (e.target.id === "ytPrivacy") { ints.settings.youTubePrivacy = e.target.value; saveInts("Saved"); }
      if (e.target.id === "hueIp") { ints.settings.hueBridge = e.target.value.trim(); hueRooms = null; saveInts("Saved"); }
      if (e.target.id === "hueRoom") { ints.settings.hueRoom = e.target.value; saveInts(e.target.value ? "Room picked" : "Saved"); }
      const nums = { betEvery: ["betsEveryMinutes", 2, 60], betOpen: ["betsOpenSeconds", 30, 120], betStart: ["betsStartPoints", 100, 100000] };
      if (nums[e.target.id]) { const [k2, lo, hi] = nums[e.target.id]; const v = Math.round(Number(e.target.value)); if (v >= lo && v <= hi) { ints.settings[k2] = v; saveInts("Saved"); } else toast(`${lo} to ${hi}`); }
      if (e.target.id === "clipSecs") { const v = Math.round(Number(e.target.value)); if (v >= 10 && v <= 60) { ints.settings.clipSeconds = v; saveInts("Saved"); } else toast("10 to 60 seconds"); }
      if (e.target.id === "clipHandle") { ints.settings.clipHandle = e.target.value.trim(); saveInts("Saved"); }
      if (e.target.id === "hueBri") { const v = Math.round(Number(e.target.value)); if (v >= 30 && v <= 254) { ints.settings.hueBrightness = v; saveInts("Saved"); } else toast("30 to 254"); }
      if (e.target.id === "ytClient" && e.target.files?.[0]) {
        readFile(e.target.files[0]).then((txt) => api.post("/api/integrations/youtube/client", txt)).then((r) => { toast(r.problem || "Google client added - now sign in"); load(); }).catch(() => toast("Couldn't read that file"));
      }
    });
    host.addEventListener("click", (e) => {
      const b = e.target.closest("button"); if (!b) return;
      if (b.id === "g61save") {
        const v = $("#g61key", host).value.trim();
        if (v.length < 10) { toast("Paste your Garage 61 key first"); return; }
        b.disabled = true; $("#g61status", host).textContent = "Checking with Garage 61…";
        api.post("/api/integrations/g61/key", { token: v }).then((r) => { $("#g61key", host).value = ""; toast(r.ok && r.drivingData ? "Garage 61 connected" : "Key saved - see the status"); load(); })
          .catch(() => toast("Couldn't reach Grandstand")).finally(() => { b.disabled = false; });
      }
      if (b.id === "g61test") { $("#g61status", host).textContent = "Checking with Garage 61…"; api.post("/api/integrations/g61/test").then(() => load()); }
      if (b.id === "dcTest") { $("#dcStatus", host).textContent = "Sending…"; api.post("/api/integrations/discord/test").then((r) => { $("#dcStatus", host).textContent = r.result; toast(r.result); }); }
      if (b.id === "ytIn") { api.post("/api/integrations/youtube/signin").then((r) => { toast(r.status); setTimeout(load, 1500); }); }
      if (b.id === "ytOut") { api.post("/api/integrations/youtube/signout").then(() => load()); }
      if (b.id === "hueFind") { $("#hueStatus", host).textContent = "Looking…"; api.post("/api/integrations/hue/discover").then((r) => { if (r.ips?.length && !ints.settings.hueBridge) { ints.settings.hueBridge = r.ips[0]; saveInts("Bridge found: " + r.ips[0]); } else load(); }); }
      if (b.id === "huePair") { $("#hueStatus", host).textContent = "Pairing…"; api.post("/api/integrations/hue/pair").then((r) => { toast(r.status); hueRooms = null; load(); }); }
      if (b.id === "hueTest") { api.post("/api/integrations/hue/test").then(() => toast("Lights: amber blink for 6 s")); }
      if (b.id === "betTest") { api.post("/api/integrations/bets/test").then(() => toast("Test bet: watch the top right of the Live view")); }
      if (b.id === "betTwitchTest") { api.post("/api/integrations/bets/twitchtest").then((r) => toast(r.result)); setTimeout(load, 2500); }
      if (b.id === "betReset" && confirm("Put every viewer back to the starting points?")) { api.post("/api/integrations/bets/reset").then(() => { toast("Points reset"); load(); }); }
      if (b.id === "betCode") {
        const code = `using System;\nusing System.Collections.Generic;\n\npublic class CPHInline\n{\n    public bool Execute()\n    {\n        // Grandstand viewer bets: opens a Twitch prediction (title, option1, option2, seconds come from Grandstand)\n        string title = args["title"].ToString();\n        var options = new List<string> { args["option1"].ToString(), args["option2"].ToString() };\n        int seconds = int.Parse(args["seconds"].ToString());\n        CPH.TwitchPredictionCreate(title, options, seconds);\n        return true;\n    }\n}\n`;
        (navigator.clipboard?.writeText(code) || Promise.reject()).then(() => toast("Copied the code for “Grandstand Bet Open”"), () => { window.prompt("Copy this:", code); });
      }
      if (b.id === "chatCopy") {
        const line = "http://127.0.0.1:8790/api/chat?cmd=%command%&user=%user%&input=%rawInput%";
        (navigator.clipboard?.writeText(line) || Promise.reject()).then(() => toast("Copied the address"), () => { window.prompt("Copy this:", line); });
      }
      // 2026-11-06: vertical clips + drivers to watch
      if (b.id === "clipNow") { b.disabled = true; api.post("/api/clip").then((r) => { toast(r.message); setTimeout(load, 1200); }).catch(() => toast("Couldn't reach Grandstand")); }
      if (b.id === "clipFfmpeg") { api.post("/api/clip/ffmpeg").then((r) => toast(r.message)); }
      if (b.id === "clipFolder") { api.post("/api/clip/folder"); }
      if (b.id === "clipPrev") { clipPrevAt = Date.now(); const im = $("#clipPrevImg", host); if (im) im.src = "/api/clip/preview.png?t=" + clipPrevAt; }
      if (b.dataset.clipopen) { api.post("/api/clip/open?name=" + encodeURIComponent(b.dataset.clipopen)); }
      if (b.id === "watchShow") { api.post("/api/desk/show?what=watch").then((r) => toast(r.message || r.result || "Drivers to watch on air")).catch(() => toast("Couldn't reach Grandstand")); }
      if (b.id === "lgReload") { $("#lgStatus", host).textContent = "Reading…"; api.post("/api/integrations/league/reload").then(() => load()); }
      if (b.dataset.sdcopy) {
        const line = `"${ints?.status?.exe || "Grandstand.exe"}" --do ${b.dataset.sdcopy}`;
        (navigator.clipboard?.writeText(line) || Promise.reject()).then(() => toast("Copied: --do " + b.dataset.sdcopy), () => { window.prompt("Copy this:", line); });
      }
    });
  };

  SCREENS.settings = () => head("Settings", "Make it yours", "") +
    `<div class="grid g2">
      <div class="panel" style="--accent: var(--gold)"><h2>Presets &amp; packs</h2>
        <p class="muted" style="margin:-6px 0 10px">A pack is your whole setup in one file — what's on, where it sits, sponsors and your own cards. Save one before you experiment; share it with your league.</p>
        <div style="display:flex;gap:10px;flex-wrap:wrap;align-items:flex-end"><label class="f" style="flex:1;min-width:180px">Save the current setup as<input id="packName" placeholder="my-league" maxlength="40"></label><button type="button" class="btn gold" id="savePack">Save pack</button></div>
        <div id="packList" style="margin-top:10px">${packs.length ? "" : `<p class="muted">No packs saved yet.</p>`}${packs.map((p) => `<div class="row"><span class="t">${esc(p)}</span><button type="button" class="btn" data-apply="${esc(p)}">Load</button><button type="button" class="btn" data-dl="${esc(p)}">Download</button><button type="button" class="mini" data-del="${esc(p)}">Delete</button></div>`).join("")}</div>
        <div style="display:flex;gap:10px;flex-wrap:wrap;margin-top:12px"><label class="btn filebtn">Load a pack file…<input type="file" accept=".json,application/json" id="importPack"></label><button type="button" class="btn" id="dlCurrent">Download current setup</button></div></div>
      <div class="panel" style="--accent: var(--red)"><h2>Start over</h2>
        <div class="row"><span class="t">Every graphic back to its place and size<small>Keeps what's switched on</small></span><button type="button" class="btn" id="resetLayout">Reset layout</button></div>
        <div class="row"><span class="t">Everything back to the factory setup<small>All graphics on, default places, Broadcast mode, default timing</small></span><button type="button" class="btn red" id="resetAll">Reset all</button></div>
        <div class="row"><span class="t">Sponsors back to the built-in partners<small>Event &amp; sponsors → Reset to defaults</small></span></div></div>
      <div class="panel" id="transPanel" style="--accent: var(--lmp2)"><h2>Class transitions</h2>
        <p class="muted" style="margin:-6px 0 10px">What covers the timing tower while it changes board (OVERALL → GTP → LMP2 → GT3).</p>
        <div id="transBody">${transBody()}</div></div>
      <div class="panel" style="--accent: var(--gt3)"><h2>Stream output</h2>
        <p class="muted" style="margin:-6px 0 10px">For OBS: Grandstand hands the finished broadcast straight to OBS on the graphics card, see-through parts and all. In OBS (with the free Spout2 plugin) add a <b>Spout2 Capture</b> source and pick <b>Grandstand</b>, on top of your game capture.</p>
        <div class="row"><span class="t">Send to OBS (Spout2)<small>${settings.spout ? "On: sender name \u201cGrandstand\u201d" : "Off: nothing is sent"}</small></span>${sw("spout", !!settings.spout, "Send to OBS")}</div>
        ${settings.spout ? `<div class="row"><span class="t">Stream only<small>${settings.streamOnly ? "On: viewers see the graphics, your own screen stays clean" : "Off: graphics on your screen and on the stream"}</small></span>${sw("streamOnly", !!settings.streamOnly, "Stream only")}</div>` : ""}</div>
      <div class="panel"><h2>Behaviour</h2>
        <div class="row"><span class="t">Hide when iRacing is loading or closed<small>Built in and always on</small></span><span class="live-tag on">ON</span></div>
        <div class="row"><span class="t">Driving and streaming on one PC<small>One click: light rendering, graphics only on the stream (sent to OBS by Spout2), rain on the lens off</small></span><button type="button" class="mini" data-singlepc="1">Set it up</button></div>
        <div class="row"><span class="t">Your own cards on air<small>Cards from the card builder (never over a story card)</small></span>${sw("customCards", settings.customCards !== false, "Your own cards")}</div>
        <div class="row"><span class="t">Settings file<small>Documents\\iRacing\\Grandstand\\control.json</small></span></div></div>
      <div class="panel" style="--accent: var(--lmp2)"><h2>Broadcast effects</h2>
        <!-- 2026-10-26 (#234 / #235) -->
        <div class="row"><span class="t">Rain on the lens<small>Water drops on trackside TV cameras while it rains (never onboard, cockpit or the blimp)</small></span>${sw("lensRain", settings.lensRain !== false, "Rain on the lens")}</div>
        <div class="row"><span class="t">Replay wipes<small>A quick REPLAY / LIVE wipe going into a replay and back to live, and a REPLAY tag while you watch behind live</small></span>${sw("replayWipes", settings.replayWipes !== false, "Replay wipes")}</div>
        <div class="row"><span class="t">Highlight markers<small>Writes down every big moment (green flag, your passes, battles, fastest laps, yellows, pit stops, the flag) with the time, the lap and the exact replay moment, plus YouTube chapters. Documents\iRacing\Grandstand\highlights</small></span>${sw("highlights", settings.highlights !== false, "Highlight markers")}</div>
        <div class="row"><span class="t">Highlights to OBS<small>Also marks each moment in OBS: a chapter in your recording (OBS 30.2+) and a saved clip when OBS's Replay Buffer is on. Uses OBS's own remote-control settings (Tools > WebSocket Server Settings) - nothing to type here</small></span>${sw("highlightsObs", settings.highlightsObs === true, "Highlights to OBS")}</div>
        <div class="row"><span class="t">OBS scene director<small>Switches OBS between the Racing, Battle, Story card, Onboard, Replay, Post-race and Starting soon scenes to match what's on screen (moves your face and pedal cams out of the way). Pick any other scene in OBS and it leaves OBS alone</small></span>${sw("obsDirector", settings.obsDirector !== false, "OBS scene director")}</div>
        <div class="row"><span class="t">Keep cams clear of graphics<small>When a graphic would cover your face cam or pedal cam in OBS, the cam slides to the nearest free spot (its gold frame with it) and goes back home 3 s after the spot is clear; with no free spot it is lifted over the graphics. Only cams that are switched on are moved - it never switches a cam on. <span id="camStatus"></span></small></span><button type="button" class="mini" id="camTest" style="margin-right:10px" title="Pretends a graphic covers the face cam for 6 s">Test</button>${sw("cams:keepClear", settings.cams?.keepClear !== false, "Keep cams clear")}</div>
        <div class="row"><span class="t">Race control as a top bar<small>Race control messages (the engineer app's race control lines, penalties, track limits) come down as a slim bar under the session bar - under the ticker in a battle, under the corner name on an onboard - instead of a card over the team radio</small></span><button type="button" class="mini" id="rcTest" style="margin-right:10px">Test</button>${sw("raceControlBar", settings.raceControlBar !== false, "Race control bar")}</div>
        <div class="row"><span class="t">CrewChief on the radio card<small>Use CrewChief instead of our race engineer? Its calls show on the radio card. In CrewChief: Properties > tick "Enable Shared Memory", then restart CrewChief</small></span>${sw("crewChiefRadio", settings.crewChiefRadio === true, "CrewChief radio")}</div>
        <div class="row"><span class="t">Include CrewChief's spotter<small>Adds the spotter's calls (car left, clear...) - there are a lot of them</small></span>${sw("crewChiefSpotter", settings.crewChiefSpotter === true, "CrewChief spotter")}</div>
        <div class="row"><span class="t">Telemetry widgets<small>Your own gauges, bars, numbers and traces for any car (speed, RPM, gear, steering, gaps; pedals, fuel and delta for your car). Set them up in Documents\\iRacing\\Grandstand\\telemetry_widgets.json - an example set is written the first time</small></span>${sw("telemetryWidgets", settings.telemetryWidgets === true, "Telemetry widgets")}</div>
        <div class="row"><span class="t">Loading splash on the TV<small>While iRacing is in its menus or loading, the TV shows what's coming up (track, session, classes) with a gold light lapping the circuit, then hands over to the game. TV / stream screen only, never your main monitor</small></span>${sw("loadingSplash", settings.loadingSplash !== false, "Loading splash")}</div>
        <div class="row"><span class="t">Session review on the TV<small>Between sessions (iRacing closed or in its menus) the TV plays your last session corner by corner: best vs typical, brake and throttle points, speed traces, and the three places with the most time to find. Picks up each new session by itself. TV / stream screen only</small></span>${sw("sessionReview", settings.sessionReview !== false, "Session review")}</div>
        <div class="row"><span class="t">No story cards in a battle while I drive<small>While you are in the car (not a replay) and a battle is on, story cards wait; anything older than a minute when the battle ends is dropped. Replays and spectating are unchanged</small></span>${sw("noCardsInBattleWhenDriving", settings.noCardsInBattleWhenDriving !== false, "No cards in battles while driving")}</div>
        <div class="row"><span class="t">Mini story cards in a battle (preview)<small>While the full cards wait for a battle to end, a small strip with the card's main fact shows in the right third under the battle map, at most one every 45 s</small></span>${sw("miniCardsInBattle", settings.miniCardsInBattle === true, "Mini cards in battles")}</div>
        <div class="row"><span class="t">Story card layout<small>Where the story cards sit. Mixed rotates the right-third layouts card by card (the Michelin pit tower and the GO FAST cards always take the tall column); Classic is the old right-half card</small></span><select class="in" id="cardLayout" style="width:190px">${CARD_LAYOUTS.map(([v, l]) => `<option value="${v}"${(settings.cardLayout || "mixed") === v ? " selected" : ""}>${l}</option>`).join("")}</select></div>
        <div class="row"><span class="t">Refined corner themes<small>A finer finish on the themed corner names (Dunlop bridge, PlayStation, Michelin, the maker wordmarks, the heritage line): softer two-layer shadows, crisper lettering, a lit rule, one capsule for the heritage line. Same look and layout otherwise</small></span>${sw("cornerRefined", settings.cornerRefined === true, "Refined corner themes")}</div></div>
      <div class="panel" id="viewerPanel" style="--accent: var(--gtp)"><h2>Viewers</h2>
        <!-- 2026-10-29 (#494) -->
        <p class="muted" style="margin:-6px 0 10px">Super Chats, tips, bits, members and subs sent by Streamer.bot / StreamElements on this PC (Grandstand never logs in to YouTube or Twitch). They wait their turn and never cover the start, a battle or the finish. Follows are never shown.</p>
        <div class="row"><span class="t">Viewer cards<small>Paid messages ride a strip under the session bar once (name, amount, message - 140 characters, word filter on); members and subs get a card. Your own blocked words: Documents\\iRacing\\Grandstand\\viewer_blocklist.txt</small></span>${sw("viewer:cards", vw().cards !== false, "Viewer cards")}</div>
        <div class="row"><span class="t">Chat comments<small>Ordinary chat messages ride the same strip - word filter and link filter on, one every 20 s at most, never over the start, a battle or the finish</small></span>${sw("viewer:comments", vw().comments === true, "Chat comments")}</div>
        <div class="row"><span class="t">Big amount<small>From this Super Chat / tip amount ($, € or £) a banner also plays on top of the timing tower</small></span><input class="in" type="number" min="1" step="1" id="vwBig" value="${esc(vw().bigAmount ?? 20)}" style="width:110px"></div>
        <div class="row"><span class="t">Big bits<small>From this many bits the banner plays too</small></span><input class="in" type="number" min="1" step="100" id="vwBits" value="${esc(vw().bigBits ?? 2000)}" style="width:110px"></div>
        <div class="row"><span class="t">Test<small>Plays a sample when the screen is clear (on track, no battle, not lap 1 or the finish). Samples never count towards the fund</small></span><div style="display:flex;gap:6px;flex-wrap:wrap;justify-content:flex-end">${VIEWER_TESTS.map(([k, l]) => `<button type="button" class="mini" data-vwtest="${k}">${l}</button>`).join("")}</div></div>
        <div class="row"><span class="t">Keep the latest message up<small>When a viewer card has finished, a small plate top right (beside the session bar) keeps the latest one up - name, amount, the start of the message - until the next one plays. Hidden at the start, in battles, onboards, replays and the finish like the cards. "New stream" clears it</small></span><button type="button" class="mini" id="latestTest" style="margin-right:10px">Test</button>${sw("viewer:keepLatest", vw().keepLatest !== false, "Keep the latest message up")}</div>
        <div class="row"><span class="t">Latest plate shows<small>Paid messages only (Super Chats, tips, bits, members, subs), or chat messages too</small></span><select class="in" id="vwLatestWhat" style="width:190px"><option value="all"${vw().latestPaidOnly ? "" : " selected"}>Paid + chat</option><option value="paid"${vw().latestPaidOnly ? " selected" : ""}>Paid only</option></select></div>
        <div class="row"><span class="t">Tier looks<small>Chat card, tier 3 card, tier 2 session-bar wrap and the tier 1 full-screen moment (instead of the strip and member card). Same rules: never over lap 1, a battle, an onboard, a replay or the finish</small></span>${sw("viewer:tierLooks", vw().tierLooks === true, "Tier looks")}</div>
        <div class="row"><span class="t">Tier 2 from ($)<small>Super Chats, tips and bits (100 bits = $1) from this amount wrap the session bar. Below it: the tier 3 card. Other currencies are compared at a rough rate (the amount is always shown as sent)</small></span><input class="in" type="number" min="1" step="1" id="vwT2" value="${esc(vw().tier2From ?? 10)}" style="width:110px"></div>
        <div class="row"><span class="t">Tier 1 from ($)<small>From this amount: the full-screen moment, then a thank-you band on the tower</small></span><input class="in" type="number" min="1" step="1" id="vwT1" value="${esc(vw().tier1From ?? 50)}" style="width:110px"></div>
        <div class="row"><span class="t">Tier stingers<small>Tier 3 plays the light lower-third cue, tiers 2 and 1 the Grandstand string swell - through the TV / stream only, never your headset. Chat cards are silent</small></span>${sw("viewer:tierSounds", vw().tierSounds !== false, "Tier stingers")}</div>
        <div class="row"><span class="t">Test the tiers<small>One sample look through the real pipeline (with Tier looks on). <b>Stream test</b> plays one of each in a row: chat, tier 3, gifted subs, tier 2, tier 1. Samples never go on the thank-you roll or the fund</small></span><div style="display:flex;gap:6px;flex-wrap:wrap;justify-content:flex-end">${TIER_TESTS.map(([k, l]) => `<button type="button" class="mini" data-vwtest="${k}">${l}</button>`).join("")}<button type="button" class="mini" data-vwtest="sequence" style="font-weight:700">Stream test</button></div></div>
        <div class="row"><span class="t">Thank-you roll after the race<small>After the post-race outro, the credits roll: everyone who chatted, joined, subscribed or supported this stream, then the supporters by tier</small></span>${sw("viewer:autoThanks", vw().autoThanks === true, "Thank-you roll")}</div>
        <div class="row"><span class="t">Thank-you roll<small>Play this stream's roll now, a sample with made-up names (to check the look), or stop it</small></span><div style="display:flex;gap:6px;flex-wrap:wrap;justify-content:flex-end"><button type="button" class="mini" data-vwroll="play">Play</button><button type="button" class="mini" data-vwroll="sample">Sample</button><button type="button" class="mini" data-vwroll="stop">Stop</button></div></div>
        <div class="row"><span class="t">Names this stream<small id="vwNames">${esc(namesText(viewerStatus))}</small></span><button type="button" class="mini" id="vwNewStream" title="Starts a fresh thank-you list (it also starts fresh by itself after 8 hours without a name)">New stream</button></div>
        <div class="row"><span class="t">Track fund goal bar<small>A slim goal bar under the session bar. Real Super Chats and tips in dollars add to it</small></span>${sw("viewer:fund", vw().fund === true, "Track fund")}</div>
        <div class="row"><span class="t">Goal<small>Shown on the bar</small></span><input class="in" id="vwGoal" maxlength="40" value="${esc(vw().fundGoal || "Track fund")}" style="width:220px"></div>
        <div class="row"><span class="t">Target ($)</span><input class="in" type="number" min="0" step="1" id="vwTarget" value="${esc(vw().fundTarget ?? 100)}" style="width:110px"></div>
        <div class="row"><span class="t">Raised<small id="vwCur">${viewerStatus ? esc(money(viewerStatus.fund?.current)) + " raised so far (fund.json)" : "Reading…"}</small></span><button type="button" class="mini" id="vwReset">Reset to $0</button></div></div>
      <div class="panel" style="grid-column: 1 / -1"><h2>Themes</h2><div class="row"><span class="t">Themes have their own page now<small>Pick a theme, set holiday themes and edit your own</small></span><a class="btn sm" href="#themes" data-goto="themes">Open Themes</a></div></div>
      <div class="panel" id="vpPanel" style="--accent: var(--gold); grid-column: 1 / -1"><h2>Visual review pack</h2>
        <!-- 2026-10-27: V1-V8, W8-W13. Everything new is OFF until you switch it on; Base / Elevated picks the design. -->
        <p class="muted" style="margin:-6px 0 10px">New broadcast graphics waiting for your OK. Each one is off until you switch it on; <b>Base</b> or <b>Elevated</b> picks the design; <b>Test</b> plays it now (switch it on first).</p>
        ${VP.map(([id, k, name, desc, styles]) => `<div class="row"><span class="t">${esc(id)} · ${esc(name)}<small>${esc(desc)}</small></span>${styles ? `<div class="seg" role="group" aria-label="${esc(name)} design" style="margin-right:10px"><button type="button" data-vpstyle="${id}" data-v="base" aria-pressed="${!vpElev(id)}">Base</button><button type="button" data-vpstyle="${id}" data-v="elevated" aria-pressed="${vpElev(id)}">Elevated</button></div>` : ""}${id !== "W9" && id !== "V2" ? `<button type="button" class="mini" data-vptest="${id}" style="margin-right:10px">Test</button>` : id === "V2" ? `<button type="button" class="mini" data-vptest="V2" style="margin-right:10px" title="Forces the night look on / back to following the sun">Night now</button>` : ""}${sw("vp:" + k, vpOn(k), name)}</div>`).join("")}
        <div class="row"><span class="t">W8 · Music bed under the break card<small>Plays your own break_bed.wav (Documents\\iRacing\\Grandstand\\audio) while the break card is up - TV only, replays only</small></span>${sw("vp:breakMusic", vpOn("breakMusic"), "Break music")}</div>
        <div class="row"><span class="t">W10 · Let the PiP cut the camera<small>Elevated PiP only: in replays / spectating (with the director off) the main camera cuts to the chasing car's cockpit</small></span>${sw("vp:pipCamera", vpOn("pipCamera"), "PiP camera cut")}</div></div>
      <div class="panel" style="--accent: var(--green)"><h2>Free forever</h2><p class="muted">No paid tier, nothing locked. If Grandstand makes your races better, you can help keep it going. Donations unlock nothing.</p>
        <div style="display:flex;gap:10px;flex-wrap:wrap"><a class="btn gold" style="display:inline-flex;align-items:center;text-decoration:none" href="https://ko-fi.com/instabilitycontrol" target="_blank" rel="noopener">Support on Ko-fi</a>
        <a class="btn" style="display:inline-flex;align-items:center;text-decoration:none;color:var(--ivory)" href="https://www.paypal.com/donate/?hosted_button_id=WMCFSMXNS8XYG" target="_blank" rel="noopener">Donate with PayPal</a>
        <a class="btn" style="display:inline-flex;align-items:center;text-decoration:none;color:var(--ivory)" href="https://github.com/instabilitycontrolracing/grandstand/issues/new/choose" target="_blank" rel="noopener">Report a problem</a></div></div>
    </div>`;
  function refreshPacks() { return api.get("/api/packs").then((r) => { packs = r.packs || []; if (screen === "settings") render(); }).catch(() => {}); }
  function afterPack(r, what) {
    if (r.settings) { settings = r.settings; settings.elements = settings.elements || {}; paintBar(); }
    sponsors = null;
    toast(what + (r.problems?.length ? ` (${r.problems.length} problem${r.problems.length > 1 ? "s" : ""}: ${r.problems[0]})` : ""));
    render();
  }
  BIND.settings = (host) => {
    if (!BIND.settings.loaded) { BIND.settings.loaded = true; refreshPacks(); }
    loadTrans();
    loadViewer();
    loadCams();   // #611
    host.addEventListener("click", (e) => { const g = e.target.closest("[data-goto]"); if (g) { e.preventDefault(); go(g.dataset.goto); } });   // 2026-11-05: themes moved to their own page
    host.addEventListener("change", (e) => {
      // 2026-10-29 (#494): viewer cards / fund / theme - read-modify-write the whole settings object (save posts it all)
      if (e.target.dataset.k?.startsWith("viewer:")) {
        const k = e.target.dataset.k.slice(7);
        settings.viewer = { ...vw(), [k]: e.target.checked };
        save(({ fund: "Track fund bar", comments: "Chat comments", tierLooks: "Tier looks", tierSounds: "Tier stingers", autoThanks: "Thank-you roll after the race", keepLatest: "Latest message plate" }[k] || "Viewer cards") + (e.target.checked ? " on" : " off"));
      }
      const num = (id, k, min) => { if (e.target.id !== id) return; const v = Number(e.target.value); if (!Number.isFinite(v) || v < min) { toast("Enter a number"); return; } settings.viewer = { ...vw(), [k]: v }; save("Saved"); };
      num("vwBig", "bigAmount", 1); num("vwBits", "bigBits", 1); num("vwTarget", "fundTarget", 0); num("vwT2", "tier2From", 1); num("vwT1", "tier1From", 1);   // #603
      if (e.target.id === "vwLatestWhat") { settings.viewer = { ...vw(), latestPaidOnly: e.target.value === "paid" }; save(e.target.value === "paid" ? "Latest plate: paid messages only" : "Latest plate: paid + chat"); }   // #608
      if (e.target.dataset.k === "cams:keepClear") { settings.cams = { ...(settings.cams || {}), keepClear: e.target.checked }; save(e.target.checked ? "Cams kept clear of the graphics" : "Cams stay where OBS has them"); }   // #611
      if (e.target.dataset.k === "raceControlBar") { settings.raceControlBar = e.target.checked; save(e.target.checked ? "Race control as a top bar" : "Race control back to the radio-column card"); }   // #619
      if (e.target.id === "vwGoal") { settings.viewer = { ...vw(), fundGoal: e.target.value.trim() || "Track fund" }; save("Goal saved"); }
      if (e.target.dataset.k === "streamOnly") { settings.streamOnly = e.target.checked; save(e.target.checked ? "Stream only: your screen stays clean" : "Graphics back on your screen"); render(); }
      if (e.target.dataset.k === "spout") { settings.spout = e.target.checked; save(e.target.checked ? "Sending to OBS (Spout2: Grandstand)" : "Stream output off"); render(); }
      if (e.target.dataset.k === "customCards") { settings.customCards = e.target.checked; save(e.target.checked ? "Your own cards can play" : "Your own cards stay off"); }
      if (e.target.dataset.k === "crewChiefRadio") { settings.crewChiefRadio = e.target.checked; save(e.target.checked ? "CrewChief on the radio card" : "CrewChief off the radio card"); }   // 2026-11-05 (#777)
      if (e.target.dataset.k === "telemetryWidgets") { settings.telemetryWidgets = e.target.checked; save(e.target.checked ? "Telemetry widgets on" : "Telemetry widgets off"); }   // 2026-11-05 (#778)
      if (e.target.dataset.k === "crewChiefSpotter") { settings.crewChiefSpotter = e.target.checked; save(e.target.checked ? "CrewChief's spotter included" : "CrewChief's spotter left out"); }   // #777
      if (e.target.dataset.k === "lensRain") { settings.lensRain = e.target.checked; save(e.target.checked ? "Rain on the lens on" : "Rain on the lens off"); }   // 2026-10-26 (#234)
      if (e.target.dataset.k === "highlights") { settings.highlights = e.target.checked; save(e.target.checked ? "Highlight markers on" : "Highlight markers off"); }   // 2026-10-28 (#487)
      if (e.target.dataset.k === "highlightsObs") { settings.highlightsObs = e.target.checked; save(e.target.checked ? "Highlights to OBS on" : "Highlights to OBS off"); }   // 2026-10-28 (#487)
      if (e.target.dataset.k === "obsDirector") { settings.obsDirector = e.target.checked; save(e.target.checked ? "OBS scene director on" : "OBS scene director off"); }   // 2026-10-28 (#488)
      if (e.target.dataset.k === "loadingSplash") { settings.loadingSplash = e.target.checked; save(e.target.checked ? "Loading splash on the TV on" : "Loading splash off"); }   // 2026-10-27
      if (e.target.id === "cardLayout") { settings.cardLayout = e.target.value; save("Story card layout: " + e.target.selectedOptions[0].textContent); }   // 2026-10-27 (#393)
      if (e.target.dataset.k === "miniCardsInBattle") { settings.miniCardsInBattle = e.target.checked; save(e.target.checked ? "Mini story cards on in battles (preview)" : "Mini story cards off"); }   // 2026-10-27 (#339)
      if (e.target.dataset.k === "noCardsInBattleWhenDriving") { settings.noCardsInBattleWhenDriving = e.target.checked; save(e.target.checked ? "Story cards held during battles while you drive" : "Story cards may play through battles again"); }   // 2026-10-27 (#338)
      if (e.target.dataset.k === "cornerRefined") { settings.cornerRefined = e.target.checked; save(e.target.checked ? "Refined corner themes on" : "Refined corner themes off"); }   // 2026-10-27 (#115 / #118)
      if (e.target.dataset.k === "sessionReview") { settings.sessionReview = e.target.checked; save(e.target.checked ? "Session review on the TV on" : "Session review off"); }   // 2026-10-27
      if (e.target.dataset.k?.startsWith("vp:")) {   // 2026-10-27 visual pack
        const k = e.target.dataset.k.slice(3);
        settings.visuals = { ...(settings.visuals || {}), [k]: e.target.checked };
        save((VP.find((x) => x[1] === k)?.[2] || k) + (e.target.checked ? " on" : " off"));
      }
      if (e.target.dataset.k === "replayWipes") { settings.replayWipes = e.target.checked; save(e.target.checked ? "Replay wipes on" : "Replay wipes off"); }   // 2026-10-26 (#235)
      if (e.target.id === "importPack" && e.target.files?.length) {
        readFile(e.target.files[0]).then((t) => api.post("/api/packs/import", t)).then((r) => afterPack(r, "Pack loaded")).catch((er) => toast("Not a Grandstand pack: " + er.message));
      }
    });
    host.addEventListener("click", (e) => {
      const t = e.target;
      const vs = t.closest("[data-vpstyle]");
      if (vs) {
        const id = vs.dataset.vpstyle, el = new Set((settings.visuals || {}).elevated || []);
        if (vs.dataset.v === "elevated") el.add(id); else el.delete(id);
        settings.visuals = { ...(settings.visuals || {}), elevated: [...el] };
        $$(`[data-vpstyle="${id}"]`, host).forEach((x) => x.setAttribute("aria-pressed", x === vs));
        save(id + (vs.dataset.v === "elevated" ? ": elevated design" : ": base design"));
        return;
      }
      if (t.dataset?.vwtest) { api.post("/api/viewer/test", { type: t.dataset.vwtest }).then((r) => toast(t.dataset.vwtest === "sequence" ? (r.onScreen ? "Stream test queued: chat, tier 3, gifted subs, tier 2, tier 1 - they play one after another when the screen is clear" : r.why) : r.onScreen ? "Sample queued: plays when the screen is clear" : r.why)).catch((er) => toast(er.message || "Couldn't reach Grandstand")); return; }
      if (t.dataset?.vwroll) { const a = t.dataset.vwroll; api.post("/api/viewer/thanks", { action: a }).then((r) => { viewerStatus = r; toast(a === "stop" ? "Thank-you roll stopped" : a === "sample" ? "Sample roll playing (made-up names)" : "Thank-you roll playing: " + namesText(r)); }).catch(() => toast("Couldn't reach Grandstand")); return; }   // #603
      if (t.id === "vwNewStream") { if (!confirm("Start a new thank-you list? The names gathered so far are forgotten.")) return; api.post("/api/viewer/names/clear", {}).then((r) => { viewerStatus = r; toast("New stream: the thank-you list starts empty"); loadViewer(); }).catch(() => toast("Couldn't reach Grandstand")); return; }   // #603
      if (t.id === "camTest") { api.post("/api/cams/test", {}).then((r) => { toast(r.result || "Testing"); setTimeout(loadCams, 1500); }).catch(() => toast("Couldn't reach Grandstand")); return; }   // #611
      if (t.id === "rcTest") { api.post("/api/test?card=rcbar", null).then(() => toast("Race control test: a spoken line, then a penalty")).catch(() => toast("Couldn't reach Grandstand")); return; }   // #619
      if (t.id === "latestTest") { api.post("/api/test?card=latestplate", null).then(() => toast("Latest plate: a sample for 9 s")).catch(() => toast("Couldn't reach Grandstand")); return; }   // #608
      if (t.id === "vwReset") { if (!confirm("Set the track fund back to $0?")) return; api.post("/api/viewer/fund", { current: 0 }).then(() => { toast("Track fund reset"); loadViewer(); }).catch(() => toast("Couldn't reach Grandstand")); return; }
      if (t.dataset?.vptest) { api.post("/api/test?card=" + encodeURIComponent("vp:" + t.dataset.vptest), null).then(() => toast("Playing " + t.dataset.vptest)).catch(() => toast("Couldn't reach Grandstand")); return; }
      const tb = t.closest("[data-trans]");
      if (tb) {
        settings.transitions = tb.dataset.trans;
        $$("[data-trans]", host).forEach((x) => x.setAttribute("aria-pressed", x === tb));
        const hint = $("#transHint"); if (hint) hint.textContent = TRANS.find((x) => x[0] === tb.dataset.trans)?.[2] || "";
        save("Class transitions: " + tb.textContent);
        setTimeout(loadTrans, 400);
        return;
      }
      if (t.dataset?.roller) {
        const cls = t.dataset.roller;
        api.post(`/api/roller?cls=${encodeURIComponent(cls)}&sec=3`, null).then((r) => {
          if (!r.started) { toast(r.msg || "Couldn't start the recording"); return; }
          toast(`Recording 3 s of the replay for ${cls.toUpperCase()}…`);
          let tries = 0;
          const poll = () => api.get("/api/roller").then((st) => {
            if (st.state === "done" && st.cls === cls) { toast(`${cls.toUpperCase()} roller saved: ${st.frames} frames${st.kept_old ? " (the old one is kept in _old_)" : ""}`); loadTrans(); return; }
            if (st.state === "error") { toast("Roller: " + (st.msg || "failed")); return; }
            if (++tries < 40) setTimeout(poll, 500);
          }).catch(() => {});
          setTimeout(poll, 1500);
        }).catch(() => toast("Couldn't reach Grandstand"));
        return;
      }
      if (t.id === "transRescan") { api.post("/api/transitions/rescan", null).then((r) => { trans = r; paintTrans(); toast("Rollers folder read again"); }).catch(() => toast("Couldn't reach Grandstand")); return; }
      if (t.id === "savePack") {
        const n = $("#packName").value.trim().replace(/\s+/g, "-");
        if (!/^[A-Za-z0-9_-]{1,40}$/.test(n)) { toast("Name it with letters, numbers, - or _"); return; }
        api.post("/api/packs/" + encodeURIComponent(n), {}, "PUT").then(() => { toast("Pack saved: " + n); refreshPacks(); }).catch((er) => toast(er.message));
      }
      if (t.dataset.apply) {
        if (!confirm(`Load "${t.dataset.apply}"? It replaces your current setup, sponsors and cards with the same names.`)) return;
        api.post(`/api/packs/${encodeURIComponent(t.dataset.apply)}/apply`, null).then((r) => afterPack(r, "Pack loaded: " + t.dataset.apply)).catch((er) => toast(er.message));
      }
      if (t.dataset.dl) api.get("/api/packs/" + encodeURIComponent(t.dataset.dl)).then((p) => download(t.dataset.dl + ".grandstand.json", p));
      if (t.dataset.del && confirm(`Delete the pack "${t.dataset.del}"?`)) api.post("/api/packs/" + encodeURIComponent(t.dataset.del), null, "DELETE").then(refreshPacks);
      if (t.id === "dlCurrent") api.get("/api/packs/current").then((p) => download("grandstand-setup.json", p));
      if (t.id === "resetLayout") { Object.keys(settings.elements).forEach((k) => { settings.elements[k] = { ...DEF_EL, enabled: settings.elements[k].enabled }; }); save("Every graphic back in its place"); }
      if (t.id === "resetAll" && confirm("Reset every Grandstand setting to the factory setup? (Save a pack first if you want to come back.)")) {
        settings = { ...settings, mode: "broadcast", onAir: true, elements: {}, storyCardSeconds: 15, storyGapMinutes: 3, performance: "auto", tempUnit: "F", director: false, directorReplays: true, directorField: false, customCards: true, transitions: "auto", spout: false, streamOnly: false, lensRain: true, replayWipes: true, highlights: true, highlightsObs: false, obsDirector: true, loadingSplash: true, sessionReview: true, cornerRefined: false, noCardsInBattleWhenDriving: true, miniCardsInBattle: false, cardLayout: "mixed", visuals: { lowerThirdSounds: true, breakCard: true, lensEffects: true, penaltyCard: true, purpleBeep: true, rev: 2, elevated: [] }, viewer: { cards: true, comments: false, bigAmount: 20, bigBits: 2000, fund: false, fundGoal: "Track fund", fundTarget: 100, tierLooks: false, tier2From: 10, tier1From: 50, autoThanks: false, tierSounds: true, keepLatest: true, latestPaidOnly: false }, raceControlBar: true, cams: { ...(settings.cams || {}), keepClear: true }, theme: "default", themeByDate: false, themeDates: { halloween: true, christmas: true, july4: true } };
        paintBar(); save("Back to the factory setup"); render();
      }
    });
  };

  // other screens / crew members may change settings: stay in sync (every 2 s)
  let pollN = 0;
  function syncSettings() {
    if (savePending) return;
    api.get("/api/settings").then((s) => {
      if (savePending || !settings || JSON.stringify(s) === JSON.stringify(settings)) return;
      const modeChanged = s.mode !== settings.mode, dirChanged = s.director !== settings.director;
      settings = s; settings.elements = settings.elements || {};
      window.GS.onSettingsSynced?.();
      paintBar();
      if (settings.mode !== "studio" && STUDIO_SCREENS.includes(screen)) go("onair");
      else if (modeChanged || ["show", "timing"].includes(screen) || (screen === "onair" && !dirChanged)) render();
      else if (dirChanged) paintOnAirLive();
    }).catch(() => {});
  }
  window.addEventListener("hashchange", () => { const h = location.hash.slice(1); if ((SCREENS[h] || STUDIO_SCREENS.includes(h)) && h !== screen) go(h, false); });

  // ---------------- polling (1 Hz, only while this page is open and visible) ----------------
  function pollDirector() { return api.get("/api/director").then((d) => { director = d; paintOnAirLive(); }).catch(() => {}); }
  function poll() {
    if (document.hidden) return;
    if (++pollN % 2 === 0) syncSettings();
    if (screen === "onair" && settings?.director) pollDirector();
    if (screen === "onair") pollReel();
    api.get("/api/state").then((s) => {
      state = s; paintStatus();
      if (screen === "onair") paintOnAirLive();
      if (screen === "show") $$("[data-live]").forEach((t) => t.classList.toggle("on", showing(t.dataset.live)));
      window.GS.onState?.();
    }).catch(() => { state = null; paintStatus(); });
  }

  // ---------------- shared with studio.js ----------------
  window.GS = {
    $, $$, esc, api, clone, toast, download, readFile, head, sw, save, render, go,
    ELEMENTS, LEAN, DEF_EL, SCREENS, BIND, leaveHooks, PARTNERS,
    get settings() { return settings; }, set settings(v) { settings = v; settings.elements = settings.elements || {}; },
    get state() { return state; }, get screen() { return screen; },
    showing, moved, studioReady: false,
  };

  // ---------------- start ----------------
  api.get("/api/settings").then((s) => {
    settings = s; settings.elements = settings.elements || {};
    paintBar();
    const start = (location.hash || "#onair").slice(1);
    screen = "";
    const ok = (SCREENS[start] || STUDIO_SCREENS.includes(start)) && (settings.mode === "studio" || !STUDIO_SCREENS.includes(start));
    if (settings.mode === "studio") loadStudio().catch(() => {});
    go(ok ? start : "onair", false);
    poll(); pollTimer = setInterval(poll, 1000);
    if (settings.director) pollDirector();
  }).catch(() => { $("#screen").innerHTML = head("Grandstand", "Not running", "Start Grandstand, then reload this page."); paintStatus(); });
})();
