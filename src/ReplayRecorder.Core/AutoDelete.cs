namespace ReplayRecorder.Core;

/// <summary>Settings → Storage "Delete clips older than N days": deletes for good, never favorites.</summary>
public static class AutoDelete
{
    /// <summary>Clips that are due: older than <paramref name="days"/> and not starred.</summary>
    public static List<ClipInfo> Due(IEnumerable<ClipInfo> clips, int days, Func<string, bool> isFavorite, DateTime now) =>
        clips.Where(c => c.Created < now.AddDays(-days) && !isFavorite(c.Path)).ToList();

    /// <summary>Deletes the due clips in the clips folder; returns how many went.</summary>
    public static int Run(string clipsFolder, int days)
    {
        ClipEdits.Reload(); // stars set in the library since this process last looked
        int n = 0;
        foreach (var c in Due(new ClipIndex(clipsFolder).Scan(), days, p => ClipEdits.Get(p).Favorite, DateTime.Now))
        {
            try { File.Delete(c.Path); n++; Log.Info($"Auto-deleted (older than {days} days): {c.Path}"); }
            catch (Exception ex) { Log.Warn($"Auto-delete failed for {c.Path}: {ex.Message}"); } // open in a player: next round
        }
        return n;
    }
}
