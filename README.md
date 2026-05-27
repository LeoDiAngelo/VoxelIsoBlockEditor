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
<img width="3839" height="2159" alt="image" src="https://github.com/user-attachments/assets/105a726b-d427-4580-80cf-fc5d44f9fb6b" />
<img width="3840" height="2064" alt="image" src="https://github.com/user-attachments/assets/91351ef0-722c-4fb1-b0aa-11e782072a0d" />

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

- **Sparse editor scene** - normal authored block models.
- **Packed full-grid scene** - procedural 256³ surface/stress-test mode with sparse overlays and deleted-cell/cavity handling.

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

## Why WPF + MonoGame

WPF handles the editor UI. MonoGame drives the hardware-accelerated 3D viewport.
The combination is uncommon but produces a native Windows tool with full DirectX
rendering performance inside a standard desktop application window.

---

## Current limitations

- This is a WPF/Windows desktop project and targets `net8.0-windows`.
- The internal namespace is still `IsoBlockCharacterEditor` for compatibility with existing XAML/resources.
- The renderer is optimized for this editor workflow, not yet packaged as a standalone reusable engine library.
- There are currently no formal unit tests included in the project zip.
- Packed 256³ mode is intended as a performance/stress-test path, not as the normal editable model format.

---

## Some suggested next steps

- Consider moving renderer components into a dedicated namespace/folder structure.
- Move editor commands out of MainWindow into focused services/commands.
- Split VoxelEditorControl into scene, input, picking and rendering coordination.
- Add unit tests for CoordinateHelper, chunk dirty expansion, greedy meshing and multi-block move transactions.
- Keep chunked greedy meshing as the primary rendering path for dense or mostly static voxel worlds.
- Add GPU instancing for sparse overlays, selection previews, brush previews, and ghost blocks.
- Investigate a reusable WPF/MonoGame viewport-hosting SDK based on the current editor viewport implementation.

---

## Article ##

# Building a High-Performance Voxel Editor with WPF + MonoGame: RenderChunkStore and the GPU Buffer Pipeline

**By Lennart Svensson**
*Voxel Iso Block Editor — github.com/LeoDiAngelo/VoxelIsoBlockEditor*

## The problem nobody talks about

Most articles about MonoGame focus on games. But what happens when you need a *tool*, a real desktop editor with a WPF UI, property panels, undo/redo, and a 3D viewport that renders 16 million voxels at 60 FPS without dropping a frame when the user edits?

The answer isn't obvious. The WPF+MonoGame combination is famously hostile: flickering, input focus fights, DPI scaling bugs, and a frame cadence that isn't yours to control. Most attempts fall apart above a few thousand blocks.

This article documents how `RenderChunkStore` solves the GPU buffer lifetime problem specifically in a `WpfGame` host and why getting it right is what separates a performant editor from one that stutters.

## Architecture overview

The renderer is split into three focused components:

```
ChunkBuildScheduler    owns background build concurrency
ChunkMeshBuilder       owns CPU mesh generation (greedy meshing)
RenderChunkStore       owns GPU buffer lifetime and draw calls
```

`ChunkedVoxelRenderer` coordinates them. `VoxelEditorControl` (the `WpfGame` subclass) drives the frame loop.

The rule that makes everything work: **`Draw()` never builds meshes. CPU mesh work never touches GPU state.**

## Why RenderChunkStore exists

In a standard game you might manage vertex buffers inline: build a mesh, upload it, draw it, done. In an editor with async background building, this falls apart immediately:

1. The user edits a block
2. A background task starts building the new mesh
3. The UI thread keeps drawing the old mesh
4. The background task finishes. When is it safe to upload?
5. The user edits again before step 4 completes

You need a store that owns GPU buffer lifetime independently of the CPU build pipeline. That's `RenderChunkStore`.

```csharp
internal sealed class RenderChunkStore : IDisposable
{
    private readonly List<RenderChunk> _chunks = new(512);
    private readonly Dictionary<int, RenderChunk> _chunkMap = new(512);
    // ...
}
```

The `List<RenderChunk>` is the stable draw list, iterated every frame. The `Dictionary<int, RenderChunk>` is the lookup map, used only when applying build results. They are separate because draw performance depends on cache-friendly sequential iteration, not dictionary overhead.

## Applying build results correctly

When a background build completes, `ApplyBuildResult` uploads the new geometry:

```csharp
public RenderChunkStoreStats ApplyBuildResult(
    GraphicsDevice graphicsDevice, BuildResult result)
{
    var rebuiltKeys = new HashSet<int>(result.RebuiltChunkKeys.Count);
    for (int i = 0; i < result.RebuiltChunkKeys.Count; i++)
        rebuiltKeys.Add(result.RebuiltChunkKeys[i]);

    var liveMeshKeys = new HashSet<int>(result.Chunks.Count);
    for (int i = 0; i < result.Chunks.Count; i++)
    {
        ChunkMeshData data = result.Chunks[i];
        liveMeshKeys.Add(data.Key);

        if (!_chunkMap.TryGetValue(data.Key, out RenderChunk? chunk))
        {
            chunk = new RenderChunk(data.Cx, data.Cy, data.Cz);
            _chunkMap.Add(data.Key, chunk);
            _chunks.Add(chunk);
        }

        chunk.ReplaceBuffer(graphicsDevice, data.Vertices);
        chunk.Bounds = data.Bounds;
        chunk.SurfaceMask = data.SurfaceMask;
        chunk.PrimitiveCount = data.Vertices.Length / 3;
    }

    // Partial rebuild: only remove chunks that were rebuilt AND are now empty.
    // Full rebuild: remove any chunk not in the new result.
    for (int i = _chunks.Count - 1; i >= 0; i--)
    {
        RenderChunk chunk = _chunks[i];
        int key = CoordinateHelper.ChunkKey(chunk.Cx, chunk.Cy, chunk.Cz);
        bool remove = result.FullRebuild
            ? !liveMeshKeys.Contains(key)
            : rebuiltKeys.Contains(key) && !liveMeshKeys.Contains(key);
        if (!remove) continue;

        chunk.Dispose();
        _chunks.RemoveAt(i);
        _chunkMap.Remove(key);
    }

    return CalculateStats();
}
```

The partial rebuild logic is critical. For a small edit, the user places one block, and only the affected chunks and their neighbours are rebuilt. The removal condition `rebuiltKeys.Contains(key) && !liveMeshKeys.Contains(key)` ensures only chunks that were actually rebuilt and came back empty get removed. Chunks that were not part of this build are untouched.

For a full rebuild (scene switch, stress test), the simpler `!liveMeshKeys.Contains(key)` removes anything not in the new result.

## GPU buffer lifetime: the DynamicVertexBuffer strategy

Each `RenderChunk` owns a `DynamicVertexBuffer`:

```csharp
public void ReplaceBuffer(GraphicsDevice graphicsDevice, VertexPositionColor[] vertices)
{
    VertexCount = vertices.Length;
    PrimitiveCount = vertices.Length / 3;

    if (VertexBuffer is null || BufferCapacity < vertices.Length)
    {
        VertexBuffer?.Dispose();
        BufferCapacity = Math.Max(vertices.Length, BufferCapacity * 2);
        VertexBuffer = new DynamicVertexBuffer(
            graphicsDevice,
            VertexPositionColor.VertexDeclaration,
            BufferCapacity,
            BufferUsage.WriteOnly);
    }

    VertexBuffer.SetData(
        0,
        vertices,
        0,
        vertices.Length,
        VertexPositionColor.VertexDeclaration.VertexStride,
        SetDataOptions.Discard);
}
```

Two decisions here that matter.

**Capacity doubling.** `BufferCapacity = Math.Max(vertices.Length, BufferCapacity * 2)` means a chunk that grows gradually does not reallocate on every edit. A chunk that shrinks keeps its existing buffer with no reallocation and no GPU stall.

**`SetDataOptions.Discard`.** This tells the DirectX driver it does not need to preserve the old buffer contents before overwriting. Without it, the driver may insert a pipeline stall to finish reading from the buffer before the CPU writes new data. In a WPF host where the compositor and MonoGame share the same thread budget, those stalls compound quickly.

## The draw loop

```csharp
public void Draw(
    GraphicsDevice graphicsDevice,
    BasicEffect effect,
    BoundingFrustum frustum,
    bool enableFrustumCulling,
    byte surfaceFilterMask,
    bool useSurfaceFilter,
    out int visibleChunks,
    out int drawCalls)
{
    visibleChunks = 0;
    drawCalls = 0;

    for (int i = 0; i < _chunks.Count; i++)
    {
        RenderChunk chunk = _chunks[i];
        if (chunk.VertexBuffer is null || chunk.PrimitiveCount <= 0)
            continue;

        // Camera-facing surface filter: skip chunks whose visible surfaces
        // don't face the camera. For an isometric view this eliminates roughly
        // half the chunks with zero geometry cost.
        if (useSurfaceFilter &&
            chunk.SurfaceMask != 0 &&
            (chunk.SurfaceMask & surfaceFilterMask) == 0)
            continue;

        if (enableFrustumCulling &&
            frustum.Contains(chunk.Bounds) == ContainmentType.Disjoint)
            continue;

        visibleChunks++;
        graphicsDevice.SetVertexBuffer(chunk.VertexBuffer);

        for (int p = 0; p < effect.CurrentTechnique.Passes.Count; p++)
        {
            effect.CurrentTechnique.Passes[p].Apply();
            graphicsDevice.DrawPrimitives(
                PrimitiveType.TriangleList, 0, chunk.PrimitiveCount);
            drawCalls++;
        }
    }
}
```

For the 256³ stress test (16,777,216 blocks), the camera-facing surface filter combined with frustum culling reduces 512 total chunks to a fraction of the total draw calls depending on camera angle. The chunk list is a plain `List<RenderChunk>` with no LINQ, no allocation, and sequential cache-friendly iteration.

## The concurrency contract

`RenderChunkStore` is always accessed from the MonoGame/UI thread: `ApplyBuildResult` in `Update()` and `Draw()` in `Draw()`. Background builds produce `BuildResult` objects on worker threads and hand them off via `ChunkBuildScheduler`. The store never touches CPU build state. The build pipeline never touches GPU state.

`ChunkBuildScheduler` enforces this with a triple guard:

```csharp
private int _buildRunning;   // Interlocked, atomic build gate
private int _buildSerial;    // Incremented on every new build start
private long _sceneId;       // Incremented on every scene boundary
```

A worker's result is only accepted if all three still match at publication time. Stale results from cancelled builds can never enter the GPU pipeline.

```csharp
private bool TryPublish(BuildResult result, CancellationToken token)
{
    if (token.IsCancellationRequested)
        return false;

    lock (_sync)
    {
        if (token.IsCancellationRequested || result.SceneId != _sceneId)
            return false;

        if (_pendingResult is not null)
        {
            if (result.Generation < _pendingResult.Generation)
                return false;
            _pendingResult.ClearTransientMeshData();
        }

        _pendingResult = result;
        return true;
    }
}
```

## Resource cleanup in WpfGame

This is where most WPF+MonoGame implementations leak. `WpfGame` does not give you a `Game.Exit()` equivalent. Resources must be explicitly disposed when the control unloads.

```csharp
private void OnUnloaded(object sender, RoutedEventArgs e)
{
    ClearWorkspaceArrowKeyState();
    DisposeEditorResources();
}

private void DisposeEditorResources()
{
    if (_resourcesDisposed) return;
    _resourcesDisposed = true;

    _inputHandler?.Detach();
    _inputHandler = null;

    if (_pieces is not null && _piecesChangedHandler is not null)
    {
        _pieces.CollectionChanged -= _piecesChangedHandler;
        _piecesChangedHandler = null;
    }

    _chunkRenderer.Dispose();
    _packedRenderer.Dispose();

    _effect?.Dispose();
    _effect = null;
    _spriteBatch?.Dispose();
    _spriteBatch = null;
    _solidRasterizer.Dispose();
    _wireRasterizer.Dispose();
    _edgeRasterizer.Dispose();
}
```

`InputHandler.Detach()` explicitly removes the WPF event handlers that were registered with `handledEventsToo: true`. Without this, the handlers stay alive and hold references to the control after it unloads.

## Conclusion

The `RenderChunkStore` and `ChunkBuildScheduler` split works because each class owns exactly one concern. GPU buffers never wait on CPU builds. CPU builds never block the draw thread. Scene boundaries are hard, no stale geometry can survive a scene switch.


## License

MIT License — see [LICENSE](LICENSE) for details.
