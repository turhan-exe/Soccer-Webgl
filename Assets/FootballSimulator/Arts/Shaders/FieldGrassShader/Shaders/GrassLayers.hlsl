// HIGHLY INSPIRED FROM NEDMAKESGAMES => https://www.youtube.com/watch?v=YghAbgCN8XA

#ifndef GRASSLAYERS_INCLUDED
#define GRASSLAYERS_INCLUDED

// URP temel fonksiyonlar
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

// kendi yard�mc�lar�m�z
#include "Helpers.hlsl"


struct Attributes {
    float4 positionOS   : POSITION;
    float3 normalOS     : NORMAL;
    float4 tangentOS    : TANGENT;
    float2 uv           : TEXCOORD0;
};

struct VertexOutput {
    float3 positionWS   : TEXCOORD0;
    float3 normalWS     : TEXCOORD1;
    float2 uv           : TEXCOORD2;
};

struct GeometryOutput {
    float3 uv                       : TEXCOORD0;
    float3 positionWS               : TEXCOORD1;
    float3 normalWS                 : TEXCOORD2;

    float4 positionCS               : SV_POSITION;
};

TEXTURE2D(_BaseTexture); SAMPLER(sampler_BaseTexture); float4 _BaseTexture_ST;
float _BaseTexturePower;

float4 _BaseColor;
float4 _TopColor;
float _TotalHeight;

TEXTURE2D(_DetailNoiseTexture); SAMPLER(sampler_DetailNoiseTexture); float4 _DetailNoiseTexture_ST;
float _DetailDepthScale;
TEXTURE2D(_SmoothNoiseTexture); SAMPLER(sampler_SmoothNoiseTexture); float4 _SmoothNoiseTexture_ST;
float _SmoothDepthScale;

TEXTURE2D(_FieldLinesTexture); SAMPLER(sampler_FieldLinesTexture); float4 _FieldLinesTexture_ST;
float4 _FieldLinesColor;

TEXTURE2D(_FieldLinesNoiseTexture); SAMPLER(sampler_FieldLinesNoiseTexture); float4 _FieldLinesNoiseTexture_ST;
float _FieldLinesNoisePower;

TEXTURE2D(_PatternHorizontalTexture); SAMPLER(sampler_PatternHorizontalTexture); float4 _PatternHorizontalTexture_ST;
float _PatternHorizontalPower;
TEXTURE2D(_PatternVerticalTexture); SAMPLER(sampler_PatternVerticalTexture); float4 _PatternVerticalTexture_ST;
float _PatternVerticalPower;

TEXTURE2D(_FieldArea); SAMPLER(sampler_FieldArea); float4 _FieldArea_ST;
float _OutOfFieldPower;
float4 _OutOfFieldColor;

TEXTURE2D(_HeightMap); SAMPLER (sampler_HeightMap); float4 _HeightMap_ST;
float _HeightMapPower;
float4 _HeightWoundColor;
TEXTURE2D(_HeightWoundTexture); SAMPLER(sampler_HeightWoundTexture); float4 _HeightWoundTexture_ST;

TEXTURE2D(_ShadowsTexture); SAMPLER(sampler_ShadowsTexture); float4 _ShadowsTexture_ST;
TEXTURE2D(_StaticDirtTexture); SAMPLER(sampler_StaticDirtTexture); float4 _StaticDirtTexture_ST;
TEXTURE2D(_DirtTexture); SAMPLER(sampler_DirtTexture); float4 _DirtTexture_ST;
float4 _ShadowColor;
float4 _DirtWoundColor;

float _SHADER_LAYER_COUNT = 5;

VertexOutput Vertex(Attributes input) {
    VertexOutput output = (VertexOutput)0;

    VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);
    VertexNormalInputs normalInput = GetVertexNormalInputs(input.normalOS, input.tangentOS);
    output.positionWS = vertexInput.positionWS;
    output.normalWS = normalInput.normalWS;

    output.uv = input.uv;
    return output;
}
 
void SetupVertex(in VertexOutput input, inout GeometryOutput output, float height) {
    float3 positionWS = input.positionWS + input.normalWS * (height * _TotalHeight);

    output.positionWS = positionWS;
    output.normalWS = input.normalWS;
    output.uv = float3(input.uv, height);
    output.positionCS = TransformWorldToHClip(positionWS);
}

[maxvertexcount(72)]
void Geometry(triangle VertexOutput inputs[3], inout TriangleStream<GeometryOutput> outputStream) {
    GeometryOutput output = (GeometryOutput)0;

    float layerCount = max (2, _SHADER_LAYER_COUNT);

    for (int l = 0; l < layerCount; l++) {
        float h = l / (float)(layerCount - 1);
        for (int t = 0; t < 3; t++) {
            SetupVertex(inputs[t], output, h);
            outputStream.Append(output);
        }
        outputStream.RestartStrip();
    }
}
half4 Fragment(GeometryOutput input) : SV_Target
{
    float2 worldUV = input.positionWS.xz;
    float2 uv = input.uv.xy;

    // --- Height & masks ---
    float heightMap = SAMPLE_TEXTURE2D(_HeightMap, sampler_HeightMap, TRANSFORM_TEX(uv, _HeightMap)).r;
    heightMap += (1 - heightMap) * (1 - _HeightMapPower);
    float3 heightWoundSample = SAMPLE_TEXTURE2D(_HeightWoundTexture, sampler_HeightWoundTexture, TRANSFORM_TEX(uv, _HeightWoundTexture)).rgb;

    float fieldAreaAlpha = SAMPLE_TEXTURE2D(_FieldArea, sampler_FieldArea, TRANSFORM_TEX(uv, _FieldArea)).r;

    float dirtMap = (
        SAMPLE_TEXTURE2D(_DirtTexture, sampler_DirtTexture, TRANSFORM_TEX(uv, _DirtTexture)).r * 0.5 +
        SAMPLE_TEXTURE2D(_StaticDirtTexture, sampler_StaticDirtTexture, TRANSFORM_TEX(uv, _DirtTexture)).r
    ) * 2 + (1 - fieldAreaAlpha) * 0.5;

    heightMap = lerp(heightMap, 2, min(1, dirtMap));

    float height = input.uv.z * heightMap;

    // --- Noise / cutout ---
    float detailNoise = SAMPLE_TEXTURE2D(_DetailNoiseTexture, sampler_DetailNoiseTexture, TRANSFORM_TEX(worldUV, _DetailNoiseTexture)).r;
    float smoothNoise = SAMPLE_TEXTURE2D(_SmoothNoiseTexture, sampler_SmoothNoiseTexture, TRANSFORM_TEX(worldUV, _SmoothNoiseTexture)).r;

    detailNoise = 1 - (1 - detailNoise) * _DetailDepthScale;
    smoothNoise = 1 - (1 - smoothNoise) * _SmoothDepthScale;
    clip(detailNoise * smoothNoise - height);

    // --- Base color ---
    float3 baseTex = SAMPLE_TEXTURE2D(_BaseTexture, sampler_BaseTexture, TRANSFORM_TEX(uv, _BaseTexture)).rgb;

    float3 albedo = lerp(_BaseColor.rgb, _TopColor.rgb, height);
    albedo = lerp(albedo, _HeightWoundColor.rgb * heightWoundSample, heightMap * _HeightMapPower);
    albedo = lerp(albedo, baseTex * _BaseColor.rgb, _BaseTexturePower);

    // --- Mowing patterns (horizontal/vertical) ---
    float patternH = SAMPLE_TEXTURE2D(_PatternHorizontalTexture, sampler_PatternHorizontalTexture, TRANSFORM_TEX(uv, _PatternHorizontalTexture)).r;
    float patternV = SAMPLE_TEXTURE2D(_PatternVerticalTexture, sampler_PatternVerticalTexture, TRANSFORM_TEX(uv, _PatternVerticalTexture)).r;

    float patternHorizontalPower = patternH * _PatternHorizontalPower * fieldAreaAlpha;
    float patternVerticalPower = patternV * _PatternVerticalPower * fieldAreaAlpha;

    float3 normalizedView = normalize(GetViewForwardDir());
    normalizedView.y = 0;
    float3 viewToPoint = GetViewToPointDir(input.positionWS);
    viewToPoint.y = 0;

    float cameraToPixel = dot(normalizedView, viewToPoint);
    float defaultHorizontal = abs(normalizedView.x);
    float dotAbs = 1 - abs(cameraToPixel);
    float sideLinesHorizontal = lerp(dotAbs, 0, defaultHorizontal);
    float sideLinesVertical = lerp(0, dotAbs, defaultHorizontal);

    float pattern_vert = (1 - defaultHorizontal) * patternVerticalPower + sideLinesVertical * patternVerticalPower;
    float pattern_hori = defaultHorizontal * patternHorizontalPower + sideLinesHorizontal * patternHorizontalPower;

    const float3 BLACK = 0;
    albedo = lerp(albedo, BLACK, pattern_hori);
    albedo = lerp(albedo, BLACK, pattern_vert);

    // --- Field lines ---
    float fieldNoise = SAMPLE_TEXTURE2D(_FieldLinesNoiseTexture, sampler_FieldLinesNoiseTexture, TRANSFORM_TEX(uv, _FieldLinesNoiseTexture)).r;
    float fieldLines = SAMPLE_TEXTURE2D(_FieldLinesTexture, sampler_FieldLinesTexture, TRANSFORM_TEX(uv, _FieldLinesTexture)).r;

    float fieldLineAlpha = fieldAreaAlpha * (_FieldLinesColor.a * fieldLines + _FieldLinesNoisePower * fieldLines);
    albedo = lerp(albedo, _FieldLinesColor.rgb, fieldLineAlpha);

    // --- Out of field tint ---
    albedo = lerp(albedo, albedo * _OutOfFieldColor.rgb, (1 - fieldAreaAlpha) * _OutOfFieldPower);

    // --- Dirt wounds ---
    albedo = lerp(albedo, heightWoundSample * _DirtWoundColor.rgb, dirtMap);

    // --- Prebaked shadows texture ---
    float shadows = SAMPLE_TEXTURE2D(_ShadowsTexture, sampler_ShadowsTexture, TRANSFORM_TEX(uv, _ShadowsTexture)).r;
    albedo = lerp(albedo, _ShadowColor.rgb, shadows);

    // --- Lighting / fog ---
    InputData lightingInput = (InputData) 0;
    lightingInput.positionWS = input.positionWS;
    lightingInput.normalWS = NormalizeNormalPerPixel(input.normalWS);
    lightingInput.viewDirectionWS = GetViewDirectionFromPosition(input.positionWS);
    lightingInput.shadowCoord = CalculateShadowCoord(input.positionWS, input.positionCS);
    lightingInput.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);

    float4 clipPos = TransformWorldToHClip(input.positionWS);
    lightingInput.fogCoord = ComputeFogFactor(clipPos.z);

    SurfaceData surfaceData = (SurfaceData) 0;
    surfaceData.albedo = albedo;
    surfaceData.specular = 1;
    surfaceData.metallic = 0;
    surfaceData.smoothness = 0;
    surfaceData.normalTS = half3(0, 0, 1);
    surfaceData.alpha = 1;

    half4 color = UniversalFragmentBlinnPhong(lightingInput, surfaceData);
    color.rgb = MixFog(color.rgb, lightingInput.fogCoord);
    return color;
}

#endif