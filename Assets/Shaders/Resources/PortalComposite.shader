// Two-pass stencil compositor for Portal.cs / PortalCompositeFeature.cs. Draws the
// portal's surface mesh twice, manually, from a custom render pass (NOT the normal
// per-object renderer - see Portal.cs, surfaceRenderer.enabled is kept false):
//
//  Pass "Mask": writes stencil only, depth-tested against whatever real geometry
//  already occupies the main camera's depth buffer, so anything nearer than the portal
//  correctly excludes it from the mask.
//
//  Pass "Composite": wherever that stencil was written, samples the linked portal's
//  render texture straight into the main camera's own color+depth buffer (clearing the
//  stencil bit as it goes).
//
// Bit 32 (the 6th stencil bit) is used rather than bit 1, to stay clear of any low bits
// URP's own passes might reserve internally.
//
// Lives in a Resources folder on purpose. PortalCompositeFeature loads it by name and no
// material uses it, so anywhere else a build strips it and portals show nothing.
Shader "Custom/PortalComposite"
{
    Properties
    {
        _MainTex ("Portal View", 2D) = "white" {}
        _StencilRef ("Stencil Ref", Int) = 32
    }
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "Mask"
            ColorMask 0
            ZWrite Off
            ZTest LEqual
            Cull Off

            Stencil
            {
                Ref [_StencilRef]
                ReadMask 32
                WriteMask 32
                Comp Always
                Pass Replace
            }

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; };

            Varyings Vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                return OUT;
            }

            half4 Frag(Varyings IN) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "Composite"
            ColorMask RGB
            ZWrite On
            ZTest LEqual
            Cull Off

            Stencil
            {
                Ref [_StencilRef]
                ReadMask 32
                WriteMask 32
                Comp Equal
                Pass Zero
            }

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 positionNDC : TEXCOORD0;
            };

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            Varyings Vert(Attributes IN)
            {
                Varyings OUT;
                VertexPositionInputs positionInputs = GetVertexPositionInputs(IN.positionOS.xyz);
                OUT.positionCS = positionInputs.positionCS;
                OUT.positionNDC = positionInputs.positionNDC;
                return OUT;
            }

            half4 Frag(Varyings IN) : SV_Target
            {
                // The portal camera renders the destination using the MAIN camera's own
                // projection matrix, so its render texture is a full-screen image already
                // aligned to this frame's screen. It therefore has to be sampled by SCREEN
                // position - each screen pixel inside the portal reads that same screen
                // pixel of the destination view, which is what makes the opening behave as
                // a window onto a continuous space.
                //
                // Sampling by the quad's own mesh UVs instead squeezes that entire
                // full-screen image into the portal rectangle. That is exactly what a video
                // feed is, and it was the reason the walls, floor and horizon never lined up
                // with the room around the opening no matter how correct the camera was.
                float2 uv = IN.positionNDC.xy / IN.positionNDC.w;
                return SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
