# Third-party components

Files in `Native/` are bundled inside `UniversalTrackIR.exe` and unpacked at runtime.

## opentrack

`NPClient.dll`, `NPClient64.dll`, `freetrackclient.dll`, `freetrackclient64.dll`, `TrackIR.exe` (from opentrack's `bin/`) and `games.csv` (opentrack's `settings/facetracknoir supported games.csv`).

Source: https://github.com/opentrack/opentrack

- The NPClient DLLs were written by uglyDwarf for linux-track (https://github.com/uglyDwarf/linuxtrack) and are distributed with opentrack.
- freetrackclient and the FreeTrack shared memory layout come from the FreeTrack and FaceTrackNoIR projects.
- The game list comes from FaceTrackNoIR.

opentrack core licensing:

> Copyright (c) 2012-2016 Stanislaw Halik
>
> Permission to use, copy, modify, and/or distribute this software for any purpose with or without fee is hereby granted, provided that the above copyright notice and this permission notice appear in all copies.

See opentrack's `OPENTRACK-LICENSING.txt` and `3rdparty-notices/` for each component's terms.

## Tobii Stream Engine

`tobii_stream_engine.dll` (x64) from the `Tobii.StreamEngine` 2.2.2.363 NuGet package, redistributed in object code form under the Tobii SDK License Agreement. Used for interactive gaming only (head and gaze input to games). No tracking data is stored or sent anywhere.

https://www.nuget.org/packages/Tobii.StreamEngine/
