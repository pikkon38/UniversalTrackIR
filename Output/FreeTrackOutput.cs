using System;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;
using Microsoft.Win32;
using UniversalTrackIR.Services;
using UniversalTrackIR.Tracking;

namespace UniversalTrackIR.Output;

/// <summary>
/// The FreeTrack shared memory block, which also feeds TrackIR games.
///
/// Games that support TrackIR load NPClient64.dll from the folder in
/// HKCU\Software\NaturalPoint\NATURALPOINT\NPClient Location\Path. FreeTrack games load
/// freetrackclient64.dll from HKCU\Software\Freetrack\FreeTrackClient\Path. Both DLLs (from opentrack)
/// read this block. When a TrackIR game registers its ID, we look up its key in the game list and
/// hand it over so the DLL can encode the data the way that game expects.
/// </summary>
public sealed unsafe class FreeTrackOutput : IDisposable
{
    private const string MapName = "FT_SharedMem";
    private const string MutexName = "FT_Mutext";

    // FTHeap layout (freetrackclient/fttypes.h in opentrack). Offsets in bytes.
    private const int OffDataId = 0;
    private const int OffCamWidth = 4;
    private const int OffCamHeight = 8;
    private const int OffYaw = 12;
    private const int OffPitch = 16;
    private const int OffRoll = 20;
    private const int OffX = 24;
    private const int OffY = 28;
    private const int OffZ = 32;
    private const int OffRawYaw = 36;
    private const int OffRawPitch = 40;
    private const int OffRawRoll = 44;
    private const int OffRawX = 48;
    private const int OffRawY = 52;
    private const int OffRawZ = 56;
    private const int OffGameId = 92;
    private const int OffTable = 96;
    private const int OffGameId2 = 104;
    private const int HeapSize = 108;

    private MemoryMappedFile? _map;
    private MemoryMappedViewAccessor? _view;
    private byte* _ptr;
    private Mutex? _mutex;
    private Process? _standIn;
    private int _gameId;
    private string _gameName = string.Empty;

    public bool IsOpen => _ptr != null;
    public string Status { get; private set; } = "TrackIR / FreeTrack: off";

    /// <summary>Name of the TrackIR game reading us right now, if it told us who it is.</summary>
    public string GameName => Volatile.Read(ref _gameName);

    public bool Open(string clientFolder, bool runStandIn, string standInPath)
    {
        if (IsOpen) return true;

        try
        {
            _mutex = new Mutex(false, MutexName);
            _map = MemoryMappedFile.CreateOrOpen(MapName, HeapSize, MemoryMappedFileAccess.ReadWrite);
            _view = _map.CreateViewAccessor(0, HeapSize, MemoryMappedFileAccess.ReadWrite);
            byte* p = null;
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            _ptr = p + _view.PointerOffset;

            Write32(OffDataId, 1);
            Write32(OffCamWidth, 100);
            Write32(OffCamHeight, 250);
            Write32(OffGameId2, 0);
            Write32(OffTable, 0);
            Write32(OffTable + 4, 0);
            _gameId = 0;

            Register(clientFolder);

            if (runStandIn) StartStandIn(standInPath);

            Status = "TrackIR / FreeTrack: ready. Start your game.";
            return true;
        }
        catch (Exception ex)
        {
            Close();
            Status = $"TrackIR / FreeTrack: {ex.Message}";
            return false;
        }
    }

    /// <summary>Publishes a pose. Degrees and mm in this app's conventions (yaw + right, pitch + up).</summary>
    public void Write(in PoseOutput pose)
    {
        if (_ptr == null) return;
        const double d2r = Math.PI / 180;

        // FreeTrack wants radians with yaw + to the left and pitch + up; position in mm.
        // Matches opentrack's proto-ft. Falcon BMS bumps at exactly 90° pitch, so stay just shy of it.
        double pitch = Math.Abs(pose.Pitch - 90) < 0.15 ? 89.86 : pose.Pitch;
        WriteF(OffYaw, (float)(-pose.Yaw * d2r));
        WriteF(OffPitch, (float)(pitch * d2r));
        WriteF(OffRoll, (float)(pose.Roll * d2r));
        WriteF(OffX, (float)pose.X);
        WriteF(OffY, (float)pose.Y);
        WriteF(OffZ, (float)pose.Z);

        WriteF(OffRawYaw, (float)(-pose.RawYaw * d2r));
        WriteF(OffRawPitch, (float)(pose.RawPitch * d2r));
        WriteF(OffRawRoll, (float)(pose.RawRoll * d2r));
        WriteF(OffRawX, (float)pose.RawX);
        WriteF(OffRawY, (float)pose.RawY);
        WriteF(OffRawZ, (float)pose.RawZ);

        int id = Read32(OffGameId);
        if (id != _gameId)
        {
            // A TrackIR game registered (or a new one did). Give the client DLL that game's key,
            // then set GameID2 = GameID, which tells the DLL the key is ready.
            var entry = GameTable.Find(id);
            ulong key = entry?.Key ?? 0;
            Write32(OffTable, (int)(key & 0xFFFFFFFF));
            Write32(OffTable + 4, (int)(key >> 32));
            Write32(OffGameId2, id);
            Write32(OffDataId, 0);
            _gameId = id;

            Volatile.Write(ref _gameName, id == 0 ? string.Empty : entry?.Name ?? $"TrackIR game #{id}");
        }
        else
        {
            Interlocked.Increment(ref *(int*)(_ptr + OffDataId));
        }
    }

    /// <summary>Points games at our client DLLs. Real TrackIR software will point them back when you run it.</summary>
    private static void Register(string folder)
    {
        string path = folder.Replace('\\', '/');
        if (!path.EndsWith('/')) path += "/";

        using (var np = Registry.CurrentUser.CreateSubKey(@"Software\NaturalPoint\NATURALPOINT\NPClient Location"))
            np.SetValue("Path", path);
        using (var ft = Registry.CurrentUser.CreateSubKey(@"Software\Freetrack\FreeTrackClient"))
            ft.SetValue("Path", path);
    }

    private void StartStandIn(string exe)
    {
        if (!File.Exists(exe)) return;
        try
        {
            // Some games only enable head tracking when a process called TrackIR is running.
            foreach (var p in Process.GetProcessesByName("TrackIR"))
            {
                p.Dispose();
                return; // real one or ours from a previous run, either way it's there
            }
            _standIn = Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
            });
        }
        catch
        {
            // Optional. Most games don't need it.
        }
    }

    private void WriteF(int offset, float value) =>
        Interlocked.Exchange(ref *(int*)(_ptr + offset), BitConverter.SingleToInt32Bits(value));

    private void Write32(int offset, int value) => Interlocked.Exchange(ref *(int*)(_ptr + offset), value);

    private int Read32(int offset) => Interlocked.CompareExchange(ref *(int*)(_ptr + offset), 0, 0);

    /// <summary>Recenters the game view (zero pose) and lets go of the shared memory.</summary>
    public void Close()
    {
        if (_ptr != null)
        {
            try { Write(default); } catch { }
            _view!.SafeMemoryMappedViewHandle.ReleasePointer();
            _ptr = null;
        }
        _view?.Dispose();
        _view = null;
        _map?.Dispose();
        _map = null;
        _mutex?.Dispose();
        _mutex = null;
        Volatile.Write(ref _gameName, string.Empty);
        _gameId = 0;

        if (_standIn != null)
        {
            try { if (!_standIn.HasExited) _standIn.Kill(); } catch { }
            _standIn.Dispose();
            _standIn = null;
        }

        Status = "TrackIR / FreeTrack: off";
    }

    public void Dispose() => Close();
}
