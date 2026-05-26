# Voxel Iso Block Editor

**Voxel Iso Block Editor** is a Windows desktop voxel/block construction editor built with **WPF**, **.NET 8**, and **MonoGame.Framework.WpfInterop**.

The project focuses on an isometric block-building workflow with hardware-accelerated 3D rendering, authored voxel-like block shapes, stress-test scenes, and a renderer architecture designed for large sparse scenes and packed 256³ test volumes.

**Author:** Lennart Svensson

---

## Highlights

- WPF desktop editor with a MonoGame-powered 3D viewport.
- Canonical block shape library:
  - Full cube
  - Slope 50%
  - Diagonal slope 50%
  - Diagonal slope inward
  - Corner cut 50%
- Block placement on floor and side construction grids.
- Face/block attachment workflow for building isometric voxel structures.
- Selection, multi-selection, paint, delete, rotate, flip, copy and paste.
- Undo/redo for core editor actions.
- Save and load support for block models.
- Hardware-accelerated chunk rendering.
- Greedy/full-cube surface meshing combined with exact authored meshes for slopes/cuts.
- Async chunk mesh build pipeline with cancellation/latest-build behavior.
- Stress-test modes for large scenes, including packed 256³ surface rendering.
- Dedicated Block Shape Library preview window.

<img width="3840" height="2064" alt="image" src="https://github.com/user-attachments/assets/aacfa55a-2157-435b-8c51-8992592426f2" />
<img width="3840" height="2064" alt="image" src="https://github.com/user-attachments/assets/cc6fe36e-6a6a-4854-8bfa-af8041a0f3ab" />
<img width="3839" height="2062" alt="image" src="https://github.com/user-attachments/assets/7a2cbb38-8217-464c-a995-1d49398dc351" />
<img width="3840" height="2064" alt="image" src="https://github.com/user-attachments/assets/bc67e8a5-6fa4-4cfa-bef9-351ca3ca84c9" />

---

## Requirements

- Windows 10 or Windows 11
- Visual Studio 2022
- .NET 8 SDK
- NuGet restore enabled

NuGet package used by the project:

```xml
<PackageReference Include="MonoGame.Framework.WpfInterop" Version="1.9.2" />
```

---

## Getting started

Clone the repository and open the solution:

```text
VoxelIsoBlockEditor.sln
```

Then build and run from Visual Studio:

```text
Configuration: Debug or Release
Platform: Any CPU
Target framework: net8.0-windows
```

The application starts with the main editor window maximized. The Block Shape Library opens as a normal centered tool window.

---

## Controls

### Viewport

| Action | Control |
|---|---|
| Rotate camera | Right mouse drag |
| Pan camera / workspace | Arrow keys |
| Zoom | Mouse wheel |
| Center view | Center Camera button |

### Editing

| Action | Control |
|---|---|
| Place block | Build mode + click grid/block face |
| Select block | Select mode + click block |
| Multi-select | Shift + click |
| Move selected blocks | W/A/S/D/Q/E |
| Delete selected blocks | Delete key or Delete button |
| Paint selected blocks | Paint button |
| Rotate selected block | Rotate 90° button |
| Flip selected block | Flip H / Flip V buttons |
| Copy / paste | Ctrl+C / Ctrl+V |
| Undo / redo | Ctrl+Z / Ctrl+Y |

---

## Project structure

The codebase has been split into focused components so the editor is easier to reason about and maintain.

| File | Responsibility |
|---|---|
| `MainWindow.xaml` / `MainWindow.xaml.cs` | Main WPF shell, editor commands, model operations and UI wiring. |
| `VoxelEditorControl.cs` | MonoGame viewport host and high-level editor/render coordination. |
| `VoxelEditorControl.Picking.cs` | Picking, ray tests and grid/cell hit logic. |
| `CameraController.cs` | Camera state, projection/view matrices, zoom, rotation and workspace pan. |
| `InputHandler.cs` | Mouse input, drag handling, wheel zoom and viewport event routing. |
| `BlockLibraryWindow.cs` | Block shape library UI and preview viewport setup. |
| `Models.cs` | Core model types, block definitions and block catalog. |
| `VoxelWorld.cs` | Authoritative sparse voxel/block world representation. |
| `CoordinateHelper.cs` | Cell/chunk coordinate packing, validation and helper utilities. |
| `ChunkedVoxelRenderer.cs` | High-level chunk renderer and draw coordination. |
| `ChunkBuildScheduler.cs` | Background chunk build scheduling, cancellation and latest-result handling. |
| `ChunkMeshBuilder.cs` | CPU mesh generation for chunk snapshots. |
| `ProceduralFullGridBuilder.cs` | Packed full-grid surface/cavity mesh generation. |
| `RenderChunkStore.cs` | GPU chunk buffer lifetime and live chunk collection. |
| `RenderSceneSources.cs` | Scene source strategies for sparse and packed rendering. |
| `PackedFullGridRenderer.cs` | Packed 256³ overlay/deleted-cell rendering support. |
| `IsoBlockMeshBuilder.cs` | Authored mesh generation for block shapes. |

Version notes and historical verification logs are kept in the `versions/` folder.

---

## Rendering architecture

The renderer is designed around chunked rendering rather than per-block immediate drawing.

Current pipeline:

1. The editor changes the voxel/block world.
2. Dirty chunks are marked.
3. A snapshot is built from the active scene source.
4. CPU mesh generation runs asynchronously.
5. Completed chunk meshes are applied to GPU buffers.
6. The viewport draws live chunk buffers every frame.

The project supports two main scene-source paths:

- **Sparse editor scene** — normal authored block models.
- **Packed full-grid scene** — procedural 256³ surface/stress-test mode with sparse overlays and deleted-cell/cavity handling.

---

## Performance design

Important performance choices in this version:

- Chunked mesh rebuilds instead of rebuilding the whole scene for small edits.
- Background CPU mesh builds to avoid doing heavy geometry work in the draw path.
- Greedy meshing for full-cube surfaces where possible.
- Exact mesh generation for authored slope/cut blocks.
- Dirty chunk + six-neighbour expansion for correct face exposure after edits.
- Packed 256³ mode avoids storing every cell as normal editor blocks.
- Coordinate packing uses compact 8-bit chunk/cell layouts for the current 256³ design.
- No LINQ in hot-path renderer/mesh code.

---

## Repository layout notes

This repository is intended to be published as a clean source project.

Recommended root contents:

```text
VoxelIsoBlockEditor.sln
VoxelIsoBlockEditor.csproj
README.md
*.cs
*.xaml
versions/
```

The `versions/` folder contains historical verification and change notes. These are useful while the project evolves, but the primary entry point for GitHub should be this README.

---

## Current limitations

- This is a WPF/Windows desktop project and targets `net8.0-windows`.
- The internal namespace is still `IsoBlockCharacterEditor` for compatibility with existing XAML/resources.
- The renderer is optimized for this editor workflow, not yet packaged as a standalone reusable engine library.
- There are currently no formal unit tests included in the project zip.
- Packed 256³ mode is intended as a performance/stress-test path, not as the normal editable model format.

---

## Suggested next steps

Good future improvements before a larger public release:

- Add unit tests for `CoordinateHelper`, chunk neighbour expansion and mesh generation.
- Add a small screenshots section to this README.
- Add a `LICENSE` file before accepting external use or contributions.
- Add GitHub Actions build verification for `net8.0-windows`.
- Consider moving renderer components into a dedicated namespace/folder structure.

---

## License

No license file is included yet. Add a `LICENSE` file before publishing if others should be allowed to use, modify or redistribute the code.
