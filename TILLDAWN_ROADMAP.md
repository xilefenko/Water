# Till Dawn — Linux Ocean Compatibility Roadmap

**Strategy:** a Linux compatibility layer around the existing FFT simulation — not a rewrite.
The canonical spectrum and wave math are preserved; only the failing tessellation/geometry
presentation path is replaced. Every layer is validated independently before the next one
is built on top of it.

**Status legend:** ✅ complete · 🔄 in progress · ⏳ not started · 🔒 approval gate (needs your eyes)

---

## Stage 0 — Frozen baseline ✅

These are immutable controls. Nothing in later stages may modify them:

| Control | Role |
|---|---|
| `SampleScene.unity` serialized parameters | Canonical simulation + material values (never saved over) |
| `FFTWater.compute` | Canonical spectrum, FFT, displacement, slope, foam math |
| `FFTWater.shader` (`Custom/FFTWater`) | Original presentation path, retained as the D3D11 control |
| `Screenshot.png` + supplied reference image | Appearance target: wave scale, whitecaps, glitter, color, horizon |
| Fixed capture times `0, 1, 10, 50, 100, 199.9, 200, 200.1` | Reproducible comparison set |
| Approved parity camera pose | pos (2.626, 9.626, −18.470), euler (10.546, 5.873, 0), FOV 75, near 0.05, far 1000 |

Two camera concepts stay explicit throughout: the Unity SampleScene camera validates the
compatibility patch; the approved Godot camera drives Till Dawn captures.

---

## Stage 1 — Prove the compute simulation + continuous diagnostic ocean ✅

*(plan steps 2 and 3 — completed 2026-07-12)*

### What was built

- **`Assets/Editor/OceanFieldDiagnostics.cs`** — reads back every simulation field at all
  fixed capture times (initial spectrum ×4, evolving spectrum ×8, displacement ×4,
  slopes ×4, buoyancy), writes per-layer stats JSON + preview PNGs, and self-verdicts.
  Exits nonzero on failure, so it doubles as the smoke test required by Stage 5.
- **`Assets/Shaders/FFTWaterCompat.shader`** (`Custom/FFTWaterCompat`) — vertex→fragment
  recomposition of the original shader's own `vp()`/`fp()` functions. Samples the same
  displacement/slope texture arrays with the same world-space tiles, band toggles, foam
  subtracts, and distance attenuation. The full `NEW_LIGHTING` fragment path is already
  ported verbatim. `_CompatDebugMode` 0–7 provides the Stage 2 isolation views
  (0 lit, 1 flat geometry diagnostic, 2 displacement, 3 normals, 4 foam, 5 specular,
  6 fresnel/environment, 7 scatter).
- **`FFTWater.cs` compat wiring** (runtime-only, never serialized): backend auto-selection
  (compat on non-D3D graphics APIs; override with `TILLDAWN_WATER_BACKEND=original|compat`)
  and a mesh-density multiplier (`TILLDAWN_COMPAT_QUADRES_MULT`, default 4 → 641,601 verts
  vs the original 40,401) so real vertices carry the density the GPU tessellator used to add.
- **`Assets/Editor/OceanCompatCapture.cs`** — renders both backends from the approved
  parity camera pose at times 0 / 50 / 50.1 / 51 / 100.

### Findings

1. **OpenGLCore silently kills the compute pipeline (Linux failure #1).** Under GLCore —
   the editor's default API on this machine — every field reads back exactly zero,
   including the initial JONSWAP spectrum. No NaNs, no errors: the dispatches simply never
   write. This alone produces a flat, static ocean. **Consequence: every Unity launch must
   use `-force-vulkan`.**
2. **The tessellation/geometry shader breaks on Vulkan (Linux failure #2).** With the
   simulation proven healthy, the original `Custom/FFTWater` still renders the familiar
   perforated surface — most triangles missing (control captures in
   `OceanParityArtifacts/compat/original/`). The presentation path, not the simulation,
   is the source of the holes.
3. **Under Vulkan the simulation is fully healthy.** All checks pass:
   - No NaN/Inf in any captured field.
   - All four bands alive — height RMS at t=50: 0.105 / 1.501 / 0.429 / 0.267 m
     (combined 1.587 m, plausible amplitude).
   - Waves genuinely animate: relative height change t=0→t=1 is 1.116.
   - 200 s periodicity exact to 0.025 % (relative diff t=0 vs t=200: 0.00025).
   - Clean continuity across the loop boundary (t=199.9 vs t=200.1: 0.259, well under the 1 s change).
4. **The compat path renders a continuous, animated ocean.** No holes, no jitter grid,
   no missing triangles, out to the horizon. Frame differences scale with time step
   (RMSE 4.8 % over 0.1 s vs 8.5 % over 1 s) — coherent wave advection, not noise.
5. **Scene quirks recorded for later stages:** bands only render when `visualizeLayerN`
   is true (it drives `_DebugLayerN`, which multiplies displacement *and* slopes); the
   scene ships layer 1 on+displacing, layer 4 visible but non-displacing, layers 2–3 off.
   The FFT water's `useTextureForFresnel` is 0, and the Atmosphere script is not driven in
   edit-mode batch renders — which is why Stage 1 lit captures show near-black water under
   a flat blue sky. That is expected debt for Stages 2–3, not a regression.

### Artifacts

- `OceanParityArtifacts/diagnostics/` — `field_diagnostics.json`, height/foam/slope/spectrum previews
- `OceanParityArtifacts/compat/compat/` — diagnostic (`diag1_*`) and lit (`lit_*`) captures
- `OceanParityArtifacts/compat/original/` — broken tessellation control under Vulkan

### 🔒 Approval gate 1 — flat diagnostic geometry

**Ready for review:** `OceanParityArtifacts/compat/compat/diag1_t050.0.png`
(continuous animated geometry, flat diagnostic shading). Compare against
`OceanParityArtifacts/compat/original/lit_t050.0.png` to see the defect it replaces.

---

## Stage 2 — Restore the reference water appearance ✅

*(plan step 4 — completed 2026-07-12)*

The `NEW_LIGHTING` code was already ported verbatim; this stage fed it real inputs and
verified every term in isolation. No artistic changes to the shading math.

### What was built (all in `Assets/Editor/OceanCompatCapture.cs`)

- **Foam warm-up protocol.** Foam is a per-frame exponential accumulator
  (`foam *= exp(-_FoamDecayRate)` per dispatch — frame-iterative, ~57-frame time
  constant), so a single large `ParityStep` leaves it at ~2 % of its 60 fps steady state.
  The capture tool now spins up 300 frames at 1/60 s before any capture time that is not
  a short continuation (< 5 s) of the previous one; short gaps still step directly so the
  50 → 50.1 → 51 temporal-coherence checks remain valid. Both backends use the identical
  protocol, so Stage 4 parity comparisons stay apples-to-apples.
- **Substitute environment cubemap.** The scene's environment/skybox cubemap
  (guid `6fa3f446...`, referenced by both `skyboxTex` and `environmentTexture`) **was
  never committed to the repo** — every render, including the D3D11 control and the
  Stage 1 captures, sampled an unbound cubemap. The capture tool now builds a 128 px
  procedural dawn-sky cubemap (horizon/zenith anchors sampled from `Screenshot.png`,
  sun glow from the serialized Atmosphere `sunDirection`/`sunColor`) and assigns it
  runtime-only, and only when `environmentTexture` is null — if the real asset ever
  appears it wins automatically. Nothing is serialized.
- **Sun-facing diagnostic pose** (euler 10.546/165/0, same position): the serialized sun
  (travel direction (−1.29, −1, 4.86)) sits *behind* the approved parity camera, so the
  specular glint field is legitimately out of view there. This extra labeled view exists
  solely to prove the specular term; it is not a parity pose.
- **Lighting-bindings evidence log** (`OCEAN_COMPAT_LIGHTING`): sun direction/color/
  irradiance, env map + strength, scatter/foam colors as the shader actually received
  them. (The env map is logged from the `FFTWater` field; `material.GetTexture` is
  unreliable for `_EnvironmentMap` because the shader declares no Properties entry.)

### Term-by-term verification (all at t=50 and t=100, `OceanParityArtifacts/compat/compat/`)

| View | File | Verdict |
|---|---|---|
| Displacement (`diag2`) | wave-scale lobes in all three axes, animated | ✅ |
| Normals (`diag3`) | up-dominant with fine slope perturbation, depth-attenuated | ✅ |
| Foam (`diag4`) | streaky whitecaps after warm-up, denser near camera | ✅ |
| Specular (`diag5`) | black at parity pose (sun behind camera — geometrically correct); `sunfacing_diag5` shows the golden Beckmann glitter path with Smith-masked sparkle | ✅ |
| Fresnel/env (`diag6`) | warm sky reflection with grazing-angle brightening (IOR-1.33 F visible as structure) | ✅ |
| Scatter (`diag7`) | teal view/peak-dependent scattering, trough-dark | ✅ |
| Composite (`lit_*`) | foam + warm sheen + teal troughs + crest brightness; `sunfacing_lit` shows the classic glitter path down the swell | ✅ |

### Notes for Stage 3

- Scene wiring was healthier than the Stage 1 notes claimed: `useTextureForFresnel: 1`,
  `environmentTexture` assigned (to the missing asset), `atmosphere` reference set — sun
  direction/color/irradiance always flowed through `FFTWater.Update` in `ParityStep`.
- The flat blue sky remains: `Atmosphere` has no `ExecuteInEditMode`, so its
  `OnRenderImage` sky/fog/sun composite never runs in edit-mode batch renders. Stage 3
  must evaluate it (or an equivalent) for the background, fog, and tone.
- The substitute cubemap is an explicit, documented deviation: the canonical asset is
  unavailable, so *appearance* judgments use the substitute on **both** backends. Exact
  sky colorimetry is Stage 3 scope.

**Checkpoint met:** foam placement, trough darkness, crest brightness, and (sun-facing)
glitter resemble the reference; wave silhouette is constrained by the 100 m plane at the
approved pose, identically on both backends.
**🔒 Approval gate 2 — ready for review:**
`OceanParityArtifacts/compat/compat/lit_t050.0.png` (parity pose composite) and
`sunfacing_lit_t050.0.png` / `sunfacing_diag5_t050.0.png` (glitter proof), with
`diag2…diag7` isolation views alongside.

## Stage 3 — Match atmosphere and camera ⏳

*(plan step 5)*

Lock down: skybox/cubemap, sun direction + HDR irradiance, linear color space, HDR
rendering, tone mapping/exposure, fog and horizon behavior, camera transform/FOV/clip
planes/aspect/resolution (2592×1355). Keep the Unity validation camera and the approved
Godot Till Dawn camera explicitly separate.

**Checkpoint:** Linux Unity result agrees with the checked-in reference in composition and
color, not just wave geometry.
**🔒 Approval gate 3:** final water + sky + sun + fog + exposure composition.

## Stage 4 — Unity reference corpus complete and visually approved; paired Godot comparison pending

*(plan step 6)*

Capture all fixed times on both backends and evaluate: SSIM, luminance histogram,
edge/silhouette difference, foam coverage, height RMS, slope RMS, dominant wave direction,
temporal difference energy, periodic continuity around 200 s.

**Acceptance rule:** the Linux capture is accepted only if demonstrated
presentation-equivalent to the D3D control (Wine bottle "UnityReference") — not merely
better-looking than the broken render.

**Accepted 2026-07-12:** the user visually approved the animated Vulkan compatibility result and
the 120-frame review atlas as the frozen Till Dawn wave, foam, and surface-lighting reference. The
capture harness now advances short intervals at exactly 1/60 s; repeated directional fold/foam
structures at 0/200 seconds are the verified periodic phase. Atmosphere refinement is deferred to
Till Dawn roadmap Phase 9 and is not a blocker for this ocean-surface reference.

## Stage 5 — Selectable, reproducible compatibility (capture complete; rendered smoke pending)

*(plan step 7 — partially banked by Stage 1)*

Already in place: automatic backend selection with manual override, original shader
retained as control, self-failing field smoke test, artifact root override
(`TILLDAWN_OCEAN_PARITY_ROOT`).

The exact capture command, hardware/API limitations, canonical filenames, metadata, and
manifest validation are now implemented. Vulkan remains an explicit command-line requirement;
the project graphics API settings are intentionally not mutated because this repository is a
reference dependency. The accepted corpus is `OceanParityArtifacts/accepted_linux_vulkan`;
its 120 images cover every manifest view/time. The missing environment cubemap and procedural
runtime substitute are explicit metadata deviations; final frames run the original atmosphere
shader deterministically with its serialized fog/sun settings and captured depth. A future
rendered-output smoke test may still automate rejection of the perforated original path; the
current acceptance is backed by the fixed corpus and manual visual approval.

---

## Reproduction commands

```bash
# Field diagnostics (Stage 1, rerunnable smoke test)
~/Unity/Hub/Editor/2023.2.2f1/Editor/Unity -projectPath /mnt/data/GitHub/Water \
  -batchmode -force-vulkan -executeMethod OceanFieldDiagnostics.Run -logFile <log>

# Backend comparison captures
~/Unity/Hub/Editor/2023.2.2f1/Editor/Unity -projectPath /mnt/data/GitHub/Water \
  -batchmode -force-vulkan -executeMethod OceanCompatCapture.Run -logFile <log>
```

`-force-vulkan` is mandatory (finding #1). A leftover GUI editor holds
`Temp/UnityLockfile` and blocks batch runs — check `pgrep -af Unity` first.

| Env var | Effect |
|---|---|
| `TILLDAWN_WATER_BACKEND` | `original` / `compat` (unset = auto: compat on non-D3D APIs) |
| `TILLDAWN_COMPAT_QUADRES_MULT` | Compat mesh density multiplier (default 4) |
| `TILLDAWN_OCEAN_PARITY_ROOT` | Artifact output root (default `OceanParityArtifacts/`) |
