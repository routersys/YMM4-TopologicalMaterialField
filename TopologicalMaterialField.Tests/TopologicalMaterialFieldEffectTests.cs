using System.Runtime.InteropServices;
using ComputeSharp;
using ComputeSharp.Interop;
using Vortice;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using Vortice.DXGI;
using Vortice.Mathematics;
using YukkuriMovieMaker.Commons;
using PixelFormat = Vortice.DCommon.PixelFormat;

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
        var source = CreateSourcePixels(width, height);

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

    [Fact]
    public void GpuPipelineReusesCapacityAcrossSmallerFrames()
    {
        using var pipeline = TopologicalMaterialPipeline.TryCreate();
        if (pipeline is null)
        {
            Assert.Skip("Direct3D 12 is unavailable.");
            return;
        }

        var largeSource = new int[64];
        var largeDestination = new int[64];
        var smallSource = new int[16];
        var smallDestination = new int[16];
        var parameters = CreatePipelineParameters(TopologicalMaterialMode.Ceramic);
        pipeline.Process(largeSource, largeDestination, 8, 8, in parameters);
        pipeline.Process(smallSource, smallDestination, 4, 4, in parameters);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        pipeline.Process(largeSource, largeDestination, 8, 8, in parameters);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void SharedTexturePackingRoundTripPreservesEveryByteValue()
    {
        var device = TryGetGraphicsDevice();
        if (device is null)
            return;

        const int width = 16;
        const int height = 16;
        var source = new Bgra32[width * height];
        for (var value = 0; value < source.Length; value++)
            source[value] = new Bgra32((byte)value, (byte)(255 - value), (byte)(value * 73), (byte)(value * 151));

        using var sourceTexture = InteropServices.AllocateSharedReadWriteTexture2D<Bgra32, Float4>(device, width, height);
        using var outputTexture = InteropServices.AllocateSharedReadWriteTexture2D<Bgra32, Float4>(device, width, height);
        using var packed = device.AllocateReadWriteBuffer<int>(source.Length);
        sourceTexture.CopyFrom(source);
        using (ComputeContext context = device.CreateComputeContext())
        {
            context.For(width, height, new SharedTextureToPackedBufferShader(sourceTexture, packed, width, height));
            context.Barrier(packed);
            context.For(width, height, new PackedBufferToSharedTextureShader(packed, outputTexture, width, height));
        }
        var result = new Bgra32[source.Length];
        outputTexture.CopyTo(result);

        for (var index = 0; index < source.Length; index++)
            Assert.Equal(source[index].PackedValue, result[index].PackedValue);
    }

    [Theory]
    [InlineData(TopologicalMaterialMode.Ceramic)]
    [InlineData(TopologicalMaterialMode.Mineral)]
    [InlineData(TopologicalMaterialMode.OxidizedMetal)]
    [InlineData(TopologicalMaterialMode.Parchment)]
    [InlineData(TopologicalMaterialMode.IceCrystal)]
    [InlineData(TopologicalMaterialMode.Textile)]
    public void SharedTexturePipelineMatchesPackedBufferPipeline(TopologicalMaterialMode material)
    {
        using var pipeline = TopologicalMaterialPipeline.TryCreate();
        if (pipeline is null)
        {
            Assert.Skip("Direct3D 12 is unavailable.");
            return;
        }

        const int width = 8;
        const int height = 8;
        var source = CreateSourcePixels(width, height);
        var expected = new int[source.Length];
        var parameters = CreatePipelineParameters(material);
        pipeline.Process(source, expected, width, height, in parameters);

        var device = GraphicsDevice.GetDefault();
        using var sourceTexture = InteropServices.AllocateSharedReadWriteTexture2D<Bgra32, Float4>(device, width, height);
        using var outputTexture = InteropServices.AllocateSharedReadWriteTexture2D<Bgra32, Float4>(device, width, height);
        var sourcePixels = new Bgra32[source.Length];
        for (var index = 0; index < source.Length; index++)
            sourcePixels[index].PackedValue = unchecked((uint)source[index]);
        sourceTexture.CopyFrom(sourcePixels);
        pipeline.ProcessSharedAndWait(sourceTexture, outputTexture, width, height, in parameters);
        var result = new Bgra32[source.Length];
        outputTexture.CopyTo(result);

        for (var index = 0; index < expected.Length; index++)
            Assert.Equal(unchecked((uint)expected[index]), result[index].PackedValue);
    }

    [Fact]
    public void SubmittedSharedTexturePipelineDoesNotAllocateManagedMemoryAfterWarmup()
    {
        using var pipeline = TopologicalMaterialPipeline.TryCreate();
        if (pipeline is null)
        {
            Assert.Skip("Direct3D 12 is unavailable.");
            return;
        }

        const int width = 8;
        const int height = 8;
        var device = GraphicsDevice.GetDefault();
        using var source = InteropServices.AllocateSharedReadWriteTexture2D<Bgra32, Float4>(device, width, height);
        using var destination = InteropServices.AllocateSharedReadWriteTexture2D<Bgra32, Float4>(device, width, height);
        var parameters = CreatePipelineParameters(TopologicalMaterialMode.Ceramic);
        for (var iteration = 0; iteration < 4; iteration++)
            pipeline.Process(source, destination, width, height, in parameters);
        var synchronizationBuffer = new Bgra32[width * height];
        destination.CopyTo(synchronizationBuffer);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        pipeline.Process(source, destination, width, height, in parameters);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        destination.CopyTo(synchronizationBuffer);

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Direct2DInteropPreservesPixelsAndDoesNotAllocateAfterWarmup()
    {
        using var devices = new GraphicsDevices();
        using var graphicsContext = devices.CreateContext();
        using var interop = TopologicalMaterialGpuInterop.TryCreate(graphicsContext);
        if (interop is null)
        {
            Assert.Skip("Direct3D 11 and Direct3D 12 sharing is unavailable.");
            return;
        }

        const int width = 8;
        const int height = 8;
        const int expected = unchecked((int)0xC0302010);
        var pixels = Enumerable.Repeat(expected, width * height).ToArray();
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        using var inputBitmap = graphicsContext.DeviceContext.CreateBitmap(
            new SizeI(width, height),
            new BitmapProperties1(
                new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                96f,
                96f,
                BitmapOptions.None));
        try
        {
            inputBitmap.CopyFromMemory(handle.AddrOfPinnedObject(), width * sizeof(int));
        }
        finally
        {
            handle.Free();
        }

        Assert.True(interop.EnsureResources(width, height));
        using var packed = interop.Device.AllocateReadWriteBuffer<int>(width * height);
        var bounds = new RawRectF(0f, 0f, width, height);
        for (var iteration = 0; iteration < 4; iteration++)
            ProcessInteropRoundTrip(interop, inputBitmap, packed, bounds, width, height);
        interop.WaitForIdle();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        ProcessInteropRoundTrip(interop, inputBitmap, packed, bounds, width, height);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        interop.WaitForIdle();

        using var staging = graphicsContext.DeviceContext.CreateBitmap(
            new SizeI(width, height),
            new BitmapProperties1(
                new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                96f,
                96f,
                BitmapOptions.CpuRead | BitmapOptions.CannotDraw));
        staging.CopyFromBitmap(interop.OutputBitmap);
        var mapped = staging.Map(MapOptions.Read);
        try
        {
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                    Assert.Equal(expected, Marshal.ReadInt32(mapped.Bits + (nint)(y * mapped.Pitch + x * sizeof(int))));
            }
        }
        finally
        {
            staging.Unmap();
        }
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void MaterialOutputAlignsWithTranslatedInputBounds()
    {
        using var devices = new GraphicsDevices();
        using var graphicsContext = devices.CreateContext();
        const int width = 4;
        const int height = 3;
        const int left = 13;
        const int top = 17;
        const int expected = unchecked((int)0xFF302010);
        var sourcePixels = Enumerable.Repeat(-1, width * height).ToArray();
        var materialPixels = Enumerable.Repeat(expected, width * height).ToArray();
        using var sourceBitmap = CreateBitmap(graphicsContext.DeviceContext, sourcePixels, width, height);
        using var materialBitmap = CreateBitmap(graphicsContext.DeviceContext, materialPixels, width, height);
        using var sourceTransform = new AffineTransform2D(graphicsContext.DeviceContext)
        {
            TransformMatrix = System.Numerics.Matrix3x2.CreateTranslation(left, top),
            BorderMode = BorderMode.Hard,
        };
        using var materialTransform = new AffineTransform2D(graphicsContext.DeviceContext)
        {
            TransformMatrix = System.Numerics.Matrix3x2.CreateTranslation(left, top),
            BorderMode = BorderMode.Hard,
        };
        sourceTransform.SetInput(0, sourceBitmap, true);
        materialTransform.SetInput(0, materialBitmap, true);
        using var sourceOutput = sourceTransform.Output;
        using var materialOutput = materialTransform.Output;
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        using var effect = new TopologicalMaterialFieldCustomEffect(graphicsContext);
        if (!effect.IsEnabled)
        {
            Assert.Skip("Direct2D custom effects are unavailable.");
            return;
        }
        effect.Amount = 1f;
        effect.SetInput(0, sourceOutput, true);
        effect.SetInput(1, materialOutput, true);
        using var output = effect.Output;
        using var target = graphicsContext.DeviceContext.CreateBitmap(
            new SizeI(width, height),
            new BitmapProperties1(
                new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                96f,
                96f,
                BitmapOptions.Target));
        graphicsContext.DeviceContext.Target = target;
        graphicsContext.DeviceContext.BeginDraw();
        graphicsContext.DeviceContext.Clear(null);
        graphicsContext.DeviceContext.DrawImage(
            output,
            new System.Numerics.Vector2(-left, -top),
            null,
            InterpolationMode.NearestNeighbor,
            CompositeMode.SourceCopy);
        graphicsContext.DeviceContext.EndDraw();
        graphicsContext.DeviceContext.Target = null;

        using var staging = graphicsContext.DeviceContext.CreateBitmap(
            new SizeI(width, height),
            new BitmapProperties1(
                new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                96f,
                96f,
                BitmapOptions.CpuRead | BitmapOptions.CannotDraw));
        staging.CopyFromBitmap(target);
        var mapped = staging.Map(MapOptions.Read);
        try
        {
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                    Assert.Equal(expected, Marshal.ReadInt32(mapped.Bits + (nint)(y * mapped.Pitch + x * sizeof(int))));
            }
        }
        finally
        {
            staging.Unmap();
        }
    }

    [Fact]
    public void TopologyResolveStoresConnectivityMask()
    {
        var device = TryGetGraphicsDevice();
        if (device is null)
            return;

        using var features = device.AllocateReadWriteBuffer(new[] { new Float4(0f, 0f, 0f, 1f), new Float4(0f, 0f, 0f, 1f), new Float4(0f, 0f, 0f, 1f) });
        using var scalar = device.AllocateReadWriteBuffer(new[] { 0.5f, 0.5f, 0.5f });
        using var flow = device.AllocateReadWriteBuffer(new[] { new ComputeSharp.Int2(17, 23), new ComputeSharp.Int2(17, 23), new ComputeSharp.Int2(17, 23) });
        using var topology = device.AllocateReadWriteBuffer<Float4>(3);
        device.For(3, 1, new TopologyResolveShader(features, scalar, flow, topology, 3, 1));
        var result = new Float4[3];
        topology.CopyTo(result);

        Assert.Equal(2f, result[0].W);
        Assert.Equal(3f, result[1].W);
        Assert.Equal(1f, result[2].W);
    }

    [Fact]
    public void ReactionSeedAmplitudeUsesRegionPhase()
    {
        var device = TryGetGraphicsDevice();
        if (device is null)
            return;

        using var topology = device.AllocateReadWriteBuffer(new[] { new Float4(1f, 0f, 0f, 0f) });
        using var flow = device.AllocateReadWriteBuffer(new[] { new ComputeSharp.Int2(17, 23) });
        using var output = device.AllocateReadWriteBuffer<Float2>(1);
        device.For(1, 1, new ReactionInitializeShader(topology, flow, output, 0, 1f, 0, 1, 1));
        var result = new Float2[1];
        output.CopyTo(result);

        var expected = 0.22f + 0.16f * RegionPhase(17, 23);
        Assert.Equal(expected, result[0].Y, 6);
        Assert.Equal(1f - expected * 0.5f, result[0].X, 6);
    }

    [Fact]
    public void ZeroReconstructionLeavesPoissonDesiredFieldUnchanged()
    {
        var device = TryGetGraphicsDevice();
        if (device is null)
            return;

        using var input = device.AllocateReadWriteBuffer(new[] { 0.1f, 0.4f, 0.8f });
        using var rhs = device.AllocateReadWriteBuffer(new[] { 0.2f, 0.5f, 0.9f });
        using var topology = device.AllocateReadWriteBuffer(new[] { new Float4(0f, 0f, 0f, 2f), new Float4(0f, 0f, 0f, 3f), new Float4(0f, 0f, 0f, 1f) });
        using var output = device.AllocateReadWriteBuffer<float>(3);
        device.For(3, 1, new PoissonJacobiShader(input, rhs, topology, output, 0f, 3, 1));
        var result = new float[3];
        output.CopyTo(result);

        Assert.Equal(new[] { 0.2f, 0.5f, 0.9f }, result);
    }

    [Fact]
    public void PoissonJacobiDoesNotCrossDisconnectedRegion()
    {
        var device = TryGetGraphicsDevice();
        if (device is null)
            return;

        using var input = device.AllocateReadWriteBuffer(new[] { 0f, 0f, 1f });
        using var rhs = device.AllocateReadWriteBuffer(new[] { 0f, 0f, 1f });
        using var topology = device.AllocateReadWriteBuffer(new[] { new Float4(0f, 0f, 0f, 2f), new Float4(0f, 0f, 0f, 1f), new Float4(0f, 0f, 0f, 0f) });
        using var output = device.AllocateReadWriteBuffer<float>(3);
        device.For(3, 1, new PoissonJacobiShader(input, rhs, topology, output, 1f, 3, 1));
        var result = new float[3];
        output.CopyTo(result);

        Assert.Equal(new[] { 0f, 0f, 1f }, result);
    }

    private static GraphicsDevice? TryGetGraphicsDevice()
    {
        try
        {
            return GraphicsDevice.GetDefault();
        }
        catch
        {
            Assert.Skip("Direct3D 12 is unavailable.");
            return null;
        }
    }

    private static float RegionPhase(int ascent, int descent)
    {
        var value = (uint)ascent * 0x9e3779b9u ^ (uint)descent * 0x85ebca6bu;
        value ^= value >> 16;
        value *= 0x7feb352du;
        value ^= value >> 15;
        value *= 0x846ca68bu;
        value ^= value >> 16;
        return value * 2.3283064e-10f;
    }

    private static int[] CreateSourcePixels(int width, int height)
    {
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
        return source;
    }

    private static ID2D1Bitmap1 CreateBitmap(ID2D1DeviceContext deviceContext, int[] pixels, int width, int height)
    {
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            var bitmap = deviceContext.CreateBitmap(
                new SizeI(width, height),
                new BitmapProperties1(
                    new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                    96f,
                    96f,
                    BitmapOptions.None));
            bitmap.CopyFromMemory(handle.AddrOfPinnedObject(), width * sizeof(int));
            return bitmap;
        }
        finally
        {
            handle.Free();
        }
    }

    private static void ProcessInteropRoundTrip(
        TopologicalMaterialGpuInterop interop,
        ID2D1Image input,
        ReadWriteBuffer<int> packed,
        RawRectF bounds,
        int width,
        int height)
    {
        interop.RenderInput(input, bounds);
        interop.BeginCompute();
        try
        {
            using ComputeContext context = interop.Device.CreateComputeContext();
            context.For(width, height, new SharedTextureToPackedBufferShader(interop.SourceTexture, packed, width, height));
            context.Barrier(packed);
            context.For(width, height, new PackedBufferToSharedTextureShader(packed, interop.OutputTexture, width, height));
            context.Submit();
        }
        finally
        {
            interop.EndCompute();
        }
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
