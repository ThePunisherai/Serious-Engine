# GameStudio Engine

A modern desktop toolchain for Serious Engine 1 assets: browse the engine's resource archives,
decode its textures, upscale them into PBR material sets, and generate an Unreal Engine 5 import
package — with the whole pipeline also exposed over MCP so an AI assistant can drive it.

It replaces the MFC-era asset browsing bolted onto the original editor tools with a single
WPF application, sharing the visual identity of [XFS Studio](https://github.com/ThePunisherai/XfsStudio).

## Projects

| Project | Target | What it is |
| --- | --- | --- |
| `GameStudio.Core` | `net10.0` | Format readers, imaging, modernization and Unreal export. No UI, no native dependencies — runs anywhere. |
| `GameStudio.Mcp` | `net10.0` | MCP server, JSON-RPC 2.0 over stdio. |
| `GameStudio.App` | `net10.0-windows` | The WPF desktop app. |
| `GameStudio.Core.Tests` | `net10.0` | xUnit suite, including decode coverage over the shipped `SE1_10.gro`. |

All the real work lives in `GameStudio.Core`, so the app and the MCP server expose the same
behaviour and the logic is testable on any platform.

## Building

```bash
dotnet build Sources/GameStudio/GameStudio.slnx
dotnet test  Sources/GameStudio/GameStudio.Core.Tests
```

Needs the .NET 10 SDK. The WPF app builds on Linux too (`EnableWindowsTargeting`), though it
only runs on Windows.

## What it understands

**`.gro` archives** are plain zip containers, so they are read directly — no unpacking step.

**`.tex` textures** come in two on-disk revisions, and both decode:

- **Version 4** stores one full-resolution frame as raw 24- or 32-bit RGB(A).
- **Version 3** stores a whole mipmap chain per frame at 16 bits per texel, packed as RGBA4444
  when the alpha flag is set and RGB5551 otherwise. Only the largest mip carries information the
  engine keeps; the rest is regenerated.

**Effect textures** (fire, water, plasma) store no pixels at all — an `FXDT` chunk describes
emitters and the frames are simulated at runtime. These are reported as such rather than treated
as corrupt.

The decoder is verified against every one of the 724 textures in the shipped `SE1_10.gro`.

## Modernizing artwork

The pipeline upscales each texture and derives a PBR material set from it:

- **Base colour** — resampled in **linear light**. Averaging sRGB-encoded bytes directly is what
  makes naive upscales of these textures look muddy.
- **Normal** — Sobel gradient of a height field derived from perceptual luminance, encoded
  tangent-space in the +Y-up convention Unreal expects. Sampling wraps, because engine textures tile.
- **Roughness** — driven by local detail contrast: busy areas read as worn, flat areas polish smooth.
- **Ambient occlusion** — cavity depth, i.e. how far a texel sits below its neighbourhood.

Upscaling uses a built-in Lanczos/Catmull-Rom resampler by default. Point it at a
`realesrgan-ncnn-vulkan`-style executable for substantially better results; if that binary is
missing or fails, the pipeline falls back to built-in resampling and says so in the report.

> These maps are an **approximation, not a reconstruction**. The originals predate PBR and carry
> no measured surface data. Treat the output as a strong starting point for an artist, not a
> finished material.

Each run writes PNGs plus a `materials.json` manifest.

## Exporting to Unreal Engine 5

`UnrealExporter` turns a manifest into an import package:

- `unreal_import.json` — every generated map with its Unreal asset name, sRGB flag and
  compression setting (`TC_NORMALMAP` for normals, `TC_MASKS` for the linear masks).
- `import_to_unreal.py` — run inside the Unreal Editor with `py "<path>"`. It imports the
  textures and builds a material per source texture with base colour, normal, roughness and
  occlusion connected. Textures that had alpha produce masked, two-sided materials.

The script reads the manifest next to it, so re-running a modernization pass only means
re-running the script.

**Scope:** this path covers textures and materials. Model and world geometry (`.mdl`, `.wld`) is
**not** exported — those formats have eight version variants with compressed vertex data, and a
geometry exporter that is subtly wrong is worse than none. Geometry is the next stage.

## Engine-side changes

Modernized artwork is worthless if the engine downsamples it on load, so the engine itself was
changed too. All of it lives in `Sources/Engine`.

**The texture dimension cap is no longer tied to world scale.** `CTextureData::Create_t` rejected
anything wider than `MAX_MEX`, which is not a resolution limit at all — it is the world-space
scale, i.e. how many mexels make up a meter. Raising it to allow bigger textures would have
silently rescaled every existing world. The two are now separate constants
(`MAX_TEXTURE_DIMENSION`, 4096), so world geometry is untouched and textures can be four times
larger than before.

**A latent release-build bug is fixed.** The same function guarded its mexel-to-pixel ratio with
`ASSERT(pixSizeU<=mexWanted)`. Assertions compile out in release builds, so an over-dense texture
left `FastLog2()` reading a zero ratio and produced a garbage mip level instead of an error. It is
now a real thrown error with a message that says what to do about it.

That guard also describes a constraint worth knowing before you upscale anything: **a texture
cannot hold more than one texel per mexel**, and a meter is `MAX_MEX` (1024) mexels. So the ceiling
is 1024 texels per meter of world surface. A 4096-pixel texture is fine — it just has to be
declared at least four meters wide. Upscaling art 4× while keeping its old world size will not
work; the extra resolution has to buy you a physically larger surface.

**Defaults now target current hardware** rather than the 2001 baseline: 32-bit texture quality,
trilinear filtering, 16× anisotropic filtering, and a texture budget of 2048×2048 (512×512 per
animated frame). Anisotropy is a request, not a demand — `gfxSetTextureFiltering()` already clamps
it to whatever the driver reports. These are all persistent user settings, so existing
configurations keep whatever they already had.

Stock game content is 512 pixels or smaller, so none of this changes how the original games look;
it only stops the engine from throwing away resolution that modernized art actually has.

**Lightmaps are 32-bit by default.** `shd_bFineQuality` was off, which stores lightmaps at 16 bits
and shows as banding across large lit surfaces. `ShadowMap.cpp` and `LayerMixer.cpp` already force
it back off when the driver reports no 32-bit texture support, so it degrades on its own. The
shadow cache went from 8 MB to 64 MB (it is clamped to 128), which is the difference between
constantly re-rendering shadowmaps and keeping a level's worth of them resident.

Lightmap *resolution* is deliberately left alone. `shd_iStaticSize` is already at its ceiling of 8,
and raising that ceiling would need a wider `MipmapTable::mmt_aslOffsets`, which is sized from
`MAX_MEX_LOG2` and would overflow at the next step up. That is a real change rather than a default,
and it is not one to make without being able to run the lightmap baker.

## Post-processing for existing games

The scene renderer is untouched — still fixed-function, still no shaders. A GLSL chain runs
*afterwards*, on the finished frame, which is what lets it improve content built for the original
engine without that content or the renderer knowing anything about it. Off by default, because it
changes how every existing game looks.

```
gfx_bPostProcessing = 1;    // master switch, off by default
gfx_fExposure       = 1.0;  // 0.1 - 8
gfx_iTonemap        = 1;    // 0 = off, 1 = filmic (ACES)
gfx_fSaturation     = 1.0;  // 0 = greyscale, 1 = unchanged
gfx_fBloomThreshold = 0.75; // brightness where bleed starts
gfx_fBloomIntensity = 0.35; // 0 disables the bloom passes entirely
gfx_bFXAA           = 1;    // edge-directed antialiasing
gfx_bSSAO           = 1;    // depth-based ambient occlusion
gfx_fSSAORadius     = 1.0;  // world units
gfx_fSSAOIntensity  = 1.0;
```

The frame is read back with `glCopyTexSubImage2D` instead of being rendered into a framebuffer
object, so nothing about how the scene is drawn has to change and no FBO support is needed — only
GLSL, which is GL 2.0. Intermediate passes draw into the back buffer and are copied straight back
out; it is not presented until the chain finishes, so using it as scratch space is free. The
fullscreen quad goes through the engine's own vertex arrays and `gfx*` state wrappers rather than
raw GL, because the engine caches its GL state and asserts against the driver in debug builds.

Worth being clear about one limit: the frame is captured from an 8-bit back buffer, so this is
tonemapping and bloom applied to LDR data. It is a look, not true HDR — real HDR would mean the
scene renderer writing float targets, which is the renderer rewrite this deliberately avoids.

### Ambient occlusion

Occlusion needs to turn a depth sample back into a view-space position, which needs the frustum the
scene was drawn with — and that cannot be read back at the end of the frame, because the 2D overlay
is drawn after the world and leaves its own orthographic projection behind. It cannot borrow the
engine's `GFX_fLast*` cache either, for the same reason: `ogl_SetOrtho` writes those too. So
`ogl_SetFrustum` records the scene projection as the world is drawn, before its own cache check so
an unchanged frustum still refreshes it. The flag is cleared once the chain consumes it — a frame
that draws no world sits occlusion out rather than reusing a stale projection.

The kernel is a spiral generated from the loop counter rather than read out of a const array,
because indexing an array with a loop variable is not something GLSL 110 guarantees. Each pixel
rotates the spiral by a hash of its coordinates, trading banding for noise, and the two blur passes
turn that noise back into a smooth term. Occlusion resolves at half resolution and multiplies into
the scene *before* bloom, so an unlit corner darkens instead of glowing.

If the driver refuses a depth texture, occlusion switches itself off for the session and the rest
of the chain carries on.

The tuning defaults — radius, intensity, the 0.02 self-occlusion bias, the 0.05 FXAA contrast
threshold — are reasoned starting points, not measured ones. They want a real build and a real
scene to settle.

## Editor integration

The GameStudio app is reachable from inside the editor rather than only as a separate program.
WorldEditor gained a **Tools** menu on both menu bars, with **GameStudio Engine...** alongside the
existing Modeler and TexMaker entries — those two were previously toolbar-only. It reuses
`CMainFrame::StartApplication()`, the same helper `OnCallModeler()` uses, and expects
`GameStudioEngine.exe` next to the editor.

## Rebranding

The engine startup banner, the editor and the modeler now identify as **GameStudio Engine**,
**GameStudio Editor** and **GameStudio Modeler**. Functional identifiers are deliberately left
alone — `SE_InitEngine("SeriousEditor")` is compared against that exact string to set
`_bWorldEditorApp`, so renaming it would change behaviour rather than branding. Croteam's copyright
notices and Serious Engine licensing contacts stay exactly where they are.

## MCP server

```
GameStudio.Mcp [--root <directory>] [--no-heartbeat]
```

Speaks JSON-RPC 2.0 over stdio, one message per line. `--root` confines every path argument to
that directory and is strongly recommended.

| Tool | Purpose |
| --- | --- |
| `engine_overview` | Summarize an installation: archives, asset counts by kind, total size. |
| `list_archive` | List archive entries, filtered by kind or path substring. |
| `texture_info` | Format version, dimensions, alpha, frame count. |
| `export_texture` | Decode a frame to PNG. |
| `modernize_textures` | Run the modernization pipeline. |
| `export_unreal` | Build the Unreal import package. |
| `create_map` | Start a new map definition. |
| `map_add_room` | Add a hollow or solid box-shaped room. |
| `map_add_light` | Add a point, spot, directional or rect light. |
| `map_add_prop` | Place a static mesh. |
| `map_describe` | Summarize rooms, lights, props, bounds and render settings. |
| `export_map_unreal` | Build the Unreal level package. |

Textures inside an archive are addressed as `archive.gro::entry/path.tex`.

Client configuration:

```json
{
  "mcpServers": {
    "gamestudio-engine": {
      "command": "C:\\path\\to\\GameStudio.Mcp.exe",
      "args": ["--root", "C:\\path\\to\\SeriousEngine"]
    }
  }
}
```

The server refreshes `%TEMP%/GameStudioMcp.heartbeat` every few seconds; the app polls it to show
a live status badge, the same signal XFS Studio uses.

## Authoring maps

Maps are built up a piece at a time through the MCP tools, each call reading the file back,
appending, and saving — so a client can author a level over a conversation without holding it in
memory:

```
create_map        map=arena.map.json name=Arena playerStart=[0,0,120]
map_add_room      map=arena.map.json name=Main center=[0,0,0] size=[2000,2000,600]
map_add_light     map=arena.map.json name=Key kind=Spot position=[0,0,250] intensity=12000
export_map_unreal map=arena.map.json output=./unreal
```

The export writes `level.json` plus a `build_level.py` that Unreal runs in-editor. Rooms are
decomposed into slabs in C# rather than in the script, so the geometry is testable without an
Unreal install; the script only spawns what the manifest describes. Actors it creates are tagged
`GameStudio`, so re-running replaces them and leaves hand-placed actors alone.

A hollow room becomes a shell of six slabs. The X-facing walls span the full depth and the Y-facing
ones are inset between them, so no two slabs occupy the same space. `openFaces` drops named faces
so rooms can connect. If the walls are too thick to leave an interior, the room falls back to a
solid block rather than emitting inside-out geometry.

## About ray tracing

Real-time ray tracing is not something that can be added to this engine. It has no shader pipeline
at all — the renderer is fixed-function OpenGL 1.x and Direct3D 8 multitexturing. Ray tracing needs
a modern API, acceleration structures and denoising, which means replacing the renderer rather than
extending it.

Baked ray-traced lighting looked promising, since the engine already ships a ray caster in
`WorldRayCasting.cpp` and bakes static light into lightmaps. It does not work either, and the
reason is worth recording so nobody re-treads it. `CCastRay` has two modes. Given an origin entity
it walks sectors, which is fast — but it seeds that walk from `AddSectorsAroundEntity`, which reads
the sectors an entity *stands in*; a zoning brush is not in its own sectors, so the walk starts
empty and nothing is occluded. Without an origin entity it calls `TestWholeWorld`, which iterates
every entity, every sector and every polygon in the level. That is O(level) per ray, against the
millions of rays an ambient-occlusion bake needs. The caster is built for a handful of gameplay
rays per frame, not for baking. Doing this properly needs a dedicated BVH over the level geometry,
which is a real subsystem rather than a patch.

So ray tracing lives on the Unreal side, where it is genuinely real-time: the exported level
carries a post-process volume configured for Lumen global illumination and Lumen reflections, with
`hardwareRayTracing` asking Lumen to trace against real triangles instead of distance-field
proxies. Lights default to movable so they participate in those bounces.

## License

GPL-2.0, matching the engine it is built on.
