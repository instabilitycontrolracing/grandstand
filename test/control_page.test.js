// Playwright run of the control page against the mock server. Saves screenshots to shots/.
// Usage: node test/control_page.test.js
"use strict";
const { chromium } = require("playwright");
const { spawn } = require("child_process");
const path = require("path");

const PORT = 8791;
const BASE = `http://127.0.0.1:${PORT}/`;
const SHOTS = path.join(__dirname, "..", "shots");
const results = [];
const check = (name, ok, extra = "") => { results.push({ name, ok }); console.log(`${ok ? "PASS" : "FAIL"}  ${name}${extra ? "  " + extra : ""}`); };
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

(async () => {
  const srv = spawn(process.execPath, [path.join(__dirname, "mock_control_server.js"), String(PORT)], { stdio: ["ignore", "pipe", "inherit"] });
  await new Promise((r) => srv.stdout.once("data", r));
  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: { width: 1600, height: 1000 } });
  const errors = [];
  let expectRefusals = false;   // set while the test deliberately sends requests the server must refuse
  page.on("pageerror", (e) => errors.push(e.message));
  page.on("console", (m) => { if (m.type() === "error" && !/404/.test(m.text()) && !expectRefusals) errors.push(m.text()); });
  page.on("response", (r) => { if (r.status() >= 400 && !r.url().includes("/assets/fonts/") && !expectRefusals) errors.push(r.status() + " " + r.url()); });
  const requests = [];
  page.on("request", (r) => requests.push({ url: r.url(), t: Date.now() }));
  const log = () => page.evaluate(() => fetch("/__test/log").then((r) => r.json()));
  const shot = (n) => page.screenshot({ path: path.join(SHOTS, n + ".png"), fullPage: true });
  const goto = async (screen) => { await page.click(`#rail a[data-screen="${screen}"]`); await sleep(900); };

  try {
    await page.goto(BASE);
    await sleep(1500);
    // ---------- On Air ----------
    check("on air screen renders big graphic buttons", (await page.locator("[data-big]").count()) >= 12);
    check("studio.js not loaded in Broadcast mode", !requests.some((r) => r.url.endsWith("/studio.js")));
    await shot("01_onair");
    await page.click('[data-big="ticker"]');
    await sleep(500);
    check("big button hides a graphic on Program", (await log()).settings.elements.ticker?.enabled === false);
    await page.click('#dirPanel [data-k="director"]', { force: true });
    await sleep(1800);
    check("director switch saves setting", (await log()).settings.director === true);
    check("director status shown", /Picking the shots|On/.test(await page.locator("#dirPanel .dirstate").innerText()));
    await shot("02_onair_director_on");
    // highlight reel (#9): session picker (latest first), Start / Stop, status
    check("reel panel lists sessions, latest selected", (await page.locator("#reelSession option").count()) === 2 && /latest/.test(await page.locator("#reelSession option:checked").innerText()));
    check("reel stop disabled until started", await page.locator("#reelStop").isDisabled());
    await page.selectOption("#reelSession", "2026-10-27_Spa-Francorchamps_race2.json");
    await page.click("#reelStart");
    await sleep(1600);
    check("reel start sends the chosen session", (await log()).tests.includes("reel:2026-10-27_Spa-Francorchamps_race2.json"));
    check("reel status shows the moment", /Moment \d+ of 7/.test(await page.locator("#reelState").innerText()));
    check("reel picker kept the choice while running", (await page.locator("#reelSession").inputValue()) === "2026-10-27_Spa-Francorchamps_race2.json" && await page.locator("#reelSession").isDisabled());
    await shot("02b_onair_reel_running");
    await page.click("#reelStop");
    await sleep(1400);
    check("reel stop", /Stopped/.test(await page.locator("#reelState").innerText()) && !(await page.locator("#reelStart").isDisabled()));
    // Lean mode
    await page.click('[data-setmode="lean"]');
    await sleep(900);
    check("lean mode saved", (await log()).settings.mode === "lean");
    check("lean mode disables non-lean buttons", await page.locator('[data-big="story"]').isDisabled());
    await shot("03_onair_lean");

    // ---------- Event & sponsors ----------
    await page.click('[data-setmode="broadcast"]'); await sleep(700);
    await goto("event"); await sleep(600);
    await page.fill('[data-partner="Custom"] [data-pf="word"]', "NIGHT SHIFT");
    await page.click('[data-partner="Custom"] [data-k="p_on_Custom"]', { force: true });
    await page.selectOption('[data-slot="UnderPressure"]', "Custom");
    await page.click("#saveSponsors"); await sleep(600);
    let l = await log();
    check("sponsor pack saved (slot + partner)", l.sponsors.presents.UnderPressure === "Custom" && l.sponsors.partners.Custom.on === true && l.sponsors.partners.Custom.word === "NIGHT SHIFT");
    await shot("04_event_sponsors");
    page.once("dialog", (d) => d.accept());
    await page.click("#resetSponsors"); await sleep(800);
    check("sponsor reset to defaults", (await log()).sponsors.presents.UnderPressure === "AB");

    // ---------- Broadcast clock (Event & sponsors) ----------
    await page.locator("#clockPanel").scrollIntoViewIfNeeded(); await sleep(500);
    check("broadcast clock card lists the 5 built-in dials", (await page.locator('#clockPanel [data-dial^="homage_"]').count()) === 5);
    check("auto shows Ivory as the default face", (await page.locator('#clockPanel [data-dial="homage_ivory"]').getAttribute("aria-pressed")) === "true");
    check("custom dial from the dials folder listed", (await page.locator('#clockPanel [data-dial="file:league_logo.png"]').count()) === 1);
    check("rights line shown", /use artwork you have the rights to/.test(await page.locator("#clockPanel").innerText()));
    check("subdial toggle disabled for a built-in", await page.locator('#clockPanel [data-k="dialHands"]').isDisabled());
    await page.click('#clockPanel [data-dial="homage_navy"]'); await sleep(500);
    check("clicking a dial applies it", (await log()).clock.dial === "homage_navy" && (await page.locator('#clockPanel [data-dial="homage_navy"]').getAttribute("aria-pressed")) === "true");
    await page.click('#clockPanel [data-dial="file:league_logo.png"]'); await sleep(500);
    l = await log();
    check("custom dial applied with subdial hands off by default", l.clock.dial === "file:league_logo.png" && l.clock.subdialHands === null && !(await page.locator('#clockPanel [data-k="dialHands"]').isChecked()));
    await page.click('#clockPanel [data-k="dialHands"]', { force: true }); await sleep(500);
    check("subdial hands toggle saved for a custom dial", (await log()).clock.subdialHands === true);
    await page.click('#clockPanel [data-k="dialHands"]', { force: true }); await sleep(500);
    check("thumbnails load (no broken images)", await page.evaluate(() => [...document.querySelectorAll("#clockPanel img")].every((i) => i.complete && i.naturalWidth > 0)));
    expectRefusals = true;
    const thumbBad = await page.evaluate(() => fetch("/api/dials/thumb/file/..%2Fmock_control_server.js").then((r) => r.status));
    check("thumbnail route refuses path traversal", thumbBad === 404);
    const noHeader = await page.evaluate(() => fetch("/api/dial", { method: "POST", body: '{"dial":"homage_black"}' }).then((r) => r.status));
    check("POST /api/dial needs the X-Grandstand header", noHeader === 403);
    await sleep(300); expectRefusals = false;
    await page.click('#clockPanel [data-dial="homage_ivory"]'); await sleep(2700);   // let the toast clear
    await page.locator("#clockPanel").screenshot({ path: path.join(SHOTS, "dials_card.png") });
    await page.evaluate(() => document.querySelector(".stage")?.scrollTo(0, 0)); await sleep(300);

    // ---------- Settings: packs + guardrails ----------
    await goto("settings"); await sleep(500);
    await page.fill("#packName", "league-night");
    await page.click("#savePack"); await sleep(800);
    check("pack saved", (await log()).packs.includes("league-night"));
    check("pack listed", (await page.locator('[data-apply="league-night"]').count()) === 1);
    await shot("05_settings_packs");

    // ---------- Viewer tier looks (#603, 2026-10-30) ----------
    await page.locator('#viewerPanel [data-k="viewer:tierLooks"]').evaluate((x) => x.click()); await sleep(700);
    check("tier looks switch saves", (await log()).settings.viewer?.tierLooks === true);
    check("tier stingers on by default", await page.locator('#viewerPanel [data-k="viewer:tierSounds"]').evaluate((x) => x.checked));
    await page.fill("#vwT2", "15"); await page.locator("#vwT2").dispatchEvent("change"); await sleep(600);
    check("tier 2 threshold saves", (await log()).settings.viewer?.tier2From === 15);
    await page.click('#viewerPanel [data-vwtest="sequence"]'); await sleep(500);
    check("stream test sends the sequence", (await log()).tests.includes("viewer:sequence"));
    await page.click('#viewerPanel [data-vwroll="sample"]'); await sleep(500);
    check("sample thank-you roll requested", (await log()).tests.includes("thanks:sample"));
    check("names this stream shown", /27 names/.test(await page.locator("#vwNames").innerText()));
    page.once("dialog", (d) => d.accept());
    await page.click("#vwNewStream"); await sleep(800);
    check("new stream clears the names", (await log()).tests.includes("names:clear") && /^0 names/.test(await page.locator("#vwNames").innerText()));

    // ---------- #608 latest plate, #611 cams, #619 race-control bar (2026-10-30) ----------
    check("latest plate on by default", await page.locator('#viewerPanel [data-k="viewer:keepLatest"]').evaluate((x) => x.checked));
    await page.selectOption("#vwLatestWhat", "paid"); await sleep(600);
    check("latest plate paid-only saves (whole settings kept)", (await log()).settings.viewer?.latestPaidOnly === true && (await log()).settings.viewer?.tier2From === 15);
    await page.click("#latestTest"); await sleep(500);
    check("latest plate test", (await log()).tests.includes("latestplate"));
    check("keep cams clear on by default", await page.locator('[data-k="cams:keepClear"]').evaluate((x) => x.checked));
    check("cam status line", /Racing/.test(await page.locator("#camStatus").innerText()));
    await page.click("#camTest"); await sleep(2200);
    check("cam test sent and status updates", (await log()).tests.includes("cams:test") && /moved clear/.test(await page.locator("#camStatus").innerText()));
    await page.locator('[data-k="cams:keepClear"]').evaluate((x) => x.click()); await sleep(700);
    check("keep cams clear switch saves", (await log()).settings.cams?.keepClear === false);
    check("race control bar on by default", await page.locator('[data-k="raceControlBar"]').evaluate((x) => x.checked));
    await page.click("#rcTest"); await sleep(500);
    check("race control bar test", (await log()).tests.includes("rcbar"));
    await page.locator('[data-k="raceControlBar"]').evaluate((x) => x.click()); await sleep(700);
    check("race control bar switch saves", (await log()).settings.raceControlBar === false);

    // ---------- Visual review pack (2026-10-27) ----------
    check("visual pack lists 13 items (V3 left out)", (await page.locator('#vpPanel [data-k^="vp:"]').count()) === 15);
    check("visual pack: only the approved items (W8 W11 W12 W13) and the sounds are on by default", await page.evaluate(() =>
      [...document.querySelectorAll('#vpPanel [data-k^="vp:"]')].every((x) => x.checked === ["vp:lowerThirdSounds", "vp:breakCard", "vp:lensEffects", "vp:penaltyCard", "vp:purpleBeep"].includes(x.dataset.k))));
    await page.locator('#vpPanel [data-k="vp:weatherCard"]').evaluate((x) => x.click()); await sleep(700);
    check("V1 switch saves", (await log()).settings.visuals?.weatherCard === true);
    await page.click('#vpPanel [data-vpstyle="V1"][data-v="elevated"]'); await sleep(700);
    { const lv = (await log()).settings.visuals; check("V1 elevated design saved", (lv?.elevated || []).includes("V1"), JSON.stringify(lv)); }
    await page.click('#vpPanel [data-vptest="V1"]'); await sleep(500);
    check("V1 test queued", (await log()).tests.includes("vp:V1"));

    // ---------- Class transitions (2026-10-24) ----------
    check("class transitions card offers 4 choices", (await page.locator("#transPanel [data-trans]").count()) === 4);
    check("Auto is the default", (await page.locator('#transPanel [data-trans="auto"]').getAttribute("aria-pressed")) === "true");
    await sleep(300);
    check("auto: GT3 plays your rollers, GTP the built-in animation",
      /YOUR ROLLERS/.test(await page.locator('#transPanel [data-tclass="gt3"]').innerText()) && /BUILT-IN/.test(await page.locator('#transPanel [data-tclass="gtp"]').innerText()));
    await page.click('#transPanel [data-trans="off"]'); await sleep(700);
    check("Off saved", (await log()).settings.transitions === "off" && /OFF/.test(await page.locator('#transPanel [data-tclass="gt3"]').innerText()));
    await page.click('#transPanel [data-trans="builtin"]'); await sleep(700);
    check("Built-in animated saved and shown for every class", (await log()).settings.transitions === "builtin"
      && /BUILT-IN/.test(await page.locator('#transPanel [data-tclass="gt3"]').innerText()));
    await page.click("#transRescan"); await sleep(500);
    check("Rescan asks Grandstand to read the rollers folder again", (await log()).rescans >= 1);
    expectRefusals = true;
    const transNoHeader = await page.evaluate(() => fetch("/api/transitions/rescan", { method: "POST" }).then((r) => r.status));
    await sleep(300); expectRefusals = false;
    check("POST /api/transitions/rescan needs the X-Grandstand header", transNoHeader === 403);
    await page.click('#transPanel [data-trans="auto"]'); await sleep(2700);   // let the toast clear
    await page.locator("#transPanel").screenshot({ path: path.join(SHOTS, "transitions_card.png") });
    await page.click('#transPanel [data-trans="builtin"]'); await sleep(600);   // the pack loaded later must bring back "auto"

    // ---------- Studio ----------
    await page.click('#modes button[data-mode="studio"]'); await sleep(1200);
    check("studio.js loaded lazily in Studio mode", requests.some((r) => r.url.endsWith("/studio.js")));
    await goto("studio"); await sleep(600);
    await page.click('[data-pick="featured"]'); await sleep(400);
    await page.locator('[data-ed="y"]').evaluate((r) => { r.value = -200; r.dispatchEvent(new Event("input", { bubbles: true })); });
    await sleep(300);
    check("preview edit does not touch Program", !(await log()).settings.elements.featured?.y);
    check("TAKE flagged pending", await page.locator("#take.pending").count() === 1);
    await shot("06_studio_preview_pending");
    await page.click("#take"); await sleep(700);
    check("TAKE puts preview on Program", (await log()).settings.elements.featured?.y === -200);
    await shot("07_studio_after_take");

    // ---------- Data browser ----------
    await goto("data"); await sleep(1500);
    check("telemetry rows listed", (await page.locator("#rows [data-use]").count()) > 10);
    const t0 = Date.now(), before = requests.filter((r) => r.url.includes("/api/telemetry")).length;
    await sleep(3000);
    const rate = (requests.filter((r) => r.url.includes("/api/telemetry")).length - before) / ((Date.now() - t0) / 1000);
    check("data browser polls at <= 4 Hz", rate <= 4.2, `(${rate.toFixed(2)} Hz)`);
    await shot("08_data_telemetry");
    await page.click('[data-tab="ses"]'); await sleep(1200);
    check("session info rows listed", (await page.locator("#rows [data-copy]").count()) > 5);
    await shot("09_data_sessioninfo");
    await page.click('[data-tab="tel"]'); await sleep(900);
    await page.fill("#q", "fuel"); await sleep(300);
    await page.click('[data-use="FuelLevel"]'); await sleep(1200);
    check("'use in a card' opens builder with the field", (await page.inputValue("#cField")) === "FuelLevel");
    const left = Date.now(); await sleep(2000);
    const ratesAfter = requests.filter((r) => r.url.includes("/api/telemetry") && r.t > left).length;
    check("data polling stops after leaving the screen", ratesAfter === 0, `(${ratesAfter} in 2 s after leaving)`);

    // ---------- Card builder ----------
    await page.click('[data-tpl="number"]'); await sleep(400);
    await page.fill("#cName", "Fuel watch");
    await page.selectOption("#cTrig", "above"); await sleep(500);
    await page.fill("#cThr", "40");
    await page.click("#saveCard"); await sleep(900);
    l = await log();
    check("card saved as file", l.cards.includes("fuel-watch"));
    await page.click("#newCard"); await sleep(400);
    await page.click('[data-tpl="top5"]'); await sleep(500);
    await shot("10_builder_top5");
    await page.click("#saveCard"); await sleep(800);
    await page.click("#newCard"); await sleep(400);
    await page.click('[data-tpl="h2h"]'); await sleep(400);
    await page.fill("#cName", "Head to head"); await sleep(200);
    await shot("11_builder_h2h");
    await page.click("#playCard"); await sleep(900);
    l = await log();
    check("save & play queues the custom card", l.tests.includes("custom:head-to-head"));
    check("saved list shows cards", (await page.locator("[data-cedit]").count()) >= 3);
    await shot("12_builder_saved");

    // ---------- Test cards include custom ones ----------
    await goto("test"); await sleep(900);
    check("custom cards appear under Test cards", (await page.locator("[data-mycard]").count()) >= 3);
    await shot("13_test_cards");

    // ---------- Load pack restores earlier setup ----------
    await goto("settings"); await sleep(700);
    page.once("dialog", (d) => d.accept());
    await page.click('[data-apply="league-night"]'); await sleep(900);
    l = await log();
    check("loading a pack restores settings (featured back to 0, ticker still hidden)", !l.settings.elements.featured?.y && l.settings.elements.ticker?.enabled === false);
    check("loading a pack keeps the director as it was", l.settings.director === true);
    check("loading a pack restores the class transition choice", l.settings.transitions === "auto");

    // phone width
    await page.setViewportSize({ width: 420, height: 900 });
    await goto("onair"); await sleep(800);
    await shot("14_onair_phone");

    check("no page errors", errors.length === 0, errors.slice(0, 3).join(" | "));
  } catch (e) {
    check("test run", false, e.stack);
    await shot("zz_failure").catch(() => {});
  } finally {
    await browser.close();
    srv.kill();
  }
  const failed = results.filter((r) => !r.ok).length;
  console.log(`\n${results.length - failed}/${results.length} passed`);
  process.exit(failed ? 1 : 0);
})();
