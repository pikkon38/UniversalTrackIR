using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using UniversalTrackIR.Models;
using UniversalTrackIR.Mvvm;
using UniversalTrackIR.Output;
using UniversalTrackIR.Services;
using UniversalTrackIR.Tracking;

namespace UniversalTrackIR.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly SettingsStore _store = new();
    private readonly AppSettings _settings;
    private readonly TrackingEngine _engine;
    private readonly DispatcherTimer _uiTimer;
    private readonly DispatcherTimer _saveTimer;
    private HotkeyService? _hotkeys;
    private TrackingProfile _profile;

    public MainViewModel(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _settings = _store.Load();
        _profile = _settings.Profiles[_settings.ActiveProfile];

        string? nativeProblem = NativeFiles.Extract();
        if (nativeProblem != null) Warning = $"Couldn't unpack game client files: {nativeProblem}";

        _engine = new TrackingEngine(_profile, _settings.Outputs);

        foreach (var axis in new[] { Axis.Yaw, Axis.Pitch, Axis.Roll })
            RotationAxes.Add(new AxisViewModel(axis, _profile.Axes[axis], ScheduleSave));
        foreach (var axis in new[] { Axis.X, Axis.Y, Axis.Z })
            PositionAxes.Add(new AxisViewModel(axis, _profile.Axes[axis], ScheduleSave));
        Eye = new EyeViewModel(_profile.Eye, ScheduleSave);

        foreach (var name in _settings.Profiles.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)) Profiles.Add(name);
        _selectedProfile = _settings.ActiveProfile;

        for (int i = 1; i <= 16; i++) VJoyDevices.Add(i);
        RefreshProcesses();

        RecenterCommand = new RelayCommand(() => _engine.Recenter());
        TogglePauseCommand = new RelayCommand(TogglePause);
        ToggleEyesCommand = new RelayCommand(ToggleEyes);
        DuplicateProfileCommand = new RelayCommand(DuplicateProfile);
        DeleteProfileCommand = new RelayCommand(DeleteProfile, () => _settings.Profiles.Count > 1);
        StartRenameCommand = new RelayCommand(() => { RenameText = _selectedProfile; IsRenaming = true; });
        CommitRenameCommand = new RelayCommand(CommitRename);
        CancelRenameCommand = new RelayCommand(() => IsRenaming = false);
        ResetProfileCommand = new RelayCommand(ResetProfile);
        RefreshProcessesCommand = new RelayCommand(RefreshProcesses);
        CaptureHotkeyCommand = new RelayCommand(p => BeginCapture(p as string));
        CheckUpdatesCommand = new RelayCommand(() => _ = CheckForUpdatesAsync(userInitiated: true), () => !_updateBusy);
        InstallUpdateCommand = new RelayCommand(() => _ = InstallUpdateAsync(), () => _availableUpdate != null && !_updateBusy);

        _uiTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background, (_, _) => UpdateLive(), _dispatcher);
        _saveTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveNow(); };

        _isEnabled = _settings.Enabled;
        if (_isEnabled) _engine.Start();
        _uiTimer.Start();

        UpdateService.CleanupOldFiles();
        _ = CheckForUpdatesAsync(userInitiated: false);
    }

    public ObservableCollection<AxisViewModel> RotationAxes { get; } = new();
    public ObservableCollection<AxisViewModel> PositionAxes { get; } = new();
    public EyeViewModel Eye { get; }
    public ObservableCollection<string> Profiles { get; } = new();
    public ObservableCollection<string> Processes { get; } = new();
    public List<int> VJoyDevices { get; } = new();

    public ICommand RecenterCommand { get; }
    public ICommand TogglePauseCommand { get; }
    public ICommand ToggleEyesCommand { get; }
    public ICommand DuplicateProfileCommand { get; }
    public ICommand DeleteProfileCommand { get; }
    public ICommand StartRenameCommand { get; }
    public ICommand CommitRenameCommand { get; }
    public ICommand CancelRenameCommand { get; }
    public ICommand ResetProfileCommand { get; }
    public ICommand RefreshProcessesCommand { get; }
    public ICommand CaptureHotkeyCommand { get; }
    public ICommand CheckUpdatesCommand { get; }
    public ICommand InstallUpdateCommand { get; }

    public string SettingsPath => _store.FilePath;
    public string StreamEngineOverridePath => System.IO.Path.Combine(NativeFiles.OverrideFolder, NativeFiles.StreamEngineDll);

    // ---------------------------------------------------------------------------------------------
    // Tracking on/off, pause, eyes

    private bool _isEnabled;

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (!Set(ref _isEnabled, value)) return;
            _settings.Enabled = value;
            if (value) _engine.Start();
            else _engine.Stop();
            ScheduleSave();
        }
    }

    public bool IsPaused => _engine.Paused;
    public string PauseButtonText => _engine.Paused ? "Resume" : "Pause";

    private void TogglePause()
    {
        _engine.Paused = !_engine.Paused;
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(PauseButtonText));
    }

    public bool EyesOn
    {
        get => _engine.EyesOn;
        set
        {
            if (_engine.EyesOn == value) return;
            _engine.EyesOn = value;
            OnPropertyChanged();
        }
    }

    private void ToggleEyes() => EyesOn = !EyesOn;

    public double HeadSmoothing
    {
        get => _profile.HeadSmoothing;
        set
        {
            value = Math.Clamp(Math.Round(value), 0, 100);
            if (_profile.HeadSmoothing == value) return;
            _profile.HeadSmoothing = value;
            OnPropertyChanged();
            ScheduleSave();
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Profiles

    private string _selectedProfile;

    public string? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (value == null || value == _selectedProfile || !_settings.Profiles.TryGetValue(value, out var profile)) return;
            _selectedProfile = value;
            _settings.ActiveProfile = value;
            UseProfile(profile);
            OnPropertyChanged();
            ScheduleSave();
        }
    }

    private void UseProfile(TrackingProfile profile)
    {
        _profile = profile;
        _engine.Profile = profile;
        foreach (var a in RotationAxes.Concat(PositionAxes)) a.Rebind(profile.Axes[a.Axis]);
        Eye.Rebind(profile.Eye);
        OnPropertyChanged(nameof(HeadSmoothing));
    }

    private void DuplicateProfile()
    {
        string name = UniqueName(_selectedProfile + " copy");
        _settings.Profiles[name] = _profile.Clone();
        InsertSorted(name);
        SelectedProfile = name;
    }

    private void DeleteProfile()
    {
        if (_settings.Profiles.Count <= 1) return;
        string doomed = _selectedProfile;
        string next = Profiles.First(p => p != doomed);
        SelectedProfile = next;
        _settings.Profiles.Remove(doomed);
        Profiles.Remove(doomed);
        ScheduleSave();
    }

    private void ResetProfile()
    {
        var fresh = new TrackingProfile();
        _settings.Profiles[_selectedProfile] = fresh;
        UseProfile(fresh);
        ScheduleSave();
    }

    private bool _isRenaming;

    public bool IsRenaming
    {
        get => _isRenaming;
        set => Set(ref _isRenaming, value);
    }

    private string _renameText = string.Empty;

    public string RenameText
    {
        get => _renameText;
        set => Set(ref _renameText, value);
    }

    private void CommitRename()
    {
        IsRenaming = false;
        string name = (RenameText ?? string.Empty).Trim();
        if (name.Length == 0 || name == _selectedProfile) return;
        if (_settings.Profiles.ContainsKey(name)) name = UniqueName(name);

        string old = _selectedProfile;
        _settings.Profiles[name] = _settings.Profiles[old];
        _settings.Profiles.Remove(old);
        _settings.ActiveProfile = name;
        _selectedProfile = name;
        Profiles.Remove(old);
        InsertSorted(name);
        OnPropertyChanged(nameof(SelectedProfile));
        ScheduleSave();
    }

    private string UniqueName(string baseName)
    {
        string name = baseName;
        for (int i = 2; _settings.Profiles.ContainsKey(name); i++) name = $"{baseName} {i}";
        return name;
    }

    private void InsertSorted(string name)
    {
        int i = 0;
        while (i < Profiles.Count && string.Compare(Profiles[i], name, StringComparison.OrdinalIgnoreCase) < 0) i++;
        Profiles.Insert(i, name);
    }

    // ---------------------------------------------------------------------------------------------
    // Outputs

    private OutputSettings O => _settings.Outputs;

    public bool GameProtocolEnabled
    {
        get => O.GameProtocolEnabled;
        set { if (O.GameProtocolEnabled != value) { O.GameProtocolEnabled = value; OnPropertyChanged(); ScheduleSave(); } }
    }

    public bool RunTrackIrStandIn
    {
        get => O.RunTrackIrStandIn;
        set { if (O.RunTrackIrStandIn != value) { O.RunTrackIrStandIn = value; OnPropertyChanged(); ScheduleSave(); } }
    }

    public bool MouseEnabled
    {
        get => O.MouseEnabled;
        set { if (O.MouseEnabled != value) { O.MouseEnabled = value; OnPropertyChanged(); ScheduleSave(); } }
    }

    public string MouseProcess
    {
        get => O.MouseProcess;
        set
        {
            value = (value ?? string.Empty).Trim();
            if (value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) value = value[..^4];
            if (O.MouseProcess == value) return;
            O.MouseProcess = value;
            OnPropertyChanged();
            ScheduleSave();
        }
    }

    public double MousePixelsPerDegree
    {
        get => O.MousePixelsPerDegree;
        set { value = Math.Clamp(Math.Round(value, 1), 1, 60); if (O.MousePixelsPerDegree != value) { O.MousePixelsPerDegree = value; OnPropertyChanged(); ScheduleSave(); } }
    }

    public bool MouseYaw
    {
        get => O.MouseYaw;
        set { if (O.MouseYaw != value) { O.MouseYaw = value; OnPropertyChanged(); ScheduleSave(); } }
    }

    public bool MousePitch
    {
        get => O.MousePitch;
        set { if (O.MousePitch != value) { O.MousePitch = value; OnPropertyChanged(); ScheduleSave(); } }
    }

    public bool VJoyEnabled
    {
        get => O.VJoyEnabled;
        set { if (O.VJoyEnabled != value) { O.VJoyEnabled = value; OnPropertyChanged(); ScheduleSave(); } }
    }

    public int VJoyDevice
    {
        get => O.VJoyDevice;
        set { value = Math.Clamp(value, 1, 16); if (O.VJoyDevice != value) { O.VJoyDevice = value; OnPropertyChanged(); ScheduleSave(); } }
    }

    private void RefreshProcesses()
    {
        string current = O.MouseProcess;
        Processes.Clear();
        foreach (var p in ProcessService.ListCandidates()) Processes.Add(p);
        if (!string.IsNullOrEmpty(current) && !Processes.Contains(current, StringComparer.OrdinalIgnoreCase)) Processes.Insert(0, current);
        OnPropertyChanged(nameof(MouseProcess)); // re-push text: the editable ComboBox blanks it when the list is cleared
    }

    // ---------------------------------------------------------------------------------------------
    // Hotkeys

    private string? _capturing;

    public string RecenterKeyText => KeyText(nameof(HotkeySettings.Recenter), _settings.Hotkeys.Recenter);
    public string PauseKeyText => KeyText(nameof(HotkeySettings.Pause), _settings.Hotkeys.Pause);
    public string EyesKeyText => KeyText(nameof(HotkeySettings.ToggleEyes), _settings.Hotkeys.ToggleEyes);
    public bool IsCapturingHotkey => _capturing != null;

    private string KeyText(string name, string value) => _capturing == name ? "Press keys... (Esc cancels)" : Hotkey.Parse(value).ToString();

    private string _hotkeyWarning = string.Empty;

    public string HotkeyWarning
    {
        get => _hotkeyWarning;
        private set => Set(ref _hotkeyWarning, value);
    }

    public void AttachWindow(IntPtr hwnd)
    {
        _hotkeys = new HotkeyService(hwnd);
        ApplyHotkeys();
    }

    private void BeginCapture(string? name)
    {
        if (name == null) return;
        _capturing = name;
        _hotkeys?.Clear(); // so pressing the current combo reaches the window instead of firing it
        RaiseHotkeyTexts();
    }

    /// <summary>Called by the window with each key press while capturing. Returns true when it used the key.</summary>
    public bool OnCaptureKey(Key key, ModifierKeys modifiers)
    {
        if (_capturing == null) return false;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            return true; // wait for the real key

        if (key != Key.Escape)
        {
            string text = key is Key.Back or Key.Delete ? "None" : new Hotkey(modifiers, key).ToString();
            switch (_capturing)
            {
                case nameof(HotkeySettings.Recenter): _settings.Hotkeys.Recenter = text; break;
                case nameof(HotkeySettings.Pause): _settings.Hotkeys.Pause = text; break;
                case nameof(HotkeySettings.ToggleEyes): _settings.Hotkeys.ToggleEyes = text; break;
            }
            ScheduleSave();
        }

        _capturing = null;
        RaiseHotkeyTexts();
        ApplyHotkeys();
        return true;
    }

    private void ApplyHotkeys()
    {
        if (_hotkeys == null) return;
        var h = _settings.Hotkeys;
        var failed = _hotkeys.SetAll(new (Hotkey, Action)[]
        {
            (Hotkey.Parse(h.Recenter), () => _engine.Recenter()),
            (Hotkey.Parse(h.Pause), TogglePause),
            (Hotkey.Parse(h.ToggleEyes), ToggleEyes),
        });
        HotkeyWarning = failed.Count == 0
            ? string.Empty
            : $"Another program already uses {string.Join(", ", failed)}. Pick a different combo.";
    }

    private void RaiseHotkeyTexts()
    {
        OnPropertyChanged(nameof(RecenterKeyText));
        OnPropertyChanged(nameof(PauseKeyText));
        OnPropertyChanged(nameof(EyesKeyText));
        OnPropertyChanged(nameof(IsCapturingHotkey));
    }

    // ---------------------------------------------------------------------------------------------
    // Live view

    public const double PreviewWidth = 440;
    public const double PreviewHeight = 230;
    public const double PreviewYawSpan = 90;   // ° from center to the side edge
    public const double PreviewPitchSpan = 60; // ° from center to the top edge
    public const double DotSize = 16;

    public const double ScreenWidth = 192;
    public const double ScreenHeight = 108;

    private string _trackerStatus = "Starting...";
    private string _gameStatus = string.Empty;
    private string _mouseStatus = string.Empty;
    private string _vjoyStatus = string.Empty;
    private string _rateText = string.Empty;
    private bool _trackerConnected;
    private bool _headVisible;
    private bool _gazeVisible;
    private double _headX, _headY, _finalX, _finalY, _gazeX, _gazeY;
    private string _poseText = string.Empty;

    public string TrackerStatus { get => _trackerStatus; private set => Set(ref _trackerStatus, value); }
    public string GameStatus { get => _gameStatus; private set => Set(ref _gameStatus, value); }
    public string MouseStatus { get => _mouseStatus; private set => Set(ref _mouseStatus, value); }
    public string VJoyStatus { get => _vjoyStatus; private set => Set(ref _vjoyStatus, value); }
    public string RateText { get => _rateText; private set => Set(ref _rateText, value); }
    public bool TrackerConnected { get => _trackerConnected; private set => Set(ref _trackerConnected, value); }
    public bool HeadVisible { get => _headVisible; private set => Set(ref _headVisible, value); }
    public bool GazeVisible { get => _gazeVisible; private set => Set(ref _gazeVisible, value); }
    public double HeadDotX { get => _headX; private set => Set(ref _headX, value); }
    public double HeadDotY { get => _headY; private set => Set(ref _headY, value); }
    public double FinalDotX { get => _finalX; private set => Set(ref _finalX, value); }
    public double FinalDotY { get => _finalY; private set => Set(ref _finalY, value); }
    public double GazeDotX { get => _gazeX; private set => Set(ref _gazeX, value); }
    public double GazeDotY { get => _gazeY; private set => Set(ref _gazeY, value); }
    public string PoseText { get => _poseText; private set => Set(ref _poseText, value); }

    // Eye deadzone box inside the mini screen
    public double GazeDeadzoneWidth => ScreenWidth * Eye.DeadzonePercent / 100;
    public double GazeDeadzoneHeight => ScreenHeight * Eye.DeadzonePercent / 100;
    public double GazeDeadzoneLeft => (ScreenWidth - GazeDeadzoneWidth) / 2;
    public double GazeDeadzoneTop => (ScreenHeight - GazeDeadzoneHeight) / 2;

    private void UpdateLive()
    {
        var snap = _engine.Snapshot;
        var pose = snap.Pose;

        TrackerStatus = _engine.IsRunning ? snap.TrackerStatus : "Tracking is off";
        GameStatus = _engine.IsRunning ? snap.GameStatus : string.Empty;
        MouseStatus = _engine.IsRunning && O.MouseEnabled ? snap.MouseStatus : string.Empty;
        VJoyStatus = _engine.IsRunning && O.VJoyEnabled ? snap.VJoyStatus : string.Empty;
        TrackerConnected = _engine.IsRunning && snap.TrackerConnected;
        RateText = snap.Paused ? "Paused. View held at center." : string.Empty;

        HeadVisible = pose.Tracking;
        GazeVisible = pose.GazeTracking && _engine.EyesOn && Eye.Enabled;

        HeadDotX = MapX(pose.HeadYaw);
        HeadDotY = MapY(pose.HeadPitch);
        FinalDotX = MapX(pose.Yaw);
        FinalDotY = MapY(pose.Pitch);
        GazeDotX = (pose.GazeX + 1) / 2 * ScreenWidth - 5;
        GazeDotY = (1 - pose.GazeY) / 2 * ScreenHeight - 5;

        PoseText = $"Yaw {pose.Yaw:+0;-0;0}°   Pitch {pose.Pitch:+0;-0;0}°   Eyes {pose.EyeYaw:+0;-0;0}° / {pose.EyePitch:+0;-0;0}°";

        foreach (var a in RotationAxes) a.UpdateLive(pose);
        foreach (var a in PositionAxes) a.UpdateLive(pose);

        OnPropertyChanged(nameof(GazeDeadzoneWidth));
        OnPropertyChanged(nameof(GazeDeadzoneHeight));
        OnPropertyChanged(nameof(GazeDeadzoneLeft));
        OnPropertyChanged(nameof(GazeDeadzoneTop));
    }

    private static double MapX(double yaw) =>
        Math.Clamp(PreviewWidth / 2 + yaw / PreviewYawSpan * PreviewWidth / 2, 0, PreviewWidth) - DotSize / 2;

    private static double MapY(double pitch) =>
        Math.Clamp(PreviewHeight / 2 - pitch / PreviewPitchSpan * PreviewHeight / 2, 0, PreviewHeight) - DotSize / 2;

    private string _warning = string.Empty;

    public string Warning
    {
        get => _warning;
        private set { if (Set(ref _warning, value)) OnPropertyChanged(nameof(HasWarning)); }
    }

    public bool HasWarning => !string.IsNullOrEmpty(_warning);

    // ---------------------------------------------------------------------------------------------
    // Updates (GitHub Releases)

    private ReleaseInfo? _availableUpdate;
    private bool _updateBusy;
    private string _updateStatus = $"v{UpdateService.CurrentVersion}";

    public string UpdateStatus
    {
        get => _updateStatus;
        private set => Set(ref _updateStatus, value);
    }

    public bool UpdateAvailable => _availableUpdate != null;

    public string UpdateButtonText => _availableUpdate == null ? "Install update" : $"Install v{_availableUpdate.Version}";

    private async Task CheckForUpdatesAsync(bool userInitiated)
    {
        if (_updateBusy) return;
        _updateBusy = true;
        if (userInitiated) UpdateStatus = "Checking for updates...";
        CommandManager.InvalidateRequerySuggested();
        try
        {
            var release = await UpdateService.CheckAsync();
            _availableUpdate = release;
            UpdateStatus = release != null
                ? $"v{UpdateService.CurrentVersion}. Update available: v{release.Version}"
                : $"v{UpdateService.CurrentVersion}. Up to date";
        }
        catch (Exception ex)
        {
            UpdateStatus = userInitiated ? $"Update check failed: {ex.Message}" : $"v{UpdateService.CurrentVersion}";
        }
        finally
        {
            _updateBusy = false;
            OnPropertyChanged(nameof(UpdateAvailable));
            OnPropertyChanged(nameof(UpdateButtonText));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task InstallUpdateAsync()
    {
        var release = _availableUpdate;
        if (release == null || _updateBusy) return;

        if (!UpdateService.CanWriteToAppFolder())
        {
            UpdateStatus = "Can't write to the app folder. Opening the download page.";
            UpdateService.OpenReleasesPage(release.PageUrl);
            return;
        }

        _updateBusy = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            // Save and let go of the tracker before the new version starts.
            SaveNow();
            bool wasRunning = _engine.IsRunning;
            _engine.Stop();

            var progress = new Progress<double>(p => UpdateStatus = $"Downloading v{release.Version}... {p * 100:0}%");
            try
            {
                await UpdateService.InstallAsync(release, progress);
            }
            catch
            {
                if (wasRunning && _isEnabled) _engine.Start(); // keep working on the current version
                throw;
            }

            UpdateStatus = "Restarting...";
            _skipSaveOnDispose = true; // already saved; the new version has loaded these settings
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            UpdateStatus = $"Update failed: {ex.Message}";
            _updateBusy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private bool _skipSaveOnDispose;

    // ---------------------------------------------------------------------------------------------

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveNow() => _store.Save(_settings);

    public void Dispose()
    {
        _uiTimer.Stop();
        _saveTimer.Stop();
        if (!_skipSaveOnDispose) SaveNow();
        _hotkeys?.Dispose();
        _engine.Dispose();
    }
}
