using System;
using UniversalTrackIR.Models;
using UniversalTrackIR.Mvvm;
using UniversalTrackIR.Tracking;

namespace UniversalTrackIR.ViewModels;

/// <summary>One row of the head tracking card. Edits the live <see cref="AxisSettings"/> the engine reads.</summary>
public sealed class AxisViewModel : ObservableObject
{
    private readonly Action _changed;
    private AxisSettings _s;

    public AxisViewModel(Axis axis, AxisSettings settings, Action changed)
    {
        Axis = axis;
        _s = settings;
        _changed = changed;
    }

    public Axis Axis { get; }
    public bool IsRotation => Axis <= Axis.Roll;

    public string Name => Axis switch
    {
        Axis.Yaw => "Yaw",
        Axis.Pitch => "Pitch",
        Axis.Roll => "Roll",
        Axis.X => "Left / right",
        Axis.Y => "Up / down",
        Axis.Z => "Lean in / out",
        _ => Axis.ToString(),
    };

    public string Description => Axis switch
    {
        Axis.Yaw => "Turn left and right",
        Axis.Pitch => "Look up and down",
        Axis.Roll => "Tilt your head",
        Axis.X => "Slide your head sideways",
        Axis.Y => "Raise and lower your head",
        Axis.Z => "Lean toward the screen",
        _ => string.Empty,
    };

    public string Unit => IsRotation ? "°" : " mm";

    // Slider ranges
    public double SensitivityMax => IsRotation ? 10 : 5;
    public double DeadzoneMax => IsRotation ? 10 : 30;
    public double DeadzoneStep => IsRotation ? 0.1 : 0.5;
    public double LimitMin => IsRotation ? 5 : 10;
    public double LimitMax => Axis switch { Axis.Yaw => 180, Axis.Pitch => 90, Axis.Roll => 90, _ => 500 };
    public double LimitStep => IsRotation ? 1 : 5;

    /// <summary>Points the row at another profile's settings (profile switch).</summary>
    public void Rebind(AxisSettings settings)
    {
        _s = settings;
        OnPropertyChanged(string.Empty);
    }

    public bool Enabled
    {
        get => _s.Enabled;
        set { if (_s.Enabled != value) { _s.Enabled = value; OnPropertyChanged(); _changed(); } }
    }

    public double Sensitivity
    {
        get => _s.Sensitivity;
        set { value = Math.Round(Math.Clamp(value, 0, SensitivityMax), 2); if (_s.Sensitivity != value) { _s.Sensitivity = value; OnPropertyChanged(); OnPropertyChanged(nameof(SensitivityText)); _changed(); } }
    }

    public string SensitivityText => $"{Sensitivity:0.0}x";

    public double CurvePercent
    {
        get => Math.Round(_s.Curve * 100);
        set { double v = Math.Clamp(Math.Round(value), 0, 100) / 100; if (_s.Curve != v) { _s.Curve = v; OnPropertyChanged(); OnPropertyChanged(nameof(CurveText)); _changed(); } }
    }

    public string CurveText => CurvePercent == 0 ? "Linear" : $"{CurvePercent:0}%";

    public double Deadzone
    {
        get => _s.Deadzone;
        set { value = Math.Round(Math.Clamp(value, 0, DeadzoneMax), 1); if (_s.Deadzone != value) { _s.Deadzone = value; OnPropertyChanged(); OnPropertyChanged(nameof(DeadzoneText)); _changed(); } }
    }

    public string DeadzoneText => $"{Deadzone:0.0}{Unit}";

    public double Limit
    {
        get => _s.Limit;
        set { value = Math.Round(Math.Clamp(value, LimitMin, LimitMax)); if (_s.Limit != value) { _s.Limit = value; OnPropertyChanged(); OnPropertyChanged(nameof(LimitText)); _changed(); } }
    }

    public string LimitText => $"±{Limit:0}{Unit}";

    public bool Invert
    {
        get => _s.Invert;
        set { if (_s.Invert != value) { _s.Invert = value; OnPropertyChanged(); _changed(); } }
    }

    // Live readout
    private double _raw;
    private double _output;

    public string LiveText => $"{_raw:+0.0;-0.0;0.0}{Unit} → {_output:+0;-0;0}{Unit}";

    /// <summary>Output as a fraction of the limit, split into the two halves of a centered meter.</summary>
    public double MeterLeft => _output < 0 ? Math.Min(1, -_output / Math.Max(1, Limit)) : 0;
    public double MeterRight => _output > 0 ? Math.Min(1, _output / Math.Max(1, Limit)) : 0;

    public void UpdateLive(in PoseOutput pose)
    {
        double raw = pose.Raw(Axis);
        double output = pose[Axis];
        if (Math.Abs(raw - _raw) < 0.05 && Math.Abs(output - _output) < 0.05) return;
        _raw = raw;
        _output = output;
        OnPropertyChanged(nameof(LiveText));
        OnPropertyChanged(nameof(MeterLeft));
        OnPropertyChanged(nameof(MeterRight));
    }
}

/// <summary>The eye tracking card.</summary>
public sealed class EyeViewModel : ObservableObject
{
    private readonly Action _changed;
    private EyeSettings _s;

    public EyeViewModel(EyeSettings settings, Action changed)
    {
        _s = settings;
        _changed = changed;
    }

    public void Rebind(EyeSettings settings)
    {
        _s = settings;
        OnPropertyChanged(string.Empty);
    }

    public bool Enabled
    {
        get => _s.Enabled;
        set { if (_s.Enabled != value) { _s.Enabled = value; OnPropertyChanged(); _changed(); } }
    }

    public double YawRange
    {
        get => _s.YawRange;
        set { value = Math.Clamp(Math.Round(value), 0, 60); if (_s.YawRange != value) { _s.YawRange = value; OnPropertyChanged(); _changed(); } }
    }

    public double PitchRange
    {
        get => _s.PitchRange;
        set { value = Math.Clamp(Math.Round(value), 0, 40); if (_s.PitchRange != value) { _s.PitchRange = value; OnPropertyChanged(); _changed(); } }
    }

    public double DeadzonePercent
    {
        get => _s.DeadzonePercent;
        set { value = Math.Clamp(Math.Round(value), 0, 80); if (_s.DeadzonePercent != value) { _s.DeadzonePercent = value; OnPropertyChanged(); _changed(); } }
    }

    public double CurvePercent
    {
        get => Math.Round(_s.Curve * 100);
        set { double v = Math.Clamp(Math.Round(value), 0, 100) / 100; if (_s.Curve != v) { _s.Curve = v; OnPropertyChanged(); _changed(); } }
    }

    public double Smoothing
    {
        get => _s.Smoothing;
        set { value = Math.Clamp(Math.Round(value), 0, 100); if (_s.Smoothing != value) { _s.Smoothing = value; OnPropertyChanged(); _changed(); } }
    }
}
