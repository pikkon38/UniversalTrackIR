# Universal TrackIR

Turns a Tobii Eye Tracker 5 into head tracking for any game. Your head does the big camera moves. Your eyes add a little extra when you look toward the edge of the screen. Every axis has its own sensitivity, curve, deadzone and limit sliders.

Games with head tracking support (TrackIR or FreeTrack) see it as a TrackIR. For games without that support, it can drive the mouse or a vJoy virtual joystick instead.

## Install

Download **UniversalTrackIR-Setup.exe** from the [latest release](https://github.com/pikkon38/UniversalTrackIR/releases/latest) and run it. It installs for your Windows user only (no admin prompt), adds a Start menu shortcut and, if you want one, a desktop shortcut. Remove it any time from Settings > Apps.

**Updates:** the app checks GitHub on startup. When a new version exists, an **Install vX** button appears at the top. Click it: the app downloads the new installer, runs it silently, and comes back up on the new version. Settings and profiles carry over.

Prefer no installer? The release also has a portable **UniversalTrackIR.exe**. Put it in any folder you can write to; its Install button swaps the exe in place.

## Requirements

- Windows 10 or 11, 64-bit.
- Tobii Eye Tracker 5 with **Tobii Experience** installed and the tracker calibrated. The app talks to the Tobii service the same way opentrack does.
- Optional: [vJoy](https://github.com/BrunnerInnovation/vJoy/releases) for the virtual joystick output.

## First-time setup

1. **Start the app.** The dot at the top turns green and says "Tobii connected". The live view shows an orange dot that follows your head.
2. **Sit how you play and look at the middle of the screen.** Press **Recenter** (default hotkey `Ctrl+Shift+F12`).
3. **Check directions.** Turn your head right: the orange dot goes right. Look up: it goes up. If a game moves the wrong way on an axis, tick **Invert** on that row.
4. **Turn on head tracking in the game.** Pick TrackIR in its settings, then launch. The status line shows "Game connected: <name>" for games that announce themselves.
5. **Tune.** See below.

## Tuning

You can only turn your head so far and still see the screen, so turning is amplified.

- **Sensitivity:** how far the view goes per degree of head turn. At 4x, turning 10° looks 40° in game. Start here.
- **Curve:** 0 (linear) moves the view at the same rate everywhere. Higher values calm the middle, for aiming and reading instruments, and speed up toward the edges so you can still check six. Turning 20° gives the same result at any curve setting.
- **Deadzone:** small movements around center are ignored, so the view holds still while you sit still.
- **Limit:** the furthest the view can go on that axis.
- **Smoothing (head):** steadier view, with a little more lag. Fast movements always come through quickly. This uses a One Euro filter.

**Eye tracking** adds an offset on top of the head. Look at the left edge and the view turns up to *Extra yaw* further. Inside the dashed box in the middle of the screen, your eyes add nothing. That way glancing at a gauge doesn't move the view. Eyes jump around a lot, so their smoothing starts higher.

**Profiles:** tune one per game. **Duplicate** copies the current profile, **Rename** names it after the game, and switching the dropdown applies it immediately.

## Outputs

| Output | Use it for | How |
|---|---|---|
| **TrackIR and FreeTrack** | Star Citizen, DCS, MSFS, IL-2, Elite Dangerous, ACC, iRacing, rFactor 2, AMS2, ETS2/ATS and [hundreds more](Native/games.csv) | On by default. Pick TrackIR in the game. |
| **Mouse look** | Games with free look on the mouse but no head tracking | Pick the game's process. The mouse only moves while that game is in front. Raise *pixels per degree* until a 90° turn of your head turns the camera 90°. |
| **vJoy** | Games that let you bind look/pan to a joystick axis | Install vJoy. Yaw, pitch and roll go to X, Y and Z. Position goes to Rx, Ry and Rz. |

**How the TrackIR output works:** games look up where TrackIR's `NPClient64.dll` lives in the registry (`HKCU\Software\NaturalPoint\NATURALPOINT\NPClient Location`). The app points that entry at its own copy, in `%LOCALAPPDATA%\UniversalTrackIR\clients`, and feeds it the pose. FreeTrack games are handled the same way. If you later run NaturalPoint's own TrackIR software, it points the registry back to its DLL. Start this app again to take it back.

## Hotkeys

| Default | Does |
|---|---|
| `Ctrl+Shift+F12` | Recenter |
| `Ctrl+Shift+F11` | Pause / resume. The view glides to center and stays there. |
| `Ctrl+Shift+F10` | Eyes on / off |

Click a hotkey in the app and press a new combo to change it. Backspace clears it. Hotkeys work while the game has focus.

## Troubleshooting

- **"No eye tracker found":** Make sure Tobii Experience is running and the tracker shows as on. Unplug and replug it, then wait a few seconds. The app retries every 3 seconds.
- **"Head tracking isn't available":** Update Tobii Experience. If it still fails, put a newer `tobii_stream_engine.dll` (x64) in `%APPDATA%\UniversalTrackIR\`. The app uses that copy instead of the bundled one.
- **The game doesn't react:** Turn on TrackIR in the game's options. Close NaturalPoint TrackIR software if it's running. Leave **Run TrackIR.exe stand-in** ticked, because some games only enable head tracking when they see it.
- **The view drifts:** Recenter. Raise the deadzone a little if it creeps when you sit still.
- **The view jitters:** Raise head smoothing, or raise curve so the middle is calmer.
- **An axis is backwards in one game:** Duplicate the profile for that game and tick **Invert** on that axis.

## Building from source

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0).

Double-click **`Update.bat`**. It builds the source in this folder, installs it to `%LOCALAPPDATA%\UniversalTrackIR\app`, creates Start menu and Desktop shortcuts, and relaunches the app. If a build fails, the installed version is left alone and the output goes to `build-log.txt`.

Settings and profiles live in `%APPDATA%\UniversalTrackIR\settings.json`. Neither updates nor rebuilds touch them.

## Releasing a new version

1. Bump `<Version>` in `UniversalTrackIR.csproj`.
2. Commit and push to `main`. GitHub Actions compiles every push.
3. **Actions > Build > Run workflow**, enter a tag like `v0.2.0`, and run it. Actions builds `UniversalTrackIR-Setup.exe` (Inno Setup, script in `installer/`) and the portable `UniversalTrackIR.exe`, and attaches both to a new release. Every running copy offers the update on its next launch.

## Credits

See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). The TrackIR and FreeTrack client DLLs and the game list come from [opentrack](https://github.com/opentrack/opentrack). Head pose and gaze come from Tobii Stream Engine.
