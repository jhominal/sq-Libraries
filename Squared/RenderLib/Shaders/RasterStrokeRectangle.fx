// O3 produces literally 1/3 the instructions of OD or O0 so let's just be kind to the driver
#pragma fxcparams(/O3 /Zi)

#include "RasterStrokeCommon.fxh"

void computePosition(
    float2 a, float2 b,
    float4 cornerWeights, out float2 xy
) {
    // HACK: Slightly increase the radius and pad it to account for
    //  pixel overhang and antialiasing
    float totalRadius = (Constants2.w * 0.55) + 1;

    xy = lerp(a - totalRadius, b + totalRadius, cornerWeights.xy);
}

void RasterStrokeRectangleVertexShader(
    in float4 cornerWeights : NORMAL2,
    in float4 ab_in : POSITION0,
    inout float4 seed : TEXCOORD0,
    inout float4 taper : TEXCOORD1,
    inout float4 colorA : COLOR0,
    inout float4 colorB : COLOR1,
    in  int2 unusedAndWorldSpace : BLENDINDICES1,
    out float2 worldPosition : NORMAL0,
    out float4 result : POSITION0,
    out float4 ab : TEXCOORD3
) {
    ab = ab_in;
    float4 position = float4(ab_in.x, ab_in.y, 0, 1);
    float2 a = ab.xy, b = ab.zw;

    computePosition(a, b, cornerWeights, position.xy);

    float2 adjustedPosition = position.xy;
    worldPosition = adjustedPosition.xy;

    if (unusedAndWorldSpace.y > 0.5) {
        adjustedPosition -= GetViewportPosition().xy;
        adjustedPosition *= GetViewportScale().xy;
    }

    result = TransformPosition(
        float4(adjustedPosition, position.z, 1), true
    );

    // We use the _Accurate conversion function here because the approximation introduces
    //  visible noise for values like (64, 64, 64) when they are dithered.
    // We do the initial conversion in the VS to avoid paying the cost per-fragment, and also
    //  take the opportunity to do a final conversion here for 'simple' fragments so that
    //  it doesn't have to be done per-fragment
    // FIXME: Is this reasonably correct for simple shapes with outlines? Probably
    if (BlendInLinearSpace) {
        colorA = pSRGBToPLinear_Accurate(colorA);
        colorB = pSRGBToPLinear_Accurate(colorB);
    }
}

void RasterStrokeRectangleFragmentShader(
    RASTERSTROKE_FS_ARGS
) {
    result = 0;
    float spacing = max(Constants2.w * Constants2.z, 0.05);
    int count = ceil((ab.w - ab.y) / spacing);
    for (int i = 0; i <= count; i++) {
        float y = min(ab.y + (spacing * i), ab.w);
        float4 _ab = float4(ab.x, y, ab.z, y);
        float4 _seed = float4(seed.x + (i * count / 2) * seed.z, seed.y + (i * seed.w), seed.z, seed.w);
        rasterStrokeLineCommon(
            worldPosition, _ab, _seed, taper, GET_VPOS, colorA, colorB, result
        );
    }

    // Unpremultiply the output, because if we don't we get unpleasant stairstepping artifacts
    //  for alpha gradients because the A we premultiply by does not match the A the GPU selected
    // It's also important to do dithering and sRGB conversion on a result that is not premultiplied
    result.rgb = float4(result.rgb / max(result.a, 0.0001), result.a);

    if (BlendInLinearSpace != OutputInLinearSpace) {
        if (OutputInLinearSpace)
            result.rgb = SRGBToLinear(result).rgb;
        else
            result.rgb = LinearToSRGB(result.rgb);
    }

    result = ApplyDither4(result, GET_VPOS);
}

technique RasterStrokeRectangle
{
    pass P0
    {
        vertexShader = compile vs_3_0 RasterStrokeRectangleVertexShader();
        pixelShader = compile ps_3_0 RasterStrokeRectangleFragmentShader();
    }
}