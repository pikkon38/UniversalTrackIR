using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using UniversalTrackIR.Models;

namespace UniversalTrackIR.Services;

public static class ProcessService
{
    /// <summary>Windowed processes, sorted. For picking the game the mouse output should work in.</summary>
    public static List<string> ListCandidates()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int self = Environment.ProcessId;

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id != self && p.MainWindowHandle != IntPtr.Zero)
                    names.Add(p.ProcessName);
            }
            catch
            {
                // access denied on some system processes
            }
            finally
            {
                p.Dispose();
            }
        }

        return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;

    public SettingsStore()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UniversalTrackIR");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "settings.json");
    }

    public string FilePath => _path;

    public AppSettings Load()
    {
        AppSettings settings;
        try
        {
            settings = File.Exists(_path)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Options) ?? new AppSettings()
                : new AppSettings();
        }
        catch
        {
            // Corrupt file: back it up and start fresh rather than crash.
            try { File.Copy(_path, _path + ".bad", true); } catch { }
            settings = new AppSettings();
        }

        settings.Profiles ??= new();
        settings.Outputs ??= new();
        settings.Hotkeys ??= new();
        if (settings.Profiles.Count == 0) settings.Profiles[AppSettings.DefaultProfileName] = new TrackingProfile();
        foreach (var p in settings.Profiles.Values) p.Repair();
        if (!settings.Profiles.ContainsKey(settings.ActiveProfile)) settings.ActiveProfile = settings.Profiles.Keys.First();
        return settings;
    }

    public void Save(AppSettings settings)
    {
        try
        {
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
            File.Move(tmp, _path, true);
        }
        catch
        {
            // Non-fatal. Settings just won't persist this time.
        }
    }
}
