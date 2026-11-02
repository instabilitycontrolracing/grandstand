// Highlight reel plan checks (#9). Usage: ./run.sh   (exit 0 = all pass)
using ApexHUD.Native.Common;
using M = ApexHUD.Native.Common.HighlightReel.Marker;

int fails = 0;
void Check(string name, bool ok, string extra = "")
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(extra.Length > 0 ? "  " + extra : "")}");
    if (!ok) fails++;
}

// ---- parsing: the exact shape HighlightLog writes (System.Text.Json defaults, PascalCase)
const string file = """
[
  { "Utc": "2026-10-28T19:00:00Z", "SessionNum": 2, "SessionTime": 300.5, "Lap": 0, "Kind": "start", "Label": "Green flag", "Cars": [], "Mine": false, "Replay": false },
  { "Utc": "2026-10-28T19:01:00Z", "SessionNum": 2, "SessionTime": 360.0, "Lap": 2, "Kind": "pass", "Label": "Up to P3 in GT3", "Cars": [7], "Mine": true, "Replay": false },
  { "Utc": "2026-10-28T19:01:04Z", "SessionNum": 2, "SessionTime": 364.0, "Lap": 2, "Kind": "battle", "Label": "Battle: #12 vs #4", "Cars": [7, 9], "Mine": true, "Replay": false },
  { "Utc": "2026-10-28T19:05:00Z", "SessionNum": 2, "SessionTime": 0, "Lap": 5, "Kind": "yellow", "Label": "no time", "Cars": [], "Mine": false, "Replay": false },
  { "Utc": "2026-10-28T19:06:00Z", "SessionNum": 2, "SessionTime": 900.0, "Lap": 9, "Kind": "pit", "Label": "Pit stop", "Cars": [7], "Mine": true, "Replay": false },
  { "SessionNum": 2, "Kind": "broken" },
  "not an object",
  { "Utc": "2026-10-28T19:30:00Z", "SessionNum": 2, "SessionTime": 2100.0, "Lap": 30, "Kind": "finish", "Label": "Chequered flag", "Cars": [], "Mine": false, "Replay": false }
]
""";
var marks = HighlightReel.ParseMarkers(file);
Check("parse keeps the 5 markers with a replay time", marks.Count == 5, $"got {marks.Count}");
Check("parse reads cars and mine", marks[1].Cars.SequenceEqual(new[] { 7 }) && marks[1].Mine);
Check("bad JSON gives an empty list", HighlightReel.ParseMarkers("{oops").Count == 0 && HighlightReel.ParseMarkers("{}").Count == 0);

var plan = HighlightReel.Build(marks);
Check("pass + battle 4 s apart are one moment", plan.Count == 4, $"got {plan.Count}: {string.Join(", ", plan.Select(p => p.Kind))}");
Check("the more important of the two is kept (pass)", plan[1].Kind == "pass");
Check("lead-in: starts 6 s before the marker", Math.Abs(plan[1].StartTime - 354.0) < 0.01, $"{plan[1].StartTime}");
Check("pass plays 12 s, pit / start / finish 8 s", plan[1].PlaySeconds == 12 && plan[2].PlaySeconds == 8 && plan[0].PlaySeconds == 8 && plan[3].PlaySeconds == 8);
Check("in time order", plan.Zip(plan.Skip(1)).All(p => p.First.StartTime < p.Second.StartTime));
Check("car = cars[0]; none -> -1", plan[1].CarIdx == 7 && plan[0].CarIdx == -1);
Check("chapter name like OBS markers", plan[1].Chapter == "L2 Up to P3 in GT3" && plan[0].Chapter == "Green flag");
Check("all of these jump (far apart)", plan.All(p => p.Seek));

// ---- overlap: 11 s apart passes -> the second carries on from the first's end, no jump back
var close = HighlightReel.Build(new[]
{
    new M(1, 500, 3, "pass", "A", new[] { 1 }, true),
    new M(1, 511, 3, "pass", "B", new[] { 2 }, true),
});
Check("overlapping moments: two moments", close.Count == 2);
Check("second starts where the first ends, no seek", close.Count == 2 && !close[1].Seek && Math.Abs(close[1].StartTime - close[0].EndTime) < 0.01,
      close.Count == 2 ? $"{close[0].EndTime} -> {close[1].StartTime} seek={close[1].Seek}" : "");
Check("second still ends 6 s after its own marker", close.Count == 2 && Math.Abs(close[1].EndTime - 517) < 0.01);

// a short moment almost entirely shown already is folded into the previous one
var fold = HighlightReel.Build(new[]
{
    new M(1, 500, 3, "pass", "A", new[] { 1 }, true),       // 494..506
    new M(1, 510.5, 3, "yellow", "Y", Array.Empty<int>(), false),   // 504.5..512.5 -> 506..512.5 = 6.5 s, kept
    new M(1, 521, 3, "pit", "P", new[] { 1 }, true),        // 515..523 -> no overlap, seek
});
Check("non-overlapping later moment seeks again", fold.Count == 3 && fold[2].Seek && !fold[1].Seek);

// different sessions never merge
var sess = HighlightReel.Build(new[] { new M(1, 500, 3, "pass", "A", new[] { 1 }, true), new M(2, 503, 1, "start", "G", Array.Empty<int>(), false) });
Check("different sessions are separate moments", sess.Count == 2 && sess.All(p => p.Seek));

// ---- the cap: 40 moments -> 20, the important ones survive, still in time order
var many = new List<M>();
for (int i = 0; i < 40; i++) many.Add(new M(2, 100 + i * 30, i, i % 4 == 0 ? "pass" : "pit", "m" + i, new[] { i }, false));
many.Add(new M(2, 50, 0, "start", "Green flag", Array.Empty<int>(), false));
many.Add(new M(2, 5000, 50, "finish", "Chequered flag", Array.Empty<int>(), false));
var capped = HighlightReel.Build(many);
Check("cap at 20", capped.Count == 20, $"got {capped.Count}");
Check("start and finish survive the cap", capped[0].Kind == "start" && capped[^1].Kind == "finish");
Check("all 10 passes survive the cap", capped.Count(c => c.Kind == "pass") == 10);
Check("capped list in time order", capped.Zip(capped.Skip(1)).All(p => p.First.MarkTime < p.Second.MarkTime));
Check("lead-in clamps at 0", HighlightReel.Build(new[] { new M(0, 2, 0, "flag", "F", Array.Empty<int>(), true) })[0].StartTime == 0);

// ---- camera groups
var groups = new List<(int, string)> { (1, "Nose"), (2, "Gearbox"), (9, "TV1"), (10, "TV2"), (11, "TV3"), (12, "Pit Lane"), (13, "Blimp"), (14, "Cockpit"), (15, "Chase") };
Check("pass -> TV cameras in turn", HighlightReel.PickGroup(groups, "pass", 0) == 9 && HighlightReel.PickGroup(groups, "pass", 1) == 10 && HighlightReel.PickGroup(groups, "battle", 2) == 11 && HighlightReel.PickGroup(groups, "pass", 3) == 9);
Check("start / finish -> TV1", HighlightReel.PickGroup(groups, "start", 5) == 9 && HighlightReel.PickGroup(groups, "finish", 7) == 9);
Check("pit -> Pit Lane", HighlightReel.PickGroup(groups, "pit", 1) == 12);
var noTv = new List<(int, string)> { (1, "Nose"), (13, "Blimp"), (14, "Cockpit"), (15, "Chase") };
Check("no TV groups -> Chase", HighlightReel.PickGroup(noTv, "pass", 0) == 15 && HighlightReel.PickGroup(noTv, "pit", 0) == 15);
Check("nothing usable -> -1 (never cockpit/blimp)", HighlightReel.PickGroup(new List<(int, string)> { (1, "Cockpit"), (2, "Blimp") }, "pass", 0) == -1);
Check("TV Mixed fallback beats TV Static", HighlightReel.PickGroup(new List<(int, string)> { (3, "TV Static"), (4, "TV Mixed") }, "pass", 0) == 4);

// ---- file names and labels
Check("safe file names", HighlightReel.SafeFileName("2026-10-28_Road-Atlanta_race2.json") && !HighlightReel.SafeFileName("..\\x.json")
      && !HighlightReel.SafeFileName("a/b.json") && !HighlightReel.SafeFileName("x.txt") && !HighlightReel.SafeFileName("C:x.json") && !HighlightReel.SafeFileName(""));
Check("session label", HighlightReel.SessionLabel("2026-10-28_Road-Atlanta_race2.json") == "2026-10-28 · Road Atlanta · race 2", HighlightReel.SessionLabel("2026-10-28_Road-Atlanta_race2.json"));
Check("total seconds", Math.Abs(HighlightReel.TotalSeconds(plan) - 36) < 0.01, $"{HighlightReel.TotalSeconds(plan)}");

Console.WriteLine(fails == 0 ? "ALL PASS" : $"{fails} FAILED");
return fails == 0 ? 0 : 1;
