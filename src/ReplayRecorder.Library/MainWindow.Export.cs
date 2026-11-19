using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using ReplayRecorder.Core;
using ReplayRecorder.Library.Player;

namespace ReplayRecorder.Library;

/// <summary>Discord export, for the open clip and for a selection of clips.</summary>
public partial class MainWindow
{
    // ================================================================== Discord export

    CancellationTokenSource? _exportCts;
    string? _exportPath;
    Point _dragStart;

    async void Export_Click(object sender, RoutedEventArgs e) => await ExportCurrent();

    async Task ExportCurrent()
    {
        if (_current == null || _exportCts != null) return;
        var clip = _current.Clip;
        var edit = new ClipEdit { In = _edit.In, Out = _edit.Out, GameVolumeDb = _edit.GameVolumeDb, MicVolumeDb = _edit.MicVolumeDb, DiscordVolumeDb = _edit.DiscordVolumeDb };
        if (clip.Duration <= 0 && Timeline.Duration > 0) clip.Duration = Timeline.Duration;

        _exportCts = new CancellationTokenSource();
        _exportPath = null;
        ShowExportPanel(save: false);
        DragTile.Opacity = 0.4;
        ExportFileText.Text = "Preparing…".T();
        ExportStatus.Text = "Starting export…".T();
        double len = edit.EffectiveOut(clip.Duration) - edit.In;
        ExportDetail.Text = "{0} clip · target {1:0.#} MB · original is never changed".T(TrimTimeline.Fmt(len), _settings.DiscordTargetMB);
        SetExportBar(0);

        var progress = new Progress<(double Fraction, string Status)>(p =>
        {
            SetExportBar(p.Fraction);
            ExportStatus.Text = p.Status;
        });
        try
        {
            var r = await Task.Run(() => DiscordExporter.ExportAsync(clip, edit, _settings, progress, _exportCts.Token));
            _exportPath = r.Path;
            SetExportBar(1);
            ExportStatus.Text = "Ready for Discord".T();
            ExportSizeText.Text = string.Create(Lang.Culture, $"{r.SizeBytes / 1e6:0.0} MB");
            ExportDetail.Text = $"{r.Plan.Describe()} · {r.Elapsed.TotalSeconds:0}s" +
                                (r.Attempts > 1 ? " · {0} passes to fit".T(r.Attempts) : "") +
                                (r.Plan.StreamCopy ? " · starts at the nearest keyframe".T() : "");
            ExportFileText.Text = Path.GetFileName(r.Path);
            DragTile.Opacity = 1;
            CopyFileBtn.IsEnabled = OpenExportBtn.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
            ExportStatus.Text = "Export cancelled".T();
            ExportFileText.Text = "";
            SetExportBar(0);
        }
        catch (Exception ex)
        {
            Log.Error("Export failed", ex);
            ExportStatus.Foreground = (System.Windows.Media.Brush)FindResource("Danger");
            ExportStatus.Text = ex.Message;
            ExportFileText.Text = "";
        }
        finally
        {
            EndExportPanel();
        }
    }

    /// <summary>The export panel, set up for a Discord export or for saving a trimmed clip (no Discord tile then).</summary>
    void ShowExportPanel(bool save)
    {
        ExportPanel.Visibility = Visibility.Visible;
        ExportBtn.IsEnabled = SaveTrimBtn.IsEnabled = false;
        CopyFileBtn.IsEnabled = OpenExportBtn.IsEnabled = false;
        var discord = save ? Visibility.Collapsed : Visibility.Visible;
        DragTile.Visibility = CopyFileBtn.Visibility = OpenExportBtn.Visibility = discord;
        ExportInfo.Margin = new Thickness(save ? 4 : 18, 0, 18, 0);
        OpenTrimBtn.Visibility = DeleteOriginalBtn.Visibility = Visibility.Collapsed;
        CancelExportBtn.Visibility = Visibility.Visible;
        CloseExportBtn.Visibility = Visibility.Collapsed;
        ExportSizeText.Text = "";
        ExportStatus.Foreground = (System.Windows.Media.Brush)FindResource("Text");
        SetExportBar(0);
    }

    void EndExportPanel()
    {
        _exportCts?.Dispose();
        _exportCts = null;
        ExportBtn.IsEnabled = true;
        CancelExportBtn.Visibility = Visibility.Collapsed;
        CloseExportBtn.Visibility = Visibility.Visible;
        UpdateTrimText();
    }

    // ================================================================== save trimmed clip

    ClipItem? _trimOriginal, _trimSaved;

    async void SaveTrim_Click(object sender, RoutedEventArgs e) => await SaveTrimmed();

    /// <summary>Saves the trimmed range as its own clip next to the original, then offers to delete the original.</summary>
    async Task SaveTrimmed()
    {
        if (_current == null || _exportCts != null) return;
        var original = _current;
        var clip = original.Clip;
        if (clip.Duration <= 0 && Timeline.Duration > 0) clip.Duration = Timeline.Duration;
        if (!_edit.IsTrimmed(clip.Duration)) return; // the S key, while the button is greyed out
        var edit = new ClipEdit { In = _edit.In, Out = _edit.Out, GameVolumeDb = _edit.GameVolumeDb, MicVolumeDb = _edit.MicVolumeDb, DiscordVolumeDb = _edit.DiscordVolumeDb };
        double len = edit.EffectiveOut(clip.Duration) - edit.In;

        _exportCts = new CancellationTokenSource();
        _trimOriginal = _trimSaved = null;
        ShowExportPanel(save: true);
        ExportStatus.Text = "Saving trimmed clip…".T();
        ExportDetail.Text = "{0} of {1} · same quality, all audio tracks · the original is kept".T(TrimTimeline.Fmt(len), TrimTimeline.Fmt(clip.Duration));
        var progress = new Progress<(double Fraction, string Status)>(p => { SetExportBar(p.Fraction); ExportStatus.Text = p.Status; });
        try
        {
            var path = await Task.Run(() => ClipTrimmer.SaveAsync(clip, edit, _settings, progress, _exportCts.Token));
            // Same volume sliders as the original (the tracks are copied as they are); the new clip is the trim, so no trim marks.
            ClipEdits.Set(path, new ClipEdit { GameVolumeDb = edit.GameVolumeDb, MicVolumeDb = edit.MicVolumeDb, DiscordVolumeDb = edit.DiscordVolumeDb });
            SyncWithDisk();
            _trimSaved = _items.FirstOrDefault(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));
            _trimOriginal = original;
            SetExportBar(1);
            ExportStatus.Text = "Saved to your library as \"{0}\"".T(Path.GetFileNameWithoutExtension(path));
            ExportSizeText.Text = string.Create(Lang.Culture, $"{new FileInfo(path).Length / 1e6:0.0} MB");
            ExportDetail.Text = "{0} clip · same quality, all audio tracks · you can delete the long original now".T(TrimTimeline.Fmt(len));
            OpenTrimBtn.Visibility = _trimSaved != null ? Visibility.Visible : Visibility.Collapsed;
            DeleteOriginalBtn.Visibility = _items.Contains(original) ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (OperationCanceledException)
        {
            ExportStatus.Text = "Saving cancelled".T();
            SetExportBar(0);
        }
        catch (Exception ex)
        {
            Log.Error("Save trimmed clip failed", ex);
            ExportStatus.Foreground = (System.Windows.Media.Brush)FindResource("Danger");
            ExportStatus.Text = ex.Message;
        }
        finally
        {
            EndExportPanel();
        }
    }

    void OpenTrim_Click(object sender, RoutedEventArgs e)
    {
        if (_trimSaved == null || !_items.Contains(_trimSaved)) return;
        ExportPanel.Visibility = Visibility.Collapsed;
        OpenClip(_trimSaved);
    }

    void DeleteOriginal_Click(object sender, RoutedEventArgs e)
    {
        if (_trimOriginal == null || !_items.Contains(_trimOriginal)) return;
        // The usual delete: asks first, then 10 s to Undo. Afterwards the player shows the new clip.
        if (!DeleteClips(new() { _trimOriginal })) return;
        _trimOriginal = null;
        ExportPanel.Visibility = Visibility.Collapsed;
        if (_trimSaved != null && _items.Contains(_trimSaved)) OpenClip(_trimSaved);
    }

    void SetExportBar(double f) =>
        ExportBar.Width = Math.Clamp(f, 0, 1) * ((FrameworkElement)ExportBar.Parent).ActualWidth;

    void DragTile_Down(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(this);

    /// <summary>Starts a real Windows file drag, exactly like dragging the file out of Explorer.</summary>
    void DragTile_Move(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _exportPath == null) return;
        var d = e.GetPosition(this) - _dragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        if (!KeepExport(_exportPath)) { ExportExpired(); return; }
        var data = new DataObject(DataFormats.FileDrop, new[] { _exportPath });
        DragDrop.DoDragDrop(DragTile, data, DragDropEffects.Copy);
    }

    void CopyFile_Click(object sender, RoutedEventArgs e)
    {
        if (_exportPath == null) return;
        if (!KeepExport(_exportPath)) { ExportExpired(); return; }
        // Same clipboard format as Ctrl+C in Explorer, so Ctrl+V in Discord attaches the file.
        Clipboard.SetFileDropList(new System.Collections.Specialized.StringCollection { _exportPath });
        ExportStatus.Text = "Copied! Press Ctrl+V in Discord".T();
    }

    void OpenExport_Click(object sender, RoutedEventArgs e)
    {
        if (_exportPath == null) return;
        if (KeepExport(_exportPath)) ShowInFolder(_exportPath); else ExportExpired();
    }

    /// <summary>
    /// Exports are deleted <see cref="DiscordExporter.ExportLifetime"/> after their file time, so using
    /// one (copy, drag, show) restarts that clock. False if it's already gone.
    /// </summary>
    static bool KeepExport(string path)
    {
        try { File.SetLastWriteTime(path, DateTime.Now); return true; }
        catch { return false; }
    }

    void ExportExpired()
    {
        ExportStatus.Foreground = (System.Windows.Media.Brush)FindResource("Danger");
        ExportStatus.Text = "This export was cleaned up after {0:0} minutes. Export it again.".T(DiscordExporter.ExportLifetime.TotalMinutes);
        DragTile.Opacity = 0.4;
        CopyFileBtn.IsEnabled = OpenExportBtn.IsEnabled = false;
    }
    void CancelExport_Click(object sender, RoutedEventArgs e) => _exportCts?.Cancel();
    void CloseExport_Click(object sender, RoutedEventArgs e) => ExportPanel.Visibility = Visibility.Collapsed;

    // ================================================================== batch Discord export

    CancellationTokenSource? _batchCts;
    List<string>? _batchPaths;

    async void SelExport_Click(object sender, RoutedEventArgs e)
    {
        var clips = Selected();
        if (clips.Count == 0 || _batchCts != null) return;
        if (clips.Count == 1)
        {
            // One clip: the player's export panel already has the drag tile and details.
            OpenClip(clips[0]);
            await ExportCurrent();
            return;
        }
        await ExportManyAsync(clips);
    }

    async Task ExportManyAsync(List<ClipItem> clips)
    {
        _batchCts = new CancellationTokenSource();
        _batchPaths = null;
        var done = new List<string>();
        int failedCount = 0;
        BatchPanel.Visibility = Visibility.Visible;
        BatchProgress.Visibility = Visibility.Visible;
        BatchCancelBtn.Visibility = Visibility.Visible;
        BatchDragTile.Visibility = BatchCopyBtn.Visibility = Visibility.Collapsed;
        SetBatchBar(0);
        UpdateSelectionUi();
        try
        {
            for (int i = 0; i < clips.Count; i++)
            {
                var clip = clips[i].Clip;
                int n = i;
                var progress = new Progress<(double Fraction, string Status)>(p =>
                {
                    BatchStatus.Text = "{0} of {1}: {2}".T(n + 1, clips.Count, p.Status);
                    SetBatchBar((n + p.Fraction) / clips.Count);
                });
                BatchStatus.Text = "Exporting {0} of {1}…".T(n + 1, clips.Count);
                try
                {
                    // Each clip keeps the trim and volumes saved for it in the player.
                    var edit = ClipEdits.Get(clip.Path);
                    if (clip.Duration <= 0) await _index.ProbeAsync(clip, _batchCts.Token);
                    var r = await Task.Run(() => DiscordExporter.ExportAsync(clip, edit, _settings, progress, _batchCts.Token));
                    done.Add(r.Path);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    failedCount++;
                    Log.Error($"Batch export failed for {clip.Path}", ex);
                }
            }
            SetBatchBar(1);
            BatchStatus.Text = failedCount == 0 ? "All exported".T() : "{0} failed (see log)".T(failedCount);
        }
        catch (OperationCanceledException) { BatchStatus.Text = "Export cancelled".T(); }
        finally
        {
            _batchCts.Dispose();
            _batchCts = null;
            BatchCancelBtn.Visibility = Visibility.Collapsed;
        }

        if (done.Count > 0)
        {
            // The clock starts now, not when each file was written: a long batch would otherwise lose its first files.
            foreach (var f in done) KeepExport(f);
            _batchPaths = done;
            long total = done.Where(File.Exists).Sum(f => new FileInfo(f).Length);
            BatchDragText.Text = (done.Count == 1 ? "Drag {0} file into Discord  ·  {1:0.0} MB" : "Drag {0} files into Discord  ·  {1:0.0} MB").T(done.Count, total / 1e6);
            BatchDragTile.Visibility = BatchCopyBtn.Visibility = Visibility.Visible;
            BatchProgress.Visibility = failedCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        UpdateSelectionUi();
    }

    void SetBatchBar(double f) => BatchBar.Width = Math.Clamp(f, 0, 1) * ((FrameworkElement)BatchBar.Parent).ActualWidth;
    void BatchCancel_Click(object sender, RoutedEventArgs e) => _batchCts?.Cancel();

    Point _batchDragStart;
    void BatchDrag_Down(object sender, MouseButtonEventArgs e) => _batchDragStart = e.GetPosition(this);

    void BatchDrag_Move(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _batchPaths == null) return;
        var d = e.GetPosition(this) - _batchDragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var files = BatchFilesStillThere();
        if (files.Length > 0) DragDrop.DoDragDrop(BatchDragTile, new DataObject(DataFormats.FileDrop, files), DragDropEffects.Copy);
    }

    /// <summary>The batch's exports that still exist (their clock restarted), saying so when some were cleaned up.</summary>
    string[] BatchFilesStillThere()
    {
        var still = _batchPaths!.Where(KeepExport).ToArray();
        if (still.Length < _batchPaths!.Count)
        {
            BatchProgress.Visibility = Visibility.Visible;
            BatchStatus.Text = still.Length == 0
                ? "These exports were cleaned up after {0:0} minutes. Export them again.".T(DiscordExporter.ExportLifetime.TotalMinutes)
                : "{0} of {1} exports were cleaned up; using the other {2}.".T(_batchPaths.Count - still.Length, _batchPaths.Count, still.Length);
        }
        return still;
    }

    void BatchCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_batchPaths == null) return;
        var files = new System.Collections.Specialized.StringCollection();
        var still = BatchFilesStillThere();
        if (still.Length == 0) return;
        files.AddRange(still);
        Clipboard.SetFileDropList(files);
        BatchStatus.Text = "Copied! Press Ctrl+V in Discord".T();
        BatchProgress.Visibility = Visibility.Visible;
        SetBatchBar(1);
    }
}
