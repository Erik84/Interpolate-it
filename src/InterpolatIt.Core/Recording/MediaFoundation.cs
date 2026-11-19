using System.Runtime.InteropServices;

namespace InterpolatIt.Core.Recording;

/// <summary>
/// The few pieces of Media Foundation the recorder uses: the sink writer (encoder + MP4 file) and what it is fed
/// with. Objects are plain COM pointers and their methods are called by their place in the interface (the
/// numbers are those of mfobjects.h and mfreadwrite.h, counted from IUnknown's three).
/// </summary>
internal static unsafe class Mf
{
    // ---- attribute keys and values (mfapi.h, mfreadwrite.h, codecapi.h) -----------------------------------------
    public static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid AllSamplesIndependent = new("c9173739-5e56-461c-b713-46fb995cb95f");
    public static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    public static readonly Guid FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    public static readonly Guid PixelAspectRatio = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    public static readonly Guid InterlaceMode = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    public static readonly Guid AverageBitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    public static readonly Guid Mpeg2Profile = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    public static readonly Guid VideoPrimaries = new("dbfbe4d7-0740-4ee0-8192-850ab0e21935");
    public static readonly Guid TransferFunction = new("5fb0fce9-be5c-4935-a811-ec838f8eed93");
    public static readonly Guid YuvMatrix = new("3e23d450-2c75-4d25-a00e-b91670d12327");
    public static readonly Guid VideoNominalRange = new("c21b8ee5-b956-4071-8daf-325edf5cab11");
    public static readonly Guid AudioChannels = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    public static readonly Guid AudioSamplesPerSecond = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    public static readonly Guid AudioAverageBytesPerSecond = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");
    public static readonly Guid AudioBlockAlignment = new("322de230-9eeb-43bd-ab7a-ff412251541d");
    public static readonly Guid AudioBitsPerSample = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    public static readonly Guid AacPayloadType = new("bfbabe79-7434-4d1c-94f0-72a3b9e17188");
    public static readonly Guid AacProfileLevel = new("7632f0e6-9538-4d61-acda-ea29c8c14456");

    public static readonly Guid EnableHardwareTransforms = new("a634a91c-822b-41b9-a494-4de4643612b0");
    public static readonly Guid DisableThrottling = new("08b845d8-2b74-4afe-9d53-be16d2d5ae4f");
    public static readonly Guid SinkWriterD3DManager = new("ec822da2-e1e9-4b29-a0d8-563c719f5269");
    public static readonly Guid LowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    public static readonly Guid ContainerType = new("150ff23f-4abc-478b-ac4f-e1916fba1cca");
    public static readonly Guid ContainerMpeg4 = new("dc6cd05d-b9d0-40ef-bd35-fa622c1ab28a");
    public static readonly Guid EncoderGopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");

    public static readonly Guid Video = new("73646976-0000-0010-8000-00AA00389B71");
    public static readonly Guid Audio = new("73647561-0000-0010-8000-00AA00389B71");
    public static readonly Guid Nv12 = new("3231564E-0000-0010-8000-00AA00389B71");
    public static readonly Guid Rgb32 = new("00000016-0000-0010-8000-00AA00389B71");
    public static readonly Guid H264 = new("34363248-0000-0010-8000-00AA00389B71");
    public static readonly Guid Hevc = new("43564548-0000-0010-8000-00AA00389B71");
    public static readonly Guid Pcm = new("00000001-0000-0010-8000-00AA00389B71");
    public static readonly Guid Aac = new("00001610-0000-0010-8000-00AA00389B71");

    public static readonly Guid IidTexture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    public static readonly Guid IidSample = new("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4");
    public static readonly Guid Iid2DBuffer = new("7DC9D5F9-9ED9-44ec-9BBF-0600BB589FBB");

    public const uint Progressive = 2;                          // MFVideoInterlace_Progressive
    public const uint PrimariesBt709 = 2, TransferBt709 = 5, MatrixBt709 = 1, Range16To235 = 2;
    public const uint H264High = 100, HevcMain = 1;

    /// <summary>What Media Foundation calls when the last user of a sample has let go of it.</summary>
    [ComImport, Guid("a27003cf-2354-4f2a-8d6a-ab7cff15437e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFAsyncCallback
    {
        [PreserveSig] int GetParameters(out uint flags, out uint queue);
        [PreserveSig] int Invoke(nint asyncResult);
    }

    // ---- functions ----------------------------------------------------------------------------------------------
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll", ExactSpelling = true)] public static extern int MFCreateAttributes(out nint attributes, uint initialSize);
    [DllImport("mfplat.dll", ExactSpelling = true)] public static extern int MFCreateMediaType(out nint mediaType);
    [DllImport("mfplat.dll", ExactSpelling = true)] public static extern int MFCreateSample(out nint sample);
    [DllImport("mfplat.dll", ExactSpelling = true)] public static extern int MFCreateTrackedSample(out nint trackedSample);
    [DllImport("mfplat.dll", ExactSpelling = true)] public static extern int MFCreateMemoryBuffer(uint maxLength, out nint buffer);
    [DllImport("mfplat.dll", ExactSpelling = true)] public static extern int MFCreateDXGISurfaceBuffer(in Guid iid, nint surface, uint subresource, int bottomUpWhenLinear, out nint buffer);
    [DllImport("mfplat.dll", ExactSpelling = true)] public static extern int MFCreateDXGIDeviceManager(out uint resetToken, out nint manager);
    [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern int MFCreateSinkWriterFromURL(string outputUrl, nint byteStream, nint attributes, out nint sinkWriter);

    private static readonly Lazy<int> Started = new(() => MFStartup(0x00020070, 0));       // MF_VERSION, MFSTARTUP_FULL

    /// <summary>Starts Media Foundation for this process (once; it stays until the process ends).</summary>
    public static void Startup() => Check(Started.Value, "start Media Foundation");

    public static void Check(int hr, string what)
    {
        if (hr < 0) throw new COMException($"Windows could not {what} (0x{hr:X8})", hr);
    }

    public static void Release(ref nint pointer)
    {
        if (pointer != 0) Marshal.Release(pointer);
        pointer = 0;
    }

    private static void** Methods(nint instance) => *(void***)instance;

    // ---- IMFAttributes (also the first part of IMFMediaType and IMFSample) -------------------------------------
    public static void SetUInt32(nint attributes, Guid key, uint value)
        => Check(((delegate* unmanaged[Stdcall]<nint, Guid*, uint, int>)Methods(attributes)[21])(attributes, &key, value), "set a property");

    public static void SetUInt64(nint attributes, Guid key, ulong value)
        => Check(((delegate* unmanaged[Stdcall]<nint, Guid*, ulong, int>)Methods(attributes)[22])(attributes, &key, value), "set a property");

    /// <summary>Two numbers in one value, as Media Foundation stores a size or a ratio.</summary>
    public static void SetPair(nint attributes, Guid key, uint high, uint low) => SetUInt64(attributes, key, ((ulong)high << 32) | low);

    public static void SetGuid(nint attributes, Guid key, Guid value)
        => Check(((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Methods(attributes)[24])(attributes, &key, &value), "set a property");

    public static void SetUnknown(nint attributes, Guid key, nint unknown)
        => Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint, int>)Methods(attributes)[27])(attributes, &key, unknown), "set a property");

    // ---- IMFSample ----------------------------------------------------------------------------------------------
    public static void SetSampleTime(nint sample, long time)
        => Check(((delegate* unmanaged[Stdcall]<nint, long, int>)Methods(sample)[36])(sample, time), "time a sample");

    public static void SetSampleDuration(nint sample, long duration)
        => Check(((delegate* unmanaged[Stdcall]<nint, long, int>)Methods(sample)[38])(sample, duration), "time a sample");

    public static void AddBuffer(nint sample, nint buffer)
        => Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Methods(sample)[42])(sample, buffer), "fill a sample");

    // ---- IMFMediaBuffer, IMF2DBuffer ----------------------------------------------------------------------------
    public static byte* Lock(nint buffer)
    {
        byte* data;
        Check(((delegate* unmanaged[Stdcall]<nint, byte**, uint*, uint*, int>)Methods(buffer)[3])(buffer, &data, null, null), "write to a buffer");
        return data;
    }

    public static void Unlock(nint buffer)
        => Check(((delegate* unmanaged[Stdcall]<nint, int>)Methods(buffer)[4])(buffer), "write to a buffer");

    public static void SetCurrentLength(nint buffer, uint length)
        => Check(((delegate* unmanaged[Stdcall]<nint, uint, int>)Methods(buffer)[6])(buffer, length), "size a buffer");

    /// <summary>The number of bytes a picture in a surface buffer stands for.</summary>
    public static uint ContiguousLength(nint buffer)
    {
        Check(Marshal.QueryInterface(buffer, in Iid2DBuffer, out nint buffer2D), "measure a picture");
        try
        {
            uint length;
            Check(((delegate* unmanaged[Stdcall]<nint, uint*, int>)Methods(buffer2D)[7])(buffer2D, &length), "measure a picture");
            return length;
        }
        finally
        {
            Marshal.Release(buffer2D);
        }
    }

    // ---- IMFDXGIDeviceManager, IMFTrackedSample -----------------------------------------------------------------
    public static void ResetDevice(nint manager, nint device, uint resetToken)
        => Check(((delegate* unmanaged[Stdcall]<nint, nint, uint, int>)Methods(manager)[7])(manager, device, resetToken), "hand the graphics device to the encoder");

    public static void SetAllocator(nint trackedSample, nint callback)
        => Check(((delegate* unmanaged[Stdcall]<nint, nint, nint, int>)Methods(trackedSample)[3])(trackedSample, callback, 0), "track a sample");

    // ---- IMFSinkWriter (these return the result: a refusal here is an answer, not an accident) ------------------
    public static int AddStream(nint writer, nint targetType, out uint stream)
    {
        uint index;
        int hr = ((delegate* unmanaged[Stdcall]<nint, nint, uint*, int>)Methods(writer)[3])(writer, targetType, &index);
        stream = index;
        return hr;
    }

    public static int SetInputMediaType(nint writer, uint stream, nint inputType, nint encodingParameters)
        => ((delegate* unmanaged[Stdcall]<nint, uint, nint, nint, int>)Methods(writer)[4])(writer, stream, inputType, encodingParameters);

    public static int BeginWriting(nint writer) => ((delegate* unmanaged[Stdcall]<nint, int>)Methods(writer)[5])(writer);

    public static int WriteSample(nint writer, uint stream, nint sample)
        => ((delegate* unmanaged[Stdcall]<nint, uint, nint, int>)Methods(writer)[6])(writer, stream, sample);

    public static int Finalize(nint writer) => ((delegate* unmanaged[Stdcall]<nint, int>)Methods(writer)[11])(writer);
}
