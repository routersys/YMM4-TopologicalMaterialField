using System.Numerics;
using Vortice;
using Vortice.Direct2D1;
using Vortice.DCommon;
using Vortice.DXGI;
using Vortice.Mathematics;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Player.Video.Effects;
using PixelFormat = Vortice.DCommon.PixelFormat;

namespace TopologicalMaterialField;

internal sealed class TopologicalMaterialFieldEffectProcessor : VideoEffectProcessorBase
{
    private readonly IGraphicsDevicesAndContext _devices;
    private readonly TopologicalMaterialFieldEffect _item;
    private readonly TopologicalMaterialPipeline? _pipeline;
    private TopologicalMaterialFieldCustomEffect? _effect;
    private ID2D1Bitmap1? _sourceBitmap;
    private ID2D1Bitmap1? _sourceStagingBitmap;
    private ID2D1Bitmap1? _outputBitmap;
    private int[]? _sourcePixels;
    private int[]? _outputPixels;
    private int _bitmapWidth;
    private int _bitmapHeight;
    private int _bufferCapacity;
    private bool _isFirst = true;
    private bool _hasOutput;
    private Parameters _parameters;

    public TopologicalMaterialFieldEffectProcessor(IGraphicsDevicesAndContext devices, TopologicalMaterialFieldEffect item)
        : base(devices)
    {
        _devices = devices;
        _item = item;
        _pipeline = TopologicalMaterialPipeline.TryCreate();
        if (_pipeline is not null)
            disposer.Collect(_pipeline);
    }

    public override DrawDescription Update(EffectDescription effectDescription)
    {
        if (IsPassThroughEffect || _effect is null || _pipeline is null || input is null)
            return effectDescription.DrawDescription;

        var frame = effectDescription.ItemPosition.Frame;
        var length = effectDescription.ItemDuration.Frame;
        var fps = effectDescription.FPS;
        var amount = (float)(_item.Amount.GetValue(frame, length, fps) / 100.0);
        var parameters = new Parameters(
            (int)_item.Material,
            _item.Quality,
            (float)_item.TopologyScale.GetValue(frame, length, fps),
            (float)(_item.FeatureThreshold.GetValue(frame, length, fps) / 100.0),
            (float)(_item.Distribution.GetValue(frame, length, fps) / 100.0),
            (float)(_item.ColorVariation.GetValue(frame, length, fps) / 100.0),
            (float)_item.PatternScale.GetValue(frame, length, fps),
            (float)(_item.PatternStrength.GetValue(frame, length, fps) / 100.0),
            (float)(_item.Reconstruction.GetValue(frame, length, fps) / 100.0),
            (float)(_item.Relief.GetValue(frame, length, fps) / 100.0),
            (float)(_item.LightAngle.GetValue(frame, length, fps) * Math.PI / 180.0),
            (float)(_item.LightElevation.GetValue(frame, length, fps) * Math.PI / 180.0),
            _item.Seed);

        if (_isFirst || _parameters.Amount != amount)
            _effect.Amount = amount;

        var dc = _devices.DeviceContext;
        var bounds = dc.GetImageLocalBounds(input);
        var width = (int)Math.Ceiling(bounds.Right - bounds.Left);
        var height = (int)Math.Ceiling(bounds.Bottom - bounds.Top);
        var pixelCountLong = (long)width * height;
        if (width <= 0 || height <= 0 || pixelCountLong > int.MaxValue)
            return effectDescription.DrawDescription;

        EnsureResources(dc, width, height);
        var sourceChanged = RenderSource(dc, bounds, width, height);
        var parametersChanged = _isFirst || !_parameters.PipelineEquals(parameters);

        if (amount > 0f && (sourceChanged || parametersChanged || !_hasOutput))
        {
            var pixelCount = (int)pixelCountLong;
            var pipelineParameters = new TopologicalMaterialPipeline.Parameters(
                parameters.Material,
                parameters.Quality,
                Math.Clamp(parameters.TopologyScale, 1f, 8f),
                Math.Clamp(parameters.FeatureThreshold, 0f, 0.25f),
                Math.Clamp(parameters.Distribution, 0f, 1f),
                Math.Clamp(parameters.ColorVariation, 0f, 2f),
                Math.Clamp(parameters.PatternScale, 1f, 256f),
                Math.Clamp(parameters.PatternStrength, 0f, 2f),
                Math.Clamp(parameters.Reconstruction, 0f, 2f),
                Math.Clamp(parameters.Relief, 0f, 2f),
                parameters.LightAngle,
                Math.Clamp(parameters.LightElevation, 0.017453292f, 1.55334306f),
                Math.Max(parameters.Seed, 0));

            _pipeline.Process(
                _sourcePixels.AsSpan(0, pixelCount),
                _outputPixels.AsSpan(0, pixelCount),
                width,
                height,
                in pipelineParameters);
            UploadOutput(width);
            _effect.SetInput(1, _outputBitmap, true);
            _hasOutput = true;
        }

        _parameters = parameters with { Amount = amount };
        _isFirst = false;
        return effectDescription.DrawDescription;
    }

    protected override ID2D1Image? CreateEffect(IGraphicsDevicesAndContext devices)
    {
        if (_pipeline is null)
            return null;

        _effect = new TopologicalMaterialFieldCustomEffect(devices);
        if (!_effect.IsEnabled)
        {
            _effect.Dispose();
            _effect = null;
            return null;
        }
        disposer.Collect(_effect);
        var output = _effect.Output;
        disposer.Collect(output);
        return output;
    }

    protected override void setInput(ID2D1Image? inputImage)
    {
        _effect?.SetInput(0, inputImage, true);
        if (!_hasOutput)
            _effect?.SetInput(1, inputImage, true);
    }

    protected override void ClearEffectChain()
    {
        _effect?.SetInput(0, null, true);
        _effect?.SetInput(1, null, true);
        _isFirst = true;
        _hasOutput = false;
    }

    private bool RenderSource(ID2D1DeviceContext dc, RawRectF bounds, int width, int height)
    {
        var pixelCount = width * height;
        var reused = _sourcePixels is not null && _bufferCapacity >= pixelCount;
        EnsureBuffers(pixelCount);

        var previousTarget = dc.Target;
        try
        {
            dc.Target = _sourceBitmap;
            dc.BeginDraw();
            dc.Clear(null);
            dc.DrawImage(
                input!,
                new Vector2(-bounds.Left, -bounds.Top),
                null,
                InterpolationMode.NearestNeighbor,
                CompositeMode.SourceCopy);
            dc.EndDraw();
        }
        finally
        {
            dc.Target = previousTarget;
        }

        _sourceStagingBitmap!.CopyFromBitmap(_sourceBitmap!);
        var mapped = _sourceStagingBitmap.Map(MapOptions.Read);
        var changed = !reused;
        try
        {
            unsafe
            {
                var basePointer = (byte*)mapped.Bits;
                for (var row = 0; row < height; row++)
                {
                    var sourceRow = new ReadOnlySpan<int>(basePointer + (nint)row * mapped.Pitch, width);
                    var destinationRow = _sourcePixels.AsSpan(row * width, width);
                    if (changed)
                    {
                        sourceRow.CopyTo(destinationRow);
                    }
                    else if (!sourceRow.SequenceEqual(destinationRow))
                    {
                        changed = true;
                        sourceRow.CopyTo(destinationRow);
                    }
                }
            }
        }
        finally
        {
            _sourceStagingBitmap.Unmap();
        }
        return changed;
    }

    private unsafe void UploadOutput(int width)
    {
        fixed (int* pointer = _outputPixels)
            _outputBitmap!.CopyFromMemory((nint)pointer, width * sizeof(int));
    }

    private void EnsureResources(ID2D1DeviceContext dc, int width, int height)
    {
        if (_sourceBitmap is not null
            && _sourceStagingBitmap is not null
            && _outputBitmap is not null
            && _bitmapWidth == width
            && _bitmapHeight == height)
            return;

        disposer.RemoveAndDispose(ref _sourceBitmap);
        disposer.RemoveAndDispose(ref _sourceStagingBitmap);
        disposer.RemoveAndDispose(ref _outputBitmap);

        var pixelFormat = new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied);
        var size = new SizeI(width, height);
        _sourceBitmap = dc.CreateBitmap(size, new BitmapProperties1(pixelFormat, 96f, 96f, BitmapOptions.Target));
        _sourceStagingBitmap = dc.CreateBitmap(size, new BitmapProperties1(pixelFormat, 96f, 96f, BitmapOptions.CpuRead | BitmapOptions.CannotDraw));
        _outputBitmap = dc.CreateBitmap(size, new BitmapProperties1(pixelFormat, 96f, 96f, BitmapOptions.None));
        disposer.Collect(_sourceBitmap);
        disposer.Collect(_sourceStagingBitmap);
        disposer.Collect(_outputBitmap);
        _bitmapWidth = width;
        _bitmapHeight = height;
        _hasOutput = false;
    }

    private void EnsureBuffers(int pixelCount)
    {
        if (_bufferCapacity >= pixelCount && _sourcePixels is not null && _outputPixels is not null)
            return;
        _sourcePixels = new int[pixelCount];
        _outputPixels = new int[pixelCount];
        _bufferCapacity = pixelCount;
    }

    private readonly record struct Parameters(
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
        int Seed,
        float Amount = 0f)
    {
        public bool PipelineEquals(Parameters other)
            => Material == other.Material
            && Quality == other.Quality
            && TopologyScale == other.TopologyScale
            && FeatureThreshold == other.FeatureThreshold
            && Distribution == other.Distribution
            && ColorVariation == other.ColorVariation
            && PatternScale == other.PatternScale
            && PatternStrength == other.PatternStrength
            && Reconstruction == other.Reconstruction
            && Relief == other.Relief
            && LightAngle == other.LightAngle
            && LightElevation == other.LightElevation
            && Seed == other.Seed;
    }
}
