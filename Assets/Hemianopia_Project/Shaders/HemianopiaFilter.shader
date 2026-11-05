// Layer 3: the hemianopia filter (v0.1).
//
// An image-space pass that runs after the scene is fully rendered. For every pixel it asks:
//   1. Which direction in the world does this pixel show?   (pixel -> ray)
//   2. Is that direction left or right of THIS EYE's line of sight to the fixation point?
// If it's on the blind side, the pixel is replaced with the blind color.
//
// Each eye renders its own image with its own camera, so this runs once per eye.
// Each eye's boundary passes through the same 3D fixation point (where both eyes converge),
// so the two boundaries cross exactly where the person is looking, like real hemianopia.
//
// Driven every frame by HemianopiaController.cs through global shader properties.
// Used by a Full Screen Pass Renderer Feature with "Fetch Color Buffer" on,
// which binds the camera image to _BlitTexture. Full write-up: Docs/FILTER.md

Shader "Hemianopia/HemianopiaFilter"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off ZTest Always Blend Off Cull Off

        Pass
        {
            Name "HemianopiaFilter"

            HLSLPROGRAM
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            #pragma vertex Vert
            #pragma fragment Frag

            // Global properties, set every frame by HemianopiaController.cs
            float  _HemiEnabled;      // 0 = pass the image through untouched, 1 = apply the filter
            float3 _HemiFixationWS;   // the 3D point both eyes are looking at, world space
            float3 _HemiHeadUpWS;     // the head's up direction, world space
            float  _HemiBlindSign;    // -1 = left half is blind, +1 = right half is blind
            half4  _HemiBlindColor;   // what the blind half shows (black for now)

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 uv = input.texcoord;
                half4 sceneColor = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv);

                if (_HemiEnabled < 0.5)
                    return sceneColor;

                // Position of the eye currently being rendered (different for left and right).
                float3 eyePos = GetCameraPositionWS();

                // 1. Pixel -> ray. Undo this eye's projection and view (inverse view-projection matrix)
                //    to find a world point on the near plane behind this pixel, then point from the eye to it.
                float3 pointWS = ComputeWorldSpacePosition(uv, UNITY_NEAR_CLIP_VALUE, UNITY_MATRIX_I_VP);
                float3 dirWS   = normalize(pointWS - eyePos);

                // 2. This eye's own line of sight: from this eye to the fixation point.
                //    Its "right" vector is perpendicular to that line and to the head's up.
                float3 eyeGaze  = normalize(_HemiFixationWS - eyePos);
                float3 eyeRight = cross(_HemiHeadUpWS, eyeGaze);
                float  len      = length(eyeRight);
                eyeRight = len > 1e-4 ? eyeRight / len : float3(1, 0, 0); // looking straight up or down

                // 3. Which side of this eye's line of sight? Positive = right, negative = left.
                float side = dot(dirWS, eyeRight);

                // 4. Blind if it's on the blind side. Hard edge for now.
                float blind = step(0.0, side * _HemiBlindSign);

                return lerp(sceneColor, _HemiBlindColor, blind);
            }
            ENDHLSL
        }
    }
}
