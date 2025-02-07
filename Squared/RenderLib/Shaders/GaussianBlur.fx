// HACK: We don't really care about running this in debug mode, since
//  blur operations are so tex (and to a lesser degree arith) intensive
//  that we want to optimize the hell out of them no matter what
#pragma fxcparams(/O3 /Zi)

#include "CompilerWorkarounds.fxh"
#include "ViewTransformCommon.fxh"
#include "FormatCommon.fxh"
#include "BitmapCommon.fxh"
#include "TargetInfo.fxh"
#include "DitherCommon.fxh"
#include "sRGBCommon.fxh"

const float OutlineBias = 0.025;
// HACK: Since we're not fully averaging out the taps, most pixels will have a shadow opacity
//  well above 1.0. We divide it by an arbitrary value to create softer edges
const float OutlineDivisor = 2.2;

// http://dev.theomader.com/gaussian-kernel-calculator/
// Sigma 2, Kernel size 9
uniform int TapCount = 5;
uniform float TapWeights[10] = { 0.20236, 0.179044, 0.124009, 0.067234, 0.028532, 0, 0, 0, 0, 0 };
uniform float2 InverseTapDivisors = float2(1, 1);

// HACK: Setting this any higher than 0.5 produces weird ringing artifacts.
// In practice we're basically super-sampling the matrix... it doesn't make it blurry with a low
//  sigma value at least?
const float TapSpacingFactor = 0.5;

// HACK: The default mip bias for things like text atlases is unnecessarily blurry, especially if
//  the atlas is high-DPI
#define DefaultShadowedTopMipBias MIP_BIAS
uniform const float  ShadowedTopMipBias, ShadowMipBias, OutlineExponent = 1.2;
uniform const bool   PremultiplyTexture;

uniform const float2 ShadowOffset;

uniform const float4 GlobalShadowColor;

sampler TapSampler : register(s0) {
    Texture = (BitmapTexture);
    MipFilter = POINT;
    MinFilter = LINEAR;
    MagFilter = LINEAR;
    AddressU = CLAMP;
    AddressV = CLAMP;
};

float computeMip (in float2 texCoordPx) {
    float2 dx = ddx(texCoordPx), dy = ddy(texCoordPx);
    float mag = max(dot(dx, dx), dot(dy, dy));
    return 0.5 * log2(mag);
}

float tapA(
    in float2 texCoord,
    in float4 texRgn,
    in float mipBias
) {
    // FIXME: Use extract value so this works with single channel textures
    float4 texColor = tex2Dbias(TapSampler, float4(clamp2(texCoord, texRgn.xy, texRgn.zw), 0, mipBias));
    return ExtractMask(texColor, BitmapTraits);
}

float4 tap(
    in float2 texCoord,
    in float4 texRgn
) {
    float4 pSRGB = tex2Dlod(TapSampler, float4(clamp2(texCoord, texRgn.xy, texRgn.zw), 0, 0));
    return pSRGBToPLinear(pSRGB);
}

float4 gaussianBlur1D(
    in float4 centerTap,
    in float2 stepSize,
    in float2 texCoord,
    in float4 texRgn
) {
    float4 sum = centerTap * TapWeights[0];

    for (int i = 1; i < TapCount; i += 1) {
        float2 offset2 = stepSize * i;

        sum += tap(texCoord - offset2, texRgn) * TapWeights[i];
        sum += tap(texCoord + offset2, texRgn) * TapWeights[i];
    }

    return sum;
}

float gaussianBlurA(
    in float centerTap,
    in float2 stepSize,
    in float2 texCoord,
    in float4 texRgn,
    in float mipBias
) {
    float sum = centerTap * TapWeights[0];

    for (int i = 1; i < TapCount; i += 1) {
        float2 offset2 = stepSize * i;

        sum += tapA(texCoord - offset2, texRgn, mipBias) * TapWeights[i];
        sum += tapA(texCoord + offset2, texRgn, mipBias) * TapWeights[i];
    }

    return sum;
}

float4 psEpilogue (
    in float4 texColor,
    in float4 multiplyColor,
    in float4 addColor
) {
    texColor = ExtractRgba(texColor, BitmapTraits);
    texColor = pLinearToPSRGB(texColor);

    if (PremultiplyTexture)
        texColor.rgb *= texColor.a;

    addColor.rgb *= addColor.a;
    addColor.a = 0;

    float4 result = multiplyColor * texColor;
    result += (addColor * result.a);
    return result;
}

void HorizontalGaussianBlurPixelShader(
    in float4 multiplyColor : COLOR0, 
    in float4 addColor : COLOR1, 
    in float2 texCoord : TEXCOORD0,
    in float4 texRgn : TEXCOORD1,
    out float4 result : COLOR0
) {
    float4 centerTap = tap(texCoord, texRgn);
    float4 sum = gaussianBlur1D(centerTap, HalfTexel * float2(TapSpacingFactor, 0), texCoord, texRgn);
    result = psEpilogue(sum * InverseTapDivisors.x, multiplyColor, addColor);
}

void VerticalGaussianBlurPixelShader(
    in float4 multiplyColor : COLOR0, 
    in float4 addColor : COLOR1, 
    in float2 texCoord : TEXCOORD0,
    in float4 texRgn : TEXCOORD1,
    out float4 result : COLOR0
) {
    float4 centerTap = tap(texCoord, texRgn);
    float4 sum = gaussianBlur1D(centerTap, HalfTexel * float2(0, TapSpacingFactor), texCoord, texRgn);
    result = psEpilogue(sum * InverseTapDivisors.x, multiplyColor, addColor);
}

void RadialGaussianBlurPixelShader(
    in float4 multiplyColor : COLOR0,
    in float4 addColor : COLOR1,
    in float2 texCoord : TEXCOORD0,
    in float4 texRgn : TEXCOORD1,
    out float4 result : COLOR0
) {
    float2 innerStepSize = HalfTexel * float2(TapSpacingFactor, 0), outerStepSize = HalfTexel * float2(0, TapSpacingFactor);

    float4 centerTap = tap(texCoord, texRgn);
    float4 centerValue = gaussianBlur1D(centerTap, innerStepSize, texCoord, texRgn);

    float4 sum = centerValue * TapWeights[0];

    for (int i = 1; i < TapCount; i += 1) {
        float2 outerOffset = outerStepSize * i;
        
        sum += gaussianBlur1D(centerTap, innerStepSize, texCoord - outerOffset, texRgn) * TapWeights[i];
        sum += gaussianBlur1D(centerTap, innerStepSize, texCoord + outerOffset, texRgn) * TapWeights[i];
    }

    result = psEpilogue(sum * InverseTapDivisors.y, multiplyColor, addColor);
}

// porter-duff A over B
float4 over(float4 top, float topOpacity, float4 bottom, float bottomOpacity) {
    top *= topOpacity;
    bottom *= bottomOpacity;

    return top + (bottom * (1 - top.a));
}

void GaussianOutlinedPixelShader(
    in float4 multiplyColor : COLOR0,
    in float4 addColor : COLOR1,
    in float4 shadowColorIn : COLOR2,
    in float2 texCoord : TEXCOORD0,
    in float4 texRgn : TEXCOORD1,
    out float4 result : COLOR0
) {
    addColor.rgb *= addColor.a;
    addColor.a = 0;

    float4 traits = BitmapTraits;
    float4 texColor = tex2Dbias(TapSampler, float4(clamp2(texCoord, texRgn.xy, texRgn.zw), 0, ShadowedTopMipBias + DefaultShadowedTopMipBias));
    bool needPremul = PremultiplyTexture || (shadowColorIn.a < 0) || (traits.z >= 1);
    traits.z = 0;
    shadowColorIn.a = abs(shadowColorIn.a);

    // Artificially expand spacing since we're going for outlines
    // We compute the mip bias (using ddx/ddy) to determine how far out we can space the taps if the source texture
    //  is being scaled down, since the actual texels are farther apart if we're not reading from mip 0
    // This should provide a roughly constant outline size in screen space pixels regardless of scale
    // TODO: Factor in the mip bias as well
    float mip = computeMip(texCoord * BitmapTextureSize.xy);
    float spacingFactor = TapSpacingFactor * clamp(mip + 1.5, 1, 8);
    float2 innerStepSize = HalfTexel * float2(spacingFactor, 0), outerStepSize = HalfTexel * float2(0, spacingFactor);

    texCoord -= ShadowOffset * HalfTexel * 2;

    float centerTap = ExtractMask(texColor, traits);
    texColor = ExtractRgba(texColor, traits);
    float centerValue = gaussianBlurA(centerTap, innerStepSize, texCoord, texRgn, ShadowMipBias);

    float sum = centerTap;

    for (int i = 1; i < TapCount; i += 1) {
        float2 outerOffset = outerStepSize * i;

        sum += gaussianBlurA(centerTap, innerStepSize, texCoord - outerOffset, texRgn, ShadowMipBias) * TapWeights[i];
        sum += gaussianBlurA(centerTap, innerStepSize, texCoord + outerOffset, texRgn, ShadowMipBias) * TapWeights[i];
    }

    float shadowAlpha = saturate(((sum * InverseTapDivisors.x) - OutlineBias) / OutlineDivisor);
    shadowAlpha = saturate(shadowAlpha * max(1, shadowColorIn.a));
    shadowAlpha = pow(shadowAlpha, OutlineExponent);

    float4 shadowColor = float4(shadowColorIn.rgb, 1) * saturate(shadowColorIn.a);
    shadowColor = lerp(GlobalShadowColor, shadowColor, shadowColorIn.a > 0 ? 1 : 0);

    float4 overColor = (texColor * multiplyColor);
    overColor += (addColor * overColor.a);

    // FIXME: Something about pSRGB is totally hosed here and produces garbage data at image edges, so we have to fudge it by hand
    overColor.rgb = SRGBToLinear(overColor.rgb);
    if (needPremul)
        overColor.rgb *= overColor.a;
    result = pLinearToPSRGB(over(overColor, 1, pSRGBToPLinear(shadowColor), shadowAlpha * multiplyColor.a));

    /*
    // Significantly improves the appearance of colored outlines and/or colored text
    float4 shadowSRGB = pSRGBToPLinear(shadowColor);

    float4 overSRGB = pSRGBToPLinear(overColor);
    result = over(overSRGB, 1, shadowSRGB, shadowAlpha * multiplyColor.a);
    result = pLinearToPSRGB(result);
    */
}

void GaussianOutlinedPixelShaderWithDiscard(
    in float4 multiplyColor : COLOR0,
    in float4 addColor : COLOR1,
    in float4 outlineColorIn : COLOR2,
    in float2 texCoord : TEXCOORD0,
    in float4 texRgn : TEXCOORD1,
    out float4 result : COLOR0
) {
    GaussianOutlinedPixelShader(
        multiplyColor, addColor,
        outlineColorIn, texCoord, texRgn,
        result
    );

    const float discardThreshold = (0.5 / 255.0);
    clip(result.a - discardThreshold);
}

technique WorldSpaceHorizontalGaussianBlur
{
    pass P0
    {
        vertexShader = compile vs_3_0 WorldSpaceVertexShader();
        pixelShader = compile ps_3_0 HorizontalGaussianBlurPixelShader();
    }
}

technique ScreenSpaceHorizontalGaussianBlur
{
    pass P0
    {
        vertexShader = compile vs_3_0 ScreenSpaceVertexShader();
        pixelShader = compile ps_3_0 HorizontalGaussianBlurPixelShader();
    }
}

technique WorldSpaceVerticalGaussianBlur
{
    pass P0
    {
        vertexShader = compile vs_3_0 WorldSpaceVertexShader();
        pixelShader = compile ps_3_0 VerticalGaussianBlurPixelShader();
    }
}

technique ScreenSpaceVerticalGaussianBlur
{
    pass P0
    {
        vertexShader = compile vs_3_0 ScreenSpaceVertexShader();
        pixelShader = compile ps_3_0 VerticalGaussianBlurPixelShader();
    }
}

technique WorldSpaceRadialGaussianBlur
{
    pass P0
    {
        vertexShader = compile vs_3_0 WorldSpaceVertexShader();
        pixelShader = compile ps_3_0 RadialGaussianBlurPixelShader();
    }
}

technique ScreenSpaceRadialGaussianBlur
{
    pass P0
    {
        vertexShader = compile vs_3_0 ScreenSpaceVertexShader();
        pixelShader = compile ps_3_0 RadialGaussianBlurPixelShader();
    }
}

technique GaussianOutlined
{
    pass P0
    {
        vertexShader = compile vs_3_0 GenericVertexShader();
        pixelShader = compile ps_3_0 GaussianOutlinedPixelShader();
    }
}

technique GaussianOutlinedWithDiscard
{
    pass P0
    {
        vertexShader = compile vs_3_0 GenericVertexShader();
        pixelShader = compile ps_3_0 GaussianOutlinedPixelShaderWithDiscard();
    }
}