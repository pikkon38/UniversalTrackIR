using System;
using UniversalTrackIR.Models;

namespace UniversalTrackIR.Tracking;

/// <summary>
/// Puts samples back into "tracker below the screen" terms when the Tobii sits somewhere else.
///
/// Tobii Experience only knows one placement: the bottom edge, with the screen above the tracker. Its display setup
/// (screen size) and calibration are all in those terms. So:
///
/// Upside down on the top edge: the tracker is the bottom-edge setup turned 180° around the axis pointing at you.
/// The screen it assumes is above it lands exactly on the real screen, just rotated, so no monitor size is needed.
/// Left/right and up/down swap sides: yaw, pitch, X, Y and the gaze point flip. Roll is around the turning axis,
/// so it stays as is.
///
/// Right side up on the top edge: nothing flips, but the screen Tobii assumes is above the tracker while the real
/// one is below it, one screen height down. In the -1..1 gaze range that's exactly 2, again with no monitor size.
/// Head angles only pick up a constant tilt, which recentering removes.
/// </summary>
public static class MountCorrection
{
    /// <summary>Gaze further than this past the edge is kept, but clamped so a wild sample can't fling the view.</summary>
    public const double GazeLimit = 1.5;

    public static void Apply(ref HeadSample head, ref GazeSample gaze, TrackerSettings tracker)
    {
        switch (tracker.Mount)
        {
            case TrackerMount.AboveMonitorFlipped:
                head.Yaw = -head.Yaw;
                head.Pitch = -head.Pitch;
                head.X = -head.X;
                head.Y = -head.Y;
                gaze.X = -gaze.X;
                gaze.Y = -gaze.Y;
                break;

            case TrackerMount.AboveMonitorUpright:
                gaze.Y += 2.0;
                break;
        }

        gaze.Y += Math.Clamp(tracker.GazeOffsetYPercent, -50, 50) / 100.0 * 2.0;
        gaze.X = Math.Clamp(gaze.X, -GazeLimit, GazeLimit);
        gaze.Y = Math.Clamp(gaze.Y, -GazeLimit, GazeLimit);
    }
}
