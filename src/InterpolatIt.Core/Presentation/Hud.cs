using InterpolatIt.Core.Gpu;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace InterpolatIt.Core.Presentation;

/// <summary>
/// The optional on-screen readout (detected / target / actual fps). It is a small opaque texture, re-rasterised with a
/// built-in 5x7 bitmap font only when the text changes and copied into a corner of the back buffer, so it costs
/// nothing per frame and needs no text-rendering dependency.
/// </summary>
public sealed unsafe class Hud : IDisposable
{
    private const int GlyphW = 5, GlyphH = 7, Padding = 6;

    public const uint White = 0xFFF2F2F2, Green = 0xFF6BE675, Yellow = 0xFFFFD257, Red = 0xFFFF6B5E, Dim = 0xFFA0A8B4;
    private const uint Background = 0xFF14161A;

    private readonly GpuDevice _gpu;
    private ID3D11Texture2D? _texture;
    private byte[] _pixels = [];
    private int _width, _height;
    private string _key = "";

    public Hud(GpuDevice gpu) => _gpu = gpu;

    /// <summary>
    /// The three lines of the fps readout: the rate detected in the source (its locked rate, or the recent
    /// average of a free-running one), the output rate being aimed for and the rate that reaches the screen
    /// (green when it is there, yellow or red when it falls short).
    /// </summary>
    public static (string Text, uint Color)[] FpsLines(double detected, double target, double actual)
    {
        uint colour = target <= 0 ? Dim : actual >= 0.97 * target ? Green : actual >= 0.90 * target ? Yellow : Red;
        var invariant = System.Globalization.CultureInfo.InvariantCulture;      // the bitmap font has no decimal comma
        return
        [
            (string.Create(invariant, $"DETECTED {detected,6:F1} FPS"), detected > 0 ? White : Dim),
            (string.Create(invariant, $"TARGET   {target,6:F1} FPS"), White),
            (string.Create(invariant, $"ACTUAL   {actual,6:F1} FPS"), colour),
        ];
    }

    /// <summary>
    /// Sets the lines to show (text is upper-cased; unsupported characters render as blanks). Colours are
    /// 0xAARRGGBB. Call under the GPU lock; the texture is only rebuilt when something changed.
    /// </summary>
    public void SetText(ReadOnlySpan<(string Text, uint Color)> lines, int scale)
    {
        scale = Math.Clamp(scale, 1, 6);
        string key = scale.ToString();
        int columns = 0;
        foreach (var (text, color) in lines)
        {
            key += $"|{color:X8}{text}";
            columns = Math.Max(columns, text.Length);
        }
        if (key == _key) return;
        _key = key;

        int cell = (GlyphW + 1) * scale, lineHeight = (GlyphH + 2) * scale;
        int width = columns * cell + 2 * Padding, height = lines.Length * lineHeight + 2 * Padding;
        if (width != _width || height != _height || _texture is null)
        {
            _texture?.Dispose();
            _width = width;
            _height = height;
            _pixels = new byte[width * height * 4];
            var desc = new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1, BindFlags.ShaderResource);
            _texture = _gpu.Device.CreateTexture2D(in desc);
        }

        fixed (byte* p = _pixels)
        {
            new Span<uint>(p, width * height).Fill(Background);
            for (int line = 0; line < lines.Length; line++)
            {
                string text = lines[line].Text.ToUpperInvariant();
                uint color = lines[line].Color;
                int y0 = Padding + line * lineHeight;
                for (int i = 0; i < text.Length; i++)
                {
                    if (!Font.TryGetValue(text[i], out byte[]? glyph)) continue;
                    int x0 = Padding + i * cell;
                    for (int gy = 0; gy < GlyphH; gy++)
                    {
                        for (int gx = 0; gx < GlyphW; gx++)
                        {
                            if ((glyph[gy] & (1 << (GlyphW - 1 - gx))) == 0) continue;
                            for (int sy = 0; sy < scale; sy++)
                            {
                                uint* row = (uint*)p + (y0 + gy * scale + sy) * width + x0 + gx * scale;
                                for (int sx = 0; sx < scale; sx++) row[sx] = color;
                            }
                        }
                    }
                }
            }
            _gpu.Context.UpdateSubresource(_texture, 0, null, (nint)p, (uint)(width * 4), (uint)(width * height * 4));
        }
    }

    /// <summary>Copies the readout into <paramref name="target"/> at (x, y), clipped to its size. Call under the GPU lock.</summary>
    public void Draw(ID3D11Texture2D target, int targetWidth, int targetHeight, int x, int y)
    {
        if (_texture is null || x < 0 || y < 0) return;
        int w = Math.Min(_width, targetWidth - x), h = Math.Min(_height, targetHeight - y);
        if (w <= 0 || h <= 0) return;
        _gpu.Context.CopySubresourceRegion(target, 0, (uint)x, (uint)y, 0, _texture, 0, new Vortice.Mathematics.Box(0, 0, 0, w, h, 1));
    }

    public void Dispose()
    {
        _texture?.Dispose();
        _texture = null;
    }

    private static readonly Dictionary<char, byte[]> Font = BuildFont();

    private static Dictionary<char, byte[]> BuildFont()
    {
        // 5x7 glyphs, one string of 35 cells per character, rows top to bottom.
        var source = new Dictionary<char, string>
        {
            ['0'] = ".###." + "#...#" + "#..##" + "#.#.#" + "##..#" + "#...#" + ".###.",
            ['1'] = "..#.." + ".##.." + "..#.." + "..#.." + "..#.." + "..#.." + ".###.",
            ['2'] = ".###." + "#...#" + "....#" + "...#." + "..#.." + ".#..." + "#####",
            ['3'] = ".###." + "#...#" + "....#" + "..##." + "....#" + "#...#" + ".###.",
            ['4'] = "...#." + "..##." + ".#.#." + "#..#." + "#####" + "...#." + "...#.",
            ['5'] = "#####" + "#...." + "####." + "....#" + "....#" + "#...#" + ".###.",
            ['6'] = "..##." + ".#..." + "#...." + "####." + "#...#" + "#...#" + ".###.",
            ['7'] = "#####" + "....#" + "...#." + "..#.." + ".#..." + ".#..." + ".#...",
            ['8'] = ".###." + "#...#" + "#...#" + ".###." + "#...#" + "#...#" + ".###.",
            ['9'] = ".###." + "#...#" + "#...#" + ".####" + "....#" + "...#." + ".##..",
            ['A'] = ".###." + "#...#" + "#...#" + "#####" + "#...#" + "#...#" + "#...#",
            ['B'] = "####." + "#...#" + "#...#" + "####." + "#...#" + "#...#" + "####.",
            ['C'] = ".###." + "#...#" + "#...." + "#...." + "#...." + "#...#" + ".###.",
            ['D'] = "####." + "#...#" + "#...#" + "#...#" + "#...#" + "#...#" + "####.",
            ['E'] = "#####" + "#...." + "#...." + "####." + "#...." + "#...." + "#####",
            ['F'] = "#####" + "#...." + "#...." + "####." + "#...." + "#...." + "#....",
            ['G'] = ".###." + "#...#" + "#...." + "#.###" + "#...#" + "#...#" + ".###.",
            ['H'] = "#...#" + "#...#" + "#...#" + "#####" + "#...#" + "#...#" + "#...#",
            ['I'] = ".###." + "..#.." + "..#.." + "..#.." + "..#.." + "..#.." + ".###.",
            ['J'] = "..###" + "...#." + "...#." + "...#." + "...#." + "#..#." + ".##..",
            ['K'] = "#...#" + "#..#." + "#.#.." + "##..." + "#.#.." + "#..#." + "#...#",
            ['L'] = "#...." + "#...." + "#...." + "#...." + "#...." + "#...." + "#####",
            ['M'] = "#...#" + "##.##" + "#.#.#" + "#.#.#" + "#...#" + "#...#" + "#...#",
            ['N'] = "#...#" + "##..#" + "#.#.#" + "#..##" + "#...#" + "#...#" + "#...#",
            ['O'] = ".###." + "#...#" + "#...#" + "#...#" + "#...#" + "#...#" + ".###.",
            ['P'] = "####." + "#...#" + "#...#" + "####." + "#...." + "#...." + "#....",
            ['Q'] = ".###." + "#...#" + "#...#" + "#...#" + "#.#.#" + "#..#." + ".##.#",
            ['R'] = "####." + "#...#" + "#...#" + "####." + "#.#.." + "#..#." + "#...#",
            ['S'] = ".####" + "#...." + "#...." + ".###." + "....#" + "....#" + "####.",
            ['T'] = "#####" + "..#.." + "..#.." + "..#.." + "..#.." + "..#.." + "..#..",
            ['U'] = "#...#" + "#...#" + "#...#" + "#...#" + "#...#" + "#...#" + ".###.",
            ['V'] = "#...#" + "#...#" + "#...#" + "#...#" + "#...#" + ".#.#." + "..#..",
            ['W'] = "#...#" + "#...#" + "#...#" + "#.#.#" + "#.#.#" + "##.##" + "#...#",
            ['X'] = "#...#" + "#...#" + ".#.#." + "..#.." + ".#.#." + "#...#" + "#...#",
            ['Y'] = "#...#" + "#...#" + ".#.#." + "..#.." + "..#.." + "..#.." + "..#..",
            ['Z'] = "#####" + "....#" + "...#." + "..#.." + ".#..." + "#...." + "#####",
            ['.'] = "....." + "....." + "....." + "....." + "....." + ".##.." + ".##..",
            [':'] = "....." + ".##.." + ".##.." + "....." + ".##.." + ".##.." + ".....",
            ['-'] = "....." + "....." + "....." + "#####" + "....." + "....." + ".....",
            ['/'] = "....#" + "....#" + "...#." + "..#.." + ".#..." + "#...." + "#....",
            ['%'] = "##..#" + "##.#." + "...#." + "..#.." + ".#..." + ".#.##" + "#..##",
            ['>'] = "#...." + ".#..." + "..#.." + "...#." + "..#.." + ".#..." + "#....",
        };
        var font = new Dictionary<char, byte[]>();
        foreach (var (ch, cells) in source)
        {
            var rows = new byte[GlyphH];
            for (int y = 0; y < GlyphH; y++)
                for (int x = 0; x < GlyphW; x++)
                    if (cells[y * GlyphW + x] == '#') rows[y] |= (byte)(1 << (GlyphW - 1 - x));
            font[ch] = rows;
        }
        return font;
    }
}
