Shader "Custom/Sprite2DOutline"
{
    Properties
    {
        _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _OutlineColor ("Outline Color", Color) = (1,0,0,1)
        _OutlineThickness ("Outline Thickness", Range(0, 20)) = 6
        [MaterialToggle] _OutlineEnabled ("Outline Enabled", Float) = 1
        _OutlineSoftness ("Outline Softness", Range(0, 1)) = 0.5
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
            "CanUseSpriteAtlas" = "True"
        }

        Cull Off
        Lighting Off
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            float4 _MainTex_TexelSize;

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float4 _OutlineColor;
                float _OutlineThickness;
                float _OutlineEnabled;
                float _OutlineSoftness;
            CBUFFER_END

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = v.uv;
                o.color = v.color * _Color;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                half4 mainTex = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv);
                mainTex *= i.color;

                if (_OutlineEnabled < 0.5)
                    return mainTex;

                // Early out: fully opaque pixels don't need outline sampling
                if (mainTex.a > 0.99)
                    return mainTex;

                // 8 directions, max 5 steps = 40 samples (vs previous 160)
                #define DIR_COUNT 8
                static const float2 dirs[DIR_COUNT] = {
                    float2(1.0, 0.0),       float2(0.7071, 0.7071),
                    float2(0.0, 1.0),       float2(-0.7071, 0.7071),
                    float2(-1.0, 0.0),      float2(-0.7071, -0.7071),
                    float2(0.0, -1.0),      float2(0.7071, -0.7071)
                };

                float minDist = _OutlineThickness + 1.0;
                float stride = max(_OutlineThickness / 5.0, 1.0);

                [unroll(DIR_COUNT)]
                for (int d = 0; d < DIR_COUNT; d++)
                {
                    [unroll(5)]
                    for (int s = 1; s <= 5; s++)
                    {
                        float dist = (float)s * stride;
                        float mask = step(dist, _OutlineThickness);
                        float2 sampleUV = i.uv + dirs[d] * dist * _MainTex_TexelSize.xy;
                        half sampledAlpha = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, sampleUV).a;
                        float effectiveDist = dist - sampledAlpha * stride + stride;
                        if (sampledAlpha * mask > 0.1)
                            minDist = min(minDist, effectiveDist);
                    }
                }

                // Gradient: smooth fade from inner edge to outer boundary
                float normalizedDist = saturate(minDist / max(_OutlineThickness, 0.001));
                // Softness controls the curve power: 0=hard, 1=very soft gradient
                float hardFade = step(minDist, _OutlineThickness) * 1.0;
                float softFade = pow(1.0 - normalizedDist, 1.0 + _OutlineSoftness * 3.0);
                float fade = lerp(hardFade, softFade, _OutlineSoftness);
                half4 outline = half4(_OutlineColor.rgb, _OutlineColor.a * fade);
                // Composite original over outline (standard alpha blending)
                half4 result;
                result.rgb = mainTex.rgb * mainTex.a + outline.rgb * (1.0 - mainTex.a);
                result.a = mainTex.a + outline.a * (1.0 - mainTex.a);
                return result;
            }
            ENDHLSL
        }
    }
    FallBack "Universal Render Pipeline/2D/Sprite-Lit-Default"
}
