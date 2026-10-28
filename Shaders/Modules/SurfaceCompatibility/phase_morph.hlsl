// NonToon extrudes in world space, but skinned input normals need not have unit length.
// Run only in the outline pass so the base surface keeps its original normal inputs.
#ifdef OUTLINE
if (_Enable > 0.5 && _OutlineCompatible > 0.5)
{
    float widthMask = _OutlineWidthMask.SampleLevel(sampler_linear_repeat,
        vertex.uv[0].xy * _MainUV.xy + _MainUV.zw, 0).r;
    if (_OutlineVertexWidth > 0.5 && _OutlineVertexWidth < 1.5) widthMask *= vertex.color.r;
    if (_OutlineVertexWidth > 1.5) widthMask *= vertex.color.a;
    widthMask *= lerp(1.0, saturate(length(head.position.xyz - vertex.position)), _OutlineFixWidth);
    if (_OutlineFromVertexColor)
    {
        float3 encodedNormal = vertex.color.rgb * 2.0 - 1.0;
        float3 direction = mul(encodedNormal, vertex.TBN);
        vertex.TBN *= widthMask / max(length(direction), 1e-6);
    }
    else
    {
        vertex.N *= widthMask / max(length(vertex.N), 1e-6);
        // The stock outline discards gray encoded normals even in regular normal mode.
        vertex.color.rgb = 1.0;
    }
}
#endif
