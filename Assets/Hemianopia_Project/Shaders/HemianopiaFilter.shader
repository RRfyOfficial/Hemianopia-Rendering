// Layer 3: the hemianopia filter (v0).
//
// An image-space pass that runs after the scene is fully rendered. For every pixel it asks:
//   1. Which direction in the world does this pixel show?   (pixel -> ray)
//   2. Is that direction left or right of where the eyes point?
// If it's on the blind side, the pixel is replaced with the blind color.
//
// Because each eye renders its own image with its own camera, this runs once per eye,
// and both eyes lose the same half of the world. That matches real (homonymous) hemianopia.
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
            float3 _HemiGazeRightWS;  // unit vector pointing "right" relative to the gaze, in world space
            float  _HemiBlindSign;    // -1 = left half is blind, +1 = right half is blind
            half4  _HemiBlindColor;   // what the blind half shows (black in v0)

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 uv = input.texcoord;
                half4 sceneColor = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv);

                if (_HemiEnabled < 0.5)
                    return sceneColor;

                // 1. Pixel -> ray. Undo this eye's projection and view (inverse view-projection matrix)
                //    to find a world point on the near plane behind this pixel, then point from the eye to it.
                float3 pointWS = ComputeWorldSpacePosition(uv, UNITY_NEAR_CLIP_VALUE, UNITY_MATRIX_I_VP);
                float3 dirWS   = normalize(pointWS - GetCameraPositionWS());

                // 2. Which side of the gaze line? Positive = right of gaze, negative = left of gaze.
                float side = dot(dirWS, _HemiGazeRightWS);

                // 3. Blind if it's on the blind side. Hard edge in v0.
                float blind = step(0.0, side * _HemiBlindSign);

                return lerp(sceneColor, _HemiBlindColor, blind);
            }
            ENDHLSL
        }
    }
}
