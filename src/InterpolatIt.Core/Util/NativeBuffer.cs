using System.Runtime.InteropServices;

namespace InterpolatIt.Core.Util;

/// <summary>
/// 64-byte aligned unmanaged memory with a stable address. Tensors that ONNX Runtime binds once and reuses every
/// frame live here, so the hot path never allocates and the GC never moves them.
/// </summary>
public sealed unsafe class NativeBuffer : IDisposable
{
    public nint Pointer { get; private set; }
    public long ByteLength { get; }

    public NativeBuffer(long byteLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteLength);
        ByteLength = byteLength;
        Pointer = (nint)NativeMemory.AlignedAlloc((nuint)byteLength, 64);
        NativeMemory.Clear((void*)Pointer, (nuint)byteLength);
    }

    public Span<T> AsSpan<T>() where T : unmanaged => new((void*)Pointer, checked((int)(ByteLength / sizeof(T))));

    public void Dispose()
    {
        if (Pointer != 0)
        {
            NativeMemory.AlignedFree((void*)Pointer);
            Pointer = 0;
        }
        GC.SuppressFinalize(this);
    }

    ~NativeBuffer() => Dispose();
}
