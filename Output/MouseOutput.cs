using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UniversalTrackIR.Models;
using UniversalTrackIR.Tracking;

namespace UniversalTrackIR.Output;

/// <summary>
/// For games with no head tracking: turns changes in yaw and pitch into relative mouse movement,
/// the same input as moving the mouse in free look. Only moves while the chosen game is in front,
/// so your desktop cursor is left alone.
/// </summary>
public sealed class MouseOutput
{
    private double _lastYaw, _lastPitch;
    private double _carryX, _carryY;
    private bool _primed;
    private bool _wasActive;

    private uint _cachedPid;
    private bool _cachedMatch;
    private string _cachedProcess = string.Empty;
    private long _cacheTicks;

    public string Status { get; private set; } = "Mouse: off";

    public void Update(in PoseOutput pose, OutputSettings s)
    {
        bool active = !string.IsNullOrWhiteSpace(s.MouseProcess) && IsForeground(s.MouseProcess);

        if (!active)
        {
            _primed = false;
            _wasActive = false;
            Status = string.IsNullOrWhiteSpace(s.MouseProcess)
                ? "Mouse: pick the game it should work in"
                : $"Mouse: waiting for {s.MouseProcess} to be in front";
            return;
        }

        if (!_wasActive) Status = $"Mouse: moving in {s.MouseProcess}";
        _wasActive = true;

        // The first frame in the game sets the reference, so switching to the game doesn't throw the view.
        if (!_primed)
        {
            _lastYaw = pose.Yaw;
            _lastPitch = pose.Pitch;
            _carryX = _carryY = 0;
            _primed = true;
            return;
        }

        double dx = s.MouseYaw ? (pose.Yaw - _lastYaw) * s.MousePixelsPerDegree : 0;
        double dy = s.MousePitch ? -(pose.Pitch - _lastPitch) * s.MousePixelsPerDegree : 0;
        _lastYaw = pose.Yaw;
        _lastPitch = pose.Pitch;

        // Keep the fractions so slow movement still adds up.
        dx += _carryX;
        dy += _carryY;
        int ix = (int)Math.Truncate(dx);
        int iy = (int)Math.Truncate(dy);
        _carryX = dx - ix;
        _carryY = dy - iy;

        if (ix != 0 || iy != 0) Send(ix, iy);
    }

    public void Reset()
    {
        _primed = false;
        _wasActive = false;
        Status = "Mouse: off";
    }

    private bool IsForeground(string processName)
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hwnd, out uint pid);

        // Looking up a process name is slow-ish; it only changes when the foreground window does.
        long now = Environment.TickCount64;
        if (pid == _cachedPid && processName == _cachedProcess && now - _cacheTicks < 2000) return _cachedMatch;

        bool match = false;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            match = string.Equals(p.ProcessName, processName.Trim().Replace(".exe", "", StringComparison.OrdinalIgnoreCase),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // process went away
        }

        _cachedPid = pid;
        _cachedProcess = processName;
        _cachedMatch = match;
        _cacheTicks = now;
        return match;
    }

    private static void Send(int dx, int dy)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = MOUSEEVENTF_MOVE },
        };
        SendInput(1, ref input, Marshal.SizeOf<INPUT>());
    }

    private const uint INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_MOVE = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    // INPUT is a union of mouse/keyboard/hardware input; the mouse member is the largest, so this is the right size.
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, ref INPUT pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
