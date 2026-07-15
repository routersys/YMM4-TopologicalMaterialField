namespace TopologicalMaterialField;

internal static class TopologicalMaterialSettings
{
    public static QualitySettings GetQuality(TopologicalMaterialQuality quality)
        => quality switch
        {
            TopologicalMaterialQuality.Balanced => new QualitySettings(16, 24, 32),
            TopologicalMaterialQuality.Ultra => new QualitySettings(48, 64, 80),
            _ => new QualitySettings(32, 40, 48),
        };

    public static int CeilLog2(int value)
    {
        if (value <= 1)
            return 0;

        var result = 0;
        var remaining = value - 1;
        while (remaining > 0)
        {
            remaining >>= 1;
            result++;
        }
        return result;
    }

    internal readonly record struct QualitySettings(int RegionSamples, int ReactionIterations, int PoissonIterations);
}
