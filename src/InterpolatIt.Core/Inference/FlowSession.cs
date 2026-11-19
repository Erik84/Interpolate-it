using System.Diagnostics;
using InterpolatIt.Core.Util;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace InterpolatIt.Core.Inference;

public sealed class FlowSessionOptions
{
    /// <summary>Folder for provider caches (TensorRT-RTX runtime cache). Created on demand.</summary>
    public string? CacheDirectory { get; init; }

    /// <summary>Fail session creation instead of silently running some nodes on the CPU (GPU backends only).</summary>
    public bool StrictGpu { get; init; }

    /// <summary>Intra-op threads for the CPU backend; 0 lets ONNX Runtime decide.</summary>
    public int CpuThreads { get; init; }

    /// <summary>When set, ONNX Runtime profiling is written to files starting with this path (diagnostics only).</summary>
    public string? ProfilePrefix { get; init; }

    /// <summary>
    /// Register DirectML through the classic device-index API instead of the EP-device API (diagnostics only).
    /// </summary>
    public bool LegacyDmlRegistration { get; init; }
}

/// <summary>
/// One static-shape flow-network session with all tensors in fixed native buffers.
/// <para>
/// Usage per captured frame: write the frame's planar RGB fp16 tensor to <see cref="NewFrameInput"/>, call
/// <see cref="SubmitFrame()"/>, then <see cref="Run"/> once per timestep; results appear in <see cref="Flow"/> and
/// <see cref="MaskLogit"/>. The submitted frame automatically becomes the older frame of the next pair.
/// </para>
/// <para>
/// Why "submit" exists: for GPU providers ONNX Runtime copies a bound CPU tensor to the device when it is
/// <i>bound</i>, not when the session runs. Writing into an already bound buffer therefore changes nothing on
/// the GPU (the CPU provider, which reads the buffer directly, would hide that mistake). Submitting re-binds,
/// which is the upload — once per captured frame, however many timesteps are run on the pair.
/// </para>
/// </summary>
public sealed unsafe class FlowSession : IDisposable
{
    private readonly InferenceSession _session;
    private readonly OrtIoBinding _binding;
    private readonly RunOptions _runOptions = new();
    private readonly NativeBuffer _imageA, _imageB, _timestep, _flow, _mask;
    private readonly OrtValue _valueA, _valueB, _valueT, _valueFlow, _valueMask;
    private bool _newFrameIsB = true;

    public InferenceBackend Backend { get; }
    public ModelInfo Model { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>Wall time spent creating the session, including any provider-side compilation.</summary>
    public double CreateMilliseconds { get; }

    /// <summary>Frames submitted so far; a pair exists from the second one.</summary>
    public long FramesSubmitted { get; private set; }

    /// <summary>
    /// Destination for the next frame: 3 x H x W fp16, planar RGB in [0, 1]. It is the buffer of the frame
    /// before the previous one, so only write here once all runs on the current pair have finished.
    /// </summary>
    public nint NewFrameInput => _newFrameIsB ? _imageB.Pointer : _imageA.Pointer;

    /// <summary>4 x H x W fp16: flow t->0 (x, y) and t->1 (x, y) in inference pixels.</summary>
    public nint Flow => _flow.Pointer;

    /// <summary>H x W fp16: blend weight of the older frame before the sigmoid.</summary>
    public nint MaskLogit => _mask.Pointer;

    public long ImageBytes => _imageA.ByteLength;

    private FlowSession(InferenceBackend backend, ModelInfo model, int width, int height, FlowSessionOptions options)
    {
        Backend = backend;
        Model = model;
        Width = width;
        Height = height;

        long pixels = (long)width * height;
        _imageA = new NativeBuffer(3 * pixels * sizeof(ushort));
        _imageB = new NativeBuffer(3 * pixels * sizeof(ushort));
        _timestep = new NativeBuffer(sizeof(ushort));
        _flow = new NativeBuffer(4 * pixels * sizeof(ushort));
        _mask = new NativeBuffer(pixels * sizeof(ushort));

        long start = Stopwatch.GetTimestamp();
        using (var so = BuildOptions(backend, width, height, options))
            _session = new InferenceSession(model.Path, so);

        var cpu = OrtMemoryInfo.DefaultInstance;
        _valueA = OrtValue.CreateTensorValueWithData(cpu, TensorElementType.Float16, [1, 3, height, width], _imageA.Pointer, _imageA.ByteLength);
        _valueB = OrtValue.CreateTensorValueWithData(cpu, TensorElementType.Float16, [1, 3, height, width], _imageB.Pointer, _imageB.ByteLength);
        _valueT = OrtValue.CreateTensorValueWithData(cpu, TensorElementType.Float16, [1, 1, 1, 1], _timestep.Pointer, _timestep.ByteLength);
        _valueFlow = OrtValue.CreateTensorValueWithData(cpu, TensorElementType.Float16, [1, 4, height, width], _flow.Pointer, _flow.ByteLength);
        _valueMask = OrtValue.CreateTensorValueWithData(cpu, TensorElementType.Float16, [1, 1, height, width], _mask.Pointer, _mask.ByteLength);

        _binding = _session.CreateIoBinding();
        _binding.BindOutput("flow", _valueFlow);
        _binding.BindOutput("mask_logit", _valueMask);
        _binding.BindInput("img0", _valueA);
        _binding.BindInput("img1", _valueB);

        // The first run performs any deferred provider-side work; count it as part of creation.
        Run(0.5f);
        CreateMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    public static FlowSession Create(InferenceBackend backend, ModelInfo model, int width, int height,
        FlowSessionOptions? options = null)
    {
        if (width % model.Multiple != 0 || height % model.Multiple != 0)
            throw new ArgumentException($"{model.Id} needs sizes that are multiples of {model.Multiple}; got {width}x{height}.");
        if (model.Precision != backend.Precision)
            throw new ArgumentException($"{backend.Id} runs the {backend.Precision} network, not {model.Precision}.");
        return new FlowSession(backend, model, width, height, options ?? new FlowSessionOptions());
    }

    private static SessionOptions BuildOptions(InferenceBackend backend, int width, int height, FlowSessionOptions options)
    {
        var so = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
        };
        // Static shapes: the whole shape-dependent part of the graph (sampling grid, resize sizes) constant-folds
        // and GPU providers compile exactly one specialised engine per resolution bucket.
        so.AddFreeDimensionOverrideByName("height", height);
        so.AddFreeDimensionOverrideByName("width", width);

        var env = OrtEnv.Instance();
        Dictionary<string, string>? epOptions = null;
        switch (backend.Kind)
        {
            case BackendKind.Cpu:
                if (options.CpuThreads > 0) so.IntraOpNumThreads = options.CpuThreads;
                break;
            case BackendKind.DirectML:
                so.EnableMemoryPattern = false;
                so.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                break;
            case BackendKind.NvTensorRtRtx:
                if (options.CacheDirectory is not null)
                {
                    string dir = Path.Combine(options.CacheDirectory, "nv-trt-rtx");
                    Directory.CreateDirectory(dir);
                    epOptions = new Dictionary<string, string> { ["nv_runtime_cache_path"] = dir };
                }
                break;
        }
        if (backend.IsGpu)
        {
            so.IntraOpNumThreads = 1;
            so.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
            if (options.StrictGpu) so.AddSessionConfigEntry("session.disable_cpu_ep_fallback", "1");
        }
        if (options.ProfilePrefix is not null)
        {
            so.EnableProfiling = true;
            so.ProfileOutputPathPrefix = options.ProfilePrefix;
        }
        if (backend.Kind == BackendKind.DirectML && options.LegacyDmlRegistration)
            so.AppendExecutionProvider_DML(0);
        else
            so.AppendExecutionProvider(env, [backend.Device], epOptions);
        return so;
    }

    /// <summary>Stops profiling (if enabled) and returns the path of the JSON trace.</summary>
    public string EndProfiling() => _session.EndProfiling();

    /// <summary>
    /// Makes the frame written to <see cref="NewFrameInput"/> the newer frame of the current pair (the
    /// previously submitted frame is the older one) and uploads the pair to the provider.
    /// </summary>
    public void SubmitFrame()
    {
        var newest = _newFrameIsB ? _valueB : _valueA;
        var previous = _newFrameIsB ? _valueA : _valueB;
        _binding.BindInput("img0", previous);   // binding is the device upload for GPU providers
        _binding.BindInput("img1", newest);
        _binding.SynchronizeBoundInputs();
        _newFrameIsB = !_newFrameIsB;
        FramesSubmitted++;
    }

    /// <summary>Copies a planar RGB fp16 tensor from <paramref name="tensor"/> and submits it.</summary>
    public void SubmitFrame(nint tensor)
    {
        Buffer.MemoryCopy((void*)tensor, (void*)NewFrameInput, ImageBytes, ImageBytes);
        SubmitFrame();
    }

    /// <summary>Estimates flow and mask for timestep <paramref name="t"/> in (0, 1) between the current pair.</summary>
    public void Run(float t)
    {
        *(ushort*)_timestep.Pointer = BitConverter.HalfToUInt16Bits((Half)t);
        _binding.BindInput("timestep", _valueT);    // re-bind: the value changed (see class remarks)
        _session.RunWithBinding(_runOptions, _binding);
        _binding.SynchronizeBoundOutputs();
    }

    public void Dispose()
    {
        _binding.Dispose();
        _valueA.Dispose(); _valueB.Dispose(); _valueT.Dispose(); _valueFlow.Dispose(); _valueMask.Dispose();
        _runOptions.Dispose();
        _session.Dispose();
        _imageA.Dispose(); _imageB.Dispose(); _timestep.Dispose(); _flow.Dispose(); _mask.Dispose();
    }
}
