using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace UniversalTrackIR.Services;

public sealed class ReleaseInfo
{
    public required Version Version { get; init; }
    public required string Tag { get; init; }
    public required string PageUrl { get; init; }
    public string? ExeUrl { get; init; }
    public string? SetupUrl { get; init; }
}

/// <summary>
/// Updates from GitHub Releases.
///
/// Check: reads the latest release of the repo and compares its tag (v0.3.0) to this build's version.
///
/// Install, when this copy came from the installer: downloads UniversalTrackIR-Setup.exe and runs it
/// silently. Setup closes this app, replaces it, keeps the Start menu entry and uninstaller current,
/// and starts the new version.
///
/// Install, for a loose exe: downloads UniversalTrackIR.exe next to the running exe, renames the
/// running exe to .old (Windows allows renaming a running exe, not deleting it), moves the new one
/// into place, and starts it. The .old file is cleaned up on the next launch.
/// </summary>
public static class UpdateService
{
    public const string Repo = "pikkon38/UniversalTrackIR";
    public const string ReleasesPage = "https://github.com/" + Repo + "/releases";
    private const string AssetName = "UniversalTrackIR.exe";
    private const string SetupAssetName = "UniversalTrackIR-Setup.exe";

    /// <summary>The installer drops this file next to the exe.</summary>
    public static bool IsInstalled => File.Exists(Path.Combine(Path.GetDirectoryName(ExePath)!, ".installed"));

    private static readonly HttpClient Http = CreateClient();

    public static Version CurrentVersion => Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));

    public static string ExePath => Environment.ProcessPath ?? throw new InvalidOperationException("Unknown exe path.");

    /// <summary>Returns the latest release if it is newer than this build, otherwise null.</summary>
    public static async Task<ReleaseInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var response = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // no releases yet
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;

        string tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;
        version = Normalize(version);
        if (version <= CurrentVersion) return null;

        string? exeUrl = null, setupUrl = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            string? name = asset.GetProperty("name").GetString();
            string? url = asset.GetProperty("browser_download_url").GetString();
            if (string.Equals(name, AssetName, StringComparison.OrdinalIgnoreCase)) exeUrl = url;
            else if (string.Equals(name, SetupAssetName, StringComparison.OrdinalIgnoreCase)) setupUrl = url;
        }

        // Release still building, or nothing this copy can install from.
        if ((IsInstalled ? setupUrl ?? exeUrl : exeUrl) == null) return null;

        return new ReleaseInfo
        {
            Version = version,
            Tag = tag,
            PageUrl = root.GetProperty("html_url").GetString() ?? ReleasesPage,
            ExeUrl = exeUrl,
            SetupUrl = setupUrl,
        };
    }

    /// <summary>
    /// Downloads and swaps in the new exe, then starts it. The caller must save settings and
    /// let go of the tracker before calling, and shut down right after this returns.
    /// </summary>
    public static async Task InstallAsync(ReleaseInfo release, IProgress<double>? progress, CancellationToken ct = default)
    {
        if (IsInstalled && release.SetupUrl != null)
        {
            await RunSetupAsync(release, progress, ct);
            return;
        }

        if (release.ExeUrl == null) throw new InvalidOperationException("This release has no exe to update from.");

        string exe = ExePath;
        string dir = Path.GetDirectoryName(exe)!;
        string download = exe + ".download";
        string old = exe + ".old";

        // 1. Download next to the exe (same drive, so the swap is an atomic rename).
        await DownloadAsync(release.ExeUrl, download, progress, ct);

        // 2. Swap. Rename the running exe out of the way, move the new one in.
        if (File.Exists(old)) TryDelete(old);
        File.Move(exe, old, true);
        try
        {
            File.Move(download, exe);
        }
        catch
        {
            File.Move(old, exe, true); // put the original back
            throw;
        }

        // 3. A build installed by Update.bat is a folder of files. The release exe is self-contained,
        //    so move the old framework-dependent pieces aside to keep the folder unambiguous.
        foreach (var name in new[] { "UniversalTrackIR.dll", "UniversalTrackIR.deps.json", "UniversalTrackIR.runtimeconfig.json" })
        {
            string path = Path.Combine(dir, name);
            if (File.Exists(path))
            {
                try { File.Move(path, path + ".old", true); } catch { /* harmless if it stays */ }
            }
        }

        // 4. Launch the new version.
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = dir });
    }

    /// <summary>Downloads the installer to %TEMP% and starts it silently. It closes this app and relaunches the new one.</summary>
    private static async Task RunSetupAsync(ReleaseInfo release, IProgress<double>? progress, CancellationToken ct)
    {
        string setup = Path.Combine(Path.GetTempPath(), $"UniversalTrackIR-Setup-{release.Version}.exe");
        await DownloadAsync(release.SetupUrl!, setup, progress, ct);

        Process.Start(new ProcessStartInfo(setup)
        {
            Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS",
            UseShellExecute = true,
        });
    }

    private static async Task DownloadAsync(string url, string path, IProgress<double>? progress, CancellationToken ct)
    {
        using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            long? total = response.Content.Headers.ContentLength;
            await using var src = await response.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);

            var buffer = new byte[1 << 16];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0) progress?.Report((double)done / total.Value);
            }
        }

        if (new FileInfo(path).Length < 1_000_000)
        {
            File.Delete(path);
            throw new InvalidDataException("Downloaded file is too small to be the app.");
        }
    }

    /// <summary>Removes leftovers from a previous update. Call at startup.</summary>
    public static void CleanupOldFiles()
    {
        try
        {
            string dir = Path.GetDirectoryName(ExePath)!;
            foreach (var file in Directory.GetFiles(dir, "*.old")) TryDelete(file);
            foreach (var file in Directory.GetFiles(dir, "*.download")) TryDelete(file);
        }
        catch
        {
            // best effort
        }
    }

    public static bool CanWriteToAppFolder()
    {
        try
        {
            string probe = Path.Combine(Path.GetDirectoryName(ExePath)!, ".write-test");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void OpenReleasesPage(string? url = null)
    {
        try { Process.Start(new ProcessStartInfo(url ?? ReleasesPage) { UseShellExecute = true }); }
        catch { }
    }

    private static Version Normalize(Version v) => new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("UniversalTrackIR", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }
}
