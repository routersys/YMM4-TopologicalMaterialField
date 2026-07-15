namespace TopologicalMaterialField.Tests;

public sealed class TopologicalMaterialFieldEffectTests
{
    private static double ValueAt(YukkuriMovieMaker.Commons.Animation animation) => animation.GetValue(0, 1, 30);

    [Fact]
    public void DefaultParameterValuesMatchSpecification()
    {
        var effect = new TopologicalMaterialFieldEffect();

        Assert.Equal(100d, ValueAt(effect.Amount), 6);
        Assert.Equal(3d, ValueAt(effect.TopologyScale), 6);
        Assert.Equal(1d, ValueAt(effect.FeatureThreshold), 6);
        Assert.Equal(85d, ValueAt(effect.Distribution), 6);
        Assert.Equal(70d, ValueAt(effect.ColorVariation), 6);
        Assert.Equal(24d, ValueAt(effect.PatternScale), 6);
        Assert.Equal(65d, ValueAt(effect.PatternStrength), 6);
        Assert.Equal(100d, ValueAt(effect.Reconstruction), 6);
        Assert.Equal(75d, ValueAt(effect.Relief), 6);
        Assert.Equal(-35d, ValueAt(effect.LightAngle), 6);
        Assert.Equal(42d, ValueAt(effect.LightElevation), 6);
        Assert.Equal(TopologicalMaterialMode.Ceramic, effect.Material);
        Assert.Equal(TopologicalMaterialQuality.High, effect.Quality);
        Assert.Equal(0, effect.Seed);
    }

    [Theory]
    [InlineData(int.MinValue, 0)]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(1234, 1234)]
    public void SeedClampsNegativeInputToZero(int input, int expected)
    {
        var effect = new TopologicalMaterialFieldEffect { Seed = input };

        Assert.Equal(expected, effect.Seed);
    }

    [Fact]
    public void CreateExoVideoFiltersReturnsEmpty()
    {
        var effect = new TopologicalMaterialFieldEffect();

        Assert.Empty(effect.CreateExoVideoFilters(0, null!));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(4, 2)]
    [InlineData(5, 3)]
    [InlineData(16, 4)]
    [InlineData(17, 5)]
    public void CeilLog2ReturnsIterationCount(int value, int expected)
    {
        Assert.Equal(expected, TopologicalMaterialSettings.CeilLog2(value));
    }

    [Theory]
    [InlineData(TopologicalMaterialQuality.Balanced, 16, 24, 32)]
    [InlineData(TopologicalMaterialQuality.High, 32, 40, 48)]
    [InlineData(TopologicalMaterialQuality.Ultra, 48, 64, 80)]
    public void QualitySettingsMatchSpecification(TopologicalMaterialQuality quality, int samples, int reaction, int poisson)
    {
        var settings = TopologicalMaterialSettings.GetQuality(quality);

        Assert.Equal(samples, settings.RegionSamples);
        Assert.Equal(reaction, settings.ReactionIterations);
        Assert.Equal(poisson, settings.PoissonIterations);
    }

    [Theory]
    [InlineData(TopologicalMaterialMode.Ceramic)]
    [InlineData(TopologicalMaterialMode.Mineral)]
    [InlineData(TopologicalMaterialMode.OxidizedMetal)]
    [InlineData(TopologicalMaterialMode.Parchment)]
    [InlineData(TopologicalMaterialMode.IceCrystal)]
    [InlineData(TopologicalMaterialMode.Textile)]
    public void GpuPipelineIsDeterministicAndPreservesPremultipliedAlpha(TopologicalMaterialMode material)
    {
        using var pipeline = TopologicalMaterialPipeline.TryCreate();
        if (pipeline is null)
        {
            Assert.Skip("Direct3D 12 is unavailable.");
            return;
        }

        const int width = 8;
        const int height = 8;
        var source = new int[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var alpha = x == 0 && y == 0 ? 0 : 96 + (x + y) * 10;
                var red = (x * 35 * alpha + 127) / 255;
                var green = (y * 35 * alpha + 127) / 255;
                var blue = ((x + y) * 17 * alpha + 127) / 255;
                source[y * width + x] = alpha << 24 | red << 16 | green << 8 | blue;
            }
        }

        var first = new int[source.Length];
        var second = new int[source.Length];
        var parameters = CreatePipelineParameters(material);

        pipeline.Process(source, first, width, height, in parameters);
        pipeline.Process(source, second, width, height, in parameters);

        Assert.Equal(first, second);
        for (var index = 0; index < source.Length; index++)
        {
            var expectedAlpha = (source[index] >> 24) & 255;
            var pixel = first[index];
            var alpha = (pixel >> 24) & 255;
            var red = (pixel >> 16) & 255;
            var green = (pixel >> 8) & 255;
            var blue = pixel & 255;
            Assert.Equal(expectedAlpha, alpha);
            Assert.InRange(red, 0, alpha);
            Assert.InRange(green, 0, alpha);
            Assert.InRange(blue, 0, alpha);
        }
    }

    [Fact]
    public void GpuPipelineDoesNotAllocateManagedMemoryAfterWarmup()
    {
        using var pipeline = TopologicalMaterialPipeline.TryCreate();
        if (pipeline is null)
        {
            Assert.Skip("Direct3D 12 is unavailable.");
            return;
        }

        const int width = 8;
        const int height = 8;
        var source = new int[width * height];
        var destination = new int[source.Length];
        var parameters = CreatePipelineParameters(TopologicalMaterialMode.Ceramic);
        pipeline.Process(source, destination, width, height, in parameters);
        pipeline.Process(source, destination, width, height, in parameters);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        pipeline.Process(source, destination, width, height, in parameters);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    private static TopologicalMaterialPipeline.Parameters CreatePipelineParameters(TopologicalMaterialMode material)
        => new(
            (int)material,
            TopologicalMaterialQuality.Balanced,
            2f,
            0.01f,
            0.85f,
            0.7f,
            8f,
            0.65f,
            1f,
            0.75f,
            -0.61086524f,
            0.7330383f,
            1234);
}
