using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReplayRecorder.Core;

namespace ReplayRecorder.Library.Player;

/// <summary>
/// Drives an embedded mpv.exe (rendered into our window via --wid) over its JSON IPC pipe.
/// mpv is used because it can play two audio tracks at once through a filter graph, which is
/// what lets you balance game vs. mic live before exporting.
/// </summary>
sealed class MpvPlayer : IDisposable
{
    readonly Process _proc;
    readonly NamedPipeClientStream _pipe;
    readonly StreamWriter _writer;
    readonly SynchronizationContext _ui;
    int _requestId;

    public double Position { get; private set; }
    public double Duration { get; private set; }
    public bool Paused { get; private set; } = true;

    /// <summary>Raised on the UI thread when position, duration or pause state changes.</summary>
    public event Action? StateChanged;
    public event Action? FileLoaded;

    public MpvPlayer(IntPtr hostWindow)
    {
        _ui = SynchronizationContext.Current ?? throw new InvalidOperationException("Create on the UI thread");
        if (Ffmpeg.MpvPath == null) throw new FileNotFoundException(Ffmpeg.MpvInstallHint);

        var pipeName = $"replayrecorder-mpv-{Guid.NewGuid():N}"; // unguessable, so other local users can't find the control pipe
        var psi = new ProcessStartInfo(Ffmpeg.MpvPath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in new[]
        {
            $"--wid={hostWindow.ToInt64()}",
            $"--input-ipc-server=\\\\.\\pipe\\{pipeName}",
            "--no-config", "--idle=yes", "--keep-open=always", "--pause",
            "--hwdec=auto-safe", "--vo=gpu-next",
            "--osc=no", "--osd-level=0", "--no-input-default-bindings", "--input-vo-keyboard=no",
            "--input-cursor=no", "--cursor-autohide=no", "--force-window=yes",
            "--hr-seek=yes", "--background-color=#0d0e11", "--volume=100", "--audio-display=no",
            "--msg-level=all=warn", "--terminal=no",
        }) psi.ArgumentList.Add(a);

        _proc = Process.Start(psi)!;
        _proc.OutputDataReceived += (_, _) => { };
        _proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) Log.Info("[mpv] " + e.Data); };
        _proc.BeginOutputReadLine();
        _proc.BeginErrorReadLine();

        _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var sw = Stopwatch.StartNew();
        while (true)
        {
            try { _pipe.Connect(200); break; }
            catch (TimeoutException) { if (sw.ElapsedMilliseconds > 8000 || _proc.HasExited) throw new IOException("mpv did not start"); }
            catch (IOException) { Thread.Sleep(50); if (sw.ElapsedMilliseconds > 8000) throw; }
        }
        _writer = new StreamWriter(_pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        _ = Task.Run(ReadLoop);

        Command("observe_property", 1, "time-pos");
        Command("observe_property", 2, "duration");
        Command("observe_property", 3, "pause");
    }

    // ------------------------------------------------------------------ commands

    public void Load(string path, string? lavfiComplex)
    {
        // Forget the previous file's numbers: until mpv reports the new ones, a stale duration
        // would stretch the new clip's timeline to the old clip's length.
        Position = 0;
        Duration = 0;
        SetProperty("lavfi-complex", lavfiComplex ?? "");
        Command("loadfile", path, "replace");
    }

    /// <summary>Unloads the file (releases it so it can be deleted).</summary>
    public void Stop() => Command("stop");

    public void SetPause(bool pause) => SetProperty("pause", pause);
    public void TogglePause() => SetPause(!Paused);

    public void Seek(double seconds, bool exact = true) =>
        Command("seek", Math.Max(0, seconds).ToString("0.000", CultureInfo.InvariantCulture), exact ? "absolute+exact" : "absolute+keyframes");

    /// <summary>Relative seek done by mpv itself, so rapid key presses add up correctly.</summary>
    public void SeekBy(double seconds) =>
        Command("seek", seconds.ToString("0.000", CultureInfo.InvariantCulture), "relative+exact");

    public void FrameStep() => Command("frame-step");
    public void FrameBackStep() => Command("frame-back-step");

    /// <summary>Loops playback between a and b (null clears the loop).</summary>
    public void SetLoop(double? a, double? b)
    {
        SetProperty("ab-loop-a", a.HasValue ? a.Value.ToString("0.000", CultureInfo.InvariantCulture) : "no");
        SetProperty("ab-loop-b", b.HasValue ? b.Value.ToString("0.000", CultureInfo.InvariantCulture) : "no");
    }

    /// <summary>Re-applies the audio mix graph (mpv re-initialises audio, a short blip at most).</summary>
    public void SetAudioGraph(string? lavfiComplex) => SetProperty("lavfi-complex", lavfiComplex ?? "");

    public void SetProperty(string name, object value) => Command("set_property", name, value);

    void Command(params object[] args)
    {
        try
        {
            var msg = new JsonObject
            {
                ["command"] = new JsonArray(args.Select(ToNode).ToArray()),
                ["request_id"] = Interlocked.Increment(ref _requestId),
            };
            lock (_writer) _writer.WriteLine(msg.ToJsonString());
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    static JsonNode? ToNode(object a) => a switch
    {
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        int i => JsonValue.Create(i),
        double d => JsonValue.Create(d),
        _ => JsonValue.Create(a.ToString()),
    };

    // ------------------------------------------------------------------ events

    async Task ReadLoop()
    {
        try
        {
            using var reader = new StreamReader(_pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                JsonNode? n;
                try { n = JsonNode.Parse(line); } catch { continue; }
                var ev = n?["event"]?.GetValue<string>();
                if (ev == "property-change")
                {
                    var name = n!["name"]?.GetValue<string>();
                    var data = n["data"];
                    _ui.Post(_ => OnProperty(name, data), null);
                }
                else if (ev == "file-loaded") _ui.Post(_ => FileLoaded?.Invoke(), null);
                else if (n?["error"] is JsonNode err && err.GetValue<string>() != "success")
                    Log.Warn($"[mpv] command failed: {line}");
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    void OnProperty(string? name, JsonNode? data)
    {
        switch (name)
        {
            case "time-pos": if (data is JsonValue v && v.TryGetValue<double>(out var p)) Position = p; break;
            case "duration": Duration = data is JsonValue d && d.TryGetValue<double>(out var du) ? du : 0; break;
            case "pause": if (data is JsonValue b && b.TryGetValue<bool>(out var pa)) Paused = pa; break;
            default: return;
        }
        StateChanged?.Invoke();
    }

    public void Dispose()
    {
        try { Command("quit"); } catch { }
        try { if (!_proc.WaitForExit(800)) _proc.Kill(); } catch { }
        // mpv has quit, so flushing the writer can hit a closed pipe; that's expected.
        try { _writer.Dispose(); } catch (IOException) { }
        try { _pipe.Dispose(); } catch (IOException) { }
        _proc.Dispose();
    }
}
