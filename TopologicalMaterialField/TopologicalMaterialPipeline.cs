using ComputeSharp;

namespace TopologicalMaterialField;

internal sealed class TopologicalMaterialPipeline : IDisposable
{
    private readonly GraphicsDevice _device;
    private ReadWriteBuffer<int>? _source;
    private ReadWriteBuffer<Float4>? _features;
    private ReadWriteBuffer<float>? _scalarA;
    private ReadWriteBuffer<float>? _scalarB;
    private ReadWriteBuffer<Int2>? _flowA;
    private ReadWriteBuffer<Int2>? _flowB;
    private ReadWriteBuffer<Float4>? _topology;
    private ReadWriteBuffer<Float4>? _transported;
    private ReadWriteBuffer<Float2>? _reactionA;
    private ReadWriteBuffer<Float2>? _reactionB;
    private ReadWriteBuffer<float>? _poissonA;
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

    public static TopologicalMaterialPipeline? TryCreate(GraphicsDevice device)
    {
        try
        {
            return new TopologicalMaterialPipeline(device);
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
        var sourceBuffer = EnsurePackedSource(pixelCount);
        sourceBuffer.CopyFrom(source[..pixelCount]);
        using (ComputeContext context = _device.CreateComputeContext())
            RecordPackedPipeline(in context, sourceBuffer, pixelCount, width, height, in parameters);
        sourceBuffer.CopyTo(destination[..pixelCount]);
    }

    public void Process(
        ReadWriteTexture2D<Bgra32, Float4> source,
        ReadWriteTexture2D<Bgra32, Float4> destination,
        int width,
        int height,
        in Parameters parameters)
    {
        var pixelCount = checked(width * height);
        EnsureCapacity(pixelCount);
        using ComputeContext context = _device.CreateComputeContext();
        RecordSharedPipeline(in context, source, destination, pixelCount, width, height, in parameters);
        context.Submit();
    }

    internal void ProcessSharedAndWait(
        ReadWriteTexture2D<Bgra32, Float4> source,
        ReadWriteTexture2D<Bgra32, Float4> destination,
        int width,
        int height,
        in Parameters parameters)
    {
        var pixelCount = checked(width * height);
        EnsureCapacity(pixelCount);
        using ComputeContext context = _device.CreateComputeContext();
        RecordSharedPipeline(in context, source, destination, pixelCount, width, height, in parameters);
    }

    private void RecordSharedPipeline(
        in ComputeContext context,
        ReadWriteTexture2D<Bgra32, Float4> source,
        ReadWriteTexture2D<Bgra32, Float4> destination,
        int pixelCount,
        int width,
        int height,
        in Parameters parameters)
    {
        var features = _features!;
        var scalarA = _scalarA!;
        context.For(width, height, new SharedTextureMaterialPreprocessShader(source, features, scalarA, width, height));
        context.Barrier(source);
        context.Barrier(features);
        context.Barrier(scalarA);
        RecordPipeline(in context, pixelCount, width, height, in parameters);
        var transported = _transported!;
        context.For(width, height, new MaterialBufferToSharedTextureShader(transported, destination, width, height));
    }

    private void RecordPackedPipeline(
        in ComputeContext context,
        ReadWriteBuffer<int> source,
        int pixelCount,
        int width,
        int height,
        in Parameters parameters)
    {
        var features = _features!;
        var scalarA = _scalarA!;
        context.For(width, height, new MaterialPreprocessShader(source, features, scalarA, width, height));
        context.Barrier(source);
        context.Barrier(features);
        context.Barrier(scalarA);
        RecordPipeline(in context, pixelCount, width, height, in parameters);
        var transported = _transported!;
        context.For(width, height, new MaterialBufferToPackedShader(transported, source, width, height));
    }

    private void RecordPipeline(
        in ComputeContext context,
        int pixelCount,
        int width,
        int height,
        in Parameters parameters)
    {

        var features = _features!;
        var scalarA = _scalarA!;
        var scalarB = _scalarB!;
        var flowA = _flowA!;
        var flowB = _flowB!;
        var topology = _topology!;
        var transported = _transported!;
        var reactionA = _reactionA!;
        var reactionB = _reactionB!;
        var poissonA = _poissonA!;

        var spatialDenominator = MathF.Max(parameters.TopologyScale * parameters.TopologyScale, 1f);
        var spatialWeight1 = MathF.Exp(-1f / spatialDenominator);
        var spatialWeight2 = MathF.Exp(-2f / spatialDenominator);
        var spatialWeight4 = MathF.Exp(-4f / spatialDenominator);
        var spatialWeight5 = MathF.Exp(-5f / spatialDenominator);
        var spatialWeight8 = MathF.Exp(-8f / spatialDenominator);
        var smoothIterations = Math.Clamp((int)MathF.Round(parameters.TopologyScale), 1, 8);
        for (var iteration = 0; iteration < smoothIterations; iteration++)
        {
            context.For(width, height, new ScalarSmoothShader(
                features,
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

        context.For(width, height, new FlowInitializeShader(features, scalarA, flowA, parameters.FeatureThreshold, width, height));
        context.Barrier(flowA);

        var flowIterations = TopologicalMaterialSettings.CeilLog2(pixelCount);
        for (var iteration = 0; iteration < flowIterations; iteration++)
        {
            context.For(width, height, new FlowCompressShader(flowA, flowB, pixelCount, width, height));
            (flowA, flowB) = (flowB, flowA);
            context.Barrier(flowA);
        }

        context.For(width, height, new TopologyResolveShader(features, scalarA, flowA, topology, width, height));
        context.Barrier(topology);

        var quality = TopologicalMaterialSettings.GetQuality(parameters.Quality);
        context.For(width, height, new SlicedTransportShader(
            features,
            topology,
            flowA,
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
            flowA,
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
                flowA,
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
            scalarB,
            poissonA,
            parameters.PatternStrength,
            parameters.Reconstruction,
            width,
            height));
        context.Barrier(scalarB);
        context.Barrier(poissonA);
        context.Barrier(scalarA);

        if (parameters.Reconstruction > 0f)
        {
            var poissonB = scalarA;
            for (var iteration = 0; iteration < quality.PoissonIterations; iteration++)
            {
                context.For(width, height, new PoissonJacobiShader(
                    poissonA,
                    scalarB,
                    topology,
                    poissonB,
                    parameters.Reconstruction,
                    width,
                    height));
                (poissonA, poissonB) = (poissonB, poissonA);
                context.Barrier(poissonA);
            }
        }

        context.For(width, height, new MaterialFinalizeShader(
            transported,
            topology,
            reactionA,
            poissonA,
            parameters.Material,
            parameters.PatternStrength,
            parameters.Relief,
            parameters.LightAngle,
            parameters.LightElevation,
            width,
            height));
        context.Barrier(transported);
    }

    private void EnsureCapacity(int pixelCount)
    {
        if (_capacity >= pixelCount)
            return;

        DisposeBuffers();
        _features = _device.AllocateReadWriteBuffer<Float4>(pixelCount);
        _scalarA = _device.AllocateReadWriteBuffer<float>(pixelCount);
        _scalarB = _device.AllocateReadWriteBuffer<float>(pixelCount);
        _flowA = _device.AllocateReadWriteBuffer<Int2>(pixelCount);
        _flowB = _device.AllocateReadWriteBuffer<Int2>(pixelCount);
        _topology = _device.AllocateReadWriteBuffer<Float4>(pixelCount);
        _transported = _device.AllocateReadWriteBuffer<Float4>(pixelCount);
        _reactionA = _device.AllocateReadWriteBuffer<Float2>(pixelCount);
        _reactionB = _device.AllocateReadWriteBuffer<Float2>(pixelCount);
        _poissonA = _device.AllocateReadWriteBuffer<float>(pixelCount);
        _capacity = pixelCount;
    }

    private ReadWriteBuffer<int> EnsurePackedSource(int pixelCount)
        => _source ??= _device.AllocateReadWriteBuffer<int>(pixelCount);

    private void DisposeBuffers()
    {
        _source?.Dispose();
        _features?.Dispose();
        _scalarA?.Dispose();
        _scalarB?.Dispose();
        _flowA?.Dispose();
        _flowB?.Dispose();
        _topology?.Dispose();
        _transported?.Dispose();
        _reactionA?.Dispose();
        _reactionB?.Dispose();
        _poissonA?.Dispose();
        _source = null;
        _features = null;
        _scalarA = null;
        _scalarB = null;
        _flowA = null;
        _flowB = null;
        _topology = null;
        _transported = null;
        _reactionA = null;
        _reactionB = null;
        _poissonA = null;
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
