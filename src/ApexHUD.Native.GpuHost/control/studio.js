/* Grandstand Control — Studio editors. Loaded by app.js only when Studio mode is on,
   so Lean / Broadcast never download or run any of this. */
(() => {
  "use strict";
  const GS = window.GS;
  const { $, $$, esc, api, clone, toast, download, readFile, head, sw, ELEMENTS, LEAN, DEF_EL, SCREENS, BIND } = GS;

  // =============================== Preview & Program ===============================
  // Edits go to Preview only. Program is what viewers see; TAKE copies Preview to Program.
  // The monitors are schematics (boxes where each graphic sits) - no second render path.
  let pending = null;
  let selected = "tower";
  GS.onSettingsSynced = () => { if (GS.screen !== "studio") pending = null; };
  const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
  const elOf = (s, id) => s.elements[id] || { ...DEF_EL };
  const isDirty = () => !same(pending.elements, GS.settings.elements) || pending.onAir !== GS.settings.onAir;

  SCREENS.studio = () => {
    const settings = GS.settings;
    if (!pending) pending = clone(settings);
    const e = ELEMENTS.find((x) => x.id === selected);
    const p = elOf(pending, selected);
    const changed = ELEMENTS.filter((x) => !same(elOf(pending, x.id), elOf(settings, x.id))).map((x) => x.label);
    return head("Studio", "Preview &amp; Program", "Change things on Preview — viewers keep seeing Program. Hit TAKE to put Preview on air.") +
      `<div class="pp">
        <div class="monitor pvw" id="pvw"><span class="lbl">PREVIEW</span>${boxes(pending, true)}</div>
        <div class="ppmid"><button type="button" class="take ${isDirty() ? "pending" : ""}" id="take">TAKE</button><button type="button" class="btn" id="undo">Undo</button>
          <small class="muted" id="chg">${changed.length ? esc(changed.length + " change" + (changed.length > 1 ? "s" : "") + " waiting") : "Preview = Program"}</small></div>
        <div class="monitor pgm" id="pgm"><span class="lbl">PROGRAM · ON AIR</span>${boxes(settings, false)}</div>
      </div>
      <div class="grid" style="grid-template-columns: 300px minmax(0,1fr); margin-top:18px">
        <div class="panel"><h2>Graphics</h2>${ELEMENTS.map((x) => `<div class="row pick ${x.id === selected ? "sel" : ""}" data-pick="${x.id}"><span class="t">${esc(x.label)}${!same(elOf(pending, x.id), elOf(settings, x.id)) ? ` <span class="soon">CHANGED</span>` : ""}</span><span class="dotst ${elOf(pending, x.id).enabled ? "on" : ""}"></span></div>`).join("")}</div>
        <div class="panel"><div style="display:flex;align-items:center;gap:12px;margin-bottom:12px"><h2 style="margin:0;flex:1">${esc(e.label)}</h2><button type="button" class="btn" id="reset">Reset to default</button><button type="button" class="btn" id="resetAllPv">Reset all on Preview</button></div>
          <div class="grid g3">
            <label class="f">Left / right <span class="mono" id="vx">${Math.round(p.x)} px</span><input type="range" min="-960" max="960" step="2" value="${p.x}" data-ed="x"></label>
            <label class="f">Up / down <span class="mono" id="vy">${Math.round(p.y)} px</span><input type="range" min="-540" max="540" step="2" value="${p.y}" data-ed="y"></label>
            <label class="f">Size <span class="mono" id="vs">${Math.round(p.scale * 100)} %</span><input type="range" min="50" max="200" step="1" value="${Math.round(p.scale * 100)}" data-ed="scale"></label>
          </div>
          <div class="row" style="margin-top:8px"><span class="t">Show this graphic<small>${settings.mode === "lean" && !LEAN.has(selected) ? "Lean mode hides it anyway" : "On Preview until you TAKE"}</small></span>${sw("pv_" + selected, p.enabled, "Show " + e.label)}</div>
          <p class="muted" style="margin:10px 0 0">Tip: drag the dashed box on Preview to move it. Green edges on Program = on screen this second.</p></div>
      </div>`;
  };
  function boxes(s, editable) {
    return ELEMENTS.filter((e) => e.id !== "splash" && e.id !== "outro" && e.id !== "spotlight").map((e) => {
      const st = elOf(s, e.id);
      const [x, y, w, h] = e.rect, sc = st.scale || 1;
      const L = ((x + st.x) / 1920) * 100, T = ((y + st.y) / 1080) * 100, W = ((w * sc) / 1920) * 100, H = ((h * sc) / 1080) * 100;
      const off = !st.enabled || !s.onAir || (s.mode === "lean" && !LEAN.has(e.id));
      const live = !editable && GS.showing(e.id);
      return `<div class="el ${off ? "off" : ""} ${live ? "live" : ""} ${editable && e.id === selected ? "sel" : ""}" data-box="${e.id}" style="left:${L}%;top:${T}%;width:${W}%;height:${H}%">${W > 12 ? esc(e.label) : ""}</div>`;
    }).join("");
  }
  GS.onState = () => {
    if (GS.screen !== "studio") return;
    $$("#pgm [data-box]").forEach((b) => b.classList.toggle("live", GS.showing(b.dataset.box)));
  };
  BIND.studio = (host) => {
    const upd = () => {
      const dirty = isDirty();
      $("#take").classList.toggle("pending", dirty);
      const n = ELEMENTS.filter((x) => !same(elOf(pending, x.id), elOf(GS.settings, x.id))).length;
      $("#chg").textContent = n ? `${n} change${n > 1 ? "s" : ""} waiting` : "Preview = Program";
      $("#pvw").innerHTML = `<span class="lbl">PREVIEW</span>${boxes(pending, true)}`; bindDrag();
    };
    const cur = () => (pending.elements[selected] = pending.elements[selected] || { ...DEF_EL });
    $$("[data-pick]", host).forEach((r) => r.addEventListener("click", () => { selected = r.dataset.pick; GS.render(); }));
    $$("[data-ed]", host).forEach((r) => r.addEventListener("input", () => {
      const c = cur(), v = +r.value;
      if (r.dataset.ed === "scale") { c.scale = v / 100; $("#vs").textContent = v + " %"; } else { c[r.dataset.ed] = v; $("#v" + r.dataset.ed).textContent = v + " px"; }
      upd();
    }));
    host.addEventListener("change", (e) => { if (e.target.dataset.k === "pv_" + selected) { cur().enabled = e.target.checked; upd(); } });
    $("#reset").addEventListener("click", () => { pending.elements[selected] = { ...DEF_EL }; GS.render(); });
    $("#resetAllPv").addEventListener("click", () => { pending.elements = {}; GS.render(); toast("Preview: every graphic back to default — TAKE to put it on air"); });
    $("#undo").addEventListener("click", () => { pending = clone(GS.settings); GS.render(); toast("Preview reset to what's on air"); });
    $("#take").addEventListener("click", () => {
      const next = { ...GS.settings, elements: clone(pending.elements), onAir: pending.onAir };
      api.post("/api/settings", next).then((s) => { GS.settings = s; pending = clone(s); GS.render(); toast("Taken to air"); }).catch(() => toast("Couldn't reach Grandstand"));
    });
    function bindDrag() {
      const box = $(`#pvw [data-box="${selected}"]`), mon = $("#pvw"); if (!box) return;
      box.onpointerdown = (ev) => {
        box.setPointerCapture(ev.pointerId);
        const r = mon.getBoundingClientRect(), c = cur(), sx = ev.clientX, sy = ev.clientY, ox = c.x, oy = c.y;
        box.onpointermove = (m) => { c.x = Math.round(ox + ((m.clientX - sx) / r.width) * 1920); c.y = Math.round(oy + ((m.clientY - sy) / r.height) * 1080);
          const e = ELEMENTS.find((x) => x.id === selected); box.style.left = ((e.rect[0] + c.x) / 1920) * 100 + "%"; box.style.top = ((e.rect[1] + c.y) / 1080) * 100 + "%";
          $("#vx").textContent = c.x + " px"; $("#vy").textContent = c.y + " px"; };
        box.onpointerup = () => { box.onpointermove = null; GS.render(); };
      };
    }
    bindDrag();
  };

  // =============================== Transitions & timing ===============================
  SCREENS.timing = () => {
    const settings = GS.settings;
    return head("Studio", "Transitions &amp; timing", "How long things stay up, and how hard the overlay works.") +
    `<div class="grid g2">
      <div class="panel" style="--accent: var(--amber)"><h2>Story cards</h2>
        <label class="f">Time on screen <span class="mono" id="vcs">${settings.storyCardSeconds} s</span><input type="range" min="4" max="30" step="1" value="${settings.storyCardSeconds}" data-t="storyCardSeconds"></label>
        <div style="height:14px"></div>
        <label class="f">Minimum gap between routine cards <span class="mono" id="vgm">${settings.storyGapMinutes} min</span><input type="range" min="1" max="15" step="0.5" value="${settings.storyGapMinutes}" data-t="storyGapMinutes"></label>
        <div style="display:flex;justify-content:flex-end;margin-top:10px"><button type="button" class="btn" id="resetTiming">Reset to default</button></div>
        <p class="muted" style="margin-top:12px">Big moments (halfway, weather) still jump the queue. Cards never cover a battle for position.</p></div>
      <div class="panel" style="--accent: var(--gt3)"><h2>Performance</h2>
        <div class="chips">${[["auto", "Auto"], ["full", "Full"], ["light", "Light"]].map(([k, l]) => `<button type="button" class="chip" data-perf="${k}" aria-pressed="${(settings.performance || "auto") === k}">${l}</button>`).join("")}</div>
        <p class="muted" style="margin-top:12px"><b>Light</b> is for slower PCs: the graphics run at 30 frames a second instead of 60 and skip the decorative motion (light sweeps, flag wipes, shines). Everything still shows. <b>Auto</b> switches to Light by itself if the overlay gets heavy on this PC.</p></div>
      <div class="panel" style="--accent: var(--amber)"><h2>Units</h2>
        <label class="f">Temperatures</label>
        <div class="chips">${[["F", "°F"], ["C", "°C"], ["auto", "Same as iRacing"]].map(([k, l]) => `<button type="button" class="chip" data-tu="${k}" aria-pressed="${(settings.tempUnit || "F") === k}">${l}</button>`).join("")}</div>
        <p class="muted" style="margin-top:12px">Weather Watch, the session bar and the pre-race weather card. <b>Same as iRacing</b> follows the units you picked in iRacing's options.</p></div>
    </div>`;
  };
  BIND.timing = (host) => {
    $$("[data-t]", host).forEach((r) => {
      r.addEventListener("input", () => { const v = +r.value; if (r.dataset.t === "storyCardSeconds") $("#vcs").textContent = v + " s"; else $("#vgm").textContent = v + " min"; });
      r.addEventListener("change", () => { GS.settings[r.dataset.t] = +r.value; GS.save("Timing saved"); });
    });
    $("#resetTiming").addEventListener("click", () => { GS.settings.storyCardSeconds = 15; GS.settings.storyGapMinutes = 3; GS.save("Timing back to default"); GS.render(); });
    $$("[data-tu]", host).forEach((b) => b.addEventListener("click", () => {
      GS.settings.tempUnit = b.dataset.tu; $$("[data-tu]", host).forEach((x) => x.setAttribute("aria-pressed", x === b));
      GS.save(b.dataset.tu === "auto" ? "Temperatures follow iRacing" : "Temperatures in °" + b.dataset.tu);
    }));
    $$("[data-perf]", host).forEach((b) => b.addEventListener("click", () => {
      GS.settings.performance = b.dataset.perf; $$("[data-perf]", host).forEach((x) => x.setAttribute("aria-pressed", x === b));
      GS.save(b.dataset.perf === "light" ? "Light mode on" : b.dataset.perf === "full" ? "Full quality" : "Auto: light mode only if needed");
    }));
  };

  // =============================== iRacing data browser ===============================
  // Polls at 2 Hz, only while this screen is open and the page is visible.
  let telemetry = [], sessionInfo = [], dataTab = "tel", dataFilter = "all", dataQuery = "", paused = false;
  const CATS = [["all", "All"], ["car", "Every car"], ["drv", "Driver & car"], ["ses", "Session & flags"], ["wx", "Weather"], ["tyre", "Tyres & fuel"], ["cam", "Camera & replay"]];
  const catOf = (n) => n.startsWith("CarIdx") ? "car" : /^(Cam|Replay|IsReplay)/.test(n) ? "cam" : /^(Session|Flag|Pace|Race|Lap$|LapBest|Radio|Player)/.test(n) ? "ses" :
    /^(Air|Track|Wind|Weather|Fog|Rel|Skies|Precip|Solar)/.test(n) ? "wx" : /^(LF|RF|LR|RR|Fuel|Tire|dp)/.test(n) ? "tyre" : "drv";
  SCREENS.data = () => head("Studio", "iRacing data", "Everything iRacing sends out, live (twice a second while this screen is open). Anything in Telemetry can go on a card.",
      `<label class="f">Search<input id="q" value="${esc(dataQuery)}" placeholder="speed, fuel, position…" style="width:280px"></label>`) +
    `<div style="display:flex;gap:12px;align-items:center;flex-wrap:wrap;margin-bottom:12px"><div class="seg" role="group" aria-label="Source"><button type="button" data-tab="tel" aria-pressed="${dataTab === "tel"}">Telemetry</button><button type="button" data-tab="ses" aria-pressed="${dataTab === "ses"}">Session info</button></div>
       <button type="button" class="btn" id="pause">${paused ? "Resume" : "Pause"}</button><span class="muted" id="cnt"></span></div>
     ${dataTab === "tel" ? `<div class="chips">${CATS.map(([k, l]) => `<button type="button" class="chip" data-cat="${k}" aria-pressed="${k === dataFilter}">${l}</button>`).join("")}</div>` : ""}
     <div class="table" id="tbl"><div class="tr th"><span>Name</span><span>Live value</span><span>${dataTab === "tel" ? "What it is" : ""}</span><span></span></div><div id="rows"></div></div>`;
  function paintRows() {
    const box = $("#rows"); if (!box) return;
    const q = dataQuery.toLowerCase();
    if (dataTab === "tel") {
      if (!telemetry.length) { box.innerHTML = `<div class="tr"><span class="muted" style="grid-column:1/-1">Start iRacing (on track or watching a replay) and every value appears here live.</span></div>`; $("#cnt").textContent = ""; return; }
      const all = telemetry.filter((t) => (dataFilter === "all" || catOf(t.name) === dataFilter) && (!q || t.name.toLowerCase().includes(q) || (t.desc || "").toLowerCase().includes(q)));
      const list = all.slice(0, 250);
      $("#cnt").textContent = `${all.length} of ${telemetry.length} values${all.length > list.length ? " (first 250 shown)" : ""}`;
      box.innerHTML = list.map((t) => `<div class="tr"><span class="mono">${esc(t.name)}</span><span class="mono val" title="${esc(t.value)}">${esc(t.value)}${t.unit ? ` <span class="muted">${esc(t.unit)}</span>` : ""}</span><span class="muted">${esc(t.desc)}</span><button type="button" class="btn sm" data-use="${esc(t.name)}" title="Make a card from this">Use in a card</button></div>`).join("");
    } else {
      if (!sessionInfo.length) { box.innerHTML = `<div class="tr"><span class="muted" style="grid-column:1/-1">Session info appears here once iRacing has a session loaded.</span></div>`; $("#cnt").textContent = ""; return; }
      const all = sessionInfo.filter((t) => !q || t.name.toLowerCase().includes(q) || t.value.toLowerCase().includes(q));
      const list = all.slice(0, 300);
      $("#cnt").textContent = `${all.length} of ${sessionInfo.length} lines${all.length > list.length ? " (first 300 shown)" : ""}`;
      box.innerHTML = list.map((t) => `<div class="tr"><span class="mono" style="grid-column:span 1">${esc(t.name)}</span><span class="mono val" title="${esc(t.value)}">${esc(t.value)}</span><span></span><button type="button" class="btn sm" data-copy="${esc(t.name)}">Copy</button></div>`).join("");
    }
  }
  function loadData() {
    if (paused || document.hidden) return;
    const p = dataTab === "tel" ? api.get("/api/telemetry").then((t) => { telemetry = t || []; }) : api.get("/api/sessioninfo").then((t) => { sessionInfo = t || []; });
    p.then(paintRows).catch(() => {});
  }
  BIND.data = (host) => {
    $("#q").addEventListener("input", (e) => { dataQuery = e.target.value; paintRows(); });
    host.addEventListener("click", (e) => {
      const c = e.target.closest("[data-cat]"); if (c) { dataFilter = c.dataset.cat; $$("[data-cat]", host).forEach((x) => x.setAttribute("aria-pressed", x === c)); paintRows(); return; }
      const t = e.target.closest("[data-tab]"); if (t) { dataTab = t.dataset.tab; GS.render(); return; }
      if (e.target.id === "pause") { paused = !paused; e.target.textContent = paused ? "Resume" : "Pause"; return; }
      const u = e.target.closest("[data-use]"); if (u) { newDraft(); draft.field = u.dataset.use; draft.name = u.dataset.use.replace(/^CarIdx/, "").replace(/([a-z])([A-Z])/g, "$1 $2"); draft.trigger = "above"; GS.go("builder"); toast("Making a card from " + u.dataset.use); return; }
      const cp = e.target.closest("[data-copy]"); if (cp) navigator.clipboard?.writeText(cp.dataset.copy).then(() => toast("Copied " + cp.dataset.copy), () => {});
    });
    paintRows();
    loadData();
    const timer = setInterval(loadData, 500);     // 2 Hz, and only while this screen is open
    GS.leaveHooks.push(() => clearInterval(timer));
  };

  // =============================== Card builder ===============================
  const TEMPLATES = [["number", "Big number", "One value, huge"], ["top5", "Top 5", "A ranked list"], ["h2h", "Head to head", "Two cars side by side"]];
  const TRIGGERS = [["gain", "A car gains places"], ["everylaps", "Every few laps"], ["above", "A value goes above"], ["manual", "Only when I press Play"]];
  const BUILTIN = ["PlacesGained", "Position", "PositionOverall", "BestLap", "LastLap", "Gap", "Interval", "Incidents", "iRating", "LapsCompleted"];
  let draft = null, savedCards = [], cardProblems = [], cardsFolder = "";
  function newDraft() { draft = { id: "", name: "Charging through", template: "number", field: "PlacesGained", label: "", trigger: "gain", amount: 4, laps: 3, threshold: 0, seconds: 8, cooldown: 5, sponsor: "none", accent: "#34D27B", enabled: true }; }
  newDraft();
  const slug = (s) => String(s || "").toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "").slice(0, 40) || "card";
  function toFile(d) {
    return { name: d.name, template: d.template, field: d.field, label: d.label, trigger: { kind: d.trigger, amount: +d.amount, laps: +d.laps, threshold: +d.threshold },
      seconds: +d.seconds, cooldown: +d.cooldown, sponsor: d.sponsor, accent: d.accent, enabled: !!d.enabled };
  }
  function fromFile(id, c) {
    const t = c.trigger || {};
    return { id, name: c.name || id, template: c.template || "number", field: c.field || "PlacesGained", label: c.label || "", trigger: t.kind || c.rule || "gain",
      amount: t.amount ?? c.amount ?? 4, laps: t.laps ?? c.laps ?? 3, threshold: t.threshold ?? 0, seconds: c.seconds ?? 8, cooldown: c.cooldown ?? 5,
      sponsor: typeof c.sponsor === "boolean" ? (c.sponsor ? "slot" : "none") : c.sponsor || "none", accent: c.accent || "#34D27B", enabled: c.enabled !== false };
  }
  function ruleText(d) {
    return d.trigger === "gain" ? `When a car gains ${d.amount}+ places in ${d.laps} lap${d.laps == 1 ? "" : "s"}`
      : d.trigger === "everylaps" ? `Every ${d.amount} laps of the leader` : d.trigger === "above" ? `When ${d.field} goes above ${d.threshold} (car on camera)` : "Only from the Play button";
  }
  function refreshCards() {
    return api.get("/api/cards").then((r) => { savedCards = r.cards || []; cardsFolder = r.folder || ""; if (GS.screen === "builder") paintSaved(); }).catch(() => {});
  }
  SCREENS.builder = () => {
    const d = draft;
    const fieldOpts = BUILTIN.map((f) => `<option value="${f}">`).join("") + telemetry.slice(0, 400).map((t) => `<option value="${esc(t.name)}">`).join("");
    const sponsorOpts = [["none", "No sponsor"], ["slot", "The pack's 'Your own cards' spot"], ...GS.PARTNERS].map(([k, n]) => `<option value="${k}" ${d.sponsor === k ? "selected" : ""}>${esc(n)}</option>`).join("");
    return head("Studio", "Card builder", "Pick a template, choose the data, set when it shows. Cards are saved as files you can share, and play on air in quiet moments.",
      `<div style="display:flex;gap:10px"><button type="button" class="btn" id="newCard">New</button><button type="button" class="btn" id="playCard">Save &amp; play now</button><button type="button" class="btn gold" id="saveCard">Save card</button></div>`) +
    `<div class="grid" style="grid-template-columns:minmax(0,1fr) minmax(0,1fr)">
      <div class="panel" style="display:flex;align-items:center;justify-content:center;min-height:380px;background:radial-gradient(120% 90% at 50% 20%, #1B2232, #0B0F17)" id="pvwrap">${cardPreview()}</div>
      <div class="panel"><h2>Build it</h2><div class="grid" style="gap:14px">
        <div class="seg" role="group" aria-label="Template">${TEMPLATES.map(([k, l, h]) => `<button type="button" data-tpl="${k}" aria-pressed="${d.template === k}" title="${h}">${l}</button>`).join("")}</div>
        <div class="grid g2"><label class="f">Headline<input id="cName" value="${esc(d.name)}" maxlength="40"></label>
          <label class="f">Data<input id="cField" value="${esc(d.field)}" list="fields" maxlength="48"><datalist id="fields">${fieldOpts}</datalist></label></div>
        <label class="f">Small line under the headline (optional)<input id="cLabel" value="${esc(d.label)}" maxlength="40" placeholder="${d.template === "number" ? "places in 3 laps" : "fastest this race"}"></label>
        <label class="f">When it shows<select id="cTrig">${TRIGGERS.map(([k, l]) => `<option value="${k}" ${d.trigger === k ? "selected" : ""}>${l}</option>`).join("")}</select></label>
        <div class="row" style="flex-wrap:wrap;gap:8px;border:0">${d.trigger === "gain" ? `Gains <input class="in" id="cAmt" type="number" min="1" max="20" value="${d.amount}" style="width:70px"> places in <input class="in" id="cLaps" type="number" min="1" max="20" value="${d.laps}" style="width:70px"> laps`
          : d.trigger === "everylaps" ? `Every <input class="in" id="cAmt" type="number" min="1" max="100" value="${d.amount}" style="width:80px"> laps`
          : d.trigger === "above" ? `Above <input class="in" id="cThr" type="number" step="any" value="${d.threshold}" style="width:110px"> <span class="muted">for the car on camera</span>` : `<span class="muted">Test cards → Your own cards, or Save &amp; play now</span>`}</div>
        <div class="grid g2"><label class="f">On screen for <span class="mono">${d.seconds} s</span><input type="range" id="cSec" min="3" max="20" value="${d.seconds}"></label>
          <label class="f">At most once every <span class="mono">${d.cooldown} min</span><input type="range" id="cCool" min="1" max="30" value="${d.cooldown}"></label></div>
        <div class="grid g2"><label class="f">Sponsor<select id="cSponsor">${sponsorOpts}</select></label><label class="f">Card colour<input id="cAccent" type="color" value="${esc(d.accent)}"></label></div>
        <div class="row"><span class="t">Card switched on<small>Off = kept, but never plays by itself</small></span>${sw("cEnabled", d.enabled, "Card on")}</div>
      </div></div></div>
      <div class="panel" style="margin-top:16px"><div style="display:flex;align-items:center;gap:12px;margin-bottom:8px"><h2 style="margin:0;flex:1">Your cards</h2><label class="btn filebtn">Import card file…<input type="file" accept=".json,application/json" id="importCard"></label></div>
        <div id="saved"><p class="muted">Loading…</p></div></div>`;
  };
  function sampleRows() {
    const names = ["S. DRIVER", "A. WINGMAN", "M. LATEBRAKE", "R. APEX", "J. KERB"];
    const val = (i) => draft.field === "BestLap" || draft.field === "LastLap" ? `1:${47 + i}.${String((213 + i * 97) % 1000).padStart(3, "0")}` : draft.field === "Incidents" ? `${i * 2}x` : draft.field === "PlacesGained" ? `+${8 - i}` : String(100 - i * 7);
    return names.map((n, i) => [i + 1, n, val(i)]);
  }
  function cardPreview() {
    const d = draft;
    const top = `<div class="top"></div><div class="in"><div class="meta"><span class="live">LIVE</span>${d.sponsor !== "none" ? "<span>Presented by " + esc(d.sponsor === "slot" ? "[your spot]" : (GS.PARTNERS.find((p) => p[0] === d.sponsor) || [, d.sponsor])[1]) + "</span>" : ""}</div>
      <div class="ttl">${esc(d.name || "Your card")}</div><div class="muted" style="font-weight:600;letter-spacing:1px;text-transform:uppercase;font-size:12px">${esc(d.label || (d.template === "number" && d.trigger === "gain" ? `places in ${d.laps} laps` : d.field))}</div>`;
    let body = "";
    if (d.template === "number") body = `<div style="display:flex;align-items:baseline;justify-content:space-between;gap:12px;margin-top:8px"><span class="big" style="color:${esc(d.accent)}">${d.field === "PlacesGained" || d.trigger === "gain" ? "+" + esc(d.amount) : "42"}</span><span style="text-align:right"><span class="name">S. Driver</span><br><span class="mono muted">#72 · GTP · P3</span></span></div>`;
    else if (d.template === "top5") body = `<div style="margin-top:10px">${sampleRows().map(([p, n, v], i) => `<div class="pv5 ${i === 0 ? "first" : ""}" style="${i === 0 ? `--accent:${esc(d.accent)}` : ""}"><b>${p}</b><span>${n}</span><span class="mono">${esc(v)}</span></div>`).join("")}</div>`;
    else body = `<div class="pvh"><b>S. DRIVER</b><i style="color:${esc(d.accent)}">VS</i><b>A. WINGMAN</b></div>${[["POSITION", "P3", "P4"], ["LAST LAP", "1:47.213", "1:47.090"], ["BEST LAP", "1:46.880", "1:46.954"], ["INTERVAL", "+0.8", "+0.4"]].map(([l, a, b]) => `<div class="pvhr"><span class="mono">${a}</span><small>${l}</small><span class="mono">${b}</span></div>`).join("")}`;
    return `<div class="cardpv" style="--accent:${esc(d.accent)}">${top}${body}<div class="mono muted" style="margin-top:12px;font-size:11px">${esc(ruleText(d))} · ${esc(d.seconds)} s · data: ${esc(d.field)}</div></div></div>`;
  }
  function paintSaved() {
    const box = $("#saved"); if (!box) return;
    box.innerHTML = (savedCards.length ? savedCards.map((c) => {
      if (!c.card) return `<div class="row"><span class="t">${esc(c.id)}<small style="color:var(--amber)">Can't be used: ${esc(c.error || "unreadable")}</small></span><button type="button" class="mini" data-cdel="${esc(c.id)}">Delete</button></div>`;
      const d = fromFile(c.id, c.card);
      return `<div class="row"><span class="sw8" style="background:${esc(d.accent)}"></span><span class="t">${esc(d.name)}${d.enabled ? "" : ` <span class="soon">OFF</span>`}<small>${esc(TEMPLATES.find((t) => t[0] === d.template)?.[1] || d.template)} · ${esc(ruleText(d))} · ${esc(c.id)}.json</small></span>
        <button type="button" class="btn sm" data-cedit="${esc(c.id)}">Edit</button><button type="button" class="btn sm" data-cplay="${esc(c.id)}">Play</button><button type="button" class="btn sm" data-cexp="${esc(c.id)}">Share</button><button type="button" class="mini" data-cdel="${esc(c.id)}">Delete</button></div>`;
    }).join("") : `<p class="muted">No cards yet. Build one above and press Save.</p>`) + (cardsFolder ? `<p class="muted mono" style="margin:8px 0 0;font-size:12px">${esc(cardsFolder)}</p>` : "");
  }
  function saveDraft() {
    if (!draft.name.trim()) { toast("Give the card a headline"); return Promise.reject(new Error("no name")); }
    const id = draft.id || slug(draft.name);
    return api.post("/api/cards/" + encodeURIComponent(id), toFile(draft), "PUT").then((r) => {
      draft.id = id;
      toast("Card saved: " + draft.name + (r.problems?.length ? ` (${r.problems.length} other file${r.problems.length > 1 ? "s" : ""} skipped)` : ""));
      refreshCards(); return id;
    }).catch((e) => { toast("Not saved: " + e.message); throw e; });
  }
  BIND.builder = (host) => {
    const repaint = () => { $("#pvwrap").innerHTML = cardPreview(); };
    const sync = (e) => {
      const t = e?.target;
      draft.name = $("#cName").value; draft.field = $("#cField").value.trim(); draft.label = $("#cLabel").value;
      if ($("#cAmt")) draft.amount = +$("#cAmt").value || 1;
      if ($("#cLaps")) draft.laps = +$("#cLaps").value || 1;
      if ($("#cThr")) draft.threshold = +$("#cThr").value || 0;
      draft.seconds = +$("#cSec").value; draft.cooldown = +$("#cCool").value; draft.sponsor = $("#cSponsor").value; draft.accent = $("#cAccent").value;
      draft.enabled = $('[data-k="cEnabled"]').checked;
      if (t?.id === "cTrig") { draft.trigger = t.value; GS.render(); return; }
      if (t?.id === "cSec" || t?.id === "cCool") { const lab = t.closest("label").querySelector(".mono"); lab.textContent = t.id === "cSec" ? draft.seconds + " s" : draft.cooldown + " min"; }
      repaint();
    };
    host.addEventListener("input", (e) => { if (e.target.closest(".panel #saved") || e.target.type === "file") return; sync(e); });
    host.addEventListener("change", (e) => {
      if (e.target.id === "importCard" && e.target.files?.length) {
        const f = e.target.files[0];
        readFile(f).then((text) => { JSON.parse(text); return api.post("/api/cards/" + encodeURIComponent(slug(f.name.replace(/\.json$/i, "").replace(/\.card$/i, ""))), text, "PUT"); })
          .then(() => { toast("Card imported"); refreshCards(); }).catch((er) => toast("Not a card file: " + er.message));
        return;
      }
      if (e.target.type !== "file") sync(e);
    });
    host.addEventListener("click", (e) => {
      const tp = e.target.closest("[data-tpl]"); if (tp) { draft.template = tp.dataset.tpl; if (tp.dataset.tpl === "top5" && draft.field === "PlacesGained" && draft.trigger === "gain") draft.trigger = "everylaps", draft.amount = 10, draft.field = "BestLap", draft.name = "Fastest so far"; GS.render(); return; }
      const t = e.target;
      if (t.id === "newCard") { newDraft(); GS.render(); return; }
      if (t.id === "saveCard") { saveDraft().catch(() => {}); return; }
      if (t.id === "playCard") { saveDraft().then((id) => api.post(`/api/cards/${encodeURIComponent(id)}/play`, null)).then(() => toast("Playing on air: " + draft.name)).catch(() => {}); return; }
      const ed = t.dataset.cedit; if (ed) { const c = savedCards.find((x) => x.id === ed); if (c?.card) { draft = fromFile(ed, c.card); GS.render(); window.scrollTo({ top: 0, behavior: "smooth" }); } return; }
      if (t.dataset.cplay) { api.post(`/api/cards/${encodeURIComponent(t.dataset.cplay)}/play`, null).then(() => toast("Playing on air")); return; }
      if (t.dataset.cexp) { const c = savedCards.find((x) => x.id === t.dataset.cexp); if (c?.card) download(c.id + ".json", c.card); return; }
      if (t.dataset.cdel && confirm(`Delete the card "${t.dataset.cdel}"?`)) api.post("/api/cards/" + encodeURIComponent(t.dataset.cdel), null, "DELETE").then(() => { toast("Card deleted"); if (draft.id === t.dataset.cdel) draft.id = ""; refreshCards(); });
    });
    paintSaved(); refreshCards();
    if (!telemetry.length) api.get("/api/telemetry").then((t) => { telemetry = t || []; const dl = $("#fields"); if (dl) dl.innerHTML = BUILTIN.map((f) => `<option value="${f}">`).join("") + telemetry.slice(0, 400).map((x) => `<option value="${esc(x.name)}">`).join(""); }).catch(() => {});
  };

  GS.studioReady = true;
  if (["studio", "timing", "data", "builder"].includes(GS.screen)) GS.render();
})();
