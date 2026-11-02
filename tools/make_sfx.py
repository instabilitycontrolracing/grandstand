"""Grandstand - original sound effects for the PUBLIC build (2026-10-24).

Every sound here is synthesised from scratch (sines, saws and filtered noise - no samples, no
recordings), so the output is ours: released as CC0 (public domain). The files go to
assets/audio_public/ and are committed. Grandstand plays a sound from
    assets/audio/<name>                          (the owner's personal sounds - never shipped)
    Documents/iRacing/Grandstand/audio/<name>    (a user's own sounds)
    assets/audio_public/<name>                   (these)
in that order (SoundFiles.cs).

Levels: every file is normalised to a -7 dBFS peak (the brief says <= -6 dBFS), 44.1 kHz 16-bit
stereo PCM WAV (what Windows' PlaySound and WPF's MediaPlayer both play).

Timings follow the graphics they go with (read from the C# sources):
  pre_poles     five start-light beeps at 0.35 + 0.2*i s, "lights out" at 1.70 s  (PreRaceCards.Panels.cs)
  pre_globe     spin 0.2-3.0, lock-on 1.6-3.0, dive 3.0-6.3, pin 5.5, rings 6.0+   (GlobeIntro.cs, 8.6 s)
  grid_opener   hits at 0.00 / 2.53 / 4.40 / 8.00 / 9.21 / 12.07 / 19.75 / 25.52 / 32.21, end 35.39 s
                (GridIntro.cs - the grid intro's pages land on these)

Usage:  python tools/make_sfx.py            (needs numpy + scipy; writes assets/audio_public/*.wav)
"""
import os
import sys
import wave

import numpy as np
from scipy import signal

SR = 44100
PEAK_DBFS = -7.0
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "assets", "audio_public")
rng = np.random.default_rng(20261024)


# ----------------------------------------------------------------------------- building blocks
def t_axis(dur):
    return np.arange(int(dur * SR)) / SR


def env_ad(n, attack, decay, curve=4.0):
    """Attack (s) then exponential decay to silence over `decay` s; n samples long."""
    t = np.arange(n) / SR
    e = np.where(t < attack, t / max(attack, 1e-4), np.exp(-curve * (t - attack) / max(decay, 1e-4)))
    return e


def sine_sweep(f0, f1, dur, shape=2.0):
    t = t_axis(dur)
    k = (t / dur) ** shape
    f = f0 + (f1 - f0) * k
    return np.sin(2 * np.pi * np.cumsum(f) / SR)


def saw(freq, dur, detune=(0.0,)):
    t = t_axis(dur)
    out = np.zeros_like(t)
    for d in detune:
        f = freq * (2 ** (d / 1200))
        ph = rng.random()
        out += 2 * ((t * f + ph) % 1.0) - 1
    return out / len(detune)


def noise(dur):
    return rng.standard_normal(int(dur * SR))


def bandpass_sweep(x, f0, f1, q=2.0, steps=48):
    """Band-pass filter whose centre glides from f0 to f1 (exponential), done in short blocks."""
    n = len(x)
    out = np.zeros(n)
    edges = np.linspace(0, n, steps + 1).astype(int)
    zi = None
    for i in range(steps):
        a, b = edges[i], edges[i + 1]
        fc = f0 * (f1 / f0) ** (i / max(steps - 1, 1))
        fc = min(max(fc, 30), SR / 2.3)
        bw = fc / q
        lo, hi = max(fc - bw / 2, 20), min(fc + bw / 2, SR / 2.1)
        bb, aa = signal.butter(2, [lo, hi], btype="band", fs=SR)
        if zi is None or len(zi) != max(len(aa), len(bb)) - 1:
            zi = signal.lfilter_zi(bb, aa) * 0
        out[a:b], zi = signal.lfilter(bb, aa, x[a:b], zi=zi)
    return out


def lowpass(x, fc, order=2):
    b, a = signal.butter(order, min(fc, SR / 2.2), btype="low", fs=SR)
    return signal.lfilter(b, a, x)


def highpass(x, fc, order=2):
    b, a = signal.butter(order, fc, btype="high", fs=SR)
    return signal.lfilter(b, a, x)


def reverb(x, seconds=1.2, mix=0.25, tone=5000):
    """Cheap, smooth tail: convolve with exponentially decaying filtered noise (stereo decorrelated)."""
    n = int(seconds * SR)
    t = np.arange(n) / SR
    outs = []
    for _ in range(2):
        ir = lowpass(rng.standard_normal(n), tone) * np.exp(-6.0 * t / seconds)
        ir[: int(0.012 * SR)] *= np.linspace(0, 1, int(0.012 * SR))
        ir /= np.sqrt(np.sum(ir ** 2)) + 1e-9
        wet = np.concatenate([signal.fftconvolve(x, ir), [0.0]])[: len(x) + n]
        dry = np.concatenate([x, np.zeros(n)])
        outs.append(dry * (1 - mix) + wet * mix * 2.2)
    return np.stack(outs, axis=1)


def place(buf, x, at):
    """Mix mono/stereo x into stereo buf starting at `at` seconds (buf grows if needed)."""
    if x.ndim == 1:
        x = np.stack([x, x], axis=1)
    i = int(at * SR)
    need = i + len(x)
    if need > len(buf):
        buf = np.concatenate([buf, np.zeros((need - len(buf), 2))])
    buf[i:need] += x
    return buf


def pan(x, p):
    """p = -1 (left) .. 1 (right), equal power."""
    a = (p + 1) * np.pi / 4
    return np.stack([x * np.cos(a), x * np.sin(a)], axis=1)


def silence(dur):
    return np.zeros((int(dur * SR), 2))


# ----------------------------------------------------------------------------- string section (2026-10-26, #217)
# Angelo: "the stingers need work, they need to sound like a swell of cellos or something". A bowed string section
# made from scratch: every player is a band-limited bowed tone (all harmonics, 1/n, the 2nd and 3rd a little strong
# like a cello), with their own slight tuning, vibrato that comes in late and a hint of bow noise; the cello's
# wooden body (resonances near 200, 450 Hz and 2.5 kHz); the tone opens up as the bow digs in (the swell), and the
# whole section sits in a big hall.
def bowed(freq, dur, swell_at, release, players=6, vib_cents=14.0, tremolo=0.0):
    t = t_axis(dur)
    n = len(t)
    out = np.zeros(n)
    nh = max(1, int(min(9000, SR / 2.2) // freq))
    for p in range(players):
        f = freq * 2 ** (rng.normal(0, 4.0) / 1200)
        rate = 5.2 + rng.random() * 1.2
        vdepth = vib_cents * (0.7 + 0.6 * rng.random()) * np.clip((t - 0.25) / 0.5, 0, 1)
        drift = np.cumsum(rng.normal(0, 1, n)) / np.sqrt(SR) * 0.6          # tiny bow-speed wander (cents)
        inst = f * 2 ** ((vdepth * np.sin(2 * np.pi * rate * t + rng.random() * 6.28) + drift) / 1200)
        ph = 2 * np.pi * np.cumsum(inst) / SR + rng.random() * 6.28
        v = np.zeros(n)
        for h in range(1, nh + 1):
            a = 1.0 / h * (1.35 if h in (2, 3) else 1.0)
            v += a * np.sin(h * ph)
        start = rng.random() * 0.05                                         # players never start together
        v *= np.clip((t - start) / 0.02, 0, 1)
        out += v
    out /= players
    out += highpass(noise(dur), 2500) * 0.02                                # rosin / bow noise
    # the swell: from almost nothing up to full at swell_at, held, then a bowed release
    e = np.clip(t / swell_at, 0, 1) ** 2.2
    e = np.where(t > dur - release, e * np.clip((dur - t) / release, 0, 1) ** 1.5, e)
    if tremolo:
        e *= 0.72 + 0.28 * np.sign(np.sin(2 * np.pi * tremolo * t))
    # brightness follows the bow: dark at the start, open at the peak (filtered in blocks)
    steps = 40
    edges = np.linspace(0, n, steps + 1).astype(int)
    y = np.zeros(n)
    zi = None
    for i in range(steps):
        a_, b_ = edges[i], edges[i + 1]
        lvl = e[min(b_ - 1, n - 1)]
        fc = 600 + 4200 * lvl
        bb, aa = signal.butter(2, fc, btype="low", fs=SR)
        if zi is None:
            zi = signal.lfilter_zi(bb, aa) * 0
        y[a_:b_], zi = signal.lfilter(bb, aa, out[a_:b_], zi=zi)
    y = body(y)
    return y * e


def peq(x, fc, gain_db, q):
    A = 10 ** (gain_db / 40)
    w = 2 * np.pi * fc / SR
    al = np.sin(w) / (2 * q)
    b = [1 + al * A, -2 * np.cos(w), 1 - al * A]
    a = [1 + al / A, -2 * np.cos(w), 1 - al / A]
    return signal.lfilter(b, a, x)


def body(x):
    x = peq(x, 210, 5.0, 1.6)
    x = peq(x, 450, 3.0, 2.0)
    x = peq(x, 900, -3.0, 1.0)
    x = peq(x, 2500, 3.0, 1.4)
    return highpass(x, 55)


def section(chord, dur=2.2, swell_at=0.9, release=0.9, spread=0.7, players=6, tremolo=0.0, hall=2.6, low=None):
    """A chord for the string section, each note across the stage (low notes to the left, higher ones to the right, like an
    orchestra's seating), in a hall."""
    buf = silence(dur)
    k = len(chord)
    for i, f in enumerate(chord):
        p = -spread + 2 * spread * (i / max(k - 1, 1))
        lv = 1.0 if f < 330 else 0.75
        buf = place(buf, pan(bowed(f, dur, swell_at, release, players, tremolo=tremolo) * lv, p), 0.0)
    if low:                                                     # double basses an octave below the root
        buf = place(buf, pan(bowed(low, dur, swell_at, release, 4, vib_cents=8) * 0.8, -0.2), 0.0)
    wet = reverb(buf[:, 0] * 0.6 + buf[:, 1] * 0.4, hall, 1.0, tone=4200)   # a big hall, a little brighter on the right
    m = len(wet)
    out = np.concatenate([buf, np.zeros((m - len(buf), 2))])
    return out * 0.8 + wet * 0.35


def hz(name):
    """Note name -> Hz, e.g. 'D2', 'F#3', 'Bb3'."""
    names = {"C": 0, "D": 2, "E": 4, "F": 5, "G": 7, "A": 9, "B": 11}
    n = names[name[0]]
    rest = name[1:]
    if rest[0] in "#b":
        n += 1 if rest[0] == "#" else -1
        rest = rest[1:]
    return 440.0 * 2 ** ((n + 12 * (int(rest) + 1) - 69) / 12)


def chord(*ns):
    return [hz(x) for x in ns]


# ----------------------------------------------------------------------------- instruments
def whoosh(dur=0.7, f0=300, f1=4500, peak_at=0.6, width=1.0, stereo_move=0.7):
    n = int(dur * SR)
    x = bandpass_sweep(noise(dur), f0, f1, q=1.6)
    t = np.arange(n) / n
    e = np.where(t < peak_at, (t / peak_at) ** 2.2, np.exp(-5 * (t - peak_at) / (1 - peak_at + 1e-6)))
    x = x * e
    # moving across the stereo field
    p = np.linspace(-stereo_move, stereo_move, n)
    a = (p + 1) * np.pi / 4
    st = np.stack([x * np.cos(a), x * np.sin(a)], axis=1)
    return st * width


def tick(freq=2400, dur=0.06, level=1.0):
    n = int(dur * SR)
    t = np.arange(n) / SR
    body = np.sin(2 * np.pi * freq * t) * np.exp(-t * 90)
    click = highpass(noise(dur), 3000) * np.exp(-t * 400) * 0.6
    return (body + click) * level


def kick(dur=0.6, f0=150, f1=42, level=1.0):
    n = int(dur * SR)
    t = np.arange(n) / SR
    f = f1 + (f0 - f1) * np.exp(-t * 28)
    x = np.sin(2 * np.pi * np.cumsum(f) / SR) * np.exp(-t * 5.5)
    x += highpass(noise(dur), 1500) * np.exp(-t * 120) * 0.25
    return np.tanh(x * 1.6) * level


def impact(dur=2.2, level=1.0, bright=1.0):
    """Cinematic hit: sub drop + noise crack + metallic shimmer, through a short room."""
    k = kick(min(dur, 1.0), f0=110, f1=34)
    n = int(dur * SR)
    t = np.arange(n) / SR
    crack = bandpass_sweep(noise(dur), 5000, 900, q=1.2) * np.exp(-t * 9) * 0.55 * bright
    shimmer = np.zeros(n)
    for f in (1840, 2770, 3910, 5230):
        shimmer += np.sin(2 * np.pi * f * t + rng.random() * 6) * np.exp(-t * (2.5 + f / 2500))
    shimmer *= 0.05 * bright
    x = np.zeros(n)
    x[: len(k)] += k
    x += crack + shimmer
    return reverb(x, seconds=1.6, mix=0.3) * level


def riser(dur=1.5, f0=200, f1=2400, level=1.0):
    n = int(dur * SR)
    t = np.arange(n) / n
    nz = bandpass_sweep(noise(dur), f0 * 2, f1 * 3, q=2.5) * 0.8
    tone = sine_sweep(f0, f1, dur, shape=2.4) * 0.25
    x = (nz + tone) * (t ** 2.4)
    x[-int(0.01 * SR):] *= np.linspace(1, 0, int(0.01 * SR))
    return pan(x, 0) * level


def chord_pad(freqs, dur, level=0.3, cutoff=1800, attack=0.05, release=0.8):
    x = np.zeros(int(dur * SR))
    for f in freqs:
        x += saw(f, dur, detune=(-9, 0, 8))
    x = lowpass(x / len(freqs), cutoff, order=2)
    n = len(x)
    e = np.minimum(1, np.arange(n) / (attack * SR + 1)) * np.minimum(1, (n - np.arange(n)) / (release * SR + 1))
    return x * e * level


def blip(freq, dur=0.09, level=1.0):
    n = int(dur * SR)
    t = np.arange(n) / SR
    x = np.sin(2 * np.pi * freq * t) * np.minimum(1, t / 0.004) * np.exp(-t * 26)
    x += 0.3 * np.sin(2 * np.pi * freq * 2 * t) * np.exp(-t * 40)
    return x * level


def beep(freq, dur=0.16, level=1.0):
    n = int(dur * SR)
    t = np.arange(n) / SR
    sq = np.sign(np.sin(2 * np.pi * freq * t)) * 0.35 + np.sin(2 * np.pi * freq * t) * 0.65
    e = np.minimum(1, t / 0.004) * np.minimum(1, (dur - t) / 0.02)
    return lowpass(sq, 5000) * e * level


def ping(freq=1320, dur=1.8, level=1.0):
    n = int(dur * SR)
    t = np.arange(n) / SR
    x = np.sin(2 * np.pi * freq * t) * np.exp(-t * 3.2) * np.minimum(1, t / 0.003)
    x += 0.35 * np.sin(2 * np.pi * freq * 1.5 * t) * np.exp(-t * 6)
    st = reverb(x, seconds=1.4, mix=0.35, tone=7000)
    # sonar echo
    d = int(0.32 * SR)
    st[d:] += st[:-d] * 0.35
    return st * level


def hum(dur, freq=55, level=1.0):
    t = t_axis(dur)
    x = np.sin(2 * np.pi * freq * t) + 0.5 * np.sin(2 * np.pi * freq * 2.01 * t) + 0.25 * np.sin(2 * np.pi * freq * 3.03 * t)
    x *= 0.6 + 0.4 * np.sin(2 * np.pi * 0.35 * t)
    air = lowpass(noise(dur), 600) * 0.4
    return (x * 0.5 + air) * level


def fade(st, fin=0.005, fout=0.05):
    n = len(st)
    a, b = int(fin * SR), int(fout * SR)
    e = np.ones(n)
    if a: e[:a] = np.linspace(0, 1, a)
    if b: e[-b:] = np.minimum(e[-b:], np.linspace(1, 0, b))
    return st * e[:, None]


# ----------------------------------------------------------------------------- the cues
def notes(*semis, root=220.0):
    return [root * 2 ** (s / 12) for s in semis]


def stinger(semis, root=220.0, dur=1.6, bright=1.0, swoosh=True):
    """Short neutral broadcast stinger: whoosh in, a hit, a chord that rings out."""
    buf = silence(0.01)
    if swoosh:
        buf = place(buf, whoosh(0.38, 500, 6000, peak_at=0.95, width=0.45), 0.0)
    at = 0.34 if swoosh else 0.0
    buf = place(buf, impact(1.4, level=0.55, bright=bright), at)
    pad = chord_pad(notes(*semis, root=root), dur, level=0.32, cutoff=2600, attack=0.01, release=dur * 0.7)
    buf = place(buf, reverb(pad, 1.0, 0.3), at)
    for i, f in enumerate(notes(*semis, root=root * 2)[:3]):
        buf = place(buf, pan(blip(f, 0.25, 0.22), (i - 1) * 0.5), at + 0.02 + i * 0.045)
    return buf


def cues():
    c = {}
    # ---- 2026-10-27 visual pack ----
    # W9: the lighter cue for small lower-third cards (driver change on the featured card, team radio card): a short
    # airy whoosh with a soft high pluck on top - the cello stays for the big graphics
    lt = whoosh(0.42, 900, 6000, 0.55, 0.55, 0.5)
    lt = place(lt, reverb(blip(1760, 0.18, 0.35), 0.6, 0.25, 7000), 0.16)
    c["lowerthird_light"] = lt
    # W13: the timing-screen purple-sector beep: two short clean tones a fifth apart
    tb = place(silence(0.01), blip(1568, 0.07, 0.8), 0.0)
    c["timing_beep"] = place(tb, blip(2349, 0.09, 0.7), 0.085)
    # ---- story cards (StorySound.CueFor) ----
    c["story_racesofar"] = stinger((0, 7, 12, 16), root=196.0)
    c["story_yourrace"] = stinger((0, 4, 7, 11), root=220.0, bright=1.1)
    c["story_moveofrace"] = place(stinger((0, 5, 7, 12), root=233.08), riser(0.4, 400, 3000, 0.25), 0.0)
    fl = silence(0.01)
    for i in range(3):
        fl = place(fl, pan(tick(3200 - i * 300, 0.05, 0.5), -0.6 + i * 0.6), i * 0.075)
    c["story_fastestlaps"] = place(fl, stinger((0, 7, 14), root=261.63, bright=1.2, swoosh=False), 0.24)
    c["story_pitstops"] = place(stinger((0, 3, 7, 10), root=174.61), whoosh(0.5, 200, 2500, 0.4, 0.4), 0.55)
    c["story_halfway"] = stinger((0, 7, 12), root=146.83, dur=2.0)
    wx = place(stinger((0, 2, 7, 9), root=196.0, bright=0.8), ping(1568, 1.4, 0.35), 0.38)
    c["story_weather"] = wx
    c["story_driverofday"] = place(stinger((0, 4, 7, 12, 16), root=261.63, dur=2.2, bright=1.2), riser(0.35, 600, 4000, 0.2), 0.0)
    ct = place(silence(0.01), impact(1.4, level=0.9, bright=1.4), 0.0)
    c["story_contact"] = place(ct, reverb(chord_pad(notes(0, 1, 6, root=110.0), 1.2, 0.3, 1200), 0.8, 0.3), 0.02)
    lim = silence(0.01)
    for i in range(2):
        lim = place(lim, beep(880, 0.11, 0.45), i * 0.16)
    c["story_limits"] = place(lim, stinger((0, 3, 7), root=207.65, swoosh=False), 0.32)
    c["story_clean"] = stinger((0, 4, 7, 14), root=293.66, bright=0.7)
    c["story_exit"] = whoosh(0.55, 4000, 400, peak_at=0.25, width=0.55, stereo_move=-0.8)

    # ---- pre-race info cards (PreRaceCards.CueSound) ----
    c["pre_panel_in"] = place(whoosh(0.5, 300, 5000, 0.7, 0.8), impact(1.0, 0.35, 0.8), 0.33)
    wthr = place(silence(0.01), ping(1175, 2.0, 0.8), 0.05)
    c["pre_weather"] = place(wthr, ping(1175, 1.2, 0.3), 0.75)
    poles = silence(0.01)
    for i in range(5):
        poles = place(poles, beep(660, 0.14, 0.7), 0.35 + 0.2 * i)
    poles = place(poles, whoosh(0.7, 200, 6000, 0.35, 0.9), 1.55)
    poles = place(poles, impact(1.2, 0.5, 1.0), 1.70)
    c["pre_poles"] = poles
    # a neutral partner panel (the old cue was a nail gun + hammer for a roofing partner)
    ab = place(whoosh(0.45, 300, 4500, 0.75, 0.7), impact(1.2, 0.5), 0.36)
    for i in range(3):
        ab = place(ab, pan(tick(1800 + 500 * i, 0.07, 0.5), -0.5 + 0.5 * i), 0.6 + 0.09 * i)
    c["pre_ab"] = ab
    # radar sweep (a neutral weather partner panel)
    st = silence(0.01)
    sweep = bandpass_sweep(noise(2.4), 300, 3000, q=6) * (0.4 + 0.6 * np.sin(np.linspace(0, np.pi, int(2.4 * SR))))
    st = place(st, pan(sweep * 0.6, 0) , 0.0)
    for i, at in enumerate((0.5, 1.1, 1.7)):
        st = place(st, ping(988 + 220 * i, 1.0, 0.3), at)
    c["pre_storm"] = st
    # globe flight: hum, lock-on blips, the dive, the pin landing, rings
    g = place(silence(0.01), pan(hum(8.4, 49, 0.35) * np.minimum(1, t_axis(8.4) / 0.6) * np.minimum(1, (8.4 - t_axis(8.4)) / 1.2), 0), 0.0)
    for i, at in enumerate(np.linspace(1.6, 2.9, 7)):
        g = place(g, pan(blip(1760 if i % 2 == 0 else 2093, 0.07, 0.35), 0.4 * (-1) ** i), at)
    g = place(g, riser(3.0, 120, 1800, 0.5), 3.0)
    g = place(g, whoosh(1.2, 200, 7000, 0.85, 0.7), 4.6)
    g = place(g, impact(2.2, 0.8, 1.1), 5.5)
    for i in range(4):
        g = place(g, ping(1318.5, 0.9, 0.18), 6.0 + 0.45 * i)
    c["pre_globe"] = g

    # ---- grid / outro ----
    c["grid_tick"] = place(silence(0.01), tick(2600, 0.08, 0.7), 0.0)
    flag = place(riser(0.6, 300, 3000, 0.35), impact(2.4, 0.9, 1.2), 0.55)
    flag = place(flag, reverb(chord_pad(notes(0, 7, 12, 16, root=196.0), 2.6, 0.35, 3000, 0.02, 1.8), 1.4, 0.3), 0.57)
    c["outro_flag"] = flag
    c["outro_podium"] = stinger((0, 4, 7, 12, 19), root=261.63, dur=2.6, bright=1.3)
    c["outro_results"] = place(whoosh(0.6, 300, 5000, 0.75, 0.7), stinger((0, 7, 12), root=196.0, dur=1.6, swoosh=False), 0.4)

    # ---- broadcast stingers for the other graphics (2026-10-26, #207; played in replays only) ----
    # #217 (Angelo: "they need to sound like a swell of cellos"): each one is now a string-section swell.
    # #219 (Angelo: "I want the very first stinger on the cello track, that one is beautiful"): the D minor swell
    # with the basses is THE Grandstand stinger - every graphic uses it.
    st = section(chord("D2", "A2", "D3", "F3", "A3"), 2.3, 0.85, 0.9, tremolo=0.0, low=hz("D1") * 2)
    for k in ("battle_in", "fastest_lap", "yellow_flag", "pit_callout", "featured_in", "custom_in", "tower_swap",
              "replay_wipe"):   # 2026-10-26 (#235): the replay / live wipe
        c[k] = st
    return c


# ----------------------------------------------------------------------------- the opener theme
HITS = [0.00, 4.40, 8.00, 12.07, 19.75, 25.52, 32.21]      # big hits (GridIntro.Hits + class pages + board out)
SMALL = [2.53, 9.21]                                        # SeriesEarlyS, MapS
END = 35.39                                                 # GridIntro.MusicEndS


def opener():
    dur = END + 0.2
    buf = silence(dur)
    # chord per section (minor-key broadcast feel), root moves at each hit
    sections = [(0.00, 4.40, (0, 7, 12, 15), 110.0), (4.40, 8.00, (0, 7, 12, 15), 98.0),
                (8.00, 12.07, (0, 7, 12, 16), 87.31), (12.07, 19.75, (0, 7, 10, 15), 110.0),
                (19.75, 25.52, (0, 7, 12, 15), 130.81), (25.52, 32.21, (0, 7, 12, 17), 116.54),
                (32.21, END, (0, 7, 12, 16, 19), 110.0)]
    bpm = 132.0
    beat = 60.0 / bpm
    for a, b, semis, root in sections:
        length = b - a
        pad = chord_pad(notes(*semis, root=root), length + 0.6, level=0.22, cutoff=1500 if a < 12 else 2400,
                        attack=0.02, release=0.6 if b < END else 2.5)
        buf = place(buf, reverb(pad, 1.2, 0.25), a)
        # bass pulse on 8ths from the third section on (builds), quarter notes before
        step = beat / 2 if a >= 8.0 else beat
        k = 0
        tt = a
        while tt < b - 0.05 and b < END:
            bn = saw(root / 2, step * 0.9, detune=(-5, 5))
            bn = lowpass(bn, 700) * env_ad(len(bn), 0.004, step * 0.8, 3) * 0.45
            buf = place(buf, bn, tt)
            if a >= 12.07:   # hats + kick drive in the middle of the song
                buf = place(buf, pan(highpass(noise(0.05), 7000) * np.exp(-np.arange(int(0.05 * SR)) / SR * 70) * 0.18, 0.3 * (-1) ** k), tt + step / 2)
                if k % (2 if step < beat else 1) == 0:
                    buf = place(buf, kick(0.4, 140, 45, 0.55), tt)
            k += 1
            tt += step
    # risers into every big hit, then the hit
    for i, h in enumerate(HITS):
        if i > 0:
            rl = min(1.6, (h - HITS[i - 1]) * 0.45)
            buf = place(buf, riser(rl, 200, 2400 + 300 * i, 0.32), h - rl)
        buf = place(buf, impact(2.6, 0.95 if i in (0, 3, 6) else 0.75, 1.2), h)
    for s in SMALL:
        buf = place(buf, whoosh(0.5, 400, 6000, 0.9, 0.5), s - 0.45)
        buf = place(buf, impact(1.2, 0.4, 0.9), s)
    # final ring-out fades to silence exactly at END
    n_end = int(END * SR)
    buf = buf[: n_end + int(0.15 * SR)]
    f0 = int((END - 1.2) * SR)
    e = np.ones(len(buf))
    e[f0:n_end] = np.linspace(1, 0, n_end - f0) ** 1.5
    e[n_end:] = 0
    return buf * e[:, None]


# ----------------------------------------------------------------------------- output
# 2026-10-27 (visual pack W9 / W13): light cues sit well under the cello stingers
PEAK_OVERRIDE = {"lowerthird_light": -15.0, "timing_beep": -17.0}


def write(name, st):
    st = np.asarray(st, dtype=np.float64)
    if st.ndim == 1:
        st = np.stack([st, st], axis=1)
    peak = np.max(np.abs(st)) + 1e-12
    st = st * (10 ** (PEAK_OVERRIDE.get(name, PEAK_DBFS) / 20) / peak)
    if name != "grid_opener":   # trim the inaudible end of reverb tails (keeps the files small)
        loud = np.where(np.max(np.abs(st), axis=1) > 10 ** (-66 / 20))[0]
        if len(loud): st = st[: min(len(st), loud[-1] + int(0.05 * SR))]
    st = fade(st, 0.002, 0.04)
    pcm = np.clip(np.round(st * 32767), -32768, 32767).astype("<i2")
    path = os.path.join(OUT, name + ".wav")
    with wave.open(path, "wb") as w:
        w.setnchannels(2)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes(pcm.tobytes())
    real_peak = 20 * np.log10(np.max(np.abs(pcm)) / 32768 + 1e-12)
    print(f"  {name + '.wav':28s} {len(pcm) / SR:6.2f} s  peak {real_peak:6.1f} dBFS")


def main():
    os.makedirs(OUT, exist_ok=True)
    only = set(sys.argv[1:])
    print("Grandstand original sounds (CC0) ->", os.path.normpath(OUT))
    for name, st in cues().items():
        if not only or name in only:
            write(name, st)
    if not only or "grid_opener" in only:
        write("grid_opener", opener())
    with open(os.path.join(OUT, "LICENSE.txt"), "w", encoding="ascii") as f:
        f.write("Grandstand original sounds - generated by tools/make_sfx.py (synthesised from scratch: no samples,\n"
                "no recordings). Released under CC0 1.0 Universal (public domain dedication):\n"
                "https://creativecommons.org/publicdomain/zero/1.0/\n")


if __name__ == "__main__":
    main()
