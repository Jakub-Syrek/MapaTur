namespace MapaTur.Application.Terrain;

/// <summary>
/// The practical split scheme for Cascaded Shadow Maps: divides the camera frustum's [near, far] depth
/// range into cascades whose far distances blend a logarithmic and a uniform partition by <c>lambda</c>
/// (0 = uniform, 1 = logarithmic). Logarithmic packs texel density close to the camera where it matters;
/// uniform keeps distant cascades from collapsing. Pure math so the GL shadow pass just consumes the
/// distances. Cascade <c>i</c> covers [previous far, <c>FarDistances()[i]</c>].
/// </summary>
public static class CascadeShadowSplits
{
    /// <summary>
    /// Far distance (camera-space, metres) of each cascade. The last entry equals <paramref name="far"/>
    /// exactly so the final cascade always reaches the far plane.
    /// </summary>
    public static IReadOnlyList<float> FarDistances(float near, float far, int cascadeCount, float lambda)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(near);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cascadeCount);
        if (far <= near)
        {
            throw new ArgumentOutOfRangeException(nameof(far), far, "far must be greater than near.");
        }
        if (lambda < 0f || lambda > 1f)
        {
            throw new ArgumentOutOfRangeException(nameof(lambda), lambda, "lambda must be in [0,1].");
        }

        var splits = new float[cascadeCount];
        float range = far - near;
        float ratio = far / near;
        for (int i = 1; i <= cascadeCount; i++)
        {
            float p = (float)i / cascadeCount;
            float logSplit = near * MathF.Pow(ratio, p);
            float uniformSplit = near + (range * p);
            splits[i - 1] = (lambda * logSplit) + ((1f - lambda) * uniformSplit);
        }

        // Pin the last cascade to the far plane (guards against float drift in the blended term).
        splits[cascadeCount - 1] = far;
        return splits;
    }
}