using System.Globalization;

namespace ReplayRecorder.Recorder;

/// <summary>Reads ffmpeg's segments.csv, the list of finished 2 s buffer pieces.</summary>
static class SegmentList
{
    /// <summary>Complete lines, "name,start,end"; the name may hold commas (then quoted). Bad lines are skipped.</summary>
    public static IEnumerable<(string Name, double Start, double End)> Parse(string text)
    {
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split(',');
            if (parts.Length < 3) continue;
            if (!double.TryParse(parts[^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var start) ||
                !double.TryParse(parts[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var end)) continue;
            yield return (string.Join(',', parts[..^2]).Trim('"'), start, end);
        }
    }
}
