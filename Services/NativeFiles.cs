using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace UniversalTrackIR.Services;

/// <summary>
/// Unpacks the native pieces carried inside the exe to %LOCALAPPDATA%\UniversalTrackIR\clients.
/// Games load NPClient64.dll / freetrackclient64.dll from that folder, so it has to hold real files.
/// </summary>
public static class NativeFiles
{
    public const string StreamEngineDll = "tobii_stream_engine.dll";
    public const string StandInExe = "TrackIR.exe";

    private static readonly string[] Files =
    {
        "NPClient.dll",
        "NPClient64.dll",
        "freetrackclient.dll",
        "freetrackclient64.dll",
        StandInExe,
        StreamEngineDll,
    };

    public static string ClientFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UniversalTrackIR", "clients");

    /// <summary>Put a newer tobii_stream_engine.dll here to use it instead of the bundled one.</summary>
    public static string OverrideFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UniversalTrackIR");

    public static string StandInPath => Path.Combine(ClientFolder, StandInExe);

    /// <summary>Writes any file that is missing or differs. Files a running game has open are left as they are.</summary>
    public static string? Extract()
    {
        var problems = new List<string>();
        Directory.CreateDirectory(ClientFolder);
        var asm = Assembly.GetExecutingAssembly();

        foreach (string name in Files)
        {
            try
            {
                using var src = asm.GetManifestResourceStream("Native." + name);
                if (src == null)
                {
                    problems.Add($"{name} missing from this build");
                    continue;
                }

                string dest = Path.Combine(ClientFolder, name);
                if (File.Exists(dest) && new FileInfo(dest).Length == src.Length && SameBytes(dest, src)) continue;

                src.Position = 0;
                string tmp = dest + ".tmp";
                using (var dst = File.Create(tmp)) src.CopyTo(dst);
                File.Move(tmp, dest, true);
            }
            catch (IOException) when (File.Exists(Path.Combine(ClientFolder, name)))
            {
                // In use by a game. The copy there is from an earlier run and still works.
            }
            catch (Exception ex)
            {
                problems.Add($"{name}: {ex.Message}");
            }
        }

        return problems.Count == 0 ? null : string.Join("; ", problems);
    }

    /// <summary>Where to look for Stream Engine, best first.</summary>
    public static IEnumerable<string> StreamEngineCandidates()
    {
        yield return Path.Combine(OverrideFolder, StreamEngineDll);
        yield return Path.Combine(AppContext.BaseDirectory, StreamEngineDll);
        yield return Path.Combine(ClientFolder, StreamEngineDll);
    }

    private static bool SameBytes(string path, Stream src)
    {
        src.Position = 0;
        using var f = File.OpenRead(path);
        var a = new byte[1 << 16];
        var b = new byte[1 << 16];
        while (true)
        {
            int ra = src.ReadAtLeast(a, a.Length, throwOnEndOfStream: false);
            int rb = f.ReadAtLeast(b, b.Length, throwOnEndOfStream: false);
            if (ra != rb) return false;
            if (ra == 0) return true;
            if (!a.AsSpan(0, ra).SequenceEqual(b.AsSpan(0, rb))) return false;
        }
    }
}
