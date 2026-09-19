// NonToon fabric / normal-detail module (provided by LilToNonToon Switcher)
//
// Hooked at __SC_PHASE_postpixel__: by that point sd.col has already been multiplied by sd.lightColor,
// and sd.lightColor is saturated - on a lit surface it is constantly 1, so the small perturbation
// coming from the main _NormalMap (the fabric weave) has no output channel at all.
//
// What it does: takes sd.N_detail (the perturbed normal computed by NonToon's own Details module, in the
// same space as sd.N) and turns its xy into a small brightness modulation that is first-order sensitive
// to the normal - so the fabric relief shows up everywhere, not only inside a narrow terminator band.
//
// The result is multiplied around 1, so NonToon's toon shading itself is untouched.
//
// Things that were tried here and DID NOT work (kept as a warning for future edits):
//   * dot(N, L): on a lit surface it is already saturated at ~1 - measured 0.998 -> 0.998, so the
//     effect vanished on exactly the areas that matter;
//   * dot(N, H) specular: first-order sensitive, but a flat/quad test surface still missed the lobe and
//     dark meshes had nothing to sample - it depends too much on getting a highlight in frame;
//   * wrapping the term in a fixed smoothstep window: saturated on lit surfaces, ate the whole variation;
//   * saturate() on the final factor: clamps the "brighter" half away, leaving only darkening;
//   * adding tangent-space detail xy to the world-space sd.N: meaningless perturbation direction.
//
// It deliberately does NOT use the shared mask: those masks carry other modules' data and their alpha
// channel is usually close to 0 in converted materials, which would cancel the whole effect.
// The module is opt-in per material anyway (the converter enables it).
//
// NOTE: keep this file ASCII-only. Non-ASCII comments got mangled by an editor round trip once, which
// silently merged a comment with the next code line and commented out the actual effect.
if (_Enable)
{
    // Normalise the direction so _FabricStrength means the same thing regardless of the direction values.
    half2 dir = normalize(half2(_FabricDirX, _FabricDirY) + half2(1e-5h, 0.0h));
    half delta = dot(sd.N_detail.xy, dir) * _FabricNormalStrength;

    // Soft compression: keeps the fine relief but kills the large swings. Without this the fabric normal
    // map turns into a harsh cross-hatch grid (measured strength 1.0 looked nothing like the reference).
    delta = delta / (1.0h + abs(delta));

    sd.col.rgb *= max(0.0h, 1.0h + delta * _FabricStrength);
}
