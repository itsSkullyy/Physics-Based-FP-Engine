// Drop this file anywhere in your Assets folder. Unity compiles it automatically and
// ImpactFrames finds it by name at runtime - no wiring, no material to assign.
//
// If you strip shaders on build, add "Hidden/ImpactRedWorld" to
// Project Settings -> Graphics -> Always Included Shaders so it survives into the player.
//
// This crushes the frame to a hard TWO-TONE red/black look: everything below a
// brightness threshold goes to near-black, everything above ramps into red, with a thin
// sharp edge where they meet. Animated film grain roughens it so it reads noisy and
// sharp rather than clean and flat.
Shader "Hidden/ImpactRedWorld"
{
    Properties
    {
        _MainTex ("Tex", 2D) = "white" {}
        _Shadow ("Shadow", Color) = (0, 0, 0, 1)
        _Mid ("Mid", Color) = (0.85, 0.02, 0.03, 1)
        _High ("High", Color) = (1, 0.15, 0.1, 1)
        _Strength ("Strength", Float) = 1
        _Threshold ("Threshold", Float) = 0.32
        _Edge ("EdgeSoftness", Float) = 0.09
        _Alpha ("Alpha", Float) = 1
        _FocusX ("FocusX", Float) = 0.5
        _FocusY ("FocusY", Float) = 0.5
        _Vignette ("Vignette", Float) = 0.9
        _Grain ("Grain", Float) = 0.35
        _GrainScale ("GrainScale", Float) = 1.5
        _Edges ("EdgeLines", Float) = 0.6
        _EdgePower ("EdgePower", Float) = 1.5
        _Spike ("GrainSpike", Float) = 0.5
        _Time01 ("TimeSeed", Float) = 0
    }
    SubShader
    {
        Tags { "Queue" = "Overlay" "IgnoreProjector" = "True" "RenderType" = "Overlay" }
        Pass
        {
            ZTest Always
            Cull Off
            ZWrite Off
            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            fixed4 _Shadow, _Mid, _High;
            float _Strength, _Threshold, _Edge, _Alpha, _FocusX, _FocusY, _Vignette;
            float _Grain, _GrainScale, _Edges, _EdgePower, _Spike, _Time01;

            // Luminance helper reused by the edge detector.
            float Luma(float2 uv) { return dot(tex2D(_MainTex, uv).rgb, float3(0.299, 0.587, 0.114)); }

            // Cheap hash noise, animated by _Time01 so the grain crawls each frame.
            float hash21(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 src = tex2D(_MainTex, i.uv);

                // Brightness of the underlying pixel.
                float lum = dot(src.rgb, float3(0.299, 0.587, 0.114));

                // Grain injected into the luminance BEFORE thresholding, so it breaks the
                // black/red boundary into a rough edge. _Spike sharpens the noise: pushing
                // it toward the extremes turns soft static into hard specks and flecks.
                float2 gp = i.uv * _ScreenParams.xy / _GrainScale;
                float n = hash21(floor(gp) + floor(_Time01 * 60.0));
                n = lerp(n, step(0.5, n), _Spike);      // spike toward 0/1
                lum += (n - 0.5) * _Grain;

                // Sobel-ish edge detect on the ORIGINAL luminance. Big brightness changes -
                // real geometry edges - come back as a bright line, which is what makes the
                // whole world read as sharp and spiky. Uses one-texel diagonal taps.
                float2 tx = _MainTex_TexelSize.xy;
                float l0 = Luma(i.uv + float2(-tx.x, -tx.y));
                float l1 = Luma(i.uv + float2( tx.x, -tx.y));
                float l2 = Luma(i.uv + float2(-tx.x,  tx.y));
                float l3 = Luma(i.uv + float2( tx.x,  tx.y));
                float gx = (l1 + l3) - (l0 + l2);
                float gy = (l2 + l3) - (l0 + l1);
                float edge = saturate(pow(sqrt(gx * gx + gy * gy) * 4.0, _EdgePower)) * _Edges;

                // Hard threshold with a thin soft edge: below -> shadow, above -> red ramp.
                float t = smoothstep(_Threshold - _Edge, _Threshold + _Edge, lum);

                // Above the threshold, ramp from mid to high by how far over it we are.
                float hi = saturate((lum - _Threshold) / max(0.001, 1.0 - _Threshold));
                float3 red = lerp(_Mid.rgb, _High.rgb, hi);

                float3 twoTone = lerp(_Shadow.rgb, red, t);
                float3 outcol = lerp(src.rgb, twoTone, _Strength);

                // Burn the detected edges in as bright hot lines over the two-tone.
                outcol = lerp(outcol, _High.rgb, edge);

                // Radial vignette pulling focus toward the impact point, stronger now so
                // the frame darkens smoothly to the edges with no hard border.
                float2 d = i.uv - float2(_FocusX, _FocusY);
                float vig = saturate(1.0 - dot(d, d) * _Vignette * 3.0);
                outcol *= lerp(1.0, vig, 0.75);

                return fixed4(outcol, _Alpha);
            }
            ENDCG
        }
    }
    Fallback Off
}
