if (_Enable > 0.5 && _LilCompatible > 0.5)
{
    half3 lilOriginalLit = sd.col.rgb * sd.lightColor;
    half3 lilLit = lilOriginalLit;
    float2 lilMainUV = sd.uv * _MainUV.xy + _MainUV.zw;
    if (_Lil0Enabled > 0.5)
    {
        half3 lilN = normalize(lerp(normalize(vertex.N), sd.N, _Lil0NormalStrength));
        if (_Lil0CustomNormal > 0.5)
        {
            half4 lilNormalTex = SCSampleRepeat(_Lil0BumpMap, lilMainUV * _Lil0BumpMap_ST.xy + _Lil0BumpMap_ST.zw);
            lilN = normalize(mul(SCUnpackNormal(lilNormalTex, _Lil0BumpScale), vertex.TBN));
        }
        float3 lilView = normalize(lerp(vertex.Head, vertex.V, _Lil0VRParallaxStrength));
        if (_Lil0Perspective < 0.5 || UNITY_MATRIX_P._m33 != 0) lilView = camera.forward;
        float3 lilUp = _Lil0ZRotCancel > 0.5 ? float3(0,1,0) : UNITY_MATRIX_V._m10_m11_m12;
        lilUp = normalize(lilUp - lilView * dot(lilUp, lilView));
        float3 lilRight = cross(lilView, lilUp);
        float2 lilUV = float2(dot(lilRight,lilN),dot(lilUp,lilN));
        lilUV = lerp(lilUV, saturate(vertex.uv[1].xy)*2-1, _Lil0BlendUV1.xy);
        lilUV = (lilUV * _Lil0Texture_ST.xy + _Lil0Texture_ST.zw) * 0.5 + 0.5;
        half4 lilCap = _Lil0Texture.SampleLevel(sampler_linear_repeat, lilUV, _Lil0Lod) * _Lil0Color;
        half3 lilMask = SCSampleRepeat(_Lil0Mask, lilMainUV * _Lil0Mask_ST.xy + _Lil0Mask_ST.zw).rgb;
        lilCap.rgb *= lerp(1, sd.lightColor, _Lil0EnableLighting);
        lilCap.rgb *= lerp(1, sd.albedoAlpha.rgb, _Lil0MainStrength);
        half3 lilWeight = _Lil0Blend * lilCap.a * lilMask * lerp(1, sd.shadow, _Lil0ShadowMask);
        if (_RenderingMode == 2 && _Lil0ApplyTransparency > 0.5) lilWeight *= sd.col.a;
        if (!isFront && _Lil0BackfaceMask > 0.5) lilWeight = 0;
        half3 lilBlended = lilCap.rgb;
        if (_Lil0BlendMode > 0.5 && _Lil0BlendMode < 1.5) lilBlended = lilLit + lilCap.rgb;
        if (_Lil0BlendMode > 1.5 && _Lil0BlendMode < 2.5) lilBlended = max(lilLit + lilCap.rgb - lilLit*lilCap.rgb, lilLit);
        if (_Lil0BlendMode > 2.5) lilBlended = lilLit * lilCap.rgb;
        lilLit = lerp(lilLit, lilBlended, lilWeight);
    }
    if (_Lil1Enabled > 0.5)
    {
        half3 lilN = normalize(lerp(normalize(vertex.N), sd.N, _Lil1NormalStrength));
        if (_Lil1CustomNormal > 0.5)
        {
            half4 lilNormalTex = SCSampleRepeat(_Lil1BumpMap, lilMainUV * _Lil1BumpMap_ST.xy + _Lil1BumpMap_ST.zw);
            lilN = normalize(mul(SCUnpackNormal(lilNormalTex, _Lil1BumpScale), vertex.TBN));
        }
        float3 lilView = normalize(lerp(vertex.Head, vertex.V, _Lil1VRParallaxStrength));
        if (_Lil1Perspective < 0.5 || UNITY_MATRIX_P._m33 != 0) lilView = camera.forward;
        float3 lilUp = _Lil1ZRotCancel > 0.5 ? float3(0,1,0) : UNITY_MATRIX_V._m10_m11_m12;
        lilUp = normalize(lilUp - lilView * dot(lilUp, lilView));
        float3 lilRight = cross(lilView, lilUp);
        float2 lilUV = float2(dot(lilRight,lilN),dot(lilUp,lilN));
        lilUV = lerp(lilUV, saturate(vertex.uv[1].xy)*2-1, _Lil1BlendUV1.xy);
        lilUV = (lilUV * _Lil1Texture_ST.xy + _Lil1Texture_ST.zw) * 0.5 + 0.5;
        half4 lilCap = _Lil1Texture.SampleLevel(sampler_linear_repeat, lilUV, _Lil1Lod) * _Lil1Color;
        half3 lilMask = SCSampleRepeat(_Lil1Mask, lilMainUV * _Lil1Mask_ST.xy + _Lil1Mask_ST.zw).rgb;
        lilCap.rgb *= lerp(1, sd.lightColor, _Lil1EnableLighting);
        lilCap.rgb *= lerp(1, sd.albedoAlpha.rgb, _Lil1MainStrength);
        half3 lilWeight = _Lil1Blend * lilCap.a * lilMask * lerp(1, sd.shadow, _Lil1ShadowMask);
        if (_RenderingMode == 2 && _Lil1ApplyTransparency > 0.5) lilWeight *= sd.col.a;
        if (!isFront && _Lil1BackfaceMask > 0.5) lilWeight = 0;
        half3 lilBlended = lilCap.rgb;
        if (_Lil1BlendMode > 0.5 && _Lil1BlendMode < 1.5) lilBlended = lilLit + lilCap.rgb;
        if (_Lil1BlendMode > 1.5 && _Lil1BlendMode < 2.5) lilBlended = max(lilLit + lilCap.rgb - lilLit*lilCap.rgb, lilLit);
        if (_Lil1BlendMode > 2.5) lilBlended = lilLit * lilCap.rgb;
        lilLit = lerp(lilLit, lilBlended, lilWeight);
    }
    // This stage precedes final lighting; add the correction after that multiplication.
    sd.postadd += lilLit - lilOriginalLit;
}
