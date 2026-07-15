using ComputeSharp;

namespace TopologicalMaterialField;

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct MaterialPreprocessShader(
    ReadWriteBuffer<int> source,
    ReadWriteBuffer<Float4> features,
    ReadWriteBuffer<float> scalar,
    int width,
    int height) : IComputeShader
{
    private readonly ReadWriteBuffer<int> source = source;
    private readonly ReadWriteBuffer<Float4> features = features;
    private readonly ReadWriteBuffer<float> scalar = scalar;
    private readonly int width = width;
    private readonly int height = height;

    public void Execute()
    {
        var x = ThreadIds.X;
        var y = ThreadIds.Y;
        if (x >= width || y >= height)
            return;

        var index = y * width + x;
        var packed = source[index];
        var alpha = (packed >> 24) & 255;
        if (alpha == 0)
        {
            features[index] = new Float4(0f, 0f, 0f, 0f);
            scalar[index] = 0f;
            return;
        }

        var inverseAlpha = 1f / alpha;
        var blue = Hlsl.Saturate(((packed >> 0) & 255) * inverseAlpha);
        var green = Hlsl.Saturate(((packed >> 8) & 255) * inverseAlpha);
        var red = Hlsl.Saturate(((packed >> 16) & 255) * inverseAlpha);
        red = ToLinear(red);
        green = ToLinear(green);
        blue = ToLinear(blue);

        var l = 0.4122214708f * red + 0.5363325363f * green + 0.0514459929f * blue;
        var m = 0.2119034982f * red + 0.6806995451f * green + 0.1073969566f * blue;
        var s = 0.0883024619f * red + 0.2817188376f * green + 0.6299787005f * blue;
        var lRoot = Hlsl.Pow(Hlsl.Max(l, 0f), 1f / 3f);
        var mRoot = Hlsl.Pow(Hlsl.Max(m, 0f), 1f / 3f);
        var sRoot = Hlsl.Pow(Hlsl.Max(s, 0f), 1f / 3f);
        var labL = 0.2104542553f * lRoot + 0.7936177850f * mRoot - 0.0040720468f * sRoot;
        var labA = 1.9779984951f * lRoot - 2.4285922050f * mRoot + 0.4505937099f * sRoot;
        var labB = 0.0259040371f * lRoot + 0.7827717662f * mRoot - 0.8086757660f * sRoot;

        features[index] = new Float4(labL, labA, labB, alpha / 255f);
        scalar[index] = labL;
    }

    private float ToLinear(float value)
        => value <= 0.04045f ? value / 12.92f : Hlsl.Pow((value + 0.055f) / 1.055f, 2.4f);
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct ScalarSmoothShader(
    ReadWriteBuffer<int> source,
    ReadWriteBuffer<float> input,
    ReadWriteBuffer<float> output,
    float spatialWeight1,
    float spatialWeight2,
    float spatialWeight4,
    float spatialWeight5,
    float spatialWeight8,
    int width,
    int height) : IComputeShader
{
    private readonly ReadWriteBuffer<int> source = source;
    private readonly ReadWriteBuffer<float> input = input;
    private readonly ReadWriteBuffer<float> output = output;
    private readonly float spatialWeight1 = spatialWeight1;
    private readonly float spatialWeight2 = spatialWeight2;
    private readonly float spatialWeight4 = spatialWeight4;
    private readonly float spatialWeight5 = spatialWeight5;
    private readonly float spatialWeight8 = spatialWeight8;
    private readonly int width = width;
    private readonly int height = height;

    public void Execute()
    {
        var x = ThreadIds.X;
        var y = ThreadIds.Y;
        if (x >= width || y >= height)
            return;

        var index = y * width + x;
        if (((source[index] >> 24) & 255) == 0)
        {
            output[index] = 0f;
            return;
        }

        var center = input[index];
        var sum = 0f;
        var weightSum = 0f;
        for (var dy = -2; dy <= 2; dy++)
        {
            var sy = y + dy;
            if (sy < 0 || sy >= height)
                continue;
            for (var dx = -2; dx <= 2; dx++)
            {
                var sx = x + dx;
                if (sx < 0 || sx >= width)
                    continue;
                var sampleIndex = sy * width + sx;
                if (((source[sampleIndex] >> 24) & 255) == 0)
                    continue;
                var value = input[sampleIndex];
                var spatial = SpatialWeight(dx * dx + dy * dy);
                var difference = value - center;
                var range = Hlsl.Exp(-(difference * difference) / 0.01f);
                var weight = spatial * range;
                sum += value * weight;
                weightSum += weight;
            }
        }
        output[index] = sum / Hlsl.Max(weightSum, 1e-6f);
    }

    private float SpatialWeight(int squaredDistance)
        => squaredDistance == 0 ? 1f
        : squaredDistance == 1 ? spatialWeight1
        : squaredDistance == 2 ? spatialWeight2
        : squaredDistance == 4 ? spatialWeight4
        : squaredDistance == 5 ? spatialWeight5
        : spatialWeight8;
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct FlowInitializeShader(
    ReadWriteBuffer<int> source,
    ReadWriteBuffer<float> scalar,
    ReadWriteBuffer<int> ascent,
    ReadWriteBuffer<int> descent,
    float threshold,
    int width,
    int height) : IComputeShader
{
    private readonly ReadWriteBuffer<int> source = source;
    private readonly ReadWriteBuffer<float> scalar = scalar;
    private readonly ReadWriteBuffer<int> ascent = ascent;
    private readonly ReadWriteBuffer<int> descent = descent;
    private readonly float threshold = threshold;
    private readonly int width = width;
    private readonly int height = height;

    public void Execute()
    {
        var x = ThreadIds.X;
        var y = ThreadIds.Y;
        if (x >= width || y >= height)
            return;

        var index = y * width + x;
        if (((source[index] >> 24) & 255) == 0)
        {
            ascent[index] = index;
            descent[index] = index;
            return;
        }

        var center = scalar[index];
        var bestHigh = center + threshold;
        var bestLow = center - threshold;
        var highIndex = index;
        var lowIndex = index;

        for (var dy = -1; dy <= 1; dy++)
        {
            var sy = y + dy;
            if (sy < 0 || sy >= height)
                continue;
            for (var dx = -1; dx <= 1; dx++)
            {
                var sx = x + dx;
                if (sx < 0 || sx >= width || (dx == 0 && dy == 0))
                    continue;
                var sampleIndex = sy * width + sx;
                if (((source[sampleIndex] >> 24) & 255) == 0)
                    continue;
                var value = scalar[sampleIndex];
                if (value > bestHigh || (value == bestHigh && sampleIndex > highIndex))
                {
                    bestHigh = value;
                    highIndex = sampleIndex;
                }
                if (value < bestLow || (value == bestLow && sampleIndex < lowIndex))
                {
                    bestLow = value;
                    lowIndex = sampleIndex;
                }
            }
        }

        ascent[index] = highIndex;
        descent[index] = lowIndex;
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct FlowCompressShader(
    ReadWriteBuffer<int> ascentInput,
    ReadWriteBuffer<int> descentInput,
    ReadWriteBuffer<int> ascentOutput,
    ReadWriteBuffer<int> descentOutput,
    int pixelCount,
    int width,
    int height) : IComputeShader
{
    private readonly ReadWriteBuffer<int> ascentInput = ascentInput;
    private readonly ReadWriteBuffer<int> descentInput = descentInput;
    private readonly ReadWriteBuffer<int> ascentOutput = ascentOutput;
    private readonly ReadWriteBuffer<int> descentOutput = descentOutput;
    private readonly int pixelCount = pixelCount;
    private readonly int width = width;
    private readonly int height = height;

    public void Execute()
    {
        var x = ThreadIds.X;
        var y = ThreadIds.Y;
        if (x >= width || y >= height)
            return;
        var index = y * width + x;
        var high = ascentInput[index];
        var low = descentInput[index];
        high = high >= 0 && high < pixelCount ? high : index;
        low = low >= 0 && low < pixelCount ? low : index;
        ascentOutput[index] = ascentInput[high];
        descentOutput[index] = descentInput[low];
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct TopologyResolveShader(
    ReadWriteBuffer<int> source,
    ReadWriteBuffer<float> scalar,
    ReadWriteBuffer<int> ascent,
    ReadWriteBuffer<int> descent,
    ReadWriteBuffer<Float4> topology,
    ReadWriteBuffer<int> connectivity,
    int width,
    int height) : IComputeShader
{
    private readonly ReadWriteBuffer<int> source = source;
    private readonly ReadWriteBuffer<float> scalar = scalar;
    private readonly ReadWriteBuffer<int> ascent = ascent;
    private readonly ReadWriteBuffer<int> descent = descent;
    private readonly ReadWriteBuffer<Float4> topology = topology;
    private readonly ReadWriteBuffer<int> connectivity = connectivity;
    private readonly int width = width;
    private readonly int height = height;

    public void Execute()
    {
        var x = ThreadIds.X;
        var y = ThreadIds.Y;
        if (x >= width || y >= height)
            return;
        var index = y * width + x;
        if (((source[index] >> 24) & 255) == 0)
        {
            topology[index] = new Float4(0f, 0f, 0f, 0f);
            connectivity[index] = -1;
            return;
        }

        var high = ascent[index];
        var low = descent[index];
        var center = scalar[index];
        var boundary = 0f;
        var neighborSum = 0f;
        var count = 0f;
        var connectionMask = 0;
        for (var dy = -1; dy <= 1; dy++)
        {
            var sy = y + dy;
            if (sy < 0 || sy >= height)
                continue;
            for (var dx = -1; dx <= 1; dx++)
            {
                var sx = x + dx;
                if (sx < 0 || sx >= width || (dx == 0 && dy == 0))
                    continue;
                var sampleIndex = sy * width + sx;
                if (((source[sampleIndex] >> 24) & 255) == 0)
                    continue;
                if (ascent[sampleIndex] != high || descent[sampleIndex] != low)
                    boundary += 1f;
                else if (dy == 0 && dx == -1)
                    connectionMask |= 1;
                else if (dy == 0 && dx == 1)
                    connectionMask |= 2;
                else if (dy == -1 && dx == 0)
                    connectionMask |= 4;
                else if (dy == 1 && dx == 0)
                    connectionMask |= 8;
                neighborSum += scalar[sampleIndex];
                count += 1f;
            }
        }

        var average = neighborSum / Hlsl.Max(count, 1f);
        var ridge = Hlsl.Max(center - average, 0f);
        var valley = Hlsl.Max(average - center, 0f);
        topology[index] = new Float4(boundary / Hlsl.Max(count, 1f), ridge, valley, 0f);
        connectivity[index] = connectionMask;
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct SlicedTransportShader(
    ReadWriteBuffer<Float4> features,
    ReadWriteBuffer<Float4> topology,
    ReadWriteBuffer<int> ascent,
    ReadWriteBuffer<int> descent,
    ReadWriteBuffer<Float4> output,
    int material,
    float strength,
    float variation,
    float patternScale,
    int seed,
    int sampleCount,
    int width,
    int height) : IComputeShader
{
    private readonly ReadWriteBuffer<Float4> features = features;
    private readonly ReadWriteBuffer<Float4> topology = topology;
    private readonly ReadWriteBuffer<int> ascent = ascent;
    private readonly ReadWriteBuffer<int> descent = descent;
    private readonly ReadWriteBuffer<Float4> output = output;
    private readonly int material = material;
    private readonly float strength = strength;
    private readonly float variation = variation;
    private readonly float patternScale = patternScale;
    private readonly int seed = seed;
    private readonly int sampleCount = sampleCount;
    private readonly int width = width;
    private readonly int height = height;

    public void Execute()
    {
        var x = ThreadIds.X;
        var y = ThreadIds.Y;
        if (x >= width || y >= height)
            return;

        var index = y * width + x;
        var centerSource = features[index];
        if (centerSource.W <= 0f)
        {
            output[index] = centerSource;
            return;
        }

        var center = new Float4(centerSource.X, centerSource.Y, centerSource.Z, topology[index].Y - topology[index].Z);
        var high = ascent[index];
        var low = descent[index];
        var mean = center;
        var valid = 1f;
        var direction0 = Direction(0);
        var direction1 = Direction(1);
        var direction2 = Direction(2);
        var direction3 = Direction(3);
        var direction4 = Direction(4);
        var direction5 = Direction(5);
        var direction6 = Direction(6);
        var direction7 = Direction(7);
        var centerProjection0 = Dot(center, direction0);
        var centerProjection1 = Dot(center, direction1);
        var centerProjection2 = Dot(center, direction2);
        var centerProjection3 = Dot(center, direction3);
        var centerProjection4 = Dot(center, direction4);
        var centerProjection5 = Dot(center, direction5);
        var centerProjection6 = Dot(center, direction6);
        var centerProjection7 = Dot(center, direction7);
        var rank0 = 0.5f;
        var rank1 = 0.5f;
        var rank2 = 0.5f;
        var rank3 = 0.5f;
        var rank4 = 0.5f;
        var rank5 = 0.5f;
        var rank6 = 0.5f;
        var rank7 = 0.5f;

        for (var sample = 0; sample < sampleCount; sample++)
        {
            var candidate = CandidateIndex(x, y, high, low, sample);
            if (ascent[candidate] != high || descent[candidate] != low || features[candidate].W <= 0f)
                continue;
            var feature = features[candidate];
            var structure = topology[candidate].Y - topology[candidate].Z;
            var candidateFeature = new Float4(feature.X, feature.Y, feature.Z, structure);
            mean += candidateFeature;
            valid += 1f;
            var tieBreak = candidate < index;
            var projected0 = Dot(candidateFeature, direction0);
            var projected1 = Dot(candidateFeature, direction1);
            var projected2 = Dot(candidateFeature, direction2);
            var projected3 = Dot(candidateFeature, direction3);
            var projected4 = Dot(candidateFeature, direction4);
            var projected5 = Dot(candidateFeature, direction5);
            var projected6 = Dot(candidateFeature, direction6);
            var projected7 = Dot(candidateFeature, direction7);
            rank0 += projected0 < centerProjection0 || (projected0 == centerProjection0 && tieBreak) ? 1f : 0f;
            rank1 += projected1 < centerProjection1 || (projected1 == centerProjection1 && tieBreak) ? 1f : 0f;
            rank2 += projected2 < centerProjection2 || (projected2 == centerProjection2 && tieBreak) ? 1f : 0f;
            rank3 += projected3 < centerProjection3 || (projected3 == centerProjection3 && tieBreak) ? 1f : 0f;
            rank4 += projected4 < centerProjection4 || (projected4 == centerProjection4 && tieBreak) ? 1f : 0f;
            rank5 += projected5 < centerProjection5 || (projected5 == centerProjection5 && tieBreak) ? 1f : 0f;
            rank6 += projected6 < centerProjection6 || (projected6 == centerProjection6 && tieBreak) ? 1f : 0f;
            rank7 += projected7 < centerProjection7 || (projected7 == centerProjection7 && tieBreak) ? 1f : 0f;
        }
        mean /= valid;

        var targetMean = StyleMean(mean);
        var reconstructed = new Float4(0f, 0f, 0f, 0f);
        reconstructed += direction0 * MapProjection(centerProjection0, rank0, valid, targetMean, direction0, 0) * 0.5f;
        reconstructed += direction1 * MapProjection(centerProjection1, rank1, valid, targetMean, direction1, 1) * 0.5f;
        reconstructed += direction2 * MapProjection(centerProjection2, rank2, valid, targetMean, direction2, 2) * 0.5f;
        reconstructed += direction3 * MapProjection(centerProjection3, rank3, valid, targetMean, direction3, 3) * 0.5f;
        reconstructed += direction4 * MapProjection(centerProjection4, rank4, valid, targetMean, direction4, 4) * 0.5f;
        reconstructed += direction5 * MapProjection(centerProjection5, rank5, valid, targetMean, direction5, 5) * 0.5f;
        reconstructed += direction6 * MapProjection(centerProjection6, rank6, valid, targetMean, direction6, 6) * 0.5f;
        reconstructed += direction7 * MapProjection(centerProjection7, rank7, valid, targetMean, direction7, 7) * 0.5f;

        output[index] = new Float4(
            Hlsl.Saturate(reconstructed.X),
            reconstructed.Y,
            reconstructed.Z,
            centerSource.W);
    }

    private int CandidateIndex(int x, int y, int high, int low, int sample)
    {
        var key = (uint)high * 0x9e3779b9u ^ (uint)low * 0x85ebca6bu ^ (uint)seed * 0xc2b2ae35u ^ (uint)sample * 0x27d4eb2fu;
        var first = Hash(key);
        var second = Hash(first ^ 0x68bc21ebu);
        int sx;
        int sy;
        if (sample < sampleCount / 2)
        {
            var radius = Hlsl.Max((int)patternScale * 2, 4);
            sx = x + (int)((float)first * 2.3283064e-10f * (radius * 2 + 1)) - radius;
            sy = y + (int)((float)second * 2.3283064e-10f * (radius * 2 + 1)) - radius;
            sx = sx < 0 ? 0 : sx >= width ? width - 1 : sx;
            sy = sy < 0 ? 0 : sy >= height ? height - 1 : sy;
        }
        else
        {
            sx = Hlsl.Min((int)((float)first * 2.3283064e-10f * width), width - 1);
            sy = Hlsl.Min((int)((float)second * 2.3283064e-10f * height), height - 1);
        }
        return sy * width + sx;
    }

    private uint Hash(uint value)
    {
        value ^= value >> 16;
        value *= 0x7feb352du;
        value ^= value >> 15;
        value *= 0x846ca68bu;
        value ^= value >> 16;
        return value;
    }

    private Float4 Direction(int index)
    {
        var y = (index & 1) == 0 ? -0.5f : 0.5f;
        var z = (index & 2) == 0 ? -0.5f : 0.5f;
        var w = (index & 4) == 0 ? -0.5f : 0.5f;
        return new Float4(0.5f, y, z, w);
    }

    private float Dot(Float4 a, Float4 b)
        => a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;

    private float MapProjection(float centerProjection, float rank, float projectionCount, Float4 targetMean, Float4 direction, int projection)
    {
        var quantile = rank / projectionCount;
        var centered = quantile * 2f - 1f;
        var shaped = (centered < 0f ? -1f : 1f) * Hlsl.Sqrt(Hlsl.Abs(centered));
        var targetProjection = Dot(targetMean, direction) + shaped * ProjectionSpread(projection);
        return centerProjection + (targetProjection - centerProjection) * strength;
    }

    private Float4 StyleMean(Float4 mean)
    {
        var amount = Hlsl.Saturate(variation);
        if (material == 0)
            return Lerp(mean, new Float4(0.82f, 0.012f, 0.035f, 0f), amount * 0.75f);
        if (material == 1)
            return new Float4(mean.X, mean.Y * (1f + 0.65f * amount), mean.Z * (1f + 0.65f * amount), mean.W);
        if (material == 2)
            return Lerp(mean, new Float4(0.55f, 0.105f, 0.105f, 0.02f), amount * 0.7f);
        if (material == 3)
            return Lerp(mean, new Float4(0.76f, 0.035f, 0.105f, -0.01f), amount * 0.75f);
        if (material == 4)
            return Lerp(mean, new Float4(0.84f, -0.035f, -0.09f, 0.035f), amount * 0.72f);
        return new Float4(mean.X, mean.Y * (1f + 0.25f * amount), mean.Z * (1f + 0.25f * amount), 0f);
    }

    private Float4 Lerp(Float4 a, Float4 b, float amount)
        => a + (b - a) * amount;

    private float ProjectionSpread(int projection)
    {
        var baseSpread = 0.055f;
        if (material == 0)
            baseSpread = 0.045f;
        else if (material == 1)
            baseSpread = 0.095f;
        else if (material == 2)
            baseSpread = 0.08f;
        else if (material == 3)
            baseSpread = 0.06f;
        else if (material == 4)
            baseSpread = 0.075f;
        var modulation = 0.82f + 0.06f * (float)projection;
        return baseSpread * modulation * Hlsl.Max(variation, 0.05f);
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct ReactionInitializeShader(
    ReadWriteBuffer<Float4> topology,
    ReadWriteBuffer<int> ascent,
    ReadWriteBuffer<int> descent,
    ReadWriteBuffer<Float2> output,
    int material,
    float patternScale,
    int seed,
    int width,
    int height) : IComputeShader
{
    private readonly ReadWriteBuffer<Float4> topology = topology;
    private readonly ReadWriteBuffer<int> ascent = ascent;
    private readonly ReadWriteBuffer<int> descent = descent;
    private readonly ReadWriteBuffer<Float2> output = output;
    private readonly int material = material;
    private readonly float patternScale = patternScale;
    private readonly int seed = seed;
    private readonly int width = width;
    private readonly int height = height;

    public void Execute()
    {
        var x = ThreadIds.X;
        var y = ThreadIds.Y;
        if (x >= width || y >= height)
            return;
        var index = y * width + x;
        var topologyValue = topology[index];
        var cell = Hlsl.Max((int)patternScale, 1);
        var cellX = x / cell;
        var cellY = y / cell;
        var hash = Hash((uint)cellX * 0x9e3779b9u ^ (uint)cellY * 0x85ebca6bu ^ (uint)ascent[index] ^ (uint)descent[index] ^ (uint)seed);
        var random = hash * 2.3283064e-10f;
        var probability = material == 5 ? 0.38f : material == 4 ? 0.24f : 0.18f;
        var seeded = random < probability || topologyValue.X > 0.45f;
        var v = seeded ? 0.22f + 0.16f * random : 0f;
        output[index] = new Float2(1f - v * 0.5f, v);
    }

    private uint Hash(uint value)
    {
        value ^= value >> 16;
        value *= 0x7feb352du;
        value ^= value >> 15;
        value *= 0x846ca68bu;
        value ^= value >> 16;
        return value;
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct ReactionDiffusionShader(
    ReadWriteBuffer<Float2> input,
    ReadWriteBuffer<Float2> output,
    ReadWriteBuffer<int> ascent,
    ReadWriteBuffer<int> descent,
    int material,
    float patternScale,
    int width,
    int height) : IComputeShader
{
    private readonly ReadWriteBuffer<Float2> input = input;
    private readonly ReadWriteBuffer<Float2> output = output;
    private readonly ReadWriteBuffer<int> ascent = ascent;
    private readonly ReadWriteBuffer<int> descent = descent;
    private readonly int material = material;
    private readonly float patternScale = patternScale;
    private readonly int width = width;
    private readonly int height = height;

    public void Execute()
    {
        var x = ThreadIds.X;
        var y = ThreadIds.Y;
        if (x >= width || y >= height)
            return;
        var index = y * width + x;
        var center = input[index];
        var step = Hlsl.Max((int)(patternScale / 12f), 1);
        var laplacian = Sample(x - step, y, index, center) + Sample(x + step, y, index, center)
            + Sample(x, y - step, index, center) + Sample(x, y + step, index, center) - center * 4f;

        var feed = material == 0 ? 0.037f
            : material == 1 ? 0.029f
            : material == 2 ? 0.026f
            : material == 3 ? 0.046f
            : material == 4 ? 0.022f
            : 0.054f;
        var kill = material == 0 ? 0.060f
            : material == 1 ? 0.057f
            : material == 2 ? 0.055f
            : material == 3 ? 0.063f
            : material == 4 ? 0.051f
            : 0.062f;
        var reaction = center.X * center.Y * center.Y;
        var u = center.X + 0.16f * laplacian.X - reaction + feed * (1f - center.X);
        var v = center.Y + 0.08f * laplacian.Y + reaction - (feed + kill) * center.Y;
        output[index] = new Float2(Hlsl.Saturate(u), Hlsl.Saturate(v));
    }

    private Float2 Sample(int x, int y, int centerIndex, Float2 fallback)
    {
        x = x < 0 ? 0 : x >= width ? width - 1 : x;
        y = y < 0 ? 0 : y >= height ? height - 1 : y;
        var index = y * width + x;
        if (ascent[index] != ascent[centerIndex] || descent[index] != descent[centerIndex])
            return fallback;
        return input[index];
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct PoissonRhsShader(
    ReadWriteBuffer<float> scalar,
    ReadWriteBuffer<Float4> transported,
    ReadWriteBuffer<Float4> topology,
    ReadWriteBuffer<Float2> reaction,
    ReadWriteBuffer<int> connectivity,
    ReadWriteBuffer<float> rhs,
    ReadWriteBuffer<float> initial,
    float patternStrength,
    float reconstruction,
    int width,
    int height) : IComputeShader
{
    private readonly ReadWriteBuffer<float> scalar = scalar;
    private readonly ReadWriteBuffer<Float4> transported = transported;
    private readonly ReadWriteBuffer<Float4> topology = topology;
    private readonly ReadWriteBuffer<Float2> reaction = reaction;
    private readonly ReadWriteBuffer<int> connectivity = connectivity;
    private readonly ReadWriteBuffer<float> rhs = rhs;
    private readonly ReadWriteBuffer<float> initial = initial;
    private readonly float patternStrength = patternStrength;
    private readonly float reconstruction = reconstruction;
    private readonly int width = width;
    private readonly int height = height;

    public void Execute()
    {
        var x = ThreadIds.X;
        var y = ThreadIds.Y;
        if (x >= width || y >= height)
            return;
        var index = y * width + x;
        var connectivityValue = connectivity[index];
        if (connectivityValue < 0)
        {
            rhs[index] = 0f;
            initial[index] = 0f;
            return;
        }
        var detail = (reaction[index].Y - 0.18f) * patternStrength * 0.24f;
        var topologicalRelief = (topology[index].Y - topology[index].Z) * reconstruction * 2.5f + topology[index].X * reconstruction * 0.035f;
        var desired = Hlsl.Saturate(transported[index].X + detail + topologicalRelief);
        var guidanceCenter = scalar[index] + detail + topologicalRelief;
        var guidanceLaplacian = Guidance(x - 1, y, guidanceCenter, connectivityValue & 1)
            + Guidance(x + 1, y, guidanceCenter, connectivityValue & 2)
            + Guidance(x, y - 1, guidanceCenter, connectivityValue & 4)
            + Guidance(x, y + 1, guidanceCenter, connectivityValue & 8)
            - 4f * guidanceCenter;
        rhs[index] = desired - reconstruction * guidanceLaplacian;
        initial[index] = desired;
    }

    private float Guidance(int x, int y, float fallback, int connected)
    {
        if (connected == 0)
            return fallback;
        x = x < 0 ? 0 : x >= width ? width - 1 : x;
        y = y < 0 ? 0 : y >= height ? height - 1 : y;
        var index = y * width + x;
        return scalar[index] + (reaction[index].Y - 0.18f) * patternStrength * 0.24f
            + (topology[index].Y - topology[index].Z) * reconstruction * 2.5f
            + topology[index].X * reconstruction * 0.035f;
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct PoissonJacobiShader(
    ReadWriteBuffer<float> input,
    ReadWriteBuffer<float> rhs,
    ReadWriteBuffer<int> connectivity,
    ReadWriteBuffer<float> output,
    float reconstruction,
    int width,
    int height) : IComputeShader
{
    private readonly ReadWriteBuffer<float> input = input;
    private readonly ReadWriteBuffer<float> rhs = rhs;
    private readonly ReadWriteBuffer<int> connectivity = connectivity;
    private readonly ReadWriteBuffer<float> output = output;
    private readonly float reconstruction = reconstruction;
    private readonly int width = width;
    private readonly int height = height;

    public void Execute()
    {
        var x = ThreadIds.X;
        var y = ThreadIds.Y;
        if (x >= width || y >= height)
            return;
        var index = y * width + x;
        var connectivityValue = connectivity[index];
        if (connectivityValue < 0)
        {
            output[index] = 0f;
            return;
        }
        var center = input[index];
        var sum = Sample(x - 1, y, center, connectivityValue & 1)
            + Sample(x + 1, y, center, connectivityValue & 2)
            + Sample(x, y - 1, center, connectivityValue & 4)
            + Sample(x, y + 1, center, connectivityValue & 8);
        output[index] = Hlsl.Saturate((rhs[index] + reconstruction * sum) / (1f + 4f * reconstruction));
    }

    private float Sample(int x, int y, float fallback, int connected)
    {
        if (connected == 0)
            return fallback;
        x = x < 0 ? 0 : x >= width ? width - 1 : x;
        y = y < 0 ? 0 : y >= height ? height - 1 : y;
        return input[y * width + x];
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct MaterialFinalizeShader(
    ReadWriteBuffer<int> sourceOutput,
    ReadWriteBuffer<Float4> transported,
    ReadWriteBuffer<Float4> topology,
    ReadWriteBuffer<Float2> reaction,
    ReadWriteBuffer<float> poisson,
    ReadWriteBuffer<int> connectivity,
    int material,
    float patternStrength,
    float relief,
    float lightAngle,
    float lightElevation,
    int width,
    int height) : IComputeShader
{
    private readonly ReadWriteBuffer<int> sourceOutput = sourceOutput;
    private readonly ReadWriteBuffer<Float4> transported = transported;
    private readonly ReadWriteBuffer<Float4> topology = topology;
    private readonly ReadWriteBuffer<Float2> reaction = reaction;
    private readonly ReadWriteBuffer<float> poisson = poisson;
    private readonly ReadWriteBuffer<int> connectivity = connectivity;
    private readonly int material = material;
    private readonly float patternStrength = patternStrength;
    private readonly float relief = relief;
    private readonly float lightAngle = lightAngle;
    private readonly float lightElevation = lightElevation;
    private readonly int width = width;
    private readonly int height = height;

    public void Execute()
    {
        var x = ThreadIds.X;
        var y = ThreadIds.Y;
        if (x >= width || y >= height)
            return;
        var index = y * width + x;
        var packed = sourceOutput[index];
        var alphaByte = (packed >> 24) & 255;
        if (alphaByte == 0)
        {
            sourceOutput[index] = 0;
            return;
        }

        var lab = transported[index];
        var lValue = poisson[index];
        var lRoot = lValue + 0.3963377774f * lab.Y + 0.2158037573f * lab.Z;
        var mRoot = lValue - 0.1055613458f * lab.Y - 0.0638541728f * lab.Z;
        var sRoot = lValue - 0.0894841775f * lab.Y - 1.2914855480f * lab.Z;
        var l = lRoot * lRoot * lRoot;
        var m = mRoot * mRoot * mRoot;
        var s = sRoot * sRoot * sRoot;
        var red = 4.0767416621f * l - 3.3077115913f * m + 0.2309699292f * s;
        var green = -1.2684380046f * l + 2.6097574011f * m - 0.3413193965f * s;
        var blue = -0.0041960863f * l - 0.7034186147f * m + 1.7076147010f * s;

        var connectivityValue = connectivity[index];
        var gradientX = SamplePoisson(x + 1, y, lValue, connectivityValue & 2) - SamplePoisson(x - 1, y, lValue, connectivityValue & 1);
        var gradientY = SamplePoisson(x, y + 1, lValue, connectivityValue & 8) - SamplePoisson(x, y - 1, lValue, connectivityValue & 4);
        var nx = -gradientX * relief * 4f;
        var ny = -gradientY * relief * 4f;
        var inverseLength = 1f / Hlsl.Sqrt(nx * nx + ny * ny + 1f);
        nx *= inverseLength;
        ny *= inverseLength;
        var nz = inverseLength;
        var cosineElevation = Hlsl.Cos(lightElevation);
        var lx = Hlsl.Cos(lightAngle) * cosineElevation;
        var ly = Hlsl.Sin(lightAngle) * cosineElevation;
        var lz = Hlsl.Sin(lightElevation);
        var diffuse = Hlsl.Saturate(nx * lx + ny * ly + nz * lz);
        var halfX = lx;
        var halfY = ly;
        var halfZ = lz + 1f;
        var halfInverse = 1f / Hlsl.Sqrt(halfX * halfX + halfY * halfY + halfZ * halfZ);
        var specularBase = Hlsl.Saturate(nx * halfX * halfInverse + ny * halfY * halfInverse + nz * halfZ * halfInverse);
        var exponent = material == 0 ? 72f : material == 1 ? 42f : material == 2 ? 96f : material == 3 ? 18f : material == 4 ? 128f : 24f;
        var specularStrength = material == 0 ? 0.22f : material == 1 ? 0.18f : material == 2 ? 0.34f : material == 3 ? 0.06f : material == 4 ? 0.38f : 0.08f;
        var specular = Hlsl.Pow(specularBase, exponent) * specularStrength;
        var weave = material == 5 ? (0.5f + 0.5f * Hlsl.Sin(x * 3.14159265f) * Hlsl.Sin(y * 3.14159265f)) * 0.08f * patternStrength : 0f;
        var boundary = topology[index].X * (0.06f + 0.08f * relief);
        var reactionDetail = (reaction[index].Y - 0.2f) * 0.08f * patternStrength;
        var shade = 0.72f + 0.28f * diffuse + boundary + reactionDetail - weave;
        red = Hlsl.Saturate(red * shade + specular);
        green = Hlsl.Saturate(green * shade + specular);
        blue = Hlsl.Saturate(blue * shade + specular);

        red = ToSrgb(red);
        green = ToSrgb(green);
        blue = ToSrgb(blue);
        var alpha = alphaByte / 255f;
        var redByte = (int)Hlsl.Round(Hlsl.Saturate(red * alpha) * 255f);
        var greenByte = (int)Hlsl.Round(Hlsl.Saturate(green * alpha) * 255f);
        var blueByte = (int)Hlsl.Round(Hlsl.Saturate(blue * alpha) * 255f);
        sourceOutput[index] = (alphaByte << 24) | (redByte << 16) | (greenByte << 8) | blueByte;
    }

    private float SamplePoisson(int x, int y, float fallback, int connected)
    {
        if (connected == 0)
            return fallback;
        x = x < 0 ? 0 : x >= width ? width - 1 : x;
        y = y < 0 ? 0 : y >= height ? height - 1 : y;
        return poisson[y * width + x];
    }

    private float ToSrgb(float value)
        => value <= 0.0031308f ? value * 12.92f : 1.055f * Hlsl.Pow(value, 1f / 2.4f) - 0.055f;
}
