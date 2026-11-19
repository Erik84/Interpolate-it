using Microsoft.ML.OnnxRuntime;
using Microsoft.Windows.AI.MachineLearning;

namespace InterpolatIt.Core.Inference;

public enum BackendKind { Cpu, DirectML, NvTensorRtRtx, AmdGpu, OpenVino, Other }

/// <summary>An ONNX Runtime execution provider bound to one hardware device: something a session can run on.</summary>
public sealed record InferenceBackend(BackendKind Kind, string EpName, string DeviceName, uint VendorId, uint DeviceId,
    OrtEpDevice Device)
{
    public bool IsGpu => Device.HardwareDevice.Type == OrtHardwareDeviceType.GPU;

    /// <summary>GPU backends run the fp16 network; the CPU runs fp32 (same fp16 tensors at the boundary).</summary>
    public string Precision => Kind == BackendKind.Cpu ? "fp32" : "fp16";

    public string Id => Kind switch
    {
        BackendKind.Cpu => "cpu",
        BackendKind.DirectML => "dml",
        BackendKind.NvTensorRtRtx => "nv",
        BackendKind.AmdGpu => "amd",
        BackendKind.OpenVino => "openvino",
        _ => EpName,
    };

    /// <summary>What to call it in a user interface.</summary>
    public string DisplayName => Kind switch
    {
        BackendKind.Cpu => $"CPU ({DeviceName})",
        BackendKind.DirectML => $"DirectML on {DeviceName}",
        BackendKind.NvTensorRtRtx => $"TensorRT for RTX on {DeviceName}",
        BackendKind.AmdGpu => $"AMD GPU provider on {DeviceName}",
        BackendKind.OpenVino => $"OpenVINO on {DeviceName}",
        _ => $"{EpName} on {DeviceName}",
    };

    public override string ToString() => $"{Id}: {EpName} on {DeviceName}";
}

/// <summary>A provider known to the Windows ML catalog (may still have to be downloaded).</summary>
public sealed record CatalogProvider(string Name, string ReadyState, string Certification, string LibraryPath);

/// <summary>
/// Discovers inference backends. CPU and DirectML ship inside Windows ML; vendor providers (NVIDIA TensorRT for
/// RTX, AMD GPU, OpenVINO) are acquired by Windows through the execution-provider catalog and then registered
/// with ONNX Runtime in this process.
/// </summary>
public static class EpCatalog
{
    public const string NvidiaProvider = "NvTensorRtRtxExecutionProvider";

    public static IReadOnlyList<CatalogProvider> ListCatalogProviders()
    {
        var catalog = ExecutionProviderCatalog.GetDefault();
        if (catalog is null) return [];
        return [.. catalog.FindAllProviders().Select(p =>
            new CatalogProvider(p.Name, p.ReadyState.ToString(), p.Certification.ToString(), p.LibraryPath ?? ""))];
    }

    /// <summary>
    /// Makes the named catalog provider usable in this process: downloads it if needed (this is the first-run
    /// download) and registers it with ONNX Runtime. Returns a description of what happened.
    /// </summary>
    public static async Task<(bool Ok, string Detail)> EnsureProviderAsync(string name, Action<double>? progress = null)
    {
        var catalog = ExecutionProviderCatalog.GetDefault();
        if (catalog is null) return (false, "the Windows ML execution-provider catalog is not available");
        var provider = catalog.FindAllProviders()
            .FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (provider is null) return (false, $"'{name}' is not offered for this hardware");

        if (provider.ReadyState != ExecutionProviderReadyState.Ready)
        {
            var operation = provider.EnsureReadyAsync();
            if (progress is not null) operation.Progress = (_, value) => progress(value);
            var result = await operation;
            if (result.Status != ExecutionProviderReadyResultState.Success)
                return (false, $"{result.Status}: {result.DiagnosticText} (0x{result.ExtendedError?.HResult ?? 0:X8})");
        }
        if (!provider.TryRegister())
            return (false, "the provider is present but could not be registered with ONNX Runtime");
        return (true, $"registered from {provider.LibraryPath}");
    }

    /// <summary>Every (provider, device) pair ONNX Runtime can currently run on, best first.</summary>
    public static IReadOnlyList<InferenceBackend> GetBackends()
    {
        var list = new List<InferenceBackend>();
        foreach (var device in OrtEnv.Instance().GetEpDevices())
        {
            var hw = device.HardwareDevice;
            list.Add(new InferenceBackend(Classify(device.EpName), device.EpName, DescribeDevice(device), hw.VendorId, hw.DeviceId, device));
        }
        return [.. list.OrderBy(b => Rank(b))];
    }

    private static int Rank(InferenceBackend b) => b.Kind switch
    {
        BackendKind.NvTensorRtRtx => 0,
        BackendKind.AmdGpu => 1,
        BackendKind.OpenVino when b.IsGpu => 2,
        BackendKind.DirectML => 3,
        BackendKind.Cpu => 9,
        _ => 5,
    };

    private static BackendKind Classify(string epName)
    {
        if (epName.Equals("CPUExecutionProvider", StringComparison.OrdinalIgnoreCase)) return BackendKind.Cpu;
        if (epName.Equals("DmlExecutionProvider", StringComparison.OrdinalIgnoreCase)) return BackendKind.DirectML;
        if (epName.Contains("NvTensorRtRtx", StringComparison.OrdinalIgnoreCase)) return BackendKind.NvTensorRtRtx;
        if (epName.Contains("AMDGPU", StringComparison.OrdinalIgnoreCase)) return BackendKind.AmdGpu;
        if (epName.Contains("OpenVINO", StringComparison.OrdinalIgnoreCase)) return BackendKind.OpenVino;
        return BackendKind.Other;
    }

    private static string DescribeDevice(OrtEpDevice device)
    {
        var hw = device.HardwareDevice;
        foreach (string key in new[] { "Description", "description", "DxgiAdapterDescription", "Name" })
            if (hw.Metadata.Entries.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value))
                return value.Trim();
        return $"{hw.Vendor} {hw.Type} {hw.DeviceId:X4}";
    }
}
