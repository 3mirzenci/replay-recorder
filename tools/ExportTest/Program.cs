// Usage: ExportTest <clip.mp4> [in] [out] [--x264]
// Exports with the same code path as the library's button and prints the result.
using System.Globalization;
using ReplayRecorder.Core;

var path = Path.GetFullPath(args[0]);
double In(int i) => args.Length > i && !args[i].StartsWith("--") ? double.Parse(args[i], CultureInfo.InvariantCulture) : 0;
var index = new ClipIndex(Path.GetDirectoryName(Path.GetDirectoryName(path))!);
var clip = index.TryCreate(path) ?? throw new Exception("not a clip");
await index.ProbeAsync(clip);
var s = Settings.Load();
s.ExportFolder = Path.Combine(Path.GetTempPath(), "replayrecorder-export-test");
var edit = new ClipEdit { In = In(1), Out = In(2) };
var progress = new Progress<(double F, string S)>(p => { });
var r = await DiscordExporter.ExportAsync(clip, edit, s, progress);
double len = edit.EffectiveOut(clip.Duration) - edit.In;
Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
    $"{len,7:0.0}s  ->  {r.SizeBytes / 1e6,6:0.00} MB  (target {DiscordExporter.TargetBytes(s) / 1e6:0.0})  {r.Plan.Describe(),-42} attempts={r.Attempts}  {r.Elapsed.TotalSeconds:0.0}s"));
