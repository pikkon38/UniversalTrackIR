using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using UniversalTrackIR.Models;
using UniversalTrackIR.Output;
using UniversalTrackIR.Services;

namespace UniversalTrackIR.Tracking;

/// <summary>What the UI shows, copied out of the tracking thread.</summary>
public sealed class EngineSnapshot
{
    public PoseOutput Pose { get; init; }
    public string TrackerStatus { get; init; } = string.Empty;
    public string GameStatus { get; init; } = string.Empty;
    public string MouseStatus { get; init; } = string.Empty;
    public string VJoyStatus { get; init; } = string.Empty;
    public bool TrackerConnected { get; init; }
    public bool Paused { get; init; }
    public double Rate { get; init; }
}

/// <summary>
/// The tracking loop. Runs on its own thread at about 250 Hz: reads the Tobii, shapes the pose,
/// and writes every enabled output. Settings objects are read live, so slider moves apply on the next frame.
/// </summary>
public sealed class TrackingEngine : IDisposable
{
    private readonly TobiiTracker _tobii = new();
    private readonly PoseProcessor _processor = new();
    private readonly FreeTrackOutput _freeTrack = new();
    private readonly MouseOutput _mouse = new();
    private readonly VJoyOutput _vjoy = new();

    private Thread? _thread;
    private volatile bool _stop;
    private volatile bool _paused;
    private volatile bool _eyesOn = true;
    private volatile int _recenter;

    private volatile TrackingProfile _profile;
    private volatile OutputSettings _outputs;
    private volatile EngineSnapshot _snapshot = new();

    public TrackingEngine(TrackingProfile profile, OutputSettings outputs)
    {
        _profile = profile;
        _outputs = outputs;
    }

    public TrackingProfile Profile
    {
        get => _profile;
        set => _profile = value;
    }

    public OutputSettings Outputs
    {
        get => _outputs;
        set => _outputs = value;
    }

    public bool IsRunning => _thread != null;

    public bool Paused
    {
        get => _paused;
        set => _paused = value;
    }

    public bool EyesOn
    {
        get => _eyesOn;
        set => _eyesOn = value;
    }

    public EngineSnapshot Snapshot => _snapshot;

    public void Recenter() => Interlocked.Exchange(ref _recenter, 1);

    public void Start()
    {
        if (_thread != null) return;
        _stop = false;
        _thread = new Thread(Run) { IsBackground = true, Name = "Tracking", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Stop()
    {
        if (_thread == null) return;
        _stop = true;
        _thread.Join(2000);
        _thread = null;
        _snapshot = new EngineSnapshot
        {
            TrackerStatus = "Tracking off",
            GameStatus = "TrackIR / FreeTrack: off",
            MouseStatus = "Mouse: off",
            VJoyStatus = "vJoy: off",
        };
    }

    private void Run()
    {
        timeBeginPeriod(1);
        try
        {
            Loop();
        }
        catch (Exception ex)
        {
            _snapshot = new EngineSnapshot { TrackerStatus = $"Tracking stopped: {ex.Message}" };
        }
        finally
        {
            _freeTrack.Close();
            _vjoy.Close();
            _mouse.Reset();
            _tobii.Dispose();
            timeEndPeriod(1);
        }
    }

    private void Loop()
    {
        var clock = Stopwatch.StartNew();
        double last = clock.Elapsed.TotalSeconds;
        double nextConnect = 0;
        double nextSnapshot = 0;
        int frames = 0;
        double rateStart = last;
        double rate = 0;
        bool wasConnected = false;

        while (!_stop)
        {
            double now = clock.Elapsed.TotalSeconds;
            double dt = Math.Clamp(now - last, 0.0005, 0.1);
            last = now;

            var profile = _profile;
            var outputs = _outputs;

            if (!_tobii.IsConnected && now >= nextConnect)
            {
                _tobii.Connect(NativeFiles.StreamEngineCandidates());
                nextConnect = now + 3;
            }
            bool connected = _tobii.IsConnected && _tobii.Poll();
            if (connected && !wasConnected) _processor.RequestRecenter();
            wasConnected = connected;

            if (Interlocked.Exchange(ref _recenter, 0) == 1) _processor.RequestRecenter();

            var head = connected ? _tobii.Head : default;
            var gaze = connected ? _tobii.Gaze : default;
            var pose = _processor.Process(head, gaze, profile, _paused, _eyesOn, dt);

            // Outputs
            if (outputs.GameProtocolEnabled)
            {
                if (!_freeTrack.IsOpen) _freeTrack.Open(NativeFiles.ClientFolder, outputs.RunTrackIrStandIn, NativeFiles.StandInPath);
                _freeTrack.Write(pose);
            }
            else if (_freeTrack.IsOpen)
            {
                _freeTrack.Close();
            }

            if (outputs.MouseEnabled) _mouse.Update(pose, outputs);
            else _mouse.Reset();

            if (outputs.VJoyEnabled)
            {
                if (_vjoy.Open(outputs.VJoyDevice)) _vjoy.Write(pose, profile);
            }
            else if (_vjoy.IsActive || _vjoy.Status != "vJoy: off")
            {
                _vjoy.Close();
            }

            frames++;
            if (now - rateStart >= 1)
            {
                rate = frames / (now - rateStart);
                frames = 0;
                rateStart = now;
            }

            if (now >= nextSnapshot)
            {
                nextSnapshot = now + 1.0 / 60;
                string game = _freeTrack.IsOpen
                    ? string.IsNullOrEmpty(_freeTrack.GameName) ? _freeTrack.Status : $"Game connected: {_freeTrack.GameName}"
                    : _freeTrack.Status;
                _snapshot = new EngineSnapshot
                {
                    Pose = pose,
                    TrackerStatus = _tobii.Status,
                    TrackerConnected = connected,
                    GameStatus = game,
                    MouseStatus = _mouse.Status,
                    VJoyStatus = _vjoy.Status,
                    Paused = _paused,
                    Rate = rate,
                };
            }

            // ~250 Hz. The Tobii delivers at its own rate; we keep outputs fresh and smooth in between.
            Thread.Sleep(4);
        }
    }

    public void Dispose() => Stop();

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint period);
}
