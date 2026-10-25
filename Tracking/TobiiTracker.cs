using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace UniversalTrackIR.Tracking;

/// <summary>Latest head pose from the tracker, already in this app's conventions.</summary>
public struct HeadSample
{
    public bool RotationValid;
    public bool PositionValid;

    /// <summary>Degrees. Yaw: + is looking right. Pitch: + is looking up. Roll: as the Tobii reports it.</summary>
    public double Yaw, Pitch, Roll;

    /// <summary>Millimetres from the tracker.</summary>
    public double X, Y, Z;

    public long TimestampUs;
}

/// <summary>Latest gaze point on the screen.</summary>
public struct GazeSample
{
    public bool Valid;

    /// <summary>-1 (left/bottom) to +1 (right/top). 0,0 is the middle of the screen the tracker is set up on.</summary>
    public double X, Y;

    public long TimestampUs;
}

/// <summary>
/// Talks to the Tobii Eye Tracker through Tobii Stream Engine (tobii_stream_engine.dll), the same C API
/// opentrack uses. The DLL is loaded at runtime so a newer copy can be dropped in without a rebuild,
/// and so the app still opens when no tracker is installed.
///
/// Not thread safe: call <see cref="Connect"/>, <see cref="Poll"/> and <see cref="Dispose"/> from one thread.
/// </summary>
public sealed unsafe class TobiiTracker : IDisposable
{
    private const int TOBII_ERROR_NO_ERROR = 0;
    private const int TOBII_ERROR_CONNECTION_FAILED = 5;
    private const int TOBII_ERROR_NOT_SUPPORTED = 3;
    private const int TOBII_VALIDITY_VALID = 1;
    private const int TOBII_FIELD_OF_USE_INTERACTIVE = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct TobiiVersion { public int Major, Minor, Revision, Build; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TobiiHeadPose
    {
        public long TimestampUs;
        public int PositionValidity;
        public float PosX, PosY, PosZ;
        public int RotXValidity, RotYValidity, RotZValidity;
        public float RotX, RotY, RotZ; // radians
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TobiiGazePoint
    {
        public long TimestampUs;
        public int Validity;
        public float X, Y; // 0..1 of the display, origin top left
    }

    private IntPtr _lib;
    private IntPtr _api;
    private IntPtr _device;
    private GCHandle _self;
    private TobiiVersion _version;
    private bool _headSubscribed;
    private bool _gazeSubscribed;
    private long _lastReconnectTicks;

    // Exports, resolved once.
    private delegate* unmanaged[Cdecl]<TobiiVersion*, int> _getApiVersion;
    private delegate* unmanaged[Cdecl]<IntPtr*, IntPtr, IntPtr, int> _apiCreate;
    private delegate* unmanaged[Cdecl]<IntPtr, int> _apiDestroy;
    private delegate* unmanaged[Cdecl]<IntPtr, delegate* unmanaged[Cdecl]<byte*, IntPtr, void>, IntPtr, int> _enumerateUrls;
    private IntPtr _deviceCreate; // signature depends on the Stream Engine version
    private delegate* unmanaged[Cdecl]<IntPtr, int> _deviceDestroy;
    private delegate* unmanaged[Cdecl]<IntPtr, int> _processCallbacks;
    private delegate* unmanaged[Cdecl]<IntPtr, int> _reconnect;
    private delegate* unmanaged[Cdecl]<IntPtr, delegate* unmanaged[Cdecl]<TobiiHeadPose*, IntPtr, void>, IntPtr, int> _headSubscribe;
    private delegate* unmanaged[Cdecl]<IntPtr, int> _headUnsubscribe;
    private delegate* unmanaged[Cdecl]<IntPtr, delegate* unmanaged[Cdecl]<TobiiGazePoint*, IntPtr, void>, IntPtr, int> _gazeSubscribe;
    private delegate* unmanaged[Cdecl]<IntPtr, int> _gazeUnsubscribe;

    private HeadSample _head;
    private GazeSample _gaze;

    public bool IsConnected => _device != IntPtr.Zero;
    public bool HasGaze => _gazeSubscribed;
    public string Status { get; private set; } = "Tobii: not started";
    public string? LoadedFrom { get; private set; }

    public HeadSample Head => _head;
    public GazeSample Gaze => _gaze;

    /// <summary>
    /// Loads the library (first time), finds the tracker and subscribes to head pose and gaze.
    /// Returns false with <see cref="Status"/> explaining why when it can't. Safe to call again to retry.
    /// </summary>
    public bool Connect(IEnumerable<string> dllCandidates)
    {
        if (IsConnected) return true;

        try
        {
            if (_lib == IntPtr.Zero && !LoadLibrary(dllCandidates)) return false;

            if (_api == IntPtr.Zero)
            {
                IntPtr api;
                int err = _apiCreate(&api, IntPtr.Zero, IntPtr.Zero);
                if (err != TOBII_ERROR_NO_ERROR) return Fail($"Tobii: couldn't start Stream Engine (error {err}).");
                _api = api;
            }

            string? url = FindDeviceUrl();
            if (url == null) return Fail("Tobii: no eye tracker found. Is it plugged in and is Tobii Experience running?");

            IntPtr device;
            byte[] urlBytes = Encoding.UTF8.GetBytes(url + "\0");
            int createErr;
            fixed (byte* u = urlBytes)
            {
                // Stream Engine 4 added a "field of use" argument in the middle.
                createErr = _version.Major >= 4
                    ? ((delegate* unmanaged[Cdecl]<IntPtr, byte*, int, IntPtr*, int>)_deviceCreate)(_api, u, TOBII_FIELD_OF_USE_INTERACTIVE, &device)
                    : ((delegate* unmanaged[Cdecl]<IntPtr, byte*, IntPtr*, int>)_deviceCreate)(_api, u, &device);
            }
            if (createErr != TOBII_ERROR_NO_ERROR) return Fail($"Tobii: found {url} but couldn't connect (error {createErr}).");
            _device = device;

            if (!_self.IsAllocated) _self = GCHandle.Alloc(this);
            IntPtr self = GCHandle.ToIntPtr(_self);

            int headErr = _headSubscribe(_device, &OnHeadPose, self);
            _headSubscribed = headErr == TOBII_ERROR_NO_ERROR;

            int gazeErr = _gazeSubscribe(_device, &OnGazePoint, self);
            _gazeSubscribed = gazeErr == TOBII_ERROR_NO_ERROR;

            if (!_headSubscribed)
            {
                string why = headErr == TOBII_ERROR_NOT_SUPPORTED
                    ? "this tracker doesn't offer head pose"
                    : $"error {headErr}";
                CloseDevice();
                return Fail($"Tobii: connected, but head tracking isn't available ({why}).");
            }

            Status = _gazeSubscribed
                ? $"Tobii connected (Stream Engine {_version.Major}.{_version.Minor}.{_version.Revision})"
                : $"Tobii connected, head only. Gaze unavailable (error {gazeErr}).";
            return true;
        }
        catch (Exception ex)
        {
            CloseDevice();
            return Fail($"Tobii: {ex.Message}");
        }
    }

    /// <summary>Delivers any queued samples. Call often (every few ms). Returns false if the connection dropped.</summary>
    public bool Poll()
    {
        if (_device == IntPtr.Zero) return false;

        int err = _processCallbacks(_device);
        if (err == TOBII_ERROR_NO_ERROR) return true;

        if (err == TOBII_ERROR_CONNECTION_FAILED)
        {
            Status = "Tobii: connection lost, reconnecting...";
            _head.RotationValid = _head.PositionValid = false;
            _gaze.Valid = false;

            // Reconnect is blocking; don't hammer it.
            long now = Environment.TickCount64;
            if (now - _lastReconnectTicks > 1000)
            {
                _lastReconnectTicks = now;
                if (_reconnect(_device) == TOBII_ERROR_NO_ERROR)
                    Status = $"Tobii connected (Stream Engine {_version.Major}.{_version.Minor}.{_version.Revision})";
            }
            return false;
        }

        return true; // transient (callback in progress etc.)
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnHeadPose(TobiiHeadPose* pose, IntPtr user)
    {
        if (GCHandle.FromIntPtr(user).Target is not TobiiTracker self) return;

        // Tobii: +x right, +y up, +z toward the user. Rotations in radians: x pitch, y yaw, z roll.
        // Signs follow opentrack's Tobii tracker, which games have been tested against.
        const double r2d = 180.0 / Math.PI;
        var h = new HeadSample { TimestampUs = pose->TimestampUs };

        h.RotationValid = pose->RotXValidity == TOBII_VALIDITY_VALID && pose->RotYValidity == TOBII_VALIDITY_VALID;
        if (h.RotationValid)
        {
            h.Yaw = -pose->RotY * r2d;
            h.Pitch = -pose->RotX * r2d;
            h.Roll = pose->RotZValidity == TOBII_VALIDITY_VALID ? pose->RotZ * r2d : 0;
        }

        h.PositionValid = pose->PositionValidity == TOBII_VALIDITY_VALID;
        if (h.PositionValid)
        {
            h.X = -pose->PosX;
            h.Y = pose->PosY;
            h.Z = pose->PosZ;
        }
        else
        {
            h.X = self._head.X;
            h.Y = self._head.Y;
            h.Z = self._head.Z;
        }

        self._head = h;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnGazePoint(TobiiGazePoint* gaze, IntPtr user)
    {
        if (GCHandle.FromIntPtr(user).Target is not TobiiTracker self) return;

        if (gaze->Validity != TOBII_VALIDITY_VALID)
        {
            self._gaze.Valid = false;
            return;
        }

        self._gaze = new GazeSample
        {
            Valid = true,
            X = Math.Clamp(gaze->X * 2.0 - 1.0, -1.5, 1.5),
            Y = Math.Clamp(1.0 - gaze->Y * 2.0, -1.5, 1.5),
            TimestampUs = gaze->TimestampUs,
        };
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnDeviceUrl(byte* url, IntPtr user)
    {
        var list = (List<string>)GCHandle.FromIntPtr(user).Target!;
        string? s = Marshal.PtrToStringUTF8((IntPtr)url);
        if (!string.IsNullOrEmpty(s)) list.Add(s);
    }

    private string? FindDeviceUrl()
    {
        var urls = new List<string>();
        var handle = GCHandle.Alloc(urls);
        try
        {
            int err = _enumerateUrls(_api, &OnDeviceUrl, GCHandle.ToIntPtr(handle));
            if (err != TOBII_ERROR_NO_ERROR) return null;
        }
        finally
        {
            handle.Free();
        }
        return urls.Count > 0 ? urls[0] : null;
    }

    private bool LoadLibrary(IEnumerable<string> candidates)
    {
        var tried = new List<string>();
        foreach (string path in candidates)
        {
            if (!File.Exists(path)) continue;
            tried.Add(path);
            if (NativeLibrary.TryLoad(path, out _lib))
            {
                LoadedFrom = path;
                break;
            }
        }

        if (_lib == IntPtr.Zero)
            return Fail(tried.Count == 0
                ? "Tobii: tobii_stream_engine.dll is missing."
                : $"Tobii: couldn't load {tried[0]}.");

        _getApiVersion = (delegate* unmanaged[Cdecl]<TobiiVersion*, int>)Export("tobii_get_api_version");
        _apiCreate = (delegate* unmanaged[Cdecl]<IntPtr*, IntPtr, IntPtr, int>)Export("tobii_api_create");
        _apiDestroy = (delegate* unmanaged[Cdecl]<IntPtr, int>)Export("tobii_api_destroy");
        _enumerateUrls = (delegate* unmanaged[Cdecl]<IntPtr, delegate* unmanaged[Cdecl]<byte*, IntPtr, void>, IntPtr, int>)Export("tobii_enumerate_local_device_urls");
        _deviceCreate = Export("tobii_device_create");
        _deviceDestroy = (delegate* unmanaged[Cdecl]<IntPtr, int>)Export("tobii_device_destroy");
        _processCallbacks = (delegate* unmanaged[Cdecl]<IntPtr, int>)Export("tobii_device_process_callbacks");
        _reconnect = (delegate* unmanaged[Cdecl]<IntPtr, int>)Export("tobii_device_reconnect");
        _headSubscribe = (delegate* unmanaged[Cdecl]<IntPtr, delegate* unmanaged[Cdecl]<TobiiHeadPose*, IntPtr, void>, IntPtr, int>)Export("tobii_head_pose_subscribe");
        _headUnsubscribe = (delegate* unmanaged[Cdecl]<IntPtr, int>)Export("tobii_head_pose_unsubscribe");
        _gazeSubscribe = (delegate* unmanaged[Cdecl]<IntPtr, delegate* unmanaged[Cdecl]<TobiiGazePoint*, IntPtr, void>, IntPtr, int>)Export("tobii_gaze_point_subscribe");
        _gazeUnsubscribe = (delegate* unmanaged[Cdecl]<IntPtr, int>)Export("tobii_gaze_point_unsubscribe");

        TobiiVersion v;
        if (_getApiVersion(&v) == TOBII_ERROR_NO_ERROR) _version = v;
        return true;
    }

    private IntPtr Export(string name) =>
        NativeLibrary.TryGetExport(_lib, name, out IntPtr p)
            ? p
            : throw new EntryPointNotFoundException($"{name} not found in {Path.GetFileName(LoadedFrom)}. The Stream Engine DLL is too old or not a Stream Engine DLL.");

    private bool Fail(string status)
    {
        Status = status;
        return false;
    }

    private void CloseDevice()
    {
        if (_device == IntPtr.Zero) return;
        try
        {
            if (_headSubscribed) _headUnsubscribe(_device);
            if (_gazeSubscribed) _gazeUnsubscribe(_device);
            _deviceDestroy(_device);
        }
        catch
        {
            // shutting down anyway
        }
        _device = IntPtr.Zero;
        _headSubscribed = _gazeSubscribed = false;
        _head = default;
        _gaze = default;
    }

    public void Dispose()
    {
        CloseDevice();
        if (_api != IntPtr.Zero)
        {
            try { _apiDestroy(_api); } catch { }
            _api = IntPtr.Zero;
        }
        if (_self.IsAllocated) _self.Free();
        // The library stays loaded. Unloading Stream Engine while its threads wind down can crash.
        Status = "Tobii: stopped";
    }
}
