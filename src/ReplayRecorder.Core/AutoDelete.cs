namespace ReplayRecorder.Core;

/// <summary>Settings → Storage "Delete clips older than N days": deletes for good, never favorites.</summary>
public static class AutoDelete
{
    /// <summary>Clips that are due: named by the recorder (game + date stamp), older than <paramref name="days"/>, not starred.
    /// Other videos in the folder, and clips the user renamed, are never touched.</summary>
    public static List<ClipInfo> Due(IEnumerable<ClipInfo> clips, int days, Func<string, bool> isFavorite, DateTime now) =>
        clips.Where(c => ClipIndex.DateInName.IsMatch(Path.GetFileName(c.Path)) && c.Created < now.AddDays(-days) && !isFavorite(c.Path)).ToList();

    /// <summary>Deletes the due clips in the clips folder; returns how many went.</summary>
    public static int Run(string clipsFolder, int days)
    {
        // Stars set in the library since this process last looked; unknown favorites mean no deleting this round.
        if (!ClipEdits.Reload()) { Log.Warn("Auto-delete skipped: the favorites file was unreadable"); return 0; }
        int n = 0;
        foreach (var c in Due(new ClipIndex(clipsFolder).Scan(), days, p => ClipEdits.Get(p).Favorite, DateTime.Now))
        {
            try { File.Delete(c.Path); n++; Log.Info($"Auto-deleted (older than {days} days): {c.Path}"); }
            catch (Exception ex) { Log.Warn($"Auto-delete failed for {c.Path}: {ex.Message}"); } // open in a player: next round
        }
        return n;
    }
}
