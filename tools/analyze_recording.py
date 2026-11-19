"""Checks a recording of the test pattern (for the sound: made with its --beep option).

Picture: the pattern's white bar moves at a constant speed, so in a recording every frame must show it where
its time says it is. This reads the MP4 back with ffmpeg and reports:
  * how many frames are black (a frame the encoder got nothing for);
  * in how many frames the bar is not where it should be (a picture out of order, or one that came too late).

Sound: the pattern plays a soft tone for 0.15 s every second and shows a white square in its bottom-right corner
while it does. Reported:
  * whether there is a sound track, in which format, and whether it is as long as the picture;
  * whether the beeps are in it, at the right pitch, one a second, with silence in between;
  * how far each beep is from the moment the square appears in the picture.

  analyze_recording.py FILE.mp4 [--max-offset 0.15] [--no-sound | --expect-silence]
                                [--no-bar | --pauses] [--max-step-error 3] [--max-shifts 2] [--skip-start S]
                                [--max-black 0]

--no-bar: do not look at the bar (the recording is of something else). --pauses: the source stood still at
times, so a bar that did not move is not an error. --max-shifts: how often a picture may stay one refresh
longer or be left out (the engine changing its playback delay by a refresh; the screen showed the same).
--skip-start: do not judge the bar's place in the first S seconds (while the encoder starts, the engine can
be late with a frame, on the screen as much as in the file).
Exit code 0 when everything asked for holds. Needs ffmpeg and ffprobe (on PATH, or FFMPEG_DIR) and numpy.
"""
import json
import os
import shutil
import subprocess
import sys

import numpy as np

BEEP_HZ = 880.0
BEEP_SECONDS = 0.15


def tool(name):
    found = shutil.which(name)
    if found:
        return found
    for folder in (os.environ.get("FFMPEG_DIR"), r"C:\Tools\FFMPEG"):
        if folder and os.path.exists(os.path.join(folder, name + ".exe")):
            return os.path.join(folder, name + ".exe")
    sys.exit(f"{name} was not found: put it on PATH or set FFMPEG_DIR")


def run(args):
    return subprocess.run(args, capture_output=True, check=True).stdout


def check_picture(ffmpeg, path, width, height, times, args, failures):
    """Black frames, and the bar's place in every frame against the frame's time."""
    def number(name, default):
        return float(args[args.index(name) + 1]) if name in args else default

    mean = np.frombuffer(run([ffmpeg, "-v", "error", "-i", path, "-an", "-vf", "scale=1:1:flags=area,format=gray",
                              "-fps_mode", "passthrough", "-f", "rawvideo", "-"]), dtype=np.uint8)
    black = np.flatnonzero(mean < 4)
    print(f"         {len(black)} of {len(mean)} frames are black" + (f" (the first at frame {black[0]})" if len(black) else ""))
    if len(black) > number("--max-black", 0):
        failures.append(f"{len(black)} of {len(mean)} frames are black")
    if "--no-bar" in args:
        return

    # The bar's band starts at 30 % of the client area's height and is 60 rows high; the recording has the
    # title bar (31 rows) above the client area. Sixteen rows from the middle of the band, averaged to one row.
    top = int(round(0.3 * height + 43))
    rows = np.frombuffer(run([ffmpeg, "-v", "error", "-i", path, "-an", "-vf",
                              f"crop={width}:16:0:{top},scale={width}:1:flags=area,format=gray",
                              "-fps_mode", "passthrough", "-f", "rawvideo", "-"]), dtype=np.uint8).reshape(-1, width)
    count = min(len(rows), len(times))
    white = rows[:count] > 190                              # the bar is white; the texture around it stays under 60 %
    widths = white.sum(axis=1)
    centre = np.where((widths >= 14) & (widths <= 40), (white * np.arange(width)).sum(axis=1) / np.maximum(widths, 1), np.nan)
    seen = np.flatnonzero(~np.isnan(centre))
    if len(seen) < count // 2:
        failures.append(f"the bar was found in only {len(seen)} of {count} frames (is this the test pattern?)")
        return
    t = np.asarray(times[:count])
    dx = centre[seen[1:]] - centre[seen[:-1]]
    dt = t[seen[1:]] - t[seen[:-1]]
    moving = (dx > 0.5) & (dt > 0)
    speed = float(np.median(dx[moving] / dt[moving])) if moving.any() else 0.0

    # Where the bar jumps back to the left edge, the frames around it show a bar in two places: not judged.
    excused = np.zeros(count, dtype=bool)
    for k in np.flatnonzero(dx < -width / 2):
        excused[max(0, seen[k] - 6):seen[k + 1] + 7] = True
    error = dx - speed * dt
    judged = ~excused[seen[1:]] & ~excused[seen[:-1]]
    judged &= t[seen[1:]] >= number("--skip-start", 0.0)
    if "--pauses" in args:
        usual = float(np.median(dt)) if len(dt) else 0.0
        judged &= (np.abs(dx) > 0.6) & (dt < 3 * usual)
    limit = number("--max-step-error", 3.0)
    # A picture that stayed for one refresh more, or one that was left out, is the engine changing its playback
    # delay by a refresh (which it does when frames come late, often once when a recording starts): the bar is
    # then one refresh's worth of pixels from where the time says. Counted, and allowed a few times.
    refresh = float(np.median(dt)) if len(dt) else 0.0
    shifted = judged & (np.abs(np.abs(error) - speed * refresh) < 1.0) & (speed * refresh > limit)
    wrong = judged & (np.abs(error) > limit) & ~shifted
    lost = int((np.isnan(centre) & ~excused).sum())
    largest = float(np.abs(error[judged & ~shifted]).max()) if (judged & ~shifted).any() else 0.0
    print(f"bar    : found in {len(seen)} of {count} frames, moving {speed:.0f} pixels a second; largest difference from its place "
          f"{largest:.1f} pixels; {int(shifted.sum())} times a picture stayed a refresh longer or was left out")
    if wrong.any():
        first = seen[1:][wrong][0]
        failures.append(f"{int(wrong.sum())} frames show the bar in the wrong place (up to {largest:.1f} pixels; the first is frame {first})")
    if shifted.sum() > number("--max-shifts", 2):
        failures.append(f"{int(shifted.sum())} times a picture stayed a refresh longer or was left out (the first at frame {seen[1:][shifted][0]})")
    if lost:
        failures.append(f"{lost} frames have no bar (the first is frame {np.flatnonzero(np.isnan(centre) & ~excused)[0]})")


def main():
    args = sys.argv[1:]
    if not args:
        sys.exit(__doc__)
    path = args[0]
    max_offset = float(args[args.index("--max-offset") + 1]) if "--max-offset" in args else 0.15
    expect_sound = "--no-sound" not in args
    expect_silence = "--expect-silence" in args          # a sound track with nothing in it: the application played nothing
    ffmpeg, ffprobe = tool("ffmpeg"), tool("ffprobe")
    failures = []

    streams = json.loads(run([ffprobe, "-v", "error", "-show_entries",
                              "stream=codec_type,codec_name,width,height,sample_rate,channels,duration,nb_frames",
                              "-of", "json", path]))["streams"]
    video = next((s for s in streams if s["codec_type"] == "video"), None)
    audio = next((s for s in streams if s["codec_type"] == "audio"), None)
    if video is None:
        sys.exit("no picture in the file")
    width, height, video_seconds = int(video["width"]), int(video["height"]), float(video["duration"])
    print(f"picture: {video['codec_name']} {width}x{height}, {video.get('nb_frames', '?')} frames, {video_seconds:.2f} s")
    times = sorted(float(t) for t in run([ffprobe, "-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts_time",
                                          "-of", "csv=p=0", path]).decode().replace(",", "").split())
    check_picture(ffmpeg, path, width, height, times, args, failures)
    if audio is None:
        print("sound  : none")
        if expect_sound:
            failures.append("there is no sound track")
        return finish(failures)
    audio_seconds = float(audio["duration"])
    print(f"sound  : {audio['codec_name']} {audio['sample_rate']} Hz, {audio['channels']} channels, {audio_seconds:.2f} s")
    if not expect_sound:
        failures.append("there is a sound track, and none was expected")
    if abs(audio_seconds - video_seconds) > 0.15:
        failures.append(f"sound and picture differ in length by {audio_seconds - video_seconds:+.2f} s")

    # ---- the beeps in the sound ---------------------------------------------------------------------------
    rate = 48000
    samples = np.frombuffer(run([ffmpeg, "-v", "error", "-i", path, "-vn", "-ac", "1", "-ar", str(rate), "-f", "f32le", "-"]), dtype=np.float32)
    hop = rate // 1000                                   # 1 ms
    usable = len(samples) // hop * hop
    energy = np.sqrt(np.mean(samples[:usable].reshape(-1, hop).astype(np.float64) ** 2, axis=1))
    smooth = np.convolve(energy, np.ones(5) / 5, mode="same")
    peak = float(np.percentile(smooth, 99.5)) if len(smooth) else 0.0
    print(f"         level of the loudest parts {20 * np.log10(max(peak, 1e-9)):.1f} dBFS")
    beeps = []
    if expect_silence:
        if peak > 3e-5:
            failures.append(f"the sound track is not silent ({20 * np.log10(peak):.0f} dBFS)")
        return finish(failures)
    if peak < 1e-5:
        failures.append("the sound track is silent")
    else:
        loud = smooth > 0.4 * peak
        i = 0
        while i < len(loud):
            if loud[i]:
                j = i
                while j < len(loud) and loud[j]:
                    j += 1
                if j - i >= 40:                          # at least 40 ms: a beep, not a click
                    beeps.append((i / 1000.0, j / 1000.0))
                i = j
            else:
                i += 1
        lengths = [b - a for a, b in beeps]
        pitches = []
        for a, b in beeps:
            piece = samples[int((a + 0.02) * rate):int((b - 0.02) * rate)]
            if len(piece) >= 2048:
                spectrum = np.abs(np.fft.rfft(piece * np.hanning(len(piece))))
                pitches.append(float(np.argmax(spectrum)) * rate / len(piece))
        between = smooth[~loud]
        floor = float(np.percentile(between, 90)) if len(between) else 0.0
        print(f"         {len(beeps)} beeps in {audio_seconds:.1f} s; length {np.median(lengths) * 1000:.0f} ms (median); "
              f"pitch {np.median(pitches):.0f} Hz (median); in between {20 * np.log10(max(floor, 1e-9)):.0f} dBFS")
        expected = audio_seconds / 1.0
        if abs(len(beeps) - expected) > 1.5:
            failures.append(f"{len(beeps)} beeps where about {expected:.0f} were played")
        if pitches and abs(np.median(pitches) - BEEP_HZ) > 15:
            failures.append(f"the beeps are at {np.median(pitches):.0f} Hz instead of {BEEP_HZ:.0f}")
        if lengths and abs(np.median(lengths) - BEEP_SECONDS) > 0.04:
            failures.append(f"the beeps last {np.median(lengths) * 1000:.0f} ms instead of {BEEP_SECONDS * 1000:.0f}")
        gaps = np.diff([a for a, _ in beeps])
        if len(gaps) and (np.max(np.abs(gaps - 1.0)) > 0.06):
            failures.append(f"the beeps are not a second apart everywhere (between {gaps.min():.3f} and {gaps.max():.3f} s)")

    # ---- the square in the picture ------------------------------------------------------------------------
    # The middle 40x40 of the square (it is 60x60, with its right edge 100 and its bottom edge 40 pixels inside
    # the pattern's client area; the recording has the window's thin border around that).
    crop = f"crop=40:40:{width - 150}:{height - 90},scale=1:1:flags=area,format=gray"
    brightness = np.frombuffer(run([ffmpeg, "-v", "error", "-i", path, "-an", "-vf", crop, "-fps_mode", "passthrough",
                                    "-f", "rawvideo", "-"]), dtype=np.uint8)
    count = min(len(brightness), len(times))
    on = brightness[:count] > 128
    appearances = [times[i] for i in range(1, count) if on[i] and not on[i - 1]]
    print(f"square : seen in {int(on.sum())} of {count} frames, appears {len(appearances)} times")
    if beeps and appearances:
        starts = np.array([a for a, _ in beeps])
        offsets = []
        for moment in appearances:
            nearest = starts[np.argmin(np.abs(starts - moment))]
            if abs(nearest - moment) < 0.5:
                offsets.append(nearest - moment)
        if offsets:
            offsets = np.array(offsets)
            print(f"timing : the beep starts {np.median(offsets) * 1000:+.0f} ms after the square appears (median of {len(offsets)}; "
                  f"between {offsets.min() * 1000:+.0f} and {offsets.max() * 1000:+.0f} ms)")
            if abs(np.median(offsets)) > max_offset:
                failures.append(f"sound and picture are {np.median(offsets) * 1000:+.0f} ms apart (limit {max_offset * 1000:.0f} ms)")
            if offsets.max() - offsets.min() > 0.08:
                failures.append(f"the distance between sound and picture wanders by {(offsets.max() - offsets.min()) * 1000:.0f} ms")
        else:
            failures.append("no beep near any appearance of the square")
    elif expect_sound:
        failures.append("the square was not found in the picture" if beeps else "no beeps to compare the picture with")
    return finish(failures)


def finish(failures):
    if failures:
        print("FAILED:")
        for failure in failures:
            print("  - " + failure)
        return 1
    print("OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
