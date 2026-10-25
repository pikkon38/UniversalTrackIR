using System;
using UniversalTrackIR.Models;

namespace UniversalTrackIR.Tracking;

/// <summary>
/// One Euro filter: smooths hard when you're still (kills jitter) and backs off when you move fast (keeps lag low).
/// https://gery.casiez.net/1euro/
/// </summary>
public sealed class OneEuroFilter
{
    private double _minCutoff;
    private double _beta;
    private const double DerivativeCutoff = 1.0;

    private bool _primed;
    private double _x;
    private double _dx;

    public OneEuroFilter(double minCutoff, double beta)
    {
        _minCutoff = minCutoff;
        _beta = beta;
    }

    public void Configure(double minCutoff, double beta)
    {
        _minCutoff = minCutoff;
        _beta = beta;
    }

    public void Reset() => _primed = false;

    public double Filter(double value, double dt)
    {
        if (!_primed || dt <= 0)
        {
            _primed = true;
            _x = value;
            _dx = 0;
            return value;
        }

        double rawDx = (value - _x) / dt;
        _dx += Alpha(DerivativeCutoff, dt) * (rawDx - _dx);

        double cutoff = _minCutoff + _beta * Math.Abs(_dx);
        _x += Alpha(cutoff, dt) * (value - _x);
        return _x;
    }

    private static double Alpha(double cutoff, double dt)
    {
        double tau = 1.0 / (2 * Math.PI * cutoff);
        return 1.0 / (1.0 + tau / dt);
    }
}

/// <summary>What the game gets, plus the pieces for the live view.</summary>
public struct PoseOutput
{
    /// <summary>Final pose. Degrees for rotation, mm for position. Yaw + right, pitch + up.</summary>
    public double Yaw, Pitch, Roll, X, Y, Z;

    /// <summary>Head-only part of yaw/pitch, before the eyes are added.</summary>
    public double HeadYaw, HeadPitch;

    /// <summary>Eye part of yaw/pitch.</summary>
    public double EyeYaw, EyePitch;

    /// <summary>Head movement from center, after smoothing, before sensitivity. For the meters.</summary>
    public double RawYaw, RawPitch, RawRoll, RawX, RawY, RawZ;

    /// <summary>Gaze after smoothing, -1..1.</summary>
    public double GazeX, GazeY;

    public bool Tracking;
    public bool GazeTracking;

    public double this[Axis axis] => axis switch
    {
        Axis.Yaw => Yaw,
        Axis.Pitch => Pitch,
        Axis.Roll => Roll,
        Axis.X => X,
        Axis.Y => Y,
        Axis.Z => Z,
        _ => 0,
    };

    public double Raw(Axis axis) => axis switch
    {
        Axis.Yaw => RawYaw,
        Axis.Pitch => RawPitch,
        Axis.Roll => RawRoll,
        Axis.X => RawX,
        Axis.Y => RawY,
        Axis.Z => RawZ,
        _ => 0,
    };
}

/// <summary>
/// Turns raw tracker samples into the pose games get:
/// subtract center, smooth, deadzone, curve, sensitivity, limit, then add the eye offset to yaw and pitch.
/// When tracking drops or you pause, the view glides back to center instead of freezing or snapping.
/// </summary>
public sealed class PoseProcessor
{
    private static readonly Axis[] AllAxes = (Axis[])Enum.GetValues(typeof(Axis));

    private readonly OneEuroFilter[] _head = new OneEuroFilter[6];
    private readonly OneEuroFilter _gazeX = new(1, 0);
    private readonly OneEuroFilter _gazeY = new(1, 0);

    private readonly double[] _center = new double[6];
    private bool _hasCenter;
    private bool _recenterRequested = true; // first good sample becomes center

    private readonly double[] _lastRaw = new double[6];
    private double _lostSeconds;
    private double _fade = 0; // 0 = at center, 1 = following your head
    private double _eyeFade;

    private PoseOutput _last;

    public PoseProcessor()
    {
        for (int i = 0; i < _head.Length; i++) _head[i] = new OneEuroFilter(1, 0.05);
    }

    public void RequestRecenter() => _recenterRequested = true;

    public PoseOutput Last => _last;

    public PoseOutput Process(in HeadSample head, in GazeSample gaze, TrackingProfile profile, bool paused, bool eyesOn, double dt)
    {
        ConfigureFilters(profile);

        bool valid = head.RotationValid;
        Span<double> raw = stackalloc double[6];

        if (valid)
        {
            raw[(int)Axis.Yaw] = head.Yaw;
            raw[(int)Axis.Pitch] = head.Pitch;
            raw[(int)Axis.Roll] = head.Roll;
            raw[(int)Axis.X] = head.X;
            raw[(int)Axis.Y] = head.Y;
            raw[(int)Axis.Z] = head.Z;

            if (_recenterRequested || !_hasCenter)
            {
                raw.CopyTo(_center);
                _hasCenter = true;
                _recenterRequested = false;
                foreach (var f in _head) f.Reset();
            }

            for (int i = 0; i < 6; i++)
            {
                double v = raw[i] - _center[i];
                if (i <= (int)Axis.Roll) v = WrapDegrees(v);
                _lastRaw[i] = _head[i].Filter(v, dt);
            }
            _lostSeconds = 0;
        }
        else
        {
            _lostSeconds += dt;
        }

        // Glide to center while paused or after a short tracking loss (blinks and glances away shouldn't snap the view).
        bool follow = !paused && (valid || _lostSeconds < 0.25) && _hasCenter;
        _fade = Approach(_fade, follow ? 1 : 0, dt, follow ? 0.15 : 0.5);

        var o = new PoseOutput
        {
            Tracking = valid && _hasCenter,
            RawYaw = _lastRaw[0],
            RawPitch = _lastRaw[1],
            RawRoll = _lastRaw[2],
            RawX = _lastRaw[3],
            RawY = _lastRaw[4],
            RawZ = _lastRaw[5],
        };

        Span<double> shaped = stackalloc double[6];
        foreach (var axis in AllAxes)
        {
            var s = profile.Axes[axis];
            shaped[(int)axis] = s.Enabled ? Shape(_lastRaw[(int)axis], s) * _fade : 0;
        }

        o.HeadYaw = shaped[0];
        o.HeadPitch = shaped[1];

        // Eyes
        var eye = profile.Eye;
        bool gazeOk = eyesOn && eye.Enabled && gaze.Valid && valid;
        double gx = 0, gy = 0;
        if (gaze.Valid)
        {
            gx = _gazeX.Filter(gaze.X, dt);
            gy = _gazeY.Filter(gaze.Y, dt);
        }
        _eyeFade = Approach(_eyeFade, gazeOk && follow ? 1 : 0, dt, 0.2);

        o.GazeX = gx;
        o.GazeY = gy;
        o.GazeTracking = gaze.Valid;
        o.EyeYaw = ShapeEye(gx, eye) * eye.YawRange * _eyeFade;
        o.EyePitch = ShapeEye(gy, eye) * eye.PitchRange * _eyeFade;

        var yawS = profile.Axes[Axis.Yaw];
        var pitchS = profile.Axes[Axis.Pitch];
        o.Yaw = Math.Clamp(o.HeadYaw + (yawS.Invert ? -o.EyeYaw : o.EyeYaw), -yawS.Limit, yawS.Limit);
        o.Pitch = Math.Clamp(o.HeadPitch + (pitchS.Invert ? -o.EyePitch : o.EyePitch), -pitchS.Limit, pitchS.Limit);
        o.Roll = shaped[2];
        o.X = shaped[3];
        o.Y = shaped[4];
        o.Z = shaped[5];

        _last = o;
        return o;
    }

    /// <summary>Deadzone, curve, sensitivity, limit, invert.</summary>
    public static double Shape(double v, AxisSettings s)
    {
        double mag = Math.Abs(v) - s.Deadzone;
        if (mag <= 0) return 0;

        // Curve blends linear with a gentle quadratic. At 20 units (20° or 20 mm) both agree,
        // so the curve changes how you get there, not how far 20° of head turn takes you.
        const double knee = 20;
        double c = Math.Clamp(s.Curve, 0, 1);
        double shaped = mag * ((1 - c) + c * mag / knee);

        double outV = Math.Min(shaped * s.Sensitivity, s.Limit) * Math.Sign(v);
        return s.Invert ? -outV : outV;
    }

    /// <summary>Gaze -1..1 to an eye offset -1..1, after the center deadzone and curve.</summary>
    public static double ShapeEye(double g, EyeSettings eye)
    {
        double dz = Math.Clamp(eye.DeadzonePercent / 100.0, 0, 0.95);
        double mag = Math.Min(Math.Abs(g), 1.0);
        if (mag <= dz) return 0;

        double t = (mag - dz) / (1 - dz);
        double c = Math.Clamp(eye.Curve, 0, 1);
        t = (1 - c) * t + c * t * t;
        return t * Math.Sign(g);
    }

    private void ConfigureFilters(TrackingProfile profile)
    {
        // Smoothing 0 = ~8 Hz cutoff (light). 100 = ~0.25 Hz (heavy). Speed still opens it up.
        double head = Lerp(8, 0.25, Math.Clamp(profile.HeadSmoothing, 0, 100) / 100.0);
        for (int i = 0; i < 6; i++)
            _head[i].Configure(head, i <= (int)Axis.Roll ? 0.05 : 0.01);

        double eye = Lerp(6, 0.15, Math.Clamp(profile.Eye.Smoothing, 0, 100) / 100.0);
        _gazeX.Configure(eye, 0.2);
        _gazeY.Configure(eye, 0.2);
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    /// <summary>Exponential approach with time constant tau (seconds).</summary>
    private static double Approach(double current, double target, double dt, double tau) =>
        current + (target - current) * (1 - Math.Exp(-dt / Math.Max(tau, 1e-3)));

    private static double WrapDegrees(double d)
    {
        d %= 360;
        if (d > 180) d -= 360;
        if (d < -180) d += 360;
        return d;
    }
}
