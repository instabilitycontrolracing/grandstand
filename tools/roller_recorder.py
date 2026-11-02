"""Record your own roller from a replay (2026-10-26, Grandstand #39).

Started by Grandstand's control app (Settings > Class transitions > Record next to a class): records the next few
seconds of the iRacing window (Windows Graphics Capture, so Grandstand's own graphics are NOT in it) at 30 frames a
second into Documents\\iRacing\\Grandstand\\transitions\\<class>\\, which the timing tower plays as that class's
transition. The frames that were there before are moved to an _old_<date> folder next to them (nothing is deleted).
Then Grandstand is asked to read the folder again.

usage: pythonw roller_recorder.py --cls gt3 --sec 3
"""
import argparse, ctypes, json, os, shutil, sys, threading, time, urllib.request

FPS = 30
OUT_W = 1920
ROOT = os.path.join(os.path.expanduser("~"), "Documents", "iRacing", "Grandstand", "transitions")
STATUS = os.path.join(ROOT, "_roller_status.json")
KEYS = ("gt3", "gtp", "lmp2", "gtd", "overall")


def status(**kw):
    kw["t"] = time.time()
    try:
        os.makedirs(ROOT, exist_ok=True)
        with open(STATUS, "w", encoding="utf-8") as f:
            json.dump(kw, f)
    except OSError:
        pass


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--cls", required=True)
    ap.add_argument("--sec", type=float, default=3.0)
    a = ap.parse_args()
    cls = a.cls.lower()
    if cls not in KEYS:
        status(state="error", cls=cls, msg="unknown class"); return
    sec = max(1.0, min(8.0, a.sec))
    m = ctypes.windll.kernel32.CreateMutexW(None, False, "Grandstand.RollerRecorder")
    if ctypes.windll.kernel32.GetLastError() == 183:
        status(state="error", cls=cls, msg="a recording is already running"); return
    if not ctypes.windll.user32.FindWindowW(None, "iRacing.com Simulator"):
        status(state="error", cls=cls, msg="iRacing isn't open"); return

    import cv2
    from windows_capture import WindowsCapture
    tmp = os.path.join(ROOT, f"_rec_{cls}_{time.strftime('%Y%m%d_%H%M%S')}")
    os.makedirs(tmp, exist_ok=True)
    st = {"n": 0, "t0": None, "next": 0.0, "done": threading.Event(), "err": ""}
    status(state="recording", cls=cls, sec=sec, frames=0)

    cap = WindowsCapture(cursor_capture=False, draw_border=False, window_name="iRacing.com Simulator")

    @cap.event
    def on_frame_arrived(frame, control):
        try:
            now = time.perf_counter()
            if st["t0"] is None:
                st["t0"] = now
            el = now - st["t0"]
            if el >= sec:
                st["done"].set(); control.stop(); return
            if el + 1e-4 < st["next"]:
                return
            st["next"] += 1.0 / FPS
            img = frame.frame_buffer[:, :, :3]
            h, w = img.shape[:2]
            if w > OUT_W:
                img = cv2.resize(img, (OUT_W, int(round(h * OUT_W / w))), interpolation=cv2.INTER_AREA)
            st["n"] += 1
            cv2.imwrite(os.path.join(tmp, f"f{st['n']:03d}.jpg"), img, [cv2.IMWRITE_JPEG_QUALITY, 90])
        except Exception as e:
            st["err"] = repr(e); st["done"].set(); control.stop()

    @cap.event
    def on_closed():
        st["done"].set()

    ctl = cap.start_free_threaded()
    if not st["done"].wait(sec + 10):
        try: ctl.stop()
        except Exception: pass
    if st["n"] < FPS // 2:
        shutil.rmtree(tmp, ignore_errors=True)
        status(state="error", cls=cls, msg=st["err"] or "no picture from iRacing (is the replay window minimised?)"); return
    # swap it in: the old frames go to _old_<date>, the new ones become the class's roller
    dest = os.path.join(ROOT, cls)
    os.makedirs(dest, exist_ok=True)
    old = [f for f in os.listdir(dest) if f.lower().endswith((".jpg", ".jpeg", ".png"))]
    if old:
        keep = os.path.join(dest, f"_old_{time.strftime('%Y%m%d_%H%M%S')}")
        os.makedirs(keep, exist_ok=True)
        for f in old:
            shutil.move(os.path.join(dest, f), os.path.join(keep, f))
    for f in sorted(os.listdir(tmp)):
        shutil.move(os.path.join(tmp, f), os.path.join(dest, f))
    shutil.rmtree(tmp, ignore_errors=True)
    try:
        req = urllib.request.Request("http://127.0.0.1:8790/api/transitions/rescan", data=b"", method="POST", headers={"X-Grandstand": "1"})
        urllib.request.urlopen(req, timeout=3).read()
    except Exception:
        pass
    status(state="done", cls=cls, frames=st["n"], kept_old=bool(old))


if __name__ == "__main__":
    try:
        main()
    except Exception as e:
        status(state="error", msg=repr(e))
