namespace InterpolatIt.Core.Pipeline;

public sealed class EngineOptions
{
    /// <summary>The window to capture and cover.</summary>
    public required nint TargetWindow { get; init; }

    /// <summary>Requested multiplier: 2, 3 or 4. The output snaps to the nearest display-synchronous rate.</summary>
    public int Factor { get; init; } = 2;

    /// <summary>Delay line without AI: every captured frame is shown unchanged a fixed number of refreshes later.
    /// Used to verify capture, overlay alignment and pacing in isolation.</summary>
    public bool Passthrough { get; init; }

    /// <summary>Model id from models/manifest.json.</summary>
    public string ModelId { get; init; } = "rife-v4.25";

    /// <summary>"nv", "dml", "cpu" ... or null for the best available backend.</summary>
    public string? BackendId { get; init; }

    public string? ModelsDirectory { get; init; }

    /// <summary>Per-refresh CSV log of what the presenter did (verification and tuning).</summary>
    public string? PacingLogPath { get; init; }

    /// <summary>Draw the fps readout in the overlay from the start.</summary>
    public bool ShowHud { get; init; }

    /// <summary>Start with interpolation switched on.</summary>
    public bool StartEnabled { get; init; } = true;

    /// <summary>Start with the clean-up of compressed video switched on: every real frame goes through the
    /// graphics driver's video enhancement before the in-between frames are made from it (see
    /// <see cref="Gpu.VideoEnhancer"/>). It can be switched while running.</summary>
    public bool Enhance { get; init; }

    /// <summary>Largest inference resolution in pixels (width x height); the governor never goes above it.</summary>
    public int MaxInferencePixels { get; init; } = 1280 * 704;

    /// <summary>Where inside a refresh interval frames are presented (0 = at the compositor tick, 1 = at the next).</summary>
    public double PresentPhase { get; init; } = 0.5;

    /// <summary>DXGI sync interval for the overlay's presents: 1 (takes effect at the next vblank; deterministic,
    /// the default) or 0 (available to the compositor at once; which composition it lands in then depends on
    /// the compositor's own timing).</summary>
    public int SyncInterval { get; init; } = 1;

    /// <summary>Let the engine correct its present-to-glass lag from DXGI frame statistics.</summary>
    public bool CalibrateLag { get; init; } = true;

    /// <summary>Write the pipeline's decisions (frames accepted or dropped, cadence lock, model switches) to
    /// standard error, one line each. Diagnostics only.</summary>
    public bool TraceEvents { get; init; }

    /// <summary>Diagnostics: write a few in-between frames together with the two real frames they were made
    /// from (PNG) into this folder, once the session has been interpolating for a few seconds.</summary>
    public string? DumpFramesDirectory { get; init; }

    /// <summary>Ask the GPU scheduler to run the engine's work ahead of other applications' (see
    /// <see cref="Gpu.GpuDevice.RaiseSchedulingPriority"/>).</summary>
    public bool RaiseGpuPriority { get; init; } = true;
}

public enum EngineState
{
    Starting,       // creating capture, overlay, model session
    Waiting,        // running, but nothing to show: source idle, cadence still unknown, or target not coverable
    Interpolating,  // generated frames are on screen
    Bypass,         // switched off by the user, or the source already runs at the output rate
    Stopped,
    Failed,
}
