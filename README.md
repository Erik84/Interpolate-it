<p align="center"><img src="docs/icon.png" width="128" alt="Interpolate-it"></p>

# Interpolate-it

**Real-time AI frame interpolation for any window on Windows 11. Made for the love of high fps.**

Pick a window, press **Start Interpolating**, and every two frames the application draws get one, two or three
AI-generated frames in between: a 30 fps stream is shown at 60 fps (x2) or 120 fps (x4), whatever the application
renders with. Videos, streams, games: if it has a window, it qualifies.

**[Download the installer](https://github.com/Erik84/Interpolate-it/releases/latest/download/Interpolate-it-Setup.exe)**
&nbsp;·&nbsp; free and open source (MIT) &nbsp;·&nbsp;
[donations welcome](https://www.paypal.com/donate/?business=FT2K6CLC4RLBJ&no_recurring=1&item_name=Enjoying+Interpolate-it?+Feel+free+to+leave+a+donation%21+Huge+thanks%2C+it+keeps+me+motivated+to+build+more+cool+tools.&currency_code=EUR)

## It started with GTA 6

Big screen, 120 Hz, and thirty frames per second. Thirty. In this economy.

Waiting for a patch felt slow (about 30 fps slow), so I asked a different question: what if the missing frames
simply showed up anyway? One day, one stubborn idea and a suspicious amount of help from GenAI later, this tool
was built and tested. It watches a window, imagines the frames in between and slips them in before your eyes can
file a complaint.

Now it is your turn. Take your favourite low-fps content, be it a console stream, a 24 fps movie or an anime that
moves like a slideshow, and give it the smoothness it always deserved. Not just GTA 6. Anything with a window.

Enjoy!

*Erik Podt, for the love of high fps*

### Free, and it stays free

No ads, no account, no "premium frames". If Interpolate-it made your evening smoother and you feel like saying
thanks, [a donation is very welcome](https://www.paypal.com/donate/?business=FT2K6CLC4RLBJ&no_recurring=1&item_name=Enjoying+Interpolate-it?+Feel+free+to+leave+a+donation%21+Huge+thanks%2C+it+keeps+me+motivated+to+build+more+cool+tools.&currency_code=EUR).
Huge thanks, it keeps me motivated to build more cool tools.

## Install

1. Download **[Interpolate-it-Setup.exe](https://github.com/Erik84/Interpolate-it/releases/latest/download/Interpolate-it-Setup.exe)**
   (about 120 MB: the AI models and everything else it needs are inside).
2. Run it. It installs for your own account and asks for no administrator rights. The installer is not signed, so
   Windows SmartScreen may want a second opinion: *More info* > *Run anyway*.
3. Start Interpolate-it from the Start menu.

What you need:

- Windows 11 24H2 or newer, 64-bit.
- A graphics card. NVIDIA RTX cards use *TensorRT for RTX*, which Windows fetches the first time (that first start
  takes a little longer); every other DirectX 12 GPU (AMD, Intel, older NVIDIA) runs through DirectML. The CPU can
  be selected but is not fast enough for real time.

It was built and measured on one PC (RTX 4080 SUPER, 120 Hz display). AMD and Intel cards should work through
DirectML but have not been tried on such hardware: reports are welcome under *Issues*.

## Using it

1. Choose the window in the list (it shows the windows you could also pick in a screen-capture dialog).
2. Choose **x2**, **x3** or **x4** and a quality: *Quality* (RIFE 4.25) or *Fast* (RIFE 4.6).
3. Press **Start Interpolating**. The first start on a new window size takes a few seconds while the network is
   prepared for it; after that it is cached.

| Control | Hotkey | What it does |
|---|---|---|
| Interpolation on | Ctrl+Alt+I | Switches the generated frames on and off at once. Off shows the real window untouched; capture and the network stay loaded, so on is immediate. With nothing running, the hotkey starts on the window you are looking at. |
| Show detected, target and actual fps | Ctrl+Alt+F | Draws a small readout in the top-left corner of the target window: the frame rate detected in the source, the output rate being aimed for and the rate actually reaching the screen. |
| Clean up compressed video | Ctrl+Alt+H | For streams and other heavily compressed video: every source frame first goes through the graphics driver's video enhancement, which removes compression artifacts and sharpens, and the in-between frames are made from the cleaned-up frames. This is NVIDIA's RTX Video Super Resolution, so it needs an RTX card and the feature switched on in the NVIDIA app (System > Video > RTX Video Enhancement > Super Resolution; its quality level is chosen there). The line under the switch tells whether the driver is really doing it and what it costs. |
| Record | Ctrl+Alt+R | Writes what the window shows to an MP4 file in `Videos\Interpolate-it`, at the rate it is shown: up to the display's refresh rate, so 120 frames per second on a 120 Hz display. Hardware-encoded (H.264 up to 2560x1440 at 120 fps, HEVC above), without the fps readout, without sound. Press again to stop. |

The output rate is always one the display can show evenly, so the requested factor is rounded to the nearest
rate that divides the refresh rate. On a 120 Hz display:

| Source | x2 | x3 | x4 |
|---|---|---|---|
| 30 fps | 60 fps | 120 fps (really x4) | 120 fps |
| 60 fps | 120 fps | 120 fps (x2) | 120 fps (x2) |
| 24 fps | 60 fps (x2.5) | 60 fps (x2.5) | 120 fps (really x5) |
| 25 fps | 60 fps (x2.4) | 60 fps (x2.4) | 120 fps (x4.8) |

Good to know:

- The picture runs 35–70 ms behind the real window (a frame between two others can only be made once the
  second one exists). Sound is not delayed; a player's audio-delay setting can compensate.
- When the window stops changing (a paused video, a page at rest) the last real frame stays up and, after half a
  second, the overlay steps aside so that the real window shows directly. The same happens at once while you
  move or resize the window.
- Streams and games do not keep a steady rhythm. Frames that arrive a little late are put back where they belong,
  near-copies of the previous frame (a 30 fps game sent as a 60 fps stream) are ignored, and a source that keeps
  changing its rate is simply followed frame by frame.
- Pixels that are identical in two consecutive real frames (window chrome, text, subtitles that stand still)
  are copied, not interpolated, so they stay pixel-exact.
- The clean-up costs about 5 ms per source frame for a 1920x1080 window (RTX 4080 SUPER) and adds a refresh
  of delay; with a 60 fps source the GPU then runs at about two thirds of its capacity. Now and then one frame
  takes the driver much longer, which costs a few in-between frames, and fine static detail right next to
  something that moves can shimmer a little. The driver takes pictures up to 2560x1440; a larger window is
  scaled down first and enhanced back to its own size (a 3840x2160 window goes through 1920x1080, which is what
  a 1080p stream shown that large consists of anyway). While the overlay steps aside (a paused picture), the
  window shows without clean-up.
- DRM-protected video is captured as black; exclusive-fullscreen games cannot be covered (use borderless);
  a window running as administrator needs Interpolate-it to run as administrator too. SDR only.
- For games, cap the frame rate so the GPU keeps headroom; a GPU at 100 % has no time left for the AI frames.

## How it works

It works from the outside. The target window is captured with Windows.Graphics.Capture, the in-between frames are
made on the GPU with a RIFE optical-flow network, and the result is shown in a click-through overlay that sits
exactly on top of the window. Mouse and keyboard keep going to the real application; the application itself is
never modified.

```
target window --Windows.Graphics.Capture--> real frames on the GPU, stamped with the refresh they are shown on
  -> duplicate / off-cadence / scene-cut gate, area downscale to the "inference resolution"   (compute shader)
  -> RIFE flow network: optical flow + blend mask between two frames at a timestep t           (ONNX Runtime)
  -> warp + blend of the two real, full-resolution frames with that flow                       (pixel shader)
  -> every output frame is assigned to one specific display refresh and presented in the overlay
```

- **The network only estimates motion**, at a reduced resolution (at most 1280x704 by default). The picture is
  composed from the two real frames at window resolution, which is exactly RIFE's own last step. Cost therefore
  follows the inference resolution, not the window size.
- **Cadence.** Capture timestamps are compositor times, quantised to the display refresh; a 25 fps video on
  120 Hz arrives as 5-5-5-5-4 refreshes. `CadenceEstimator` locks onto the real period and hands out evenly
  spaced timestamps. When source rate x factor matches the display, timesteps are exact fractions (k/N);
  otherwise every output slot gets the timestep its moment in time stands for.
- **Two models.** The chosen model runs the exact cadences. Cadences that need timesteps anywhere between two
  frames (24/25 fps on 120 Hz, free-running games and streams) automatically use RIFE 4.6, which follows the
  timestep linearly from 0.05 to 0.95; RIFE 4.25 keeps flat objects on the nearest frame below 0.2
  (`tools\export\measure_timestep_linearity.py`). The network is prepared once the source's rhythm is known.
- **Static pixels are copied.** Where both real frames are identical around a pixel and nothing moving is brought
  there by the flow, the pixel is taken over unchanged. A static fine pattern next to motion otherwise gets
  shifted by a fraction of its period.
- **Clean-up before interpolation (optional).** A real frame that is going to be shown is run through the
  driver's video processor once, in place; the in-between frames are warps of the cleaned-up frames, so the
  cost is per source frame, not per output frame. Motion is still estimated on the frames as captured, and
  so is the test for static pixels. The driver gives no sign of whether it enhances at all, so the engine
  checks: a test picture is converted with and without the request and the results are compared.
- **Presentation.** One decision per display refresh, woken by the compositor clock. A present made in refresh
  interval r is on the glass at r + 2 (measured, and re-checked continuously with DXGI frame statistics); a
  frame that is ready is presented at the start of the interval so that a busy GPU cannot make it miss.
- **Backends.** One ONNX Runtime code path through Windows ML; the execution provider is picked from what the
  machine has.

Measured on an RTX 4080 SUPER, 1216x704 inference, fp16: 4.6 ms per generated frame with RIFE 4.25 and 3.0 ms
with RIFE 4.6 on TensorRT for RTX (about twice that when the GPU clocks down at light load; DirectML 9.4 / 4.7 ms).

## Build from source

You need the .NET 10 SDK and, once, Python 3.12 with PyTorch to turn the RIFE weights into the ONNX models the
application runs.

```powershell
# 1. Models (once). Downloads the RIFE 4.6 / 4.25 weights and exports them to ONNX under models\.
py -3.12 -m venv tools\export\.venv --system-site-packages
tools\export\.venv\Scripts\python.exe -m pip install -r tools\export\requirements.txt
.\tools\export\fetch_weights.ps1
tools\export\.venv\Scripts\python.exe -I tools\export\export_rife_flow.py --model 4.25 --verify --golden
tools\export\.venv\Scripts\python.exe -I tools\export\export_rife_flow.py --model 4.6 --verify --golden

# 2. Build and run.
dotnet build InterpolatIt.sln -c Release
.\artifacts\bin\InterpolatIt.App\release_win-x64\InterpolatIt.exe

# 3. The installer (needs Inno Setup 6: winget install JRSoftware.InnoSetup).
.\tools\build-installer.ps1        # -> installer\output\Interpolate-it-Setup.exe
```

## Verifying it

Everything is checked against what actually reaches the screen, not against what the engine believes it did:

- `tools\InterpolatIt.TestPattern` — a window with an exact cadence, a frame counter as a Gray-code strip, a
  moving bar and a static fine grid.
- `tools\InterpolatIt.ScreenProbe` — reads the desktop back with Desktop Duplication and reports per refresh
  which picture was on screen: rate, how long each picture stayed, where the bar was against time, whether
  static pixels changed.
- `tools\run-matrix.ps1` — pattern + engine + probe for 24 / 25 / 30 / 60 fps sources and x2 / x3 / x4, plus
  sources that misbehave the way real ones do: blinking near-copies, frames a refresh late, a pause.
- `tools\test-app.ps1` — the application itself: start, on/off, fps readout, clean-up, recording
  (`-AppPath` tests an installed copy).
- `tools\InterpolatIt.Bench` — inference and synthesis against golden frames from PyTorch, per backend;
  `--window WxH` for the cost of every stage at a window size, `--record-probe WxH` for the recorder and
  `--enhance-probe WxH` for the clean-up step at a picture size (none of them needs a window).
- `dotnet test` — the timing library (cadence lock, pacing, scheduling) against synthetic timestamps.
- `InterpolatIt.Cli run --title <text> --factor 2 [--hud] [--enhance] [--log pacing.csv] [--trace]` — the engine without the UI.
- `tools\run-offscreen.ps1` — the engine on a test pattern parked beyond the edge of the screen, for checks
  of the engine's own numbers while the desktop is in use.

Typical probe results on a 120 Hz display (1280x720 pattern): 30 fps x2 → 60 pictures/s, each on screen for
exactly 2 refreshes, bar within 0.7 px of the exact half-way positions; 25 fps x4 → 119.3 pictures/s, motion
within 1.1 px (p99) of a straight line; static grid identical to the original in every case; with the clean-up
on, 60 fps x2 → 120.0 pictures/s. The recorder keeps up with 120 pictures per second from 1920x1080 (H.264,
22 Mbit/s) to 3840x2160 (HEVC, 60 Mbit/s) and 7680x2160 without skipping a frame (RTX 4080 SUPER).

## Repository layout

```
src\InterpolatIt.Timing    cadence estimation, pacing policy, frame scheduling (no Windows dependencies)
src\InterpolatIt.Core      capture, GPU passes and shaders, inference, overlay, presenter, session
src\InterpolatIt.App       the WPF application
src\InterpolatIt.Cli       headless host: list-windows | trace | run
tools\export               RIFE -> ONNX export (flow + mask only) and its validation
tools\InterpolatIt.*       bench, test pattern, screen probe
tests\                     xUnit tests for the timing library
installer\                 Inno Setup script of the installer
models\                    generated ONNX files and manifest (not in git; part of the installer)
```

## Not there yet

- Automatic choice of the inference resolution from measured load (it is fixed at up to 1280x704 for now), and
  preparing networks in the background instead of at the moment they are first needed.
- Covering only the video inside a page instead of the whole window.
- Sound in recordings. A backend picker in the UI, tray icon, configurable hotkeys. HDR.
- Clean-up of compressed video on AMD and Intel cards (their drivers have their own enhancement), a choice of
  quality inside the app, and showing a small window's picture enlarged.

## Licence

Interpolate-it is free and open source under the [MIT License](LICENSE).

It builds on the work of others, which keeps its own terms: the RIFE frame-interpolation models (MIT) by hzwer
and HolyWu, Vortice.Windows (MIT), and Microsoft's .NET (MIT) and Windows ML runtime with ONNX Runtime and
DirectML (Microsoft's licence terms). The details are in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md); the
installer puts every licence text next to the application.

Not affiliated with, or endorsed by, any game or its publisher. Game titles are mentioned only to say what was
being played when the idea struck.
