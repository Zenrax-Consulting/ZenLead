namespace ZenLead.Application.Analytics;

public static class RateMath
{
    /// <summary>
    /// <paramref name="numerator"/> / <paramref name="denominator"/>, or null when there is nothing to divide by (never 0, never NaN).
    /// Clamped to [0, 1]: a late or duplicated event can never push a displayed rate above 100 %.
    /// </summary>
    public static double? Rate(int numerator, int denominator)
        => denominator <= 0 ? null : Math.Clamp((double)numerator / denominator, 0d, 1d);
}
