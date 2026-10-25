using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using UniversalTrackIR.Models;
using UniversalTrackIR.Tracking;

namespace UniversalTrackIR.Output;

/// <summary>
/// Feeds a vJoy virtual joystick (https://github.com/BrunnerInnovation/vJoy or the original jshafer817 build).
/// Yaw, pitch, roll go to X, Y, Z. Position goes to Rx, Ry, Rz. In the game, bind those axes to look/head controls.
/// vJoy's own installer puts vJoyInterface.dll on the machine; we load it from there.
/// </summary>
public sealed unsafe class VJoyOutput : IDisposable
{
    private const int VJD_STAT_OWN = 0;
    private const int VJD_STAT_FREE = 1;
    private const int VJD_STAT_BUSY = 2;
    private const int VJD_STAT_MISS = 3;

    private const uint HID_USAGE_X = 0x30;
    private const uint HID_USAGE_Y = 0x31;
    private const uint HID_USAGE_Z = 0x32;
    private const uint HID_USAGE_RX = 0x33;
    private const uint HID_USAGE_RY = 0x34;
    private const uint HID_USAGE_RZ = 0x35;

    private const int AxisMin = 1;
    private const int AxisMax = 0x8000;
    private const int AxisCenter = 0x4000;

    private IntPtr _lib;
    private delegate* unmanaged<int> _enabled;
    private delegate* unmanaged<uint, int> _status;
    private delegate* unmanaged<uint, int> _acquire;
    private delegate* unmanaged<uint, void> _relinquish;
    private delegate* unmanaged<int, uint, uint, int> _setAxis;
    private delegate* unmanaged<uint, int> _reset;

    private uint _device;
    private bool _owned;
    private long _lastTry;

    public string Status { get; private set; } = "vJoy: off";
    public bool IsActive => _owned;

    /// <summary>Grabs the vJoy device. Retries at most every 3 s when it isn't available.</summary>
    public bool Open(int deviceId)
    {
        if (_owned && _device == deviceId) return true;
        if (_owned) Close();

        long now = Environment.TickCount64;
        if (now - _lastTry < 3000) return false;
        _lastTry = now;

        try
        {
            if (_lib == IntPtr.Zero && !Load()) return false;

            if (_enabled() == 0)
            {
                Status = "vJoy: installed but disabled. Turn it on in Configure vJoy.";
                return false;
            }

            uint id = (uint)Math.Clamp(deviceId, 1, 16);
            int st = _status(id);
            switch (st)
            {
                case VJD_STAT_BUSY:
                    Status = $"vJoy: device {id} is in use by another program.";
                    return false;
                case VJD_STAT_MISS:
                    Status = $"vJoy: device {id} doesn't exist. Add it in Configure vJoy.";
                    return false;
                case VJD_STAT_OWN:
                case VJD_STAT_FREE:
                    break;
                default:
                    Status = $"vJoy: device {id} is in an unknown state.";
                    return false;
            }

            if (st == VJD_STAT_FREE && _acquire(id) == 0)
            {
                Status = $"vJoy: couldn't take device {id}.";
                return false;
            }

            _device = id;
            _owned = true;
            _reset(id);
            Status = $"vJoy: driving device {id}";
            return true;
        }
        catch (Exception ex)
        {
            Status = $"vJoy: {ex.Message}";
            return false;
        }
    }

    public void Write(in PoseOutput pose, TrackingProfile profile)
    {
        if (!_owned) return;

        // Each axis spans its own limit, so full deflection = the most that axis can ever output.
        Set(HID_USAGE_X, pose.Yaw, profile.Axes[Axis.Yaw].Limit);
        Set(HID_USAGE_Y, -pose.Pitch, profile.Axes[Axis.Pitch].Limit); // stick forward (up) = negative Y on most games
        Set(HID_USAGE_Z, pose.Roll, profile.Axes[Axis.Roll].Limit);
        Set(HID_USAGE_RX, pose.X, profile.Axes[Axis.X].Limit);
        Set(HID_USAGE_RY, pose.Y, profile.Axes[Axis.Y].Limit);
        Set(HID_USAGE_RZ, pose.Z, profile.Axes[Axis.Z].Limit);
    }

    private void Set(uint usage, double value, double limit)
    {
        double t = limit > 0 ? Math.Clamp(value / limit, -1, 1) : 0;
        int v = (int)Math.Round(AxisCenter + t * (AxisMax - AxisCenter));
        _setAxis(Math.Clamp(v, AxisMin, AxisMax), _device, usage);
    }

    private bool Load()
    {
        foreach (string path in Candidates())
        {
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out _lib)) break;
        }

        if (_lib == IntPtr.Zero)
        {
            Status = "vJoy: not installed. Get it from github.com/BrunnerInnovation/vJoy.";
            return false;
        }

        _enabled = (delegate* unmanaged<int>)NativeLibrary.GetExport(_lib, "vJoyEnabled");
        _status = (delegate* unmanaged<uint, int>)NativeLibrary.GetExport(_lib, "GetVJDStatus");
        _acquire = (delegate* unmanaged<uint, int>)NativeLibrary.GetExport(_lib, "AcquireVJD");
        _relinquish = (delegate* unmanaged<uint, void>)NativeLibrary.GetExport(_lib, "RelinquishVJD");
        _setAxis = (delegate* unmanaged<int, uint, uint, int>)NativeLibrary.GetExport(_lib, "SetAxis");
        _reset = (delegate* unmanaged<uint, int>)NativeLibrary.GetExport(_lib, "ResetVJD");
        return true;
    }

    private static IEnumerable<string> Candidates()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "vJoyInterface.dll");

        foreach (var root in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            string? dir = null;
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, root);
                using var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{8E31F76F-74C3-47F1-9550-E041EEDC5FBB}_is1");
                dir = key?.GetValue("InstallLocation") as string;
            }
            catch
            {
                // no access, fall through to the default path
            }
            if (!string.IsNullOrEmpty(dir)) yield return Path.Combine(dir, "x64", "vJoyInterface.dll");
        }

        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Path.Combine(pf, "vJoy", "x64", "vJoyInterface.dll");
    }

    public void Close()
    {
        if (_owned)
        {
            try
            {
                _reset(_device);
                _relinquish(_device);
            }
            catch
            {
                // driver gone
            }
            _owned = false;
        }
        _lastTry = 0;
        Status = "vJoy: off";
    }

    public void Dispose() => Close();
}
