Shader "Grass/GrassLayersModded_WebGL"
{
    Properties
    {
        _BaseTexture ("Base Texture", 2D) = "white" {}
        _BaseTexturePower ("Base texture power", Range(0,1)) = 1

        [HDR]_BaseColor ("Base color", Color) = (0,0.5,0,1)
        [HDR]_TopColor  ("Top color",  Color) = (0,1,0,1)

        _FieldLinesTexture ("Field lines", 2D) = "white" {}
        [HDR]_FieldLinesColor ("Field lines color", Color) = (1,1,1,1)

        _FieldArea ("Field Area Mask", 2D) = "white" {}
        [HDR]_OutOfFieldColor ("Out of field color", Color) = (0.05,0.15,0.05,1)
        _OutOfFieldPower ("Out of field power", Range(0,2)) = 1

        _SmoothNoiseTexture ("Smooth noise", 2D) = "gray" {}
        _SmoothDepthScale ("Smooth depth scale", Range(0,1)) = 0.5

        _GrainyNoiseTexture ("Grainy noise", 2D) = "gray" {}
        _DetailDepthScale ("Grainy depth scale", Range(0,1)) = 0.25
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        LOD 100

        Pass
        {
            Name "ForwardUnlit"
            Tags { "LightMode"="UniversalForward" }

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex vert
            #pragma fragment frag
            // WebGL: geometry / tessellation KULLANMA

            // URP çekirdek
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float4 _TopColor;
                float  _BaseTexturePower;
                float4 _FieldLinesColor;
                float4 _OutOfFieldColor;
                float  _OutOfFieldPower;
                float  _SmoothDepthScale;
                float  _DetailDepthScale;
            CBUFFER_END

            TEXTURE2D(_BaseTexture);        SAMPLER(sampler_BaseTexture);
            TEXTURE2D(_FieldLinesTexture);  SAMPLER(sampler_FieldLinesTexture);
            TEXTURE2D(_FieldArea);          SAMPLER(sampler_FieldArea);
            TEXTURE2D(_SmoothNoiseTexture); SAMPLER(sampler_SmoothNoiseTexture);
            TEXTURE2D(_GrainyNoiseTexture); SAMPLER(sampler_GrainyNoiseTexture);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
            };

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = IN.uv;
                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                // Temel çim rengi
                half4 baseTex = SAMPLE_TEXTURE2D(_BaseTexture, sampler_BaseTexture, IN.uv);
                half3 baseCol = lerp(_BaseColor.rgb, _TopColor.rgb, baseTex.r) * _BaseTexturePower;

                // Noise ile çok hafif ton varyasyonu
                half n1 = SAMPLE_TEXTURE2D(_SmoothNoiseTexture, sampler_SmoothNoiseTexture, IN.uv * 2).r;
                half n2 = SAMPLE_TEXTURE2D(_GrainyNoiseTexture, sampler_GrainyNoiseTexture, IN.uv * 10).r;
                baseCol *= (1 + (n1 - 0.5h) * _SmoothDepthScale + (n2 - 0.5h) * _DetailDepthScale);

                // Saha çizgileri (beyaz overlay)
                half l = SAMPLE_TEXTURE2D(_FieldLinesTexture, sampler_FieldLinesTexture, IN.uv).r;
                baseCol = lerp(baseCol, _FieldLinesColor.rgb, l);

                // Saha alan maskesi (dış bölgede farklı renk)
                half mask = SAMPLE_TEXTURE2D(_FieldArea, sampler_FieldArea, IN.uv).r; // 1 = saha içi
                baseCol = lerp(_OutOfFieldColor.rgb, baseCol, saturate(mask * _OutOfFieldPower));

                return half4(baseCol, 1);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
