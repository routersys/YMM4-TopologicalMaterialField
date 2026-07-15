using ComputeSharp;

namespace TopologicalMaterialField;

internal sealed class TopologicalMaterialPipeline : IDisposable
{
    private readonly GraphicsDevice _device;
    private ReadWriteBuffer<int>? _source;
    private ReadWriteBuffer<Float4>? _features;
    private ReadWriteBuffer<float>? _scalarA;
    private ReadWriteBuffer<float>? _scalarB;
    private ReadWriteBuffer<int>? _ascentA;
    private ReadWriteBuffer<int>? _ascentB;
    private ReadWriteBuffer<int>? _descentA;
    private ReadWriteBuffer<int>? _descentB;
    private ReadWriteBuffer<Float4>? _topology;
    private ReadWriteBuffer<int>? _connectivity;
    private ReadWriteBuffer<Float4>? _transported;
    private ReadWriteBuffer<Float2>? _reactionA;
    private ReadWriteBuffer<Float2>? _reactionB;
    private ReadWriteBuffer<float>? _poissonA;
    private ReadWriteBuffer<float>? _poissonB;
    private int _capacity;

    private TopologicalMaterialPipeline(GraphicsDevice device)
    {
        _device = device;
    }

    public static TopologicalMaterialPipeline? TryCreate()
    {
        try
        {
            return new TopologicalMaterialPipeline(GraphicsDevice.GetDefault());
        }
        catch
        {
            return null;
        }
    }

    public void Process(ReadOnlySpan<int> source, Span<int> destination, int width, int height, in Parameters parameters)
    {
        var pixelCount = checked(width * height);
        EnsureCapacity(pixelCount);

        var sourceBuffer = _source!;
        var features = _features!;
        var scalarA = _scalarA!;
        var scalarB = _scalarB!;
        var ascentA = _ascentA!;
        var ascentB = _ascentB!;
        var descentA = _descentA!;
        var descentB = _descentB!;
        var topology = _topology!;
        var connectivity = _connectivity!;
        var transported = _transported!;
        var reactionA = _reactionA!;
        var reactionB = _reactionB!;
        var poissonA = _poissonA!;
        var poissonB = _poissonB!;

        sourceBuffer.CopyFrom(source[..pixelCount]);
        var spatialDenominator = MathF.Max(parameters.TopologyScale * parameters.TopologyScale, 1f);
        var spatialWeight1 = MathF.Exp(-1f / spatialDenominator);
        var spatialWeight2 = MathF.Exp(-2f / spatialDenominator);
        var spatialWeight4 = MathF.Exp(-4f / spatialDenominator);
        var spatialWeight5 = MathF.Exp(-5f / spatialDenominator);
        var spatialWeight8 = MathF.Exp(-8f / spatialDenominator);
        using (ComputeContext context = _device.CreateComputeContext())
        {
            context.For(width, height, new MaterialPreprocessShader(sourceBuffer, features, scalarA, width, height));
            context.Barrier(features);
            context.Barrier(scalarA);

            var smoothIterations = Math.Clamp((int)MathF.Round(parameters.TopologyScale), 1, 8);
            for (var iteration = 0; iteration < smoothIterations; iteration++)
            {
                context.For(width, height, new ScalarSmoothShader(
                    sourceBuffer,
                    scalarA,
                    scalarB,
                    spatialWeight1,
                    spatialWeight2,
                    spatialWeight4,
                    spatialWeight5,
                    spatialWeight8,
                    width,
                    height));
                (scalarA, scalarB) = (scalarB, scalarA);
                context.Barrier(scalarA);
            }

            context.For(width, height, new FlowInitializeShader(sourceBuffer, scalarA, ascentA, descentA, parameters.FeatureThreshold, width, height));
            context.Barrier(ascentA);
            context.Barrier(descentA);

            var flowIterations = TopologicalMaterialSettings.CeilLog2(pixelCount);
            for (var iteration = 0; iteration < flowIterations; iteration++)
            {
                context.For(width, height, new FlowCompressShader(ascentA, descentA, ascentB, descentB, pixelCount, width, height));
                (ascentA, ascentB) = (ascentB, ascentA);
                (descentA, descentB) = (descentB, descentA);
                context.Barrier(ascentA);
                context.Barrier(descentA);
            }

            context.For(width, height, new TopologyResolveShader(sourceBuffer, scalarA, ascentA, descentA, topology, connectivity, width, height));
            context.Barrier(topology);
            context.Barrier(connectivity);

            var quality = TopologicalMaterialSettings.GetQuality(parameters.Quality);
            context.For(width, height, new SlicedTransportShader(
                features,
                topology,
                ascentA,
                descentA,
                transported,
                parameters.Material,
                parameters.Distribution,
                parameters.ColorVariation,
                parameters.PatternScale,
                parameters.Seed,
                quality.RegionSamples,
                width,
                height));
            context.Barrier(transported);

            context.For(width, height, new ReactionInitializeShader(
                topology,
                ascentA,
                descentA,
                reactionA,
                parameters.Material,
                parameters.PatternScale,
                parameters.Seed,
                width,
                height));
            context.Barrier(reactionA);

            for (var iteration = 0; iteration < quality.ReactionIterations; iteration++)
            {
                context.For(width, height, new ReactionDiffusionShader(
                    reactionA,
                    reactionB,
                    ascentA,
                    descentA,
                    parameters.Material,
                    parameters.PatternScale,
                    width,
                    height));
                (reactionA, reactionB) = (reactionB, reactionA);
                context.Barrier(reactionA);
            }

            context.For(width, height, new PoissonRhsShader(
                scalarA,
                transported,
                topology,
                reactionA,
                connectivity,
                scalarB,
                poissonA,
                parameters.PatternStrength,
                parameters.Reconstruction,
                width,
                height));
            context.Barrier(scalarB);
            context.Barrier(poissonA);

            if (parameters.Reconstruction > 0f)
            {
                for (var iteration = 0; iteration < quality.PoissonIterations; iteration++)
                {
                    context.For(width, height, new PoissonJacobiShader(
                        poissonA,
                        scalarB,
                        connectivity,
                        poissonB,
                        parameters.Reconstruction,
                        width,
                        height));
                    (poissonA, poissonB) = (poissonB, poissonA);
                    context.Barrier(poissonA);
                }
            }

            context.For(width, height, new MaterialFinalizeShader(
                sourceBuffer,
                transported,
                topology,
                reactionA,
                poissonA,
                connectivity,
                parameters.Material,
                parameters.PatternStrength,
                parameters.Relief,
                parameters.LightAngle,
                parameters.LightElevation,
                width,
                height));
        }
        sourceBuffer.CopyTo(destination[..pixelCount]);
    }

    private void EnsureCapacity(int pixelCount)
    {
        if (_capacity == pixelCount)
            return;

        DisposeBuffers();
        _source = _device.AllocateReadWriteBuffer<int>(pixelCount);
        _features = _device.AllocateReadWriteBuffer<Float4>(pixelCount);
        _scalarA = _device.AllocateReadWriteBuffer<float>(pixelCount);
        _scalarB = _device.AllocateReadWriteBuffer<float>(pixelCount);
        _ascentA = _device.AllocateReadWriteBuffer<int>(pixelCount);
        _ascentB = _device.AllocateReadWriteBuffer<int>(pixelCount);
        _descentA = _device.AllocateReadWriteBuffer<int>(pixelCount);
        _descentB = _device.AllocateReadWriteBuffer<int>(pixelCount);
        _topology = _device.AllocateReadWriteBuffer<Float4>(pixelCount);
        _connectivity = _device.AllocateReadWriteBuffer<int>(pixelCount);
        _transported = _device.AllocateReadWriteBuffer<Float4>(pixelCount);
        _reactionA = _device.AllocateReadWriteBuffer<Float2>(pixelCount);
        _reactionB = _device.AllocateReadWriteBuffer<Float2>(pixelCount);
        _poissonA = _device.AllocateReadWriteBuffer<float>(pixelCount);
        _poissonB = _device.AllocateReadWriteBuffer<float>(pixelCount);
        _capacity = pixelCount;
    }

    private void DisposeBuffers()
    {
        _source?.Dispose();
        _features?.Dispose();
        _scalarA?.Dispose();
        _scalarB?.Dispose();
        _ascentA?.Dispose();
        _ascentB?.Dispose();
        _descentA?.Dispose();
        _descentB?.Dispose();
        _topology?.Dispose();
        _connectivity?.Dispose();
        _transported?.Dispose();
        _reactionA?.Dispose();
        _reactionB?.Dispose();
        _poissonA?.Dispose();
        _poissonB?.Dispose();
        _source = null;
        _features = null;
        _scalarA = null;
        _scalarB = null;
        _ascentA = null;
        _ascentB = null;
        _descentA = null;
        _descentB = null;
        _topology = null;
        _connectivity = null;
        _transported = null;
        _reactionA = null;
        _reactionB = null;
        _poissonA = null;
        _poissonB = null;
        _capacity = 0;
    }

    public void Dispose()
    {
        DisposeBuffers();
    }

    internal readonly record struct Parameters(
        int Material,
        TopologicalMaterialQuality Quality,
        float TopologyScale,
        float FeatureThreshold,
        float Distribution,
        float ColorVariation,
        float PatternScale,
        float PatternStrength,
        float Reconstruction,
        float Relief,
        float LightAngle,
        float LightElevation,
        int Seed);
}
