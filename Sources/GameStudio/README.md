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

## License

GPL-2.0, matching the engine it is built on.
