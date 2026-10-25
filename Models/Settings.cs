using System.Collections.Generic;

namespace UniversalTrackIR.Models;

/// <summary>The six head axes, in the order games see them.</summary>
public enum Axis
{
    Yaw,
    Pitch,
    Roll,
    X,
    Y,
    Z,
}

/// <summary>How one head axis is shaped before it reaches the game.</summary>
public sealed class AxisSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Output per unit of head movement. 4 means 10° of head turn looks 40° in game.</summary>
    public double Sensitivity { get; set; } = 1.0;

    /// <summary>0 = linear. Higher = calmer near center and faster toward the edges.</summary>
    public double Curve { get; set; } = 0.3;

    /// <summary>Head movement ignored around center, in degrees (rotation) or mm (position).</summary>
    public double Deadzone { get; set; } = 0.5;

    /// <summary>Largest output the game gets, in degrees (rotation) or mm (position).</summary>
    public double Limit { get; set; } = 180;

    public bool Invert { get; set; }

    public AxisSettings Clone() => (AxisSettings)MemberwiseClone();
}

/// <summary>Eye tracking adds a fine offset on top of the head: look toward an edge and the view keeps going.</summary>
public sealed class EyeSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Extra yaw, in degrees, when looking at the left or right edge of the screen.</summary>
    public double YawRange { get; set; } = 20;

    /// <summary>Extra pitch, in degrees, when looking at the top or bottom edge.</summary>
    public double PitchRange { get; set; } = 12;

    /// <summary>Middle part of the screen, as a percentage, where your eyes add nothing. Keeps the view still while you read gauges.</summary>
    public double DeadzonePercent { get; set; } = 35;

    /// <summary>0 = linear past the deadzone. Higher = gentle at first, strong at the edge.</summary>
    public double Curve { get; set; } = 0.5;

    /// <summary>0 to 100. Eyes jump constantly, so this is higher than the head's.</summary>
    public double Smoothing { get; set; } = 70;

    public EyeSettings Clone() => (EyeSettings)MemberwiseClone();
}

/// <summary>Everything you tune per game. Outputs and hotkeys are global.</summary>
public sealed class TrackingProfile
{
    public Dictionary<Axis, AxisSettings> Axes { get; set; } = DefaultAxes();

    /// <summary>0 to 100. Higher = steadier but laggier head.</summary>
    public double HeadSmoothing { get; set; } = 40;

    public EyeSettings Eye { get; set; } = new();

    public TrackingProfile Clone()
    {
        var copy = new TrackingProfile { HeadSmoothing = HeadSmoothing, Eye = Eye.Clone(), Axes = new() };
        foreach (var (axis, settings) in Axes) copy.Axes[axis] = settings.Clone();
        return copy;
    }

    /// <summary>Fills in axes missing from an older or hand-edited settings file.</summary>
    public void Repair()
    {
        var defaults = DefaultAxes();
        foreach (var (axis, settings) in defaults)
            if (!Axes.ContainsKey(axis)) Axes[axis] = settings;
        Eye ??= new EyeSettings();
    }

    /// <summary>
    /// A starting point for an eye tracker on a monitor. Your head can only turn so far while you still see
    /// the screen, so rotation is amplified. Roll starts off because most people find it disorienting.
    /// </summary>
    public static Dictionary<Axis, AxisSettings> DefaultAxes() => new()
    {
        [Axis.Yaw] = new AxisSettings { Sensitivity = 4.0, Curve = 0.4, Deadzone = 0.5, Limit = 180 },
        [Axis.Pitch] = new AxisSettings { Sensitivity = 3.0, Curve = 0.4, Deadzone = 0.5, Limit = 90 },
        [Axis.Roll] = new AxisSettings { Enabled = false, Sensitivity = 1.0, Curve = 0, Deadzone = 1, Limit = 45 },
        [Axis.X] = new AxisSettings { Sensitivity = 1.0, Curve = 0, Deadzone = 2, Limit = 300 },
        [Axis.Y] = new AxisSettings { Sensitivity = 1.0, Curve = 0, Deadzone = 2, Limit = 300 },
        [Axis.Z] = new AxisSettings { Sensitivity = 1.0, Curve = 0, Deadzone = 3, Limit = 300 },
    };
}

public sealed class OutputSettings
{
    /// <summary>TrackIR and FreeTrack. Most sims, Star Citizen and racing games read one of these.</summary>
    public bool GameProtocolEnabled { get; set; } = true;

    /// <summary>Runs a stand-in TrackIR.exe. Some games only look for head tracking when it's running.</summary>
    public bool RunTrackIrStandIn { get; set; } = true;

    /// <summary>Moves the mouse with your head, for games without head tracking support.</summary>
    public bool MouseEnabled { get; set; }

    public double MousePixelsPerDegree { get; set; } = 12;

    /// <summary>The mouse only moves while this process has the foreground. Empty = never.</summary>
    public string MouseProcess { get; set; } = string.Empty;

    public bool MouseYaw { get; set; } = true;
    public bool MousePitch { get; set; } = true;

    /// <summary>Feeds a vJoy virtual joystick. Bind its axes to look left/right/up/down in the game.</summary>
    public bool VJoyEnabled { get; set; }

    public int VJoyDevice { get; set; } = 1;
}

public sealed class HotkeySettings
{
    public string Recenter { get; set; } = "Ctrl+Shift+F12";
    public string Pause { get; set; } = "Ctrl+Shift+F11";
    public string ToggleEyes { get; set; } = "Ctrl+Shift+F10";
}

public sealed class AppSettings
{
    public bool Enabled { get; set; } = true;
    public string ActiveProfile { get; set; } = DefaultProfileName;
    public Dictionary<string, TrackingProfile> Profiles { get; set; } = new();
    public OutputSettings Outputs { get; set; } = new();
    public HotkeySettings Hotkeys { get; set; } = new();

    public const string DefaultProfileName = "Default";
}
