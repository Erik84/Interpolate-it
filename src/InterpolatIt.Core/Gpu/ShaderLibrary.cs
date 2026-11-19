using System.Collections.Concurrent;
using System.Reflection;
using Vortice.D3DCompiler;
using Vortice.Direct3D;

namespace InterpolatIt.Core.Gpu;

/// <summary>Compiles the HLSL sources embedded in this assembly (Shaders/*.hlsl) with the system d3dcompiler.</summary>
public static class ShaderLibrary
{
    private static readonly ConcurrentDictionary<string, byte[]> Cache = new();

    public static byte[] Compile(string file, string entryPoint, string profile)
        => Cache.GetOrAdd($"{file}|{entryPoint}|{profile}", _ => CompileCore(file, entryPoint, profile));

    private static byte[] CompileCore(string file, string entryPoint, string profile)
        => CompileSource(LoadSource(file), file, entryPoint, profile);

    /// <summary>Compiles HLSL given as text (used by the test tools for their own shaders).</summary>
    public static byte[] CompileSource(string source, string name, string entryPoint, string profile)
    {
        var result = Compiler.Compile(source, null!, null!, entryPoint, name, profile,
            ShaderFlags.OptimizationLevel3, EffectFlags.None, out Blob blob, out Blob errors);
        using (errors)
        using (blob)
        {
            if (result.Failure || blob is null)
                throw new InvalidOperationException($"HLSL compile failed for {name}:{entryPoint} ({profile}):\n{errors?.AsString()}");
            return blob.AsBytes();
        }
    }

    public static string LoadSource(string file)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream($"Shaders/{file}")
            ?? throw new FileNotFoundException($"Embedded shader 'Shaders/{file}' not found. Available: "
                                               + string.Join(", ", asm.GetManifestResourceNames()));
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
