using System.Runtime.InteropServices;
using InterpolatIt.Core.Native;
using InterpolatIt.Core.Recording;

namespace InterpolatIt.Bench;

/// <summary>
/// Does capturing one application's sound work on this PC? The probe plays a tone itself - so softly that it is
/// not heard (-60 dBFS) - captures the sound of its own process and looks at what comes back: how much, at which
/// level and pitch, and whether the packets' times follow one another without holes. The packets also go through
/// the timeline the recorder uses, to see that it hands out exactly as much as came in.
/// </summary>
internal static unsafe class AudioProbe
{
    private const uint SoundAsync = 0x1, SoundNoDefault = 0x2, SoundMemory = 0x4, SoundLoop = 0x8;
    private const double ToneHz = 880, LevelDb = -60;

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern bool PlaySoundW(nint sound, nint module, uint flags);

    public static int Run(double seconds)
    {
        Console.WriteLine($"\n== sound of one application (this process), {seconds:F0} s of a {ToneHz:F0} Hz tone at {LevelDb:F0} dBFS");
        ProcessAudioCapture capture;
        try
        {
            capture = ProcessAudioCapture.Start((uint)Environment.ProcessId);
        }
        catch (InvalidOperationException e)
        {
            Console.WriteLine($"   capture did not start: {e.Message}");
            return 1;
        }

        var wave = GCHandle.Alloc(ToneWave(), GCHandleType.Pinned);
        var samples = new List<short>();
        var timeline = new AudioTimeline();
        int packets = 0, silentPackets = 0;
        double firstTime = double.NaN, expectedNext = double.NaN, largestHole = 0, lateness = 0;
        long framesIn = 0;
        using (capture)
        {
            PlaySoundW(wave.AddrOfPinnedObject(), 0, SoundAsync | SoundNoDefault | SoundMemory | SoundLoop);
            double start = Qpc.Now;
            while (Qpc.Now - start < seconds)
            {
                capture.DataAvailable.WaitOne(10);
                capture.Drain((pcm, frames, time, silent) =>
                {
                    packets++;
                    if (silent) silentPackets++;
                    if (double.IsNaN(firstTime)) firstTime = time;
                    if (!double.IsNaN(expectedNext)) largestHole = Math.Max(largestHole, Math.Abs(time - expectedNext));
                    expectedNext = time + frames / (double)ProcessAudioCapture.SampleRate;
                    lateness = Math.Max(lateness, Qpc.Now - expectedNext);
                    framesIn += frames;
                    var values = MemoryMarshal.Cast<byte, short>(pcm);
                    for (int i = 0; i < values.Length; i += 2) samples.Add(silent ? (short)0 : values[i]);
                    timeline.Write(pcm, frames, time - firstTime, silent);
                });
            }
            PlaySoundW(0, 0, 0);
        }
        wave.Free();

        if (packets == 0)
        {
            Console.WriteLine("   nothing was captured");
            return 1;
        }

        // Out of the timeline again, in the 20 ms pieces the encoder gets.
        long framesOut = 0;
        var chunk = new byte[AudioTimeline.ChunkFrames * AudioTimeline.FrameBytes];
        double covered = framesIn / (double)ProcessAudioCapture.SampleRate;
        while (timeline.TryRead(covered, chunk, out _)) framesOut += AudioTimeline.ChunkFrames;

        double sum = 0;
        foreach (short sample in samples) sum += (double)sample * sample;
        double rms = Math.Sqrt(sum / Math.Max(1, samples.Count)) / 32768.0;

        // The pitch: the frequency between 700 and 1100 Hz at which the captured sound has the most energy.
        // (Counting zero crossings does not work on a tone this soft: it is only 33 steps of the 16 bits high.)
        double pitch = 0, strongest = 0;
        int count = Math.Min(samples.Count, ProcessAudioCapture.SampleRate);
        for (double hz = 700; hz <= 1100; hz += 1)
        {
            double re = 0, im = 0, step = 2 * Math.PI * hz / ProcessAudioCapture.SampleRate;
            for (int i = 0; i < count; i++)
            {
                re += samples[i] * Math.Cos(step * i);
                im += samples[i] * Math.Sin(step * i);
            }
            double power = re * re + im * im;
            if (power > strongest) { strongest = power; pitch = hz; }
        }
        Console.WriteLine($"   {packets} packets ({silentPackets} marked silent), {framesIn} frames = {covered:F2} s of sound in {seconds:F0} s");
        Console.WriteLine($"   level {20 * Math.Log10(Math.Max(rms * Math.Sqrt(2), 1e-9)):F1} dBFS (peak of a sine), pitch {pitch:F0} Hz");
        Console.WriteLine($"   largest hole between the end of one packet and the start of the next: {largestHole * 1000:F2} ms; " +
                          $"a packet was handed over at most {lateness * 1000:F0} ms after its last sample was played");
        Console.WriteLine($"   through the recorder's timeline: {framesOut} frames out for {framesIn} in, {timeline.Lost} lost");
        // (The packets' times are when Windows handed them on, a few milliseconds more or less: the timeline
        // appends anything within 40 ms of where the stream stands, so holes must stay well below that.)
        bool ok = covered > 0.7 * seconds && Math.Abs(pitch - ToneHz) < 5 && largestHole < 0.025 && timeline.Lost == 0
                  && framesIn - framesOut < AudioTimeline.ChunkFrames + 2000;
        Console.WriteLine($"   => {(ok ? "OK" : "NOT OK")}");
        return ok ? 0 : 1;
    }

    /// <summary>One second of the tone as a WAV file in memory (a whole number of periods, so that it loops cleanly).</summary>
    private static byte[] ToneWave()
    {
        const int rate = 48000;
        var wave = new byte[44 + rate * 4];
        using var w = new BinaryWriter(new MemoryStream(wave));
        w.Write("RIFF"u8); w.Write(36 + rate * 4); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(rate); w.Write(rate * 4); w.Write((short)4); w.Write((short)16);
        w.Write("data"u8); w.Write(rate * 4);
        double amplitude = 32767 * Math.Pow(10, LevelDb / 20);
        for (int i = 0; i < rate; i++)
        {
            short sample = (short)Math.Round(amplitude * Math.Sin(2 * Math.PI * ToneHz * i / rate));
            w.Write(sample);
            w.Write(sample);
        }
        return wave;
    }
}
