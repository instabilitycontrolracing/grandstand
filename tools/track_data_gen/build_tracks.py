#!/usr/bin/env python3
"""Track packs -> src/ApexHUD.Native.Common/TrackData/tracks.json (2026-10-28).

Turns the verified per-track research files (one JSON per circuit, schema: sc_packs/SCHEMA.md) into the one data file
Grandstand reads at startup (TrackPacks.cs). Only what the research files say goes in; nothing is invented here.
Corner POSITIONS are never written: every name is pinned to a landmark that already exists in CrewChief's
trackLandmarksData.json (by its distanceRoundLapStart), so the overlay keeps reading positions from CrewChief.

  python3 -I build_tracks.py RESEARCH_DIR LANDMARKS_JSON OUT_JSON  > report.txt

The six IMSA-season tracks done by hand in code (Le Mans, Mexico, Suzuka, Silverstone, Sebring, Monza) keep their
behaviour exactly as it is: Le Mans is skipped, and the other five get a pack only for their OTHER layouts (Silverstone
2011, Suzuka West / Moto, Monza Combined / Junior ...), marked "hand": the code checks their hand tables first, and the
generator never renames a CrewChief landmark the hand-made Renames table already renames (read from TrackCorners.cs).

Round 2 (2026-10-28): every research file except the two off-road tracks; layouts mapped config -> key from the
explicit config_key_map first, then by corners, then by the layout's own name; text-only "portrait" themes for places the
research flags portrait AND names after a driver; an empty per-track "kerbs" list (hook for the kerb-colour research:
a research file's "kerb_colours" list of "#RRGGBB" goes straight in).

  python3 -I build_tracks.py RESEARCH_DIR LANDMARKS_JSON OUT_JSON [TRACKCORNERS_CS]  > report.txt
"""
import glob, json, os, re, sys

RESEARCH, LANDMARKS, OUT = sys.argv[1], sys.argv[2], sys.argv[3]
TC_CS = sys.argv[4] if len(sys.argv) > 4 else os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "src",
                                                            "ApexHUD.Native.Common", "TrackCorners.cs")

# not sports-car tracks (off-road / dirt): no pack
EXCLUDE = {"lucas_oil_speedway", "wild_west_motorsports_park"}
# Le Mans is the reference: untouched
DONE_IN_CODE = {"circuit_des_24_heures_du_mans"}
# hand-made in code: these exact layouts keep the hand tables only (the five IMSA previews); the track's other layouts
# get a pack marked "hand" (hand tables first)
HAND = {"autodromo_hermanos_rodriguez": {"mexicocity gp"}, "autodromo_nazionale_monza": {"monza full"},
        "sebring_international_raceway": {"sebring international"}, "silverstone_circuit": {"silverstone 2019 gp"},
        "suzuka_international_racing_course": {"suzuka grandprix"}}

# the hand-made Renames table (TrackCorners.cs): key -> CrewChief names it renames; the pack leaves those landmarks alone
HAND_RENAMES = {}
HAND_TO = {}
try:
    _tc = open(TC_CS, encoding="utf-8").read()
    _rn = _tc[_tc.index("Renames ="):]
    _rn = _rn[:_rn.index("};")]
    for _ks, _from, _to in re.findall(r'\("([a-z0-9 |]+)", "([^"]+)", (?:"([^"]+)"|null)\)', _rn):
        for _k in _ks.split("|"):
            HAND_RENAMES.setdefault(_k, set()).add(_from.lower())
            if _to:
                HAND_TO.setdefault(_k, set()).add(_to.lower())
except (OSError, ValueError):
    print("WARNING: TrackCorners.cs not read - hand renames unknown", file=sys.stderr)

# Earth (text theme, no picture): only where the place's own name says what it looks like - water, woods, sun, rock,
# fen, glacial country - and the research file flags it "earth". Palette names are defined in CornerTheme.cs.
EARTH = {
    "circuit_de_spa_francorchamps": {"Eau Rouge": "redearth", "La Source": "water", "Pouhon": "water", "Fagnes": "woods"},
    "autodromo_enzo_e_dino_ferrari": {"Acque Minerali": "water"},
    "autodromo_jose_carlos_pace": {"Descida do Lago": "water", "Curva do Sol": "sunset"},
    "brands_hatch": {"Westfield Bend": "woods"},
    "donington": {"Hollywood": "woods", "Coppice": "woods"},
    "chicago_street_course": {"Lake Shore Drive": "water"},
    "hungaroring": {"Spring": "water", "Danube": "water"},
    "misano_world_circuit_marco_simoncelli": {"Curva del Tramonto": "sunset", "Curva Quercia": "oaks"},
    "nurburgring_nordschleife": {"Hatzenbachbogen": "water", "Hatzenbach-Bogen": "water", "Hatzenbach": "water",
                                 "Hocheichen": "oaks", "Adenauer Forst": "woods", "Brünnchen": "water", "Eschbach": "water",
                                 "Antoniusbuche": "woods", "Pflanzgarten": "woods", "Pflanzgarten 1": "woods",
                                 "Pflanzgarten 2": "woods", "Pflanzgarten II": "woods"},
    "nurburgring_combined": {"Hatzenbachbogen": "water", "Hatzenbach-Bogen": "water", "Hatzenbach": "water",
                             "Hocheichen": "oaks", "Adenauer Forst": "woods", "Brünnchen": "water", "Eschbach": "water",
                             "Antoniusbuche": "woods", "Pflanzgarten": "woods", "Pflanzgarten 1": "woods",
                             "Pflanzgarten 2": "woods", "Pflanzgarten II": "woods"},
    "oulton_park": {"Knickerbrook": "water"},
    "road_america": {"Moraine Sweep": "woods", "Kettle Bottoms": "woods"},
    "virginia_international_raceway": {"Oak Tree": "oaks"},
    "mount_panorama": {"The Cutting": "stone"},
    "circuit_zandvoort": {"Scheivlak": "stone"},
    "cadwell_park": {"The Mountain": "stone"},
}

# FIRST RACE shows a year. Taken from the research "first_race" text (its first year), except where that text is not
# the circuit's first race: Interlagos (1973 is the first F1 round; it opened 1940), Phillip Island (1928 is the old
# island ROAD course; the circuit opened 1952 with no first-race event given), Navarra (opening was a MotoGP showing).
FIRST_RACE_NONE = {"autodromo_jose_carlos_pace", "phillip_island_circuit", "circuito_de_navarra"}

GENERIC_STRAIGHT = re.compile(r"^(main|front|pit|home|start/finish|start-finish|s/f)\s+straight(away)?$|^frontstretch$|^tri-oval|^short chute", re.I)
THE_NOUNS = ("straight", "straightaway", "backstretch", "frontstretch", "runway")


def norm_key(k):
    return re.sub(r"\s+", " ", (k or "").replace("_", " ").strip().lower())


def clean_key(k):
    return norm_key(re.sub(r"\(.*?\)", "", k or ""))


def clean_name(n):
    """'Senna S (Turn 2)' -> 'Senna S'; 'The Esses / Rollercoaster' -> 'The Esses'. None = not a name to show."""
    if not n:
        return None
    s = re.sub(r"\s*\(.*?\)\s*", " ", n).strip()
    s = s.split(" / ")[0].strip()
    if not s or re.match(r"^(turn|t|curve)\s*\d", s, re.I) or re.search(r"\b(exit|link)\b", s, re.I) or "(CC" in n:
        return None
    return s


def is_turn(raw):
    return re.match(r"(?i)^turn_?\d+", raw) is not None


def pretty(raw):
    """Approximation of TrackCorners.Pretty (only used to count real renames and resolve straights)."""
    if not raw:
        return None
    n = " ".join(w[:1].upper() + w[1:] for w in raw.replace("_", " ").strip().lower().split(" "))
    n = re.sub(r"(\D)(\d+)$", r"\1 \2", n)
    return {"Radillion": "Raidillon", "Lesmos 1": "Lesmo 1", "Lesmos 2": "Lesmo 2", "Stavlot": "Stavelot",
            "Les Fanges": "Les Fagnes", "Mulsanne": "Mulsanne Corner"}.get(n, n.replace("Nascar", "NASCAR"))


# ---- CrewChief landmarks, the way TrackCorners reads them
lmdata = json.load(open(LANDMARKS, encoding="utf-8"))["trackLandmarksData"]
LM = {}
IDS = set()        # every iRacing track id CrewChief knows, with or without landmarks
for t in lmdata:
    ir = t.get("irTrackName")
    if isinstance(ir, str):
        IDS.add(norm_key(ir))
    if not isinstance(ir, str) or not t.get("trackLandmarks"):
        continue
    out = []
    for lm in t["trackLandmarks"]:
        names = [x for x in lm.get("landmarkNames", []) if x]
        pick = next((x for x in names if x.lower() != "no_name" and not is_turn(x)), None)
        turn = next((x for x in names if is_turn(x)), None)
        a = float(lm.get("distanceRoundLapStart", 0))
        b = float(lm.get("distanceRoundLapEnd", a))
        out.append({"a": a, "b": b, "raw": [x.lower() for x in names], "name": pretty(pick or turn)})
    LM[norm_key(ir)] = out

# the places on a hand-made track's main layout (CrewChief names + the hand renames): the hand tables decided those
# (some heritage lines were left out on purpose - Stowe, Vale, Spoon ...), so the pack adds heritage / themes only for
# places its other layouts have that the main one doesn't (Silverstone 2011's Bridge, Priory ...)
# Corner names too: a landmark whose name the main layout already shows keeps it (Monza's Curva Grande and Parabolica stay,
# as on the GP), and the main layout's named straights (KnownStraights) get no pack heritage.
HAND_PREFIX = {"autodromo_hermanos_rodriguez": "mexicocity", "autodromo_nazionale_monza": "monza",
               "sebring_international_raceway": "sebring", "silverstone_circuit": "silverstone",
               "suzuka_international_racing_course": "suzuka"}
_ks_names = {}
try:
    _kn = _tc[_tc.index("KnownStraights ="):]
    _kn = _kn[:_kn.index("};")]
    for _t, _nc, _nm in re.findall(r'\("([a-z]+)", "([^"]+)", "([^"]+)"\)', _kn):
        _ks_names.setdefault(_t, set()).add(re.sub(r"^the ", "", _nm.lower()))
except (NameError, ValueError):
    pass
PRIMARY = {}
for _slug, _ks in HAND.items():
    P = PRIMARY.setdefault(_slug, set())
    for _k in _ks:
        for _l in LM.get(_k, []):
            if _l["name"] and _l["name"].lower() not in HAND_RENAMES.get(_k, set()):
                P.add(_l["name"].lower())
        P.update(HAND_TO.get(_k, set()))
    P.update(_ks_names.get(HAND_PREFIX[_slug], set()))
    P.add("main straight")

report = []
tracks = []
for path in sorted(glob.glob(os.path.join(RESEARCH, "*.json"))):
    slug = os.path.basename(path)[:-5]
    if slug in DONE_IN_CODE or slug in EXCLUDE:
        continue
    d = json.load(open(path, encoding="utf-8"))
    hand = slug in HAND
    keys = []
    for k in d.get("crewchief_keys", []):
        k = norm_key(k)
        if k in IDS and k not in keys and not (hand and k in HAND[slug]):
            keys.append(k)
    if hand and not keys:
        continue
    # scheduled configs first, so a name's heritage line comes from the layout Angelo races when two layouts differ
    sched = set((d.get("configs") or {}).keys()) | {"all", "", None}
    d["corners"] = sorted(d.get("corners", []), key=lambda c: 0 if c.get("config") in sched else 1)
    for k in keys:
        LM.setdefault(k, [])
    rep = {"slug": slug, "renames": 0, "fills": 0, "drops": 0, "unplaced": [], "heritage": 0, "straights": 0,
           "themes": 0, "skipped_heritage": [], "skipped_straights": [], "skipped_themes": [], "layouts": {}}

    # config -> keys (explicit maps first, else by which key's landmarks the config's corners sit on)
    cmap = {}
    for m in (d.get("config_key_map"), d.get("config_to_crewchief_key")):
        for cfg, v in (m or {}).items():
            for k in (v if isinstance(v, list) else [v]):
                if isinstance(k, str) and clean_key(k) in keys:
                    cmap.setdefault(cfg, set()).add(clean_key(k))
    explicit = {c: set(v) for c, v in cmap.items()}
    corners = d.get("corners", [])

    def lm_match(key, c, by_name=True):
        lms = LM[key]
        raws = {x.lower() for x in [c.get("crewchief_name")] + (c.get("crewchief_aliases") or []) + (c.get("crewchief_landmark_names") or []) if x}
        if c.get("start_m") is not None:
            for i, l in enumerate(lms):
                if abs(l["a"] - float(c["start_m"])) < 2 and (not raws or raws & set(l["raw"]) or "no_name" in raws):
                    return i
        if by_name:
            hits = [i for i, l in enumerate(lms) if raws - {"no_name"} and (raws - {"no_name"}) & set(l["raw"])]
            if len(hits) == 1:
                return hits[0]
        return None

    for k in keys:
        if any(k in v for v in cmap.values()):
            continue
        scores = {}
        for c in corners:
            if c.get("crewchief_key") and norm_key(c["crewchief_key"]) != k:
                continue
            if lm_match(k, c, by_name=False) is not None:
                scores[c["config"]] = scores.get(c["config"], 0) + 1
        if scores:
            best = max(scores.values())
            for cfg, s in scores.items():
                if s == best:
                    cmap.setdefault(cfg, set()).add(k)

    def keys_for(c):
        if c.get("crewchief_key"):
            k = norm_key(c["crewchief_key"])
            return [k] if k in keys else []
        if c.get("config") == "all":
            return keys
        ks = sorted(cmap.get(c.get("config"), set()))
        if not ks and len(keys) == 1:
            ks = keys
        return ks

    # straights whose CrewChief landmark is only a marker on the straight (Kemmel, Döttinger Höhe, Road America's
    # Moraine Sweep / Kettle Bottoms / Thunder Valley ...): that landmark is dropped so the straight reads by its name
    straight_names = {clean_name(s.get("name")) for s in d.get("straights", [])}
    names = {k: {} for k in keys}          # key -> {landmark index: final name or None (drop)}
    her = {}                               # final name -> heritage line
    her_late = {}                          # ... from layouts not on the schedule (after the straights' own lines)
    for c in corners:
        ks = keys_for(c)
        placed = False
        for k in ks:
            i = lm_match(k, c)
            if i is None:
                continue
            placed = True
            l = LM[k][i]
            if l["name"] and (l["name"].lower() in HAND_RENAMES.get(k, set()) or (hand and l["name"].lower() in PRIMARY[slug])):
                continue                       # the hand-made table renames this one / the main layout shows this name
            nm = clean_name(c.get("official_name"))
            if c.get("kind") == "straight" and nm in straight_names:
                if l["name"] is not None and i not in names[k]:
                    names[k][i] = None
                    rep["drops"] += 1
                final = None
            elif nm and nm != l["name"]:
                if i not in names[k]:
                    names[k][i] = nm
                    rep["fills" if l["name"] is None else "renames"] += 1
                final = names[k][i]
            else:
                final = names[k].get(i, l["name"])
            h = (c.get("heritage") or "").strip()
            hname = nm if (c.get("kind") == "straight" and nm in straight_names) else final
            off = re.sub(r"\s*\(.*", "", c.get("official_name") or "").strip()
            lz = lambda x: re.sub(r"[^A-Z0-9]+", "", x.upper())
            if h and hname and off and lz(h).startswith(lz(off)) and lz(off) != lz(hname):
                rep["skipped_heritage"].append(f"{hname}: its line names {off} (CrewChief's newer name shown)")
            elif h and hname:
                H = h.upper()
                if len(H) > 48:
                    rep["skipped_heritage"].append(f"{hname}: >48 chars")
                else:
                    (her if c.get("config") in sched else her_late).setdefault(hname, H)
        if not placed:
            rep["unplaced"].append(c.get("official_name") or c.get("crewchief_name"))
    for s in d.get("straights", []):
        if s.get("crewchief_name") and s.get("start_m") is not None:
            for k in ([norm_key(s["crewchief_key"])] if s.get("crewchief_key") else keys):
                if k not in keys:
                    continue
                for i, l in enumerate(LM[k]):
                    if abs(l["a"] - float(s["start_m"])) < 2 and s["crewchief_name"].lower() in l["raw"] and l["name"] is not None and i not in names[k]:
                        names[k][i] = None
                        rep["drops"] += 1

    def final_list(k):
        lst = []
        for i, l in enumerate(LM[k]):
            nm = names[k].get(i, l["name"]) if i in names[k] else l["name"]
            if nm:
                lst.append((l["a"], l["b"], nm))
        lst.sort()
        merged = []
        for x in lst:
            if merged and merged[-1][2] == x[2] and x[0] - merged[-1][1] < 30:
                merged[-1] = (merged[-1][0], max(merged[-1][1], x[1]), x[2])
            else:
                merged.append(x)
        return merged

    # named straights: by the corner they follow, per key
    st_out = {}
    main_her = {}                          # heritage line(s) of the front straight -> "Main straight"
    main_names = set()                     # the research's names for the front straight
    busy = []
    official_by_cc = {}
    for c in corners:
        nm = clean_name(c.get("official_name"))
        for x in [c.get("crewchief_name"), c.get("official_name")]:
            if x and nm:
                official_by_cc[x.lower()] = nm
    for s in d.get("straights", []):
        nm = clean_name(s.get("name"))
        after = s.get("after_corner")
        # the corner it follows, as written, without notes, the research's official name for it, or CrewChief's raw id
        cands = [after, clean_name(after) or "", official_by_cc.get(after.lower(), ""), re.sub(r"\s*\(.*?\)", "", after).strip(),
                 pretty(after) or ""] if after else []
        cfg = s.get("config")
        ks = keys if cfg in ("all", "") or (cfg not in cmap and len(keys) == 1) else sorted(cmap.get(cfg, set()))
        if s.get("crewchief_key"):
            ks = [norm_key(s["crewchief_key"])]
        if not nm or GENERIC_STRAIGHT.match(nm) or re.search(r"\b(into|run to)\b", nm, re.I):
            # the front straight: Grandstand already calls the stretch from the last corner over the line "the main
            # straight" - its sourced heritage line / Speed flag go on that name when the research's corner before it
            # IS the last corner of the CrewChief lap
            last = [k for k in ks if k in keys and final_list(k) and any(c and final_list(k)[-1][2].lower() == c.lower() for c in cands)]
            if nm and re.match(r"(?i)^(main|front|pit|home|start/finish|start-finish|s/f)\s+straight(away)?$|^frontstretch$", nm) and last:
                if s.get("heritage") and len(s["heritage"].strip()) <= 48:
                    main_her.setdefault(s["heritage"].strip().upper(), 0)
                main_names.add(s.get("name"))
                rep["main_straight"] = rep.get("main_straight", 0) + 1
            else:
                rep["skipped_straights"].append(f"{s.get('name')}: generic")
            continue
        if not after:
            rep["skipped_straights"].append(f"{s.get('name')}: no corner before it")
            continue
        ok = False
        for k in ks:
            if k not in keys:
                continue
            fl = [x[2] for x in final_list(k)]
            loose = lambda x: re.sub(r"[\s\-_]+", " ", x.lower()).strip()      # "Warsteiner-Kurve" = "Warsteiner Kurve"
            hit = next((n for cand in cands if cand for n in fl if loose(n) == loose(cand)), None)
            if hit is None:
                continue
            phrase = ("the " + nm[0].lower() + nm[1:] if nm.lower().startswith("back straight") else
                      "the " + nm if nm.split()[-1].lower() in THE_NOUNS and not nm.lower().startswith("the ") else nm)
            st_out.setdefault(k, {})
            if hit not in st_out[k]:
                st_out[k][hit] = phrase
                ok = True
            elif st_out[k][hit] != phrase:
                busy.append(f"{s.get('name')}: '{hit}' already leads onto {st_out[k][hit]}")
        if ok:
            rep["straights"] += 1
            h = (s.get("heritage") or "").strip().upper()
            disp = phrase[4:] if phrase.startswith("the ") else phrase
            disp = disp[0].upper() + disp[1:]
            if h and len(h) <= 48:
                her.setdefault(disp, h)
            elif h:
                rep["skipped_heritage"].append(f"{disp}: >48 chars")
        elif busy and busy[-1].startswith(f"{s.get('name')}:"):
            rep["skipped_straights"].append(busy[-1] + " (two straights after one corner in the research)")
        else:
            rep["skipped_straights"].append(f"{s.get('name')}: corner '{after}' not on the CrewChief lap")
    if main_her:
        her.setdefault("Main straight", next(iter(main_her)))
        if len(main_her) > 1:
            rep["skipped_heritage"].append(f"Main straight: {len(main_her)} different lines, the first kept")

    for n, h in her_late.items():
        her.setdefault(n, h)

    # themes (text only): Earth from the curated list, Speed for the long straights the research flags
    all_names = set()
    for k in keys:
        all_names |= {x[2] for x in final_list(k)}
    st_disp = {"Main straight"} if main_names else set()
    for k, m in st_out.items():
        for p in m.values():
            q = p[4:] if p.startswith("the ") else p
            st_disp.add(q[0].upper() + q[1:])
    themes = {}
    for nm, pal in EARTH.get(slug, {}).items():
        if nm in all_names or nm in st_disp:
            themes[nm] = "earth:" + pal
    # portrait (text only, no photo): the research flags the place "portrait" AND its named_after says a driver
    named_after = {}
    for c in corners + d.get("straights", []):
        if isinstance(c.get("named_after"), dict):
            named_after.setdefault(clean_name(c.get("official_name") or c.get("name")), c["named_after"].get("type"))
    on_lap = all_names | st_disp
    for t in d.get("themes", []):
        if t.get("theme") != "portrait":
            continue
        nm = clean_name(t.get("name"))
        disp = nm and nm[0].upper() + nm[1:]
        if hand:
            rep["skipped_themes"].append(f"{t.get('name')}: portrait not added on a hand-made track (its main layout must not change)")
        elif not nm or GENERIC_STRAIGHT.match(nm):
            rep["skipped_themes"].append(f"{t.get('name')}: portrait - not a name")
        elif named_after.get(nm) != "driver":
            rep["skipped_themes"].append(f"{t.get('name')}: portrait - named after a {named_after.get(nm) or 'unknown'}, not a driver")
        elif disp not in on_lap:
            rep["skipped_themes"].append(f"{t.get('name')}: portrait - not a stop on the CrewChief lap")
        else:
            themes.setdefault(disp, "portrait")
    for t in d.get("themes", []):
        nm = clean_name(t.get("name"))
        if t.get("theme") == "speed":
            if t.get("name") in main_names:
                themes.setdefault("Main straight", "speed")
            elif nm and nm[0].upper() + nm[1:] in st_disp:
                themes.setdefault(nm[0].upper() + nm[1:], "speed")
            else:
                rep["skipped_themes"].append(f"{t.get('name')}: speed only for named straights")
        elif t.get("theme") == "earth" and not (nm in themes) and t.get("name") not in EARTH.get(slug, {}):
            rep["skipped_themes"].append(f"{t.get('name')}: earth not a landscape name / not on lap")
    rep["themes"] = len(themes)

    # heritage only for names that are on the lap (or a named straight)
    her = {n: h for n, h in her.items() if n in all_names or n in st_disp}
    for n in [n for n, h in her.items() if re.sub(r"[^A-Z0-9]", "", n.upper()) == re.sub(r"[^A-Z0-9]", "", h)]:
        rep["skipped_heritage"].append(f"{n}: the line only repeats the name")
        del her[n]
    if hand:
        for n in [n for n in her if n.lower() in PRIMARY.get(slug, set())]:
            rep["skipped_heritage"].append(f"{n}: on the hand-made main layout (hand table decides)")
            del her[n]
        for n in [n for n in themes if n.lower() in PRIMARY.get(slug, set())]:
            rep["skipped_themes"].append(f"{n}: on the hand-made main layout (hand table decides)")
            del themes[n]
        rep["themes"] = len(themes)
    rep["heritage"] = len(her)

    # first race year
    fr = None
    if slug not in FIRST_RACE_NONE and d.get("first_race"):
        m = re.search(r"\b(1[89]\d\d|20\d\d)\b", d["first_race"])
        fr = m.group(1) if m else None

    # layout facts per key: (1) the research's explicit config -> key map, (2) the config whose corners sit on that
    # key's CrewChief landmarks, (3) the layout's own name ("gp" ~ "Grand Prix"), (4) the one key left with the one
    # config left. Only a length the research gives for that layout; never borrowed from another layout.
    layouts = {}
    det = {}
    for c, v in (d.get("configs_detail") or {}).items():
        if isinstance(v, dict) and (v.get("length_km") or v.get("iracing_length_km")):
            det[c] = {"km": v.get("length_km") or v.get("iracing_length_km"), "turns": v.get("turns") or 0}

    def lay_norm(x):
        x = x.lower().replace("grand prix", "gp").replace("grandprix", "gp")
        x = re.sub(r"\b(circuit|course|layout|configuration|config|track)\b", "", x)
        return re.sub(r"[^a-z0-9]", "", x)
    # the hand-made layouts take part in the matching (so their config is not handed to another key) but get no pack
    lkeys = keys + sorted(k for k in HAND.get(slug, set()))
    pick = {}
    for k in lkeys:
        ex = [c for c, v in explicit.items() if k in v and c in det]
        if len({json.dumps(det[c], sort_keys=True) for c in ex}) == 1:
            pick[k] = (ex[0], "map")
    taken = lambda: {c for c, _ in pick.values()} | {c for c in explicit if c in det}
    for k in lkeys:
        if k in pick:
            continue
        suf = " ".join(w for w in k.split(" ")[1:] if not re.fullmatch(r"(19|20)\d\d", w))
        hits = [c for c in det if c not in explicit and suf and lay_norm(c) == lay_norm(suf)]
        if len(hits) == 1:
            pick[k] = (hits[0], "name")
    for k in lkeys:
        if k in pick:
            continue
        # name and corner evidence may share a config between two ids of the same layout (legacy + 2026 ids), never
        # one the research maps to another key
        auto = [c for c, v in cmap.items() if k in v and c in det and c not in explicit]
        if len(auto) == 1:
            pick[k] = (auto[0], "corners")
    # the research lists the scheduled layout's id first: it takes the one scheduled config nothing else took
    first = norm_key((d.get("crewchief_keys") or [""])[0])
    sc = [c for c in det if c in (d.get("configs") or {}) and c not in taken()]
    if first in lkeys and first not in pick and len(sc) == 1:
        pick[first] = (sc[0], "scheduled")
    left_k = [k for k in lkeys if k not in pick]
    left_c = [c for c in det if c not in taken()]
    if len(left_k) == 1 and len(left_c) == 1:
        pick[left_k[0]] = (left_c[0], "last")
    for k, (c, how) in pick.items():
        if k in keys:
            layouts[k] = det[c]
            rep["layouts"][k] = f"{c} ({how})"
    rep["no_layout"] = [k for k in keys if k not in layouts]

    prefixes = []
    if not keys:   # Sonoma: iRacing's id is not in the CrewChief file (research: "sonoma 2025 nascarlong (probable)")
        for n in d.get("iracing_track_names", []):
            p = norm_key(re.sub(r"\(.*?\)", "", n)).split(" ")[0]
            if p and p not in prefixes:
                prefixes.append(p)

    country = {"United States": "USA"}.get(d.get("country"), d.get("country"))
    logos = sorted({t["logo_file"] for t in d.get("themes", []) if t.get("logo_file")})
    entry = {"slug": slug, "display": d.get("display_name"), "iso2": (d.get("iso2") or "").lower(), "country": country,
             "keys": keys, "prefixes": prefixes, "story": d.get("story"), "firstRace": fr, "layouts": layouts,
             "names": {k: [[round(LM[k][i]["a"], 4), nm] for i, nm in sorted(v.items())] for k, v in names.items() if v},
             "straights": {k: [[a, p] for a, p in v.items()] for k, v in st_out.items()},
             "heritage": her, "themes": themes, "logoFiles": logos,
             # kerb paint for the story cards: "#RRGGBB" list from the research ("kerb_colours"), empty = classic red/white
             "kerbs": [c for c in (d.get("kerb_colours") or []) if isinstance(c, str) and re.fullmatch(r"#[0-9A-Fa-f]{6}", c)]}
    if hand:
        entry["hand"] = True
    tracks.append(entry)
    rep["story"] = 1 if d.get("story") else 0
    rep["firstRace"] = fr
    rep["keys"] = keys
    report.append(rep)

os.makedirs(os.path.dirname(OUT), exist_ok=True)
with open(OUT, "w", encoding="utf-8") as f:
    json.dump({"note": "Generated by tools/track_data_gen/build_tracks.py from the sourced track research files. "
                       "Names are pinned to CrewChief landmark start metres; no positions live here.",
               "tracks": tracks}, f, ensure_ascii=False, indent=1)
for r in report:
    print(f"{r['slug']:40} keys={len(r['keys'])} renames={r['renames']} fills={r['fills']} drops={r['drops']} "
          f"straights={r['straights']} heritage={r['heritage']} themes={r['themes']} story={r['story']} first={r['firstRace']}")
    if r["layouts"]:
        print(f"    layouts: {r['layouts']}")
    for k in ("unplaced", "skipped_heritage", "skipped_straights", "skipped_themes"):
        if r[k]:
            print(f"    {k}: {r[k]}")
