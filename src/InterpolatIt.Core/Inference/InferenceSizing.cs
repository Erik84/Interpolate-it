namespace InterpolatIt.Core.Inference;

/// <summary>
/// Chooses the resolution at which motion is estimated. The picture itself is always composed at window
/// resolution; this only decides how fine the flow field is and how long an inference takes.
/// </summary>
public static class InferenceSizing
{
    /// <summary>Pixel budgets the governor steps through, smallest first (about 0.25 / 0.37 / 0.90 / 2.1 MP).</summary>
    public static readonly int[] Tiers = [640 * 384, 832 * 448, 1280 * 704, 1920 * 1088];

    /// <summary>
    /// Width and height that are multiples of <paramref name="multiple"/>, keep the window's aspect ratio as
    /// closely as that allows, do not exceed the window itself, and stay within <paramref name="maxPixels"/>.
    /// </summary>
    public static (int Width, int Height) For(int contentWidth, int contentHeight, int maxPixels, int multiple = 64)
    {
        if (contentWidth <= 0 || contentHeight <= 0) throw new ArgumentOutOfRangeException(nameof(contentWidth));
        double scale = Math.Min(1.0, Math.Sqrt((double)maxPixels / ((double)contentWidth * contentHeight)));
        int w = Round(contentWidth * scale, multiple), h = Round(contentHeight * scale, multiple);
        // Rounding up both axes can overshoot the budget (or the window): take the excess off the longer axis.
        while ((long)w * h > maxPixels * 1.06 && (w > 2 * multiple || h > 2 * multiple))
        {
            if (w >= h && w > 2 * multiple) w -= multiple;
            else h -= multiple;
        }
        return (w, h);
    }

    private static int Round(double value, int multiple)
        => Math.Max(2 * multiple, (int)Math.Round(value / multiple) * multiple);

    /// <summary>The largest tier that is not larger than <paramref name="maxPixels"/> (at least the smallest).</summary>
    public static int TierAtOrBelow(int maxPixels)
    {
        int best = Tiers[0];
        foreach (int tier in Tiers)
            if (tier <= maxPixels) best = tier;
        return best;
    }

    /// <summary>The next smaller tier, or the same when already at the smallest.</summary>
    public static int Lower(int pixels)
    {
        int result = Tiers[0];
        foreach (int tier in Tiers)
            if (tier < pixels) result = tier;
        return result;
    }
}
