using System.Text.Json;

namespace InterpolatIt.Core.Inference;

/// <summary>One exported flow network (see tools/export/export_rife_flow.py).</summary>
/// <param name="MinTimestep">
/// The network puts motion where the timestep says for t in [MinTimestep, 1 - MinTimestep]; nearer to a frame it
/// returns (almost) that frame. Measured with tools/export/measure_timestep_linearity.py.
/// </param>
public sealed record ModelInfo(string Id, string Version, string File, string Precision, int Multiple, string Path, double MinTimestep)
{
    public override string ToString() => $"{Id} ({Precision})";

    /// <summary>
    /// Measured on a 20 px move at 1216x704: RIFE 4.6 is within a pixel of linear from 0.05 to 0.95, for a
    /// panning texture as well as a flat bar. RIFE 4.25 is linear from 0.10 to 0.90 on the panning texture but
    /// keeps a flat object on the nearest frame up to 0.15 (and is a pixel off up to 0.4).
    /// </summary>
    public static double DefaultMinTimestep(string version) => version == "4.6" ? 0.05 : 0.10;
}

/// <summary>Reads models/manifest.json and resolves model files.</summary>
public sealed class ModelRegistry
{
    public string Directory { get; }
    public IReadOnlyList<ModelInfo> Models { get; }

    private ModelRegistry(string directory, IReadOnlyList<ModelInfo> models)
    {
        Directory = directory;
        Models = models;
    }

    public static ModelRegistry Load(string? directory = null)
    {
        directory ??= LocateModelsDirectory();
        string manifest = System.IO.Path.Combine(directory, "manifest.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
        var models = new List<ModelInfo>();
        foreach (var m in doc.RootElement.GetProperty("models").EnumerateArray())
        {
            string file = m.GetProperty("file").GetString()!;
            string path = System.IO.Path.Combine(directory, file);
            if (!File.Exists(path)) continue;
            string version = m.GetProperty("version").GetString()!;
            double minTimestep = m.TryGetProperty("timestep_min", out var tm) && tm.TryGetDouble(out double v) && v > 0 && v < 0.5
                ? v : ModelInfo.DefaultMinTimestep(version);
            models.Add(new ModelInfo(
                m.GetProperty("id").GetString()!, version, file,
                m.GetProperty("precision").GetString()!, m.GetProperty("multiple").GetInt32(), path, minTimestep));
        }
        if (models.Count == 0)
            throw new FileNotFoundException($"No models listed in {manifest}. Run tools\\export\\export_rife_flow.py first.");
        return new ModelRegistry(directory, models);
    }

    public IEnumerable<string> Ids => Models.Select(m => m.Id).Distinct();

    public ModelInfo Find(string id, string precision)
        => Models.FirstOrDefault(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase) && m.Precision == precision)
           ?? throw new KeyNotFoundException($"Model '{id}' with precision {precision} is not in {Directory}.");

    /// <summary>
    /// The model to use when timesteps can fall anywhere between two frames (a cadence that does not divide the
    /// display's rate, or a free-running source): the one with the widest linear range, or
    /// <paramref name="chosen"/> itself when nothing does better.
    /// </summary>
    public ModelInfo ForAnyTimestep(ModelInfo chosen)
    {
        ModelInfo best = chosen;
        foreach (var m in Models)
            if (m.Precision == chosen.Precision && m.Multiple == chosen.Multiple && m.MinTimestep < best.MinTimestep - 1e-9)
                best = m;
        return best;
    }

    /// <summary>
    /// INTERPOLATIT_MODELS, then a "models" folder next to the executable, then the same folder in any parent
    /// directory (so builds under artifacts\ find the repository's models\).
    /// </summary>
    public static string LocateModelsDirectory()
    {
        string? env = Environment.GetEnvironmentVariable("INTERPOLATIT_MODELS");
        if (!string.IsNullOrEmpty(env) && File.Exists(System.IO.Path.Combine(env, "manifest.json"))) return env;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = System.IO.Path.Combine(dir.FullName, "models");
            if (File.Exists(System.IO.Path.Combine(candidate, "manifest.json"))) return candidate;
        }
        throw new DirectoryNotFoundException(
            "models\\manifest.json not found next to the application or in a parent folder. " +
            "Run tools\\export\\export_rife_flow.py, or set INTERPOLATIT_MODELS.");
    }
}
