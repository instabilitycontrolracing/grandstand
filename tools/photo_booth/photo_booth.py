"""Grandstand photo booth (2026-10-25, Angelo: "more realistic shading", "a front view for the large pictures",
"once per session for each car that makes the start", "on the formation lap").

Watches iRacing. When a REPLAY of a race is on its formation / pace laps (never while you drive), it photographs every
starter from the front with iRacing's own Rear Chase camera, capturing only the iRacing window (Windows Graphics
Capture, so Grandstand's graphics are not in the shot). The replay director is paused for the shoot (about 1.2 s a car)
and handed back straight after. Then, in the background, each shot has its background cut away (rembg, isnet model) and
is saved to Documents\\iRacing\\Grandstand\\livery\\photos\\<car>_<user>_<team>_<paint stamp>_front.png, which
Grandstand uses for its big car pictures. A car whose paint hasn't changed is never shot twice.

Run: photo_venv\\Scripts\\python.exe photo_booth.py   (Grandstand starts it; one copy at a time)
"""
import ctypes, json, os, sys, time, urllib.request, threading, traceback
import irsdk
from PIL import Image

DOCS = os.path.join(os.path.expanduser("~"), "Documents", "iRacing")
OUT = os.path.join(DOCS, "Grandstand", "livery", "photos")
RAW = os.path.join(OUT, "_raw")
DONE = os.path.join(OUT, "_sessions.txt")
LOG = os.path.join(OUT, "_photo_booth.log")
API = "http://127.0.0.1:8790/api/settings"
os.makedirs(RAW, exist_ok=True)


def log(msg):
    line = time.strftime("%H:%M:%S ") + msg
    print(line, flush=True)
    try:
        with open(LOG, "a", encoding="utf-8") as f: f.write(line + "\n")
    except OSError: pass


def single_instance():
    h = ctypes.windll.kernel32.CreateMutexW(None, False, "Grandstand.PhotoBooth")
    if ctypes.windll.kernel32.GetLastError() == 183:   # already running
        sys.exit(0)
    return h


def paint_stamp(car, uid, tid):
    d = os.path.join(DOCS, "paint", car)
    for name in ([f"car_num_team_{tid}.tga", f"car_team_{tid}.tga"] if tid else []) + ([f"car_num_{uid}.tga", f"car_{uid}.tga"] if uid else []):
        p = os.path.join(d, name)
        if os.path.exists(p): return format(int(os.path.getmtime(p)), "x")
    return "ir"


def photo_name(car, uid, tid, stamp): return f"{car}_{uid}_{tid}_{stamp}_front.png"


def director(enable):
    """Pause / restore Grandstand's replay director (returns the previous settings to restore)."""
    try:
        cur = json.loads(urllib.request.urlopen(API, timeout=3).read())
        new = dict(cur)
        for k in list(new):
            if k.lower() == "director": new[k] = enable
        urllib.request.urlopen(urllib.request.Request(API, data=json.dumps(new).encode(), method="POST",
                               headers={"X-Grandstand": "1", "Content-Type": "application/json"}), timeout=3).read()
        return cur
    except Exception as e:
        log(f"director switch failed: {e}")
        return None


def restore(settings):
    if settings is None: return
    try:
        urllib.request.urlopen(urllib.request.Request(API, data=json.dumps(settings).encode(), method="POST",
                               headers={"X-Grandstand": "1", "Content-Type": "application/json"}), timeout=3).read()
    except Exception as e: log(f"restore failed: {e}")


def grab(path):
    from windows_capture import WindowsCapture
    state = {"ok": False}
    cap = WindowsCapture(cursor_capture=False, draw_border=False, window_name="iRacing.com Simulator")

    @cap.event
    def on_frame_arrived(frame, control):
        frame.save_as_image(path); state["ok"] = True; control.stop()

    @cap.event
    def on_closed(): pass
    cap.start()
    return state["ok"]


# ------------------------------------------------------------------ cut-outs (background thread)
_session = None
def cutout(raw_path, out_path):
    global _session
    from rembg import new_session, remove
    import numpy as np
    from scipy import ndimage
    if _session is None: _session = new_session("isnet-general-use")
    im = Image.open(raw_path).convert("RGB")
    W, H = im.size
    # the Rear Chase camera keeps the car around the middle of the frame
    box = (int(W * 0.22), int(H * 0.18), int(W * 0.82), int(H * 0.80))
    crop = im.crop(box)
    cut = remove(crop, session=_session)
    a = np.asarray(cut.getchannel("A")) > 60
    lab, n = ndimage.label(a)
    if n == 0: return False
    # keep the biggest blob nearest the centre (a car following close behind shows up above it)
    cy, cx = a.shape[0] / 2, a.shape[1] / 2
    best, score = 0, -1
    for i in range(1, n + 1):
        ys, xs = np.nonzero(lab == i)
        if len(xs) < 2000: continue
        d = ((xs.mean() - cx) ** 2 + (ys.mean() - cy) ** 2) ** 0.5
        sc = len(xs) / (1 + d / 200)
        if sc > score: best, score = i, sc
    if best == 0: return False
    keep = ndimage.binary_dilation(lab == best, iterations=6)
    alpha = np.asarray(cut.getchannel("A")).copy(); alpha[~keep] = 0
    cut.putalpha(Image.fromarray(alpha))
    bb = cut.getbbox()
    if not bb or bb[2] - bb[0] < 160: return False                    # too small / not found: no picture
    if bb[0] <= 2 or bb[2] >= cut.size[0] - 2: return False           # cut off at the edge
    cut = cut.crop((max(0, bb[0] - 6), max(0, bb[1] - 6), min(cut.size[0], bb[2] + 6), min(cut.size[1], bb[3] + 6)))
    # a car seen from the front is wider than tall (prototypes ~0.7, GT cars ~0.75); taller = another car merged in
    ratio = cut.size[1] / cut.size[0]
    if ratio > 0.80: return None
    return cut, ratio


def process_queue(jobs):
    ok = 0
    for raws, out, label in jobs:
        try:
            best = None
            for raw in raws:
                r = cutout(raw, out)
                if r and (best is None or r[1] < best[1]): best = r
            if best:
                tmp = out + ".tmp.png"; best[0].save(tmp); os.replace(tmp, out)
                ok += 1; log(f"photo ready: {label}")
            else: log(f"photo rejected (car not clear): {label}")
        except Exception as e: log(f"cut-out failed {label}: {e!r}")
        finally:
            for raw in raws:
                try: os.remove(raw)
                except OSError: pass
    log(f"cut-outs done: {ok}/{len(jobs)}")


def two_shots(raw_base):
    """Two frames half a second apart (the cleaner one is kept)."""
    shots = []
    for k in (0, 1):
        p = raw_base.replace(".png", f"_{k}.png")
        if grab(p): shots.append(p)
        if k == 0: time.sleep(0.5)
    return shots


# ------------------------------------------------------------------ the shoot
def done_sessions():
    try: return set(open(DONE, encoding="utf-8").read().split())
    except OSError: return set()


def keep_going(ir, live):
    """Stop the moment it isn't safe / useful any more: you got in the car (live), or the formation laps ended (replay)."""
    if ir["IsOnTrack"]: return False
    if live: return not ir["IsReplayPlaying"] and not ir["IsInGarage"]
    return ir["IsReplayPlaying"] and ir["SessionState"] in (2, 3)


def shoot(ir, key, live=False):
    groups = {g["GroupName"]: g["GroupNum"] for g in ir["CameraInfo"]["Groups"]}
    cam = groups.get("Rear Chase")
    if cam is None: log("no Rear Chase camera here"); return
    drivers = ir["DriverInfo"]["Drivers"]
    surf = ir["CarIdxTrackSurface"] or []
    todo = []
    for d in drivers:
        if d.get("CarIsPaceCar") or d.get("IsSpectator") or d["CarIdx"] >= len(surf) or surf[d["CarIdx"]] < 0: continue
        car, uid, tid = d["CarPath"], d.get("UserID", 0), d.get("TeamID", 0)
        out = os.path.join(OUT, photo_name(car, uid, tid, paint_stamp(car, uid, tid)))
        if not os.path.exists(out): todo.append((d, out))
    log(f"shoot {key}: {len(todo)} cars need a photo")
    if not todo: return
    cam0 = (ir["CamCarIdx"], ir["CamGroupNumber"])
    saved = director(False)
    jobs = []
    try:
        for d, out in todo:
            if not keep_going(ir, live): log("stopping the shoot (in the car / formation over)"); break
            ir.cam_switch_num(d["CarNumber"], cam, 0)
            time.sleep(1.1)
            raw = os.path.join(RAW, os.path.basename(out).replace("_front.png", ".png"))
            shots = two_shots(raw)
            if shots: jobs.append((shots, out, f"#{d['CarNumber']} {d['UserName']}"))
    finally:
        try:
            if not ir["IsOnTrack"]:      # never touch the camera once you're driving
                num = next(x["CarNumber"] for x in drivers if x["CarIdx"] == cam0[0])
                ir.cam_switch_num(num, cam0[1], 0)
        except Exception: pass
        restore(saved)
    log(f"shoot {key}: {len(jobs)} shots taken, cutting out in the background")
    threading.Thread(target=process_queue, args=(jobs,), daemon=True).start()


def main():
    single_instance()
    ctypes.windll.user32.SetProcessDPIAware()
    ir = irsdk.IRSDK()
    last_live = {}
    log("photo booth watching")
    while True:
        try:
            if not ir.is_initialized or not ir.is_connected:
                ir.shutdown(); ir.startup(); time.sleep(3); continue
            on_track = ir["IsOnTrack"] and not ir["IsReplayPlaying"]
            sess = ir["SessionInfo"]["Sessions"][ir["SessionNum"]]
            race = "race" in str(sess.get("SessionType", "")).lower()
            key = f"{ir['WeekendInfo'].get('SubSessionID', 0)}:{ir['SessionNum']}"
            # formation / pace laps (SessionState 3 = parade laps; 2 = warm-up grid) of a replay, not driving
            if not on_track and ir["IsReplayPlaying"] and race and ir["SessionState"] == 3 and ir["ReplayPlaySpeed"] == 1 \
                    and key not in done_sessions():
                with open(DONE, "a", encoding="utf-8") as f: f.write(key + "\n")
                shoot(ir, key)
            # live session, you're out of the car watching (not in the garage screen, not driving): photograph the cars
            # that have no photo yet - checked every 5 minutes, so late joiners get theirs too
            elif not ir["IsReplayPlaying"] and not ir["IsOnTrack"] and not ir["IsInGarage"] and time.time() - last_live.get(key, 0) > 300:
                last_live[key] = time.time()
                shoot(ir, "live " + key, live=True)
        except Exception as e:
            log("error: " + repr(e)); traceback.print_exc()
            time.sleep(5)
        time.sleep(2)


if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] == "now":
        # manual test: shoot whatever is on screen right now (any session state)
        ir = irsdk.IRSDK(); ir.startup(); ctypes.windll.user32.SetProcessDPIAware()
        globals()["shoot_any"] = True
        groups_ok = True
        drivers = ir["DriverInfo"]["Drivers"]
        n = int(sys.argv[2]) if len(sys.argv) > 2 else 3
        surf = ir["CarIdxTrackSurface"] or []
        cam = {g["GroupName"]: g["GroupNum"] for g in ir["CameraInfo"]["Groups"]}["Rear Chase"]
        cam0 = (ir["CamCarIdx"], ir["CamGroupNumber"]); saved = director(False); jobs = []
        try:
            for d in [x for x in drivers if not x.get("CarIsPaceCar") and x["CarIdx"] < len(surf) and surf[x["CarIdx"]] >= 0][:n]:
                car, uid, tid = d["CarPath"], d.get("UserID", 0), d.get("TeamID", 0)
                out = os.path.join(OUT, photo_name(car, uid, tid, paint_stamp(car, uid, tid)))
                ir.cam_switch_num(d["CarNumber"], cam, 0); time.sleep(1.1)
                raw = os.path.join(RAW, os.path.basename(out).replace("_front.png", ".png"))
                shots = two_shots(raw)
                if shots: jobs.append((shots, out, f"#{d['CarNumber']} {d['UserName']}"))
        finally:
            num = next(x["CarNumber"] for x in drivers if x["CarIdx"] == cam0[0]); ir.cam_switch_num(num, cam0[1], 0); restore(saved)
        process_queue(jobs)
    else:
        main()
