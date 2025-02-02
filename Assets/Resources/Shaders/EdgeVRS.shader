Shader "EdgeVRS"
{
    Properties{
        _ShadingRate1x1("_ShadingRateColor1x1", Color) = (1.0, 0.0, 0.0, 1)
        _ShadingRate2x2("_ShadingRateColor2x2", Color) = (0.0, 1.0, 0.0, 1)
        _ShadingRate4x4("_ShadingRateColor4x4", Color) = (0.0, 0.0, 1.0, 1)      
    }


SubShader
{
    Tags { "RenderType"="Opaque" "RenderPipeline" = "UniversalPipeline"}
    ZWrite Off Cull Off
    Pass
    {
        Name "ColorBlitPass"
        HLSLPROGRAM
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
        #pragma vertex Vert
        #pragma fragment Frag

        float4 _ShadingRate1x1;
        float4 _ShadingRate2x2;
        float4 _ShadingRate4x4;     
 
        float4 Frag(Varyings input) : SV_Target0
        {
            // this is needed so we account XR platform differences in how they handle texture arrays
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            // sample the texture using the SAMPLE_TEXTURE2D_X_LOD
            float2 uv = input.texcoord.xy;
            float mask = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_PointRepeat, uv,
            0).r;

            half4 color = _ShadingRate1x1;
            if (mask > 0.333){
                color = _ShadingRate2x2;
            }
                
            return color;
        }
        ENDHLSL
    }
    }
}
