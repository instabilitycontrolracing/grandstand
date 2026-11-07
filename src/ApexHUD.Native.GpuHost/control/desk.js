/* Grandstand Control - PRODUCER DESK (2026-11-06) and the app's health banners (updates, crash reports, first-run setup).
   Registers SCREENS.desk / BIND.desk with app.js through window.GS. */
(() => {
  "use strict";
  const G = window.GS;
  const { $, $$, esc, api, toast, head, sw } = G;
  let desk = null;

  const opts = (sel) => (desk?.cars || []).map((c) => `<option value="${c.idx}"${String(sel) === String(c.idx) ? " selected" : ""}>${esc(c.label)}</option>`).join("");
  const pick = { a: "", b: "", focus: "", pen: "" };

  function body() {
    if (!desk) return `<p class="muted">Reading…</p>`;
    const s = desk.settings, noCars = !(desk.cars || []).length;
    const carSel = (id, key) => `<select class="in" id="${id}" data-pick="${key}" style="width:100%">${noCars ? `<option value="">No cars - iRacing isn't in a session</option>` : `<option value="">Pick a car…</option>` + opts(pick[key])}</select>`;
    const marks = desk.marks || [];
    return `<div class="grid g2">
      <div class="panel" style="--accent: var(--red); grid-row: span 2"><h2>Instant replay</h2>
        <p class="muted" style="margin:-6px 0 10px">Mark a moment the second it happens, then play it whenever you like: from 6 s before, on a TV camera, at normal speed or in slow motion, then straight back to live. Grandstand marks the big moments by itself too. Only while you're spectating or watching a replay - never while you drive.</p>
        <div style="display:flex;gap:8px;margin-bottom:10px"><input class="in" id="markLabel" maxlength="60" placeholder="What happened (optional)" style="flex:1"><button type="button" class="btn red" id="markNow">Mark now</button><button type="button" class="btn" id="goLive">Back to live</button><button type="button" class="btn" id="deskClip" title="The last 30 seconds of the stream as a 9:16 Short / TikTok (Integrations → Vertical clips)">Vertical clip</button></div>
        <p class="muted" style="font-size:12px;margin:0 0 8px">${esc(desk.replay || "")}</p>
        <div class="desk-marks">${marks.length ? marks.map((m) => `<div class="row"><span class="t">${esc(m.label)}<small>Lap ${m.lap} · ${esc(m.clock)} · ${esc(m.kind)}</small></span><div style="display:flex;gap:6px"><button type="button" class="mini" data-play="${m.i}">Play</button><button type="button" class="mini" data-play="${m.i}" data-slow="1">Slow-mo</button></div></div>`).join("") : `<p class="muted">No moments yet this session.</p>`}</div></div>
      <div class="panel" style="--accent: var(--gold)"><h2>On air now</h2>
        <div class="lvchips" style="margin-bottom:12px"><button type="button" class="chip gold" data-show="champ" ${desk.league ? "" : "disabled title=\"Set up League standings (Integrations)\""}>Live championship</button><button type="button" class="chip" data-show="pits">Pit stops</button><button type="button" class="chip" data-show="sectors">Fastest sectors</button><button type="button" class="chip" data-show="watch" title="Three drivers to keep an eye on, and why (Garage 61 pace, grid, iRating)">Drivers to watch</button><button type="button" class="chip" data-show="clear">Clear</button></div>
        <div class="kicker" style="margin:4px 0 6px">Two drivers</div>
        <div class="grid g2" style="gap:8px">${carSel("carA", "a")}${carSel("carB", "b")}</div>
        <div class="lvchips" style="margin:8px 0 12px"><button type="button" class="chip" data-show="h2h">Head to head</button><button type="button" class="chip" data-show="gap">Gap graph (last 10 laps)</button></div>
        <div class="kicker" style="margin:4px 0 6px">Driver focus</div>
        <div style="display:flex;gap:8px">${carSel("carF", "focus")}<button type="button" class="btn sm" data-show="focus" title="Name tag with the driver's details, and the camera on them when you're spectating">Show</button></div>
        <div class="kicker" style="margin:14px 0 6px">Name tags</div>
        <div class="lvchips">${(s.commentators || []).length ? s.commentators.map((c, i) => `<button type="button" class="chip" data-tag="${i}">${esc(c.name)}<small style="opacity:.6;margin-left:6px">${esc(c.role)}</small></button>`).join("") : `<span class="muted" style="font-size:12px">Add your commentators below.</span>`}</div></div>
      <div class="panel" style="--accent: var(--amber, #e8a33a)"><h2>Race control (stewards)</h2>
        <p class="muted" style="margin:-6px 0 10px">A steward's decision goes out on the race-control bar under the session bar.</p>
        ${carSel("carP", "pen")}
        <div style="display:flex;gap:8px;margin-top:8px"><select class="in" id="penKind" style="width:170px">${[["penalty", "Penalty"], ["warning", "Warning"], ["investigation", "Under investigation"], ["noted", "Incident noted"], ["cleared", "No further action"], ["limits", "Track limits"], ["dq", "Disqualified"]].map(([v, l]) => `<option value="${v}">${l}</option>`).join("")}</select><input class="in" id="penText" maxlength="70" placeholder="e.g. DRIVE THROUGH - CAUSING A COLLISION" style="flex:1"></div>
        <button type="button" class="btn gold" id="penSend" style="margin-top:8px">Send to race control</button></div>
      <div class="panel" style="--accent: var(--gtp)"><h2>Commentator page</h2>
        <p class="muted" style="margin:-6px 0 10px">A screen for the commentators: every car with gaps, intervals, last and best laps, pit stops, stint length, iRating, places gained, who's catching whom, and storylines worth talking about. Open it on a second monitor${s.lanOn ? " or on their laptop (link below)" : ""}.</p>
        <div style="display:flex;gap:8px;flex-wrap:wrap"><a class="btn gold" href="/booth.html" target="_blank" rel="noopener" style="text-decoration:none;display:inline-flex;align-items:center">Open the commentator page</a></div></div>
      <div class="panel" style="--accent: var(--gt3)"><h2>Commentators</h2>
        <div id="cmList">${(s.commentators || []).map((c, i) => `<div style="display:flex;gap:8px;margin-bottom:6px"><input class="in" data-cm="${i}" data-f="name" value="${esc(c.name)}" placeholder="Name" style="flex:1"><input class="in" data-cm="${i}" data-f="role" value="${esc(c.role)}" placeholder="Role" style="width:150px"><button type="button" class="mini" data-cmdel="${i}">Remove</button></div>`).join("")}</div>
        <button type="button" class="mini" id="cmAdd">Add a commentator</button></div>
      <div class="panel" style="--accent: var(--gold)"><h2>League</h2>
        <div class="row"><span class="t">Roster<small>A Google Sheet or CSV with <b>iRacing name</b>, <b>Display name</b>, <b>Team</b>, <b>Country</b>, <b>Number</b>. Display names replace iRacing names on every graphic. ${esc(desk.roster || "")}</small></span><div style="display:flex;gap:6px"><input class="in" id="rosterSrc" value="${esc(s.rosterSource)}" placeholder="Sheet link or file" style="width:200px"><button type="button" class="mini" id="rosterReload">Reload</button></div></div>
        <div class="row"><span class="t">Points per class position<small>For the live championship (League standings on the Integrations page)</small></span><input class="in" id="ptsTable" value="${esc(s.pointsTable)}" style="width:260px"></div>
        <div class="row"><span class="t">Championship card after the flag<small>Before / after standings, 25 s after the finish</small></span>${sw("desk:autoChampionshipAtFlag", s.autoChampionshipAtFlag !== false, "Championship at the flag")}</div>
        <div class="row"><span class="t">Driver change lower third<small>Endurance / team races: who takes over the car</small></span>${sw("desk:autoDriverChange", s.autoDriverChange !== false, "Driver changes")}</div>
        <div class="row"><span class="t">Producer cards stay up<small>Seconds</small></span><input class="in" type="number" min="5" max="60" id="cardSecs" value="${esc(s.cardSeconds ?? 12)}" style="width:80px"></div></div>
      <div class="panel" style="--accent: var(--lmp2)"><h2>Remote control</h2>
        <p class="muted" style="margin:-6px 0 10px">Run Grandstand from a laptop, tablet or phone on your home network - the whole control app and the commentator page. Every device needs the PIN (it's in the link). Takes effect when Grandstand restarts; Windows may ask once to allow it through the firewall.</p>
        <div class="row"><span class="t">Phones and laptops on my network</span>${sw("desk:lanOn", s.lanOn === true, "Remote control")}</div>
        <div class="row"><span class="t">PIN<small>Change it any time</small></span><input class="in" id="lanPin" value="${esc(s.lanPin)}" maxlength="12" style="width:110px"></div>
        ${s.lanOn ? (desk.lanUrls || []).map((u) => `<div class="row"><span class="t"><code>${esc(u)}</code></span><button type="button" class="mini" data-copy="${esc(u)}">Copy</button></div>`).join("") : ""}</div>
    </div>`;
  }

  G.SCREENS.desk = () => head("Producer", "The producer's desk", "Instant replays, graphics on demand, stewards' decisions and the commentator page - for running a league broadcast.") + `<div id="deskBody">${body()}</div>`;
  G.BIND.desk = (host) => {
    const paint = () => { const b = $("#deskBody", host); if (b && !host.contains(document.activeElement?.closest?.("input,select"))) b.innerHTML = body(); };
    const load = () => api.get("/api/desk").then((r) => { desk = r; paint(); }).catch(() => {});
    load();
    const t = setInterval(load, 4000);
    G.leaveHooks.push(() => clearInterval(t));
    const saveDesk = (msg) => api.post("/api/desk", desk.settings).then(() => { if (msg) toast(msg); load(); }).catch(() => toast("Couldn't reach Grandstand"));
    const post = (url) => api.post(url).then((r) => { toast(r.result || "Done"); setTimeout(load, 600); }).catch(() => toast("Couldn't reach Grandstand"));
    host.addEventListener("change", (e) => {
      if (!desk) return;
      const t2 = e.target;
      if (t2.dataset.pick) { pick[t2.dataset.pick] = t2.value; return; }
      if (t2.dataset.k?.startsWith("desk:")) { desk.settings[t2.dataset.k.slice(5)] = t2.checked; saveDesk(t2.dataset.k === "desk:lanOn" ? "Saved - restart Grandstand to switch remote control " + (t2.checked ? "on" : "off") : "Saved"); return; }
      if (t2.dataset.cm != null) { desk.settings.commentators[+t2.dataset.cm][t2.dataset.f] = t2.value; saveDesk("Saved"); return; }
      if (t2.id === "rosterSrc") { desk.settings.rosterSource = t2.value.trim(); saveDesk("Reading the roster…"); }
      if (t2.id === "ptsTable") { desk.settings.pointsTable = t2.value.trim(); saveDesk("Saved"); }
      if (t2.id === "lanPin") { desk.settings.lanPin = t2.value.trim(); saveDesk("PIN saved"); }
      if (t2.id === "cardSecs") { const v = Math.round(+t2.value); if (v >= 5 && v <= 60) { desk.settings.cardSeconds = v; saveDesk("Saved"); } }
    });
    host.addEventListener("click", (e) => {
      const b = e.target.closest("button"); if (!b || !desk) return;
      if (b.id === "markNow") { post("/api/desk/mark?label=" + encodeURIComponent($("#markLabel", host).value.trim())); $("#markLabel", host).value = ""; }
      if (b.id === "goLive") post("/api/desk/live");
      if (b.id === "deskClip") api.post("/api/clip?by=producer").then((r) => toast(r.message)).catch(() => toast("Couldn't reach Grandstand"));   // 2026-11-06
      if (b.dataset.play != null) post(`/api/desk/replay?i=${b.dataset.play}${b.dataset.slow ? "&slow=1" : ""}`);
      if (b.dataset.show) {
        const w = b.dataset.show;
        const a = w === "focus" ? pick.focus : pick.a;
        post(`/api/desk/show?what=${w}&a=${a || -1}&b=${pick.b || -1}`);
      }
      if (b.dataset.tag != null) post(`/api/desk/show?what=tag&i=${b.dataset.tag}`);
      if (b.id === "penSend") post(`/api/desk/penalty?car=${pick.pen || -1}&kind=${$("#penKind", host).value}&text=${encodeURIComponent($("#penText", host).value)}`);
      if (b.id === "rosterReload") post("/api/desk/roster");
      if (b.id === "cmAdd") { desk.settings.commentators = [...(desk.settings.commentators || []), { name: "New commentator", role: "Commentator" }]; saveDesk("Added - type the name"); }
      if (b.dataset.cmdel != null) { desk.settings.commentators.splice(+b.dataset.cmdel, 1); saveDesk("Removed"); }
      if (b.dataset.copy) (navigator.clipboard?.writeText(b.dataset.copy) || Promise.reject()).then(() => toast("Copied"), () => window.prompt("Copy this:", b.dataset.copy));
    });
  };

  // ---------------- health: update banner, crash report, first-run setup ----------------
  function banner(html, cls) {
    let el = $("#gsBanner");
    if (!el) { el = document.createElement("div"); el.id = "gsBanner"; document.body.appendChild(el); }
    el.className = "gs-banner " + (cls || ""); el.innerHTML = html;
  }
  function wizard(h) {
    const m = document.createElement("div");
    m.className = "gs-wizard";
    m.innerHTML = `<div class="gs-wbox"><div class="kicker">Welcome to Grandstand</div><h2>Let's check your setup</h2>
      <p class="muted">Grandstand works on its own; these make it better. Green = found on this PC.</p>
      ${h.checks.map((c) => `<div class="row"><span class="t"><span class="ilight ${c.ok ? "ok" : ""}" style="display:inline-block;margin-right:10px"></span>${esc(c.name)}<small>${esc(c.tip)}</small></span>${c.ok ? "" : `<a class="mini" href="${esc(c.link)}" target="_blank" rel="noopener">Get it</a>`}</div>`).join("")}
      <div class="row"><span class="t">Driving and streaming on one PC?<small>One click: light mode, graphics only on the stream, sent to OBS</small></span><button type="button" class="mini" data-singlepc="1">Set it up</button></div>
      <div style="display:flex;justify-content:flex-end;gap:8px;margin-top:14px"><button type="button" class="btn gold" id="wizDone">Done</button></div></div>`;
    document.body.appendChild(m);
    m.addEventListener("click", (e) => { if (e.target.id === "wizDone") { api.post("/api/health/wizard").catch(() => {}); m.remove(); } });
  }
  function health() {
    api.get("/api/health").then((h) => {
      const ver = $("#ver"); if (ver) ver.textContent = h.version;
      if (h.crash) banner(`Grandstand closed unexpectedly last time. <a href="${esc(h.crashIssue)}" target="_blank" rel="noopener">Report it</a> (opens GitHub with the details filled in - you press Submit) <button type="button" class="mini" id="crashOk">Dismiss</button>`, "warn");
      else if (h.update) banner(`Grandstand ${esc(h.update.version)} is out (you have ${esc(h.version)}). <a href="${esc(h.update.url)}" target="_blank" rel="noopener">See what's new and download</a> <button type="button" class="mini" id="crashOk">Later</button>`);
      if (h.wizard && !h.crash) wizard(h);
    }).catch(() => {});
  }
  document.addEventListener("click", (e) => {
    if (e.target.id === "crashOk") { $("#gsBanner")?.remove(); api.post("/api/health/wizard").catch(() => {}); }
    const sp = e.target.closest("[data-singlepc]");
    if (sp && G.settings) {
      // 2026-11-06: driving and streaming on one PC - light rendering, graphics only to OBS (Spout2), rain on the lens off
      G.settings = { ...G.settings, performance: "light", spout: true, streamOnly: true, lensRain: false };
      G.save("Single-PC setup: light mode, graphics to OBS only");
    }
  });
  setTimeout(health, 1500);

  // ---------------- 2026-11-06 (Angelo: "the watch face should also be at the top of the app"): the broadcast watch,
  // live, in the header - the same face as the session bar (your chosen dial), real hands; the digital clock when the
  // watch is switched off. Click it for the clock settings.
  (function headerWatch() {
    const bar = document.querySelector("header.bar"); if (!bar) return;
    const w = document.createElement("a");
    w.className = "hdr-watch"; w.href = "#event"; w.title = "Broadcast clock - change the face in Event & sponsors";
    // 2026-11-06 redo: the REAL overlay watch (drawn by the engine, same code as the session bar) - /api/watch.png -
    // centred in the bar, sitting over the bar's bottom edge like it does on the broadcast.
    w.innerHTML = `<span class="face"><img alt="" draggable="false"><b class="dig"></b></span>`;
    bar.insertBefore(w, bar.querySelector(".right"));
    w.addEventListener("click", (e) => { e.preventDefault(); G.go("event"); });
    const dig = w.querySelector(".dig");
    let img = w.querySelector("img"), busy = false, fails = 0;
    function frame() {
      const watchOn = G.settings ? G.settings.showWatch !== false : true;
      w.classList.toggle("digital", !watchOn || fails > 5);
      if (!watchOn || fails > 5) {
        dig.textContent = new Date().toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", hour12: false });
        if (fails > 5 && watchOn && Math.random() < 0.1) fails = 0;   // try the watch again now and then
        return setTimeout(frame, 1000);
      }
      if (document.hidden || busy) return setTimeout(frame, 500);
      busy = true;
      const next = new Image();                                         // load off-screen, swap when ready = no flicker
      next.alt = ""; next.draggable = false;
      next.onload = () => { img.replaceWith(next); img = next; busy = false; fails = 0; setTimeout(frame, 200); };
      next.onerror = () => { busy = false; fails++; setTimeout(frame, 1000); };
      next.src = "/api/watch.png?s=" + Math.round(96 * Math.min(2, window.devicePixelRatio || 1)) + "&t=" + Date.now();
    }
    frame();
  })();
})();
