using ComputeSharp;

namespace TopologicalMaterialField;

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct MaterialPreprocessShader(
    ReadOnlyBuffer<int> source,
    ReadWriteBuffer<Float4> features,
    ReadWriteBuffer<float> scalar,
    int width,
    int height) : IComputeShader
{
    private readonly ReadOnlyBuffer<int> source = source;
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
    ReadOnlyBuffer<int> source,
    ReadWriteBuffer<float> input,
    ReadWriteBuffer<float> output,
    float scale,
    int width,
    int height) : IComputeShader
{
    private readonly ReadOnlyBuffer<int> source = source;
    private readonly ReadWriteBuffer<float> input = input;
    private readonly ReadWriteBuffer<float> output = output;
    private readonly float scale = scale;
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
        var spatialDenominator = Hlsl.Max(scale * scale, 1f);
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
                var spatial = Hlsl.Exp(-(dx * dx + dy * dy) / spatialDenominator);
                var difference = value - center;
                var range = Hlsl.Exp(-(difference * difference) / 0.01f);
                var weight = spatial * range;
                sum += value * weight;
                weightSum += weight;
            }
        }
        output[index] = sum / Hlsl.Max(weightSum, 1e-6f);
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct FlowInitializeShader(
    ReadOnlyBuffer<int> source,
    ReadWriteBuffer<float> scalar,
    ReadWriteBuffer<int> ascent,
    ReadWriteBuffer<int> descent,
    float threshold,
    int width,
    int height) : IComputeShader
{
    private readonly ReadOnlyBuffer<int> source = source;
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
    ReadOnlyBuffer<int> source,
    ReadWriteBuffer<float> scalar,
    ReadWriteBuffer<int> ascent,
    ReadWriteBuffer<int> descent,
    ReadWriteBuffer<Float4> topology,
    int width,
    int height) : IComputeShader
{
    private readonly ReadOnlyBuffer<int> source = source;
    private readonly ReadWriteBuffer<float> scalar = scalar;
    private readonly ReadWriteBuffer<int> ascent = ascent;
    private readonly ReadWriteBuffer<int> descent = descent;
    private readonly ReadWriteBuffer<Float4> topology = topology;
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
            return;
        }

        var high = ascent[index];
        var low = descent[index];
        var center = scalar[index];
        var boundary = 0f;
        var neighborSum = 0f;
        var count = 0f;
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
                neighborSum += scalar[sampleIndex];
                count += 1f;
            }
        }

        var average = neighborSum / Hlsl.Max(count, 1f);
        var ridge = Hlsl.Max(center - average, 0f);
        var valley = Hlsl.Max(average - center, 0f);
        topology[index] = new Float4(boundary / Hlsl.Max(count, 1f), ridge, valley, Hash01(high, low));
    }

    private float Hash01(int a, int b)
    {
        var value = (uint)a * 0x9e3779b9u ^ (uint)b * 0x85ebca6bu;
        value ^= value >> 16;
        value *= 0x7feb352du;
        value ^= value >> 15;
        value *= 0x846ca68bu;
        value ^= value >> 16;
        return value * 2.3283064e-10f;
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

        for (var sample = 0; sample < sampleCount; sample++)
        {
            var candidate = CandidateIndex(x, y, high, low, sample);
            if (ascent[candidate] != high || descent[candidate] != low || features[candidate].W <= 0f)
                continue;
            var feature = features[candidate];
            var structure = topology[candidate].Y - topology[candidate].Z;
            mean += new Float4(feature.X, feature.Y, feature.Z, structure);
            valid += 1f;
        }
        mean /= valid;

        var targetMean = StyleMean(mean);
        var reconstructed = new Float4(0f, 0f, 0f, 0f);
        for (var projection = 0; projection < 8; projection++)
        {
            var direction = Direction(projection);
            var centerProjection = Dot(center, direction);
            var rank = 0.5f;
            var projectionCount = 1f;
            for (var sample = 0; sample < sampleCount; sample++)
            {
                var candidate = CandidateIndex(x, y, high, low, sample);
                if (ascent[candidate] != high || descent[candidate] != low || features[candidate].W <= 0f)
                    continue;
                var feature = features[candidate];
                var candidateFeature = new Float4(feature.X, feature.Y, feature.Z, topology[candidate].Y - topology[candidate].Z);
                var projected = Dot(candidateFeature, direction);
                if (projected < centerProjection || (projected == centerProjection && candidate < index))
                    rank += 1f;
                projectionCount += 1f;
            }

            var quantile = rank / projectionCount;
            var centered = quantile * 2f - 1f;
            var shaped = (centered < 0f ? -1f : 1f) * Hlsl.Sqrt(Hlsl.Abs(centered));
            var targetProjection = Dot(targetMean, direction) + shaped * ProjectionSpread(projection);
            var mapped = centerProjection + (targetProjection - centerProjection) * strength;
            reconstructed += direction * mapped * 0.5f;
        }

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
        var probability = material == 5 ? 0.38f : material == 4 ? 0.24f : 0.18f;
        var seeded = hash * 2.3283064e-10f < probability || topologyValue.X > 0.45f;
        var v = seeded ? 0.22f + 0.16f * topologyValue.W : 0f;
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
        var detail = (reaction[index].Y - 0.18f) * patternStrength * 0.24f;
        var topologicalRelief = (topology[index].Y - topology[index].Z) * reconstruction * 2.5f + topology[index].X * reconstruction * 0.035f;
        var desired = Hlsl.Saturate(transported[index].X + detail + topologicalRelief);
        var guidanceCenter = scalar[index] + detail + topologicalRelief;
        var guidanceLaplacian = Guidance(x - 1, y) + Guidance(x + 1, y) + Guidance(x, y - 1) + Guidance(x, y + 1) - 4f * guidanceCenter;
        var screening = 1f + reconstruction * 6f;
        rhs[index] = screening * desired - reconstruction * guidanceLaplacian;
        initial[index] = desired;
    }

    private float Guidance(int x, int y)
    {
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
    ReadWriteBuffer<float> output,
    float screening,
    int width,
    int height) : IComputeShader
{
    private readonly ReadWriteBuffer<float> input = input;
    private readonly ReadWriteBuffer<float> rhs = rhs;
    private readonly ReadWriteBuffer<float> output = output;
    private readonly float screening = screening;
    private readonly int width = width;
    private readonly int height = height;

    public void Execute()
    {
        var x = ThreadIds.X;
        var y = ThreadIds.Y;
        if (x >= width || y >= height)
            return;
        var index = y * width + x;
        var sum = Sample(x - 1, y) + Sample(x + 1, y) + Sample(x, y - 1) + Sample(x, y + 1);
        output[index] = Hlsl.Saturate((rhs[index] + sum) / (screening + 4f));
    }

    private float Sample(int x, int y)
    {
        x = x < 0 ? 0 : x >= width ? width - 1 : x;
        y = y < 0 ? 0 : y >= height ? height - 1 : y;
        return input[y * width + x];
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct MaterialFinalizeShader(
    ReadOnlyBuffer<int> source,
    ReadWriteBuffer<Float4> transported,
    ReadWriteBuffer<Float4> topology,
    ReadWriteBuffer<Float2> reaction,
    ReadWriteBuffer<float> poisson,
    ReadWriteBuffer<int> output,
    int material,
    float patternStrength,
    float relief,
    float lightAngle,
    float lightElevation,
    int width,
    int height) : IComputeShader
{
    private readonly ReadOnlyBuffer<int> source = source;
    private readonly ReadWriteBuffer<Float4> transported = transported;
    private readonly ReadWriteBuffer<Float4> topology = topology;
    private readonly ReadWriteBuffer<Float2> reaction = reaction;
    private readonly ReadWriteBuffer<float> poisson = poisson;
    private readonly ReadWriteBuffer<int> output = output;
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
        var packed = source[index];
        var alphaByte = (packed >> 24) & 255;
        if (alphaByte == 0)
        {
            output[index] = 0;
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

        var gradientX = SamplePoisson(x + 1, y) - SamplePoisson(x - 1, y);
        var gradientY = SamplePoisson(x, y + 1) - SamplePoisson(x, y - 1);
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
        output[index] = (alphaByte << 24) | (redByte << 16) | (greenByte << 8) | blueByte;
    }

    private float SamplePoisson(int x, int y)
    {
        x = x < 0 ? 0 : x >= width ? width - 1 : x;
        y = y < 0 ? 0 : y >= height ? height - 1 : y;
        return poisson[y * width + x];
    }

    private float ToSrgb(float value)
        => value <= 0.0031308f ? value * 12.92f : 1.055f * Hlsl.Pow(value, 1f / 2.4f) - 0.055f;
}
