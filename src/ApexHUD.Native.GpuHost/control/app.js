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
  const DEF_EL = { enabled: true, x: 0, y: 0, scale: 1 };
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
      return `<button type="button" class="big ${on && !lean ? "on" : ""} ${showing(e.id) ? "live" : ""}" data-big="${e.id}" aria-pressed="${on}" ${lean ? "disabled" : ""}>
        <span class="lt">LIVE</span><b>${esc(e.label)}</b><small>${esc(e.hint)}</small><span class="st">${st}</span></button>`;
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
      <div class="row"><span class="t">Show other drivers too<small>${settings?.directorField ? "Whole field: battles, passes and crashes anywhere" : "Off: stays on your car and only picks the camera (gearbox when someone's close behind, nose when you're closing on someone)"}</small></span>${sw("directorField", !!settings?.directorField, "Show other drivers")}</div>
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
        <button type="button" class="btn gold" id="reelStart" ${running || !list.length ? "disabled" : ""}>Start reel</button><button type="button" class="btn red" id="reelStop" ${running ? "" : "disabled"}>Stop</button></div>
      <div id="reelState">${reelStateHtml()}</div></div>`;
  }
  function paintReel() {
    const st = $("#reelState"); if (st) st.innerHTML = reelStateHtml();
    const running = !!reel?.running, has = !!reelSessions?.length;
    const a = $("#reelStart"), b = $("#reelStop"), sel = $("#reelSession");
    if (a) a.disabled = running || !has; if (b) b.disabled = !running; if (sel) sel.disabled = running || !has;
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
    return head("On air", "Your broadcast", "Tap a graphic to show or hide it — it changes on the broadcast straight away. Close this page any time: the broadcast keeps going at full speed.",
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
    host.addEventListener("click", (e) => {
      if (e.target.id === "reelStart") {
        const file = $("#reelSession")?.value || "";
        e.target.disabled = true;
        api.post("/api/reel/start", { file }).then((r) => { reel = r.reel; paintReel(); toast(r.started ? "Highlight reel started" : r.msg || "The reel couldn't start"); }).catch(() => { toast("Couldn't reach Grandstand"); paintReel(); });
        return;
      }
      if (e.target.id === "reelStop") { api.post("/api/reel/stop", null).then((r) => { reel = r.reel; paintReel(); toast("Highlight reel stopping"); }).catch(() => toast("Couldn't reach Grandstand")); return; }
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

  // ---- Show ----
  SCREENS.show = () => {
    const grp = (g, title, sub) => `<div class="panel"><h2>${title}</h2><p class="muted" style="margin:-6px 0 6px">${sub}</p>` +
      ELEMENTS.filter((e) => e.group === g).map((e) => {
        return `<div class="row"><span class="t">${esc(e.label)}<small>${leanOff(e.id) ? "Off in Lean mode" : esc(e.hint)}</small></span>${moved(settings.elements[e.id]) ? `<button type="button" class="mini" data-reset="${e.id}" title="Put it back where it started">Reset</button>` : ""}<span class="live-tag ${showing(e.id) ? "on" : ""}" data-live="${e.id}">LIVE</span>${sw(e.id, el(e.id).enabled, e.label)}</div>`;
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
      const r = e.target.closest("[data-reset]"); if (r) { settings.elements[r.dataset.reset] = { ...DEF_EL, enabled: el(r.dataset.reset).enabled }; save("Back to its default place and size"); render(); }
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
    return `<div class="kicker" style="margin:0 0 10px">Built in</div>
      <div class="dials">${(dials.builtIns || []).map((d) => dialPick(d, false)).join("")}</div>
      <div class="kicker" style="margin:18px 0 10px">Your dials</div>
      ${custom.length ? `<div class="dials">${custom.map((d) => dialPick(d, true)).join("")}</div>`
        : `<p class="muted" style="margin:0">No pictures in your dials folder yet. Drop a square-ish PNG, JPG or WEBP there and press Refresh.</p>`}
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
      <h3 style="margin:18px 0 6px">Theme editor</h3>
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
  function paintThemes() { const b = $("#themeBody"); if (b) b.innerHTML = themeBody(); }
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
      <div class="panel" id="themePanel" style="--accent: var(--amber); grid-column: 1 / -1"><h2>Themes</h2>
        <!-- 2026-10-30 (#601) -->
        <div id="themeBody">${themeBody()}</div></div>
      <div class="panel" id="vpPanel" style="--accent: var(--gold); grid-column: 1 / -1"><h2>Visual review pack</h2>
        <!-- 2026-10-27: V1-V8, W8-W13. Everything new is OFF until you switch it on; Base / Elevated picks the design. -->
        <p class="muted" style="margin:-6px 0 10px">New broadcast graphics waiting for your OK. Each one is off until you switch it on; <b>Base</b> or <b>Elevated</b> picks the design; <b>Test</b> plays it now (switch it on first).</p>
        ${VP.map(([id, k, name, desc, styles]) => `<div class="row"><span class="t">${esc(id)} · ${esc(name)}<small>${esc(desc)}</small></span>${styles ? `<div class="seg" role="group" aria-label="${esc(name)} design" style="margin-right:10px"><button type="button" data-vpstyle="${id}" data-v="base" aria-pressed="${!vpElev(id)}">Base</button><button type="button" data-vpstyle="${id}" data-v="elevated" aria-pressed="${vpElev(id)}">Elevated</button></div>` : ""}${id !== "W9" && id !== "V2" ? `<button type="button" class="mini" data-vptest="${id}" style="margin-right:10px">Test</button>` : id === "V2" ? `<button type="button" class="mini" data-vptest="V2" style="margin-right:10px" title="Forces the night look on / back to following the sun">Night now</button>` : ""}${sw("vp:" + k, vpOn(k), name)}</div>`).join("")}
        <div class="row"><span class="t">W8 · Music bed under the break card<small>Plays your own break_bed.wav (Documents\\iRacing\\Grandstand\\audio) while the break card is up - TV only, replays only</small></span>${sw("vp:breakMusic", vpOn("breakMusic"), "Break music")}</div>
        <div class="row"><span class="t">W10 · Let the PiP cut the camera<small>Elevated PiP only: in replays / spectating (with the director off) the main camera cuts to the chasing car's cockpit</small></span>${sw("vp:pipCamera", vpOn("pipCamera"), "PiP camera cut")}</div></div>
      <div class="panel" style="--accent: var(--green)"><h2>Free forever</h2><p class="muted">No paid tier, nothing locked. If Grandstand makes your races better, you can help keep it going. Donations unlock nothing.</p>
        <div style="display:flex;gap:10px;flex-wrap:wrap"><a class="btn gold" style="display:inline-flex;align-items:center;text-decoration:none" href="https://ko-fi.com/instabilitycontrol" target="_blank" rel="noopener">Support on Ko-fi</a>
        <a class="btn" style="display:inline-flex;align-items:center;text-decoration:none;color:var(--ivory)" href="https://www.paypal.com/donate/?hosted_button_id=WMCFSMXNS8XYG" target="_blank" rel="noopener">Donate with PayPal</a>
        <a class="btn" style="display:inline-flex;align-items:center;text-decoration:none;color:var(--ivory)" href="https://github.com/instabilitycontrol/grandstand/issues/new/choose" target="_blank" rel="noopener">Report a problem</a></div></div>
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
    bindThemes(host);   // 2026-10-30 (#601)
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
