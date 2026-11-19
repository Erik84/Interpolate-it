using InterpolatIt.Core.Capture;
using InterpolatIt.Core.Gpu;
using InterpolatIt.Core.Presentation;
using InterpolatIt.Timing;

namespace InterpolatIt.Core.Pipeline;

/// <summary>State shared by the threads of one running session.</summary>
internal sealed class SessionContext
{
    public required EngineOptions Options { get; init; }
    public required GpuDevice Gpu { get; init; }
    public required OverlayWindow Overlay { get; init; }
    public required WindowTracker Tracker { get; init; }
    public required VBlankClock Clock { get; init; }
    public required FrameScheduler Scheduler { get; init; }
    public required EngineStats Stats { get; init; }
    public PresentQueue Queue { get; } = new();
    public PacingLog? Log { get; init; }
    public CancellationTokenSource Stop { get; } = new();

    /// <summary>Guards <see cref="Clock"/> and <see cref="Scheduler"/>, which both threads use.</summary>
    public Lock Timing { get; } = new();

    /// <summary>Position inside the refresh interval at which frames are presented.</summary>
    public double Phase { get; init; } = 0.5;

    /// <summary>DXGI sync interval used for the overlay's presents.</summary>
    public uint SyncInterval { get; init; }

    /// <summary>How DXGI frame statistics placed recent presents relative to their plan, in refreshes:
    /// index 0 = two early ... index 2 = as planned ... index 4 = two late.</summary>
    public long[] LagHistogram { get; } = new long[5];

    /// <summary>User switch: off = overlay cloaked, nothing generated, capture and model stay warm.</summary>
    public volatile bool Enabled = true;

    /// <summary>User switch: clean up compressed video (the driver's enhancement) before interpolating.</summary>
    public volatile bool Enhance;

    /// <summary>Size of the picture currently being captured; the overlay only shows frames of this size.</summary>
    public volatile int ContentWidth, ContentHeight;

    /// <summary>Latest estimate of the time between source frames, for the idle timeout.</summary>
    public double SourcePeriod = 1.0 / 30;

    /// <summary>Allow the presenter to correct the lag from frame statistics.</summary>
    public bool CalibrateLag { get; init; } = true;

    /// <summary>Planned minus actual refresh of the most recently verified present (0 = lag is right).</summary>
    public volatile int LastLagError;

    /// <summary>The running recording, if any. It gets every picture that reaches the screen: the frames the
    /// overlay presents, and the window's own frames while the overlay has stepped aside.</summary>
    public volatile Recording.Mp4Recorder? Recorder;

    /// <summary>Draws the fps readout into the back buffer; called under the GPU lock just before Present.</summary>
    public Action<WindowSwapChain>? DrawHud { get; set; }

    public string? StopReason { get; private set; }
    public bool Failed { get; private set; }
    public event Action? StopRequested;

    public void RequestStop(string reason, bool failed = false)
    {
        if (Stop.IsCancellationRequested) return;
        StopReason = reason;
        Failed = failed;
        Stats.Message = reason;
        Stats.State = failed ? EngineState.Failed : EngineState.Stopped;
        Stop.Cancel();
        StopRequested?.Invoke();
    }
}
