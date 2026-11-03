using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace ReplayRecorder.Core;

public sealed record UpdateInfo(Version Version, string DownloadUrl, long Size, string? Sha256, string Notes);

/// <summary>
/// Checks the GitHub releases of this app's repo and installs a newer version in place.
///
/// Safety: only this repo's latest release is looked at, only the asset with the installer's exact
/// name is downloaded, only over HTTPS from GitHub's own hosts, and the file must match the size and
/// (when GitHub lists one) the SHA-256 of that asset before it is run. The installer then upgrades
/// over the current install without uninstalling, so settings and clips are untouched.
/// </summary>
public static class Updater
{
    public const string Repo = "3mirzenci/replay-recorder";
    public const string AssetName = "ReplayRecorder-Setup.exe";
    public static string ReleasesPage => $"https://github.com/{Repo}/releases";

    public static Version Current { get; } = Normalize(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0));

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var h = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromMinutes(10) };
        h.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ReplayRecorder", Current.ToString()));
        return h;
    }

    static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    /// <summary>The newer release, or null if up to date / offline / anything unexpected.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            using var resp = await Http.SendAsync(req, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
            var root = doc.RootElement;
            if (root.TryGetProperty("draft", out var d) && d.GetBoolean()) return null;
            if (root.TryGetProperty("prerelease", out var p) && p.GetBoolean()) return null;

            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return null;
            latest = Normalize(latest);
            if (latest <= Current) return null;

            foreach (var a in root.GetProperty("assets").EnumerateArray())
            {
                if (a.GetProperty("name").GetString() != AssetName) continue;
                var url = a.GetProperty("browser_download_url").GetString() ?? "";
                if (!IsGitHub(url)) return null;
                string? sha = a.TryGetProperty("digest", out var dg) && dg.GetString() is { } s &&
                              s.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? s[7..].ToLowerInvariant() : null;
                var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
                return new UpdateInfo(latest, url, a.GetProperty("size").GetInt64(), sha, notes);
            }
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Log.Info($"Update check skipped: {ex.Message}");
            return null;
        }
    }

    static bool IsGitHub(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps &&
        (u.Host == "github.com" || u.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>Downloads and verifies the installer. Returns its path in %TEMP%.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo u, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ReplayRecorder-update");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"ReplayRecorder-Setup-{u.Version}.exe");
        var part = file + ".part";

        using (var resp = await Http.GetAsync(u.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            // Redirects must stay on GitHub's hosts too.
            if (resp.RequestMessage?.RequestUri is { } final && !IsGitHub(final.ToString()))
                throw new InvalidOperationException($"Update download was redirected to an unexpected host ({final.Host}).");
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            var buf = new byte[1 << 16];
            long total = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                total += n;
                if (total > u.Size) throw new InvalidOperationException("Update download is larger than expected.");
                progress?.Report(u.Size > 0 ? (double)total / u.Size : 0);
            }
            if (total != u.Size) throw new InvalidOperationException("Update download is incomplete.");
        }

        if (u.Sha256 != null)
        {
            await using var fs = File.OpenRead(part);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
            if (hash != u.Sha256)
            {
                File.Delete(part);
                throw new InvalidOperationException("Update file failed its integrity check and was discarded.");
            }
        }
        File.Move(part, file, true);
        Log.Info($"Downloaded update {u.Version} ({u.Size / 1e6:0.0} MB, sha256 {(u.Sha256 != null ? "verified" : "not listed")})");
        return file;
    }

    /// <summary>
    /// Runs the installer as an in-place upgrade (progress window only, no questions). It closes both
    /// apps, installs, and starts the recorder again, plus the library when <paramref name="reopenLibrary"/>.
    /// </summary>
    public static void Install(string installer, bool reopenLibrary)
    {
        var args = "/SILENT /SUPPRESSMSGBOXES /NORESTART /UPDATE=1" + (reopenLibrary ? " /OPENLIBRARY=1" : "");
        Log.Info($"Starting update installer: {installer} {args}");
        Process.Start(new ProcessStartInfo(installer, args) { UseShellExecute = true });
    }
}
