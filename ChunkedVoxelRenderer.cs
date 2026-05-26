using System.Buffers;
using System.Collections.ObjectModel;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using static IsoBlockCharacterEditor.RenderSurfaceFlags;

namespace IsoBlockCharacterEditor;

/// <summary>
/// High performance chunk-backed renderer.
/// 
/// Design rules:
/// - Draw() never builds voxel meshes.
/// - A scene change snapshots the BlockPiece collection once and starts a background CPU build.
/// - The UI/MonoGame thread only uploads completed chunk vertex buffers.
/// - Full cubes are greedy meshed per exposed face and per color.
/// - Special block shapes are kept as authored triangle geometry.
/// - Runtime draw path is one DrawPrimitives call per visible chunk.
/// </summary>
public sealed class ChunkedVoxelRenderer : IDisposable
{
    public const int ChunkSize = CoordinateHelper.ChunkSize;
    private const int ChunkShift = CoordinateHelper.ChunkShift;
    private const int ChunkMask = CoordinateHelper.ChunkMask;
    private readonly RenderChunkStore _chunkStore = new();

    private volatile bool _dirty = true;
    private volatile bool _fullRebuildRequested = true;
    private readonly ChunkBuildScheduler _buildScheduler = new();
    private int _gridSize;
    private int _requestedGeneration;
    private int _uploadedGeneration;
    private readonly object _dirtyLock = new();
    private readonly HashSet<int> _dirtyChunks = new();
    private int _lastDirtyChunkCount;
    private int _lastRebuiltChunkCount;
    private bool _lastBuildWasFullRebuild = true;
    private double _lastBuildMilliseconds;


    private MeshStats _stats;
    public MeshStats Stats => _stats;

    public readonly record struct MeshStats(
        int Chunks,
        int VisibleChunks,
        int Blocks,
        int Vertices,
        int DrawVertices,
        int Triangles,
        int Quads,
        int DrawCalls,
        int DirtyChunks,
        int RebuiltChunks,
        bool FullRebuild,
        double LastBuildMilliseconds);

    public void MarkDirty() => MarkAllDirty();

    /// <summary>
    /// Hard scene boundary used when switching between normal editor scenes and
    /// packed/procedural stress scenes. It invalidates any in-flight background
    /// build by advancing the generation, drops pending build results, and clears
    /// live GPU chunk buffers so stale geometry can never be uploaded into the
    /// next scene.
    /// </summary>
    public void ResetForNewScene(bool markDirty)
    {
        BuildResult? droppedPendingResult = _buildScheduler.ResetForNewScene();
        droppedPendingResult?.ClearTransientMeshData();

        lock (_dirtyLock)
        {
            _dirtyChunks.Clear();
            _fullRebuildRequested = markDirty;
            _dirty = markDirty;
            unchecked { _requestedGeneration++; }
            _uploadedGeneration = _requestedGeneration;
            _lastDirtyChunkCount = 0;
            _lastRebuiltChunkCount = 0;
            _lastBuildWasFullRebuild = markDirty;
            _lastBuildMilliseconds = 0;
        }

        _chunkStore.Clear();
        _stats = default;
    }

    public void MarkProceduralFullGridDirty(int gridSize)
    {
        lock (_dirtyLock)
        {
            _dirtyChunks.Clear();
            _fullRebuildRequested = true;
            _dirty = true;
            _gridSize = Math.Clamp(gridSize, 1, 256);
            unchecked { _requestedGeneration++; }
        }
    }

    /// <summary>
    /// Marks only the procedural full-grid chunks affected by a cell edit.
    /// This is used for delete/undelete/sparse overlay edits in 256³ mode so a
    /// single voxel no longer forces a rebuild of the whole procedural shell.
    /// Boundary neighbours are included because a deleted cell can expose a face
    /// in an adjacent chunk.
    /// </summary>
    public void MarkProceduralFullGridCellDirty(int x, int y, int z, int gridSize)
    {
        int safeGridSize = Math.Clamp(gridSize, 1, 256);
        if ((uint)x >= (uint)safeGridSize || (uint)y >= (uint)safeGridSize || (uint)z >= (uint)safeGridSize)
            return;

        int cx = x >> ChunkShift;
        int cy = y >> ChunkShift;
        int cz = z >> ChunkShift;

        lock (_dirtyLock)
        {
            _gridSize = safeGridSize;

            // Do not downgrade an already pending full rebuild. This can happen
            // during load/test-switch before the first procedural shell has been
            // uploaded. Partial dirty is only safe once a shell exists.
            if (_fullRebuildRequested || !_chunkStore.HasLiveChunks)
            {
                _fullRebuildRequested = true;
                _dirty = true;
                unchecked { _requestedGeneration++; }
                return;
            }

            _fullRebuildRequested = false;
            AddDirtyProceduralChunk(cx, cy, cz, safeGridSize);
            if ((x & ChunkMask) == 0) AddDirtyProceduralChunk(cx - 1, cy, cz, safeGridSize);
            if ((x & ChunkMask) == ChunkMask) AddDirtyProceduralChunk(cx + 1, cy, cz, safeGridSize);
            if ((y & ChunkMask) == 0) AddDirtyProceduralChunk(cx, cy - 1, cz, safeGridSize);
            if ((y & ChunkMask) == ChunkMask) AddDirtyProceduralChunk(cx, cy + 1, cz, safeGridSize);
            if ((z & ChunkMask) == 0) AddDirtyProceduralChunk(cx, cy, cz - 1, safeGridSize);
            if ((z & ChunkMask) == ChunkMask) AddDirtyProceduralChunk(cx, cy, cz + 1, safeGridSize);

            _dirty = true;
            unchecked { _requestedGeneration++; }
        }
    }

    public void MarkAllDirty()
    {
        lock (_dirtyLock)
        {
            _dirtyChunks.Clear();
            _fullRebuildRequested = true;
            _dirty = true;
            unchecked { _requestedGeneration++; }
        }
    }

    public void MarkChunkDirty(int cx, int cy, int cz)
    {
        if (cx < 0 || cy < 0 || cz < 0) return;
        lock (_dirtyLock)
        {
            _dirtyChunks.Add(CoordinateHelper.ChunkKey(cx, cy, cz));
            _dirty = true;
            unchecked { _requestedGeneration++; }
        }
    }

    public void MarkChunkAndNeighborsDirtyForCell(int x, int y, int z)
    {
        int cx = x >> ChunkShift;
        int cy = y >> ChunkShift;
        int cz = z >> ChunkShift;
        lock (_dirtyLock)
        {
            AddDirtyChunk(cx, cy, cz);
            if ((x & ChunkMask) == 0) AddDirtyChunk(cx - 1, cy, cz);
            if ((x & ChunkMask) == ChunkMask) AddDirtyChunk(cx + 1, cy, cz);
            if ((y & ChunkMask) == 0) AddDirtyChunk(cx, cy - 1, cz);
            if ((y & ChunkMask) == ChunkMask) AddDirtyChunk(cx, cy + 1, cz);
            if ((z & ChunkMask) == 0) AddDirtyChunk(cx, cy, cz - 1);
            if ((z & ChunkMask) == ChunkMask) AddDirtyChunk(cx, cy, cz + 1);
            _dirty = true;
            unchecked { _requestedGeneration++; }
        }
    }

    public void MarkChunksDirtyForPieces(IEnumerable<BlockPiece> pieces, bool includeNeighbors)
    {
        bool any = false;
        lock (_dirtyLock)
        {
            foreach (BlockPiece p in pieces)
            {
                int cx = p.X >> ChunkShift;
                int cy = p.Y >> ChunkShift;
                int cz = p.Z >> ChunkShift;
                AddDirtyChunk(cx, cy, cz);
                any = true;

                if (!includeNeighbors)
                    continue;

                if ((p.X & ChunkMask) == 0) AddDirtyChunk(cx - 1, cy, cz);
                if ((p.X & ChunkMask) == ChunkMask) AddDirtyChunk(cx + 1, cy, cz);
                if ((p.Y & ChunkMask) == 0) AddDirtyChunk(cx, cy - 1, cz);
                if ((p.Y & ChunkMask) == ChunkMask) AddDirtyChunk(cx, cy + 1, cz);
                if ((p.Z & ChunkMask) == 0) AddDirtyChunk(cx, cy, cz - 1);
                if ((p.Z & ChunkMask) == ChunkMask) AddDirtyChunk(cx, cy, cz + 1);
            }

            if (any)
            {
                _dirty = true;
                unchecked { _requestedGeneration++; }
            }
        }
    }

    private void AddDirtyChunk(int cx, int cy, int cz)
    {
        if (cx < 0 || cy < 0 || cz < 0) return;
        _dirtyChunks.Add(CoordinateHelper.ChunkKey(cx, cy, cz));
    }

    private void AddDirtyProceduralChunk(int cx, int cy, int cz, int gridSize)
    {
        if (cx < 0 || cy < 0 || cz < 0) return;
        int chunksPerAxis = CoordinateHelper.ChunksPerAxis(gridSize);
        if (cx >= chunksPerAxis || cy >= chunksPerAxis || cz >= chunksPerAxis) return;
        _dirtyChunks.Add(CoordinateHelper.ChunkKey(cx, cy, cz));
    }

    public void Dispose()
    {
        _buildScheduler.Dispose();

        _chunkStore.Clear();
        lock (_dirtyLock) _dirtyChunks.Clear();
    }


    public void EnsureProceduralFullGridBuilt(GraphicsDevice graphicsDevice, int gridSize)
        => EnsureProceduralFullGridBuilt(graphicsDevice, gridSize, deletedCells: null, buildFocus: new Vector3(gridSize * 0.5f));

    public void EnsureProceduralFullGridBuilt(GraphicsDevice graphicsDevice, int gridSize, IReadOnlyCollection<int>? deletedCells)
        => EnsureProceduralFullGridBuilt(graphicsDevice, gridSize, deletedCells, buildFocus: new Vector3(gridSize * 0.5f));

    public void EnsureProceduralFullGridBuilt(GraphicsDevice graphicsDevice, int gridSize, IReadOnlyCollection<int>? deletedCells, Vector3 buildFocus)
    {
        ApplyPendingBuild(graphicsDevice);

        if (!_dirty || _buildScheduler.IsBusy)
            return;

        int generation;
        int localGridSize;
        int chunksPerAxis;
        bool fullRebuild;
        int[] dirtyKeys;
        int dirtyCount;
        int dirtyChunkCountForStats;

        lock (_dirtyLock)
        {
            if (!_dirty)
                return;

            _dirty = false;
            fullRebuild = _fullRebuildRequested;
            localGridSize = Math.Clamp(gridSize, 1, 256);
            _gridSize = localGridSize;
            chunksPerAxis = CoordinateHelper.ChunksPerAxis(localGridSize);
            dirtyCount = fullRebuild ? 0 : _dirtyChunks.Count;
            dirtyKeys = dirtyCount == 0 ? Array.Empty<int>() : ArrayPool<int>.Shared.Rent(dirtyCount);
            if (dirtyCount > 0)
            {
                int dirtyIndex = 0;
                foreach (int key in _dirtyChunks)
                    dirtyKeys[dirtyIndex++] = key;
                dirtyCount = dirtyIndex;
            }

            dirtyChunkCountForStats = fullRebuild ? chunksPerAxis * chunksPerAxis * chunksPerAxis : dirtyCount;
            _lastDirtyChunkCount = dirtyChunkCountForStats;
            _dirtyChunks.Clear();
            _fullRebuildRequested = false;
            generation = _requestedGeneration;
        }

        if (!fullRebuild && dirtyCount == 0)
        {
            if (dirtyKeys.Length > 0)
                ArrayPool<int>.Shared.Return(dirtyKeys, clearArray: false);
            return;
        }

        int[] deletedSnapshot;
        int deletedCount = deletedCells?.Count ?? 0;
        bool ownsDeletedSnapshot = deletedCount > 0;
        if (deletedCount > 0)
        {
            deletedSnapshot = ArrayPool<int>.Shared.Rent(deletedCount);
            int deletedIndex = 0;
            foreach (int key in deletedCells!)
                deletedSnapshot[deletedIndex++] = key;
            deletedCount = deletedIndex;
        }
        else
        {
            deletedSnapshot = Array.Empty<int>();
        }

        var source = new PackedFullGridSceneSource(
            localGridSize,
            generation,
            _buildScheduler.SceneId,
            fullRebuild,
            dirtyKeys,
            dirtyCount,
            dirtyChunkCountForStats,
            deletedSnapshot,
            deletedCount,
            ownsDeletedSnapshot,
            buildFocus);

        if (!_buildScheduler.TryStart(source))
            MarkAllDirty();
    }

    public void EnsureBuilt(GraphicsDevice graphicsDevice, VoxelWorld? world, int gridSize)
        => EnsureBuilt(graphicsDevice, world, gridSize, buildFocus: new Vector3(gridSize * 0.5f));

    public void EnsureBuilt(GraphicsDevice graphicsDevice, VoxelWorld? world, int gridSize, Vector3 buildFocus)
    {
        ApplyPendingBuild(graphicsDevice);

        if (world is null || !_dirty || _buildScheduler.IsBusy)
            return;

        bool fullRebuild;
        int[] dirtyKeys;
        int dirtyCount;
        int dirtyChunkCountForStats;
        int generation;
        int localGridSize = Math.Clamp(gridSize, 1, 256);
        lock (_dirtyLock)
        {
            if (!_dirty)
                return;

            _dirty = false;
            fullRebuild = _fullRebuildRequested;
            dirtyCount = fullRebuild ? 0 : _dirtyChunks.Count;
            dirtyKeys = dirtyCount == 0 ? Array.Empty<int>() : ArrayPool<int>.Shared.Rent(dirtyCount);
            if (dirtyCount > 0)
            {
                int dirtyIndex = 0;
                foreach (int key in _dirtyChunks)
                    dirtyKeys[dirtyIndex++] = key;
                dirtyCount = dirtyIndex;
            }

            dirtyChunkCountForStats = fullRebuild ? -1 : dirtyCount;
            _lastDirtyChunkCount = dirtyChunkCountForStats;
            _dirtyChunks.Clear();
            _fullRebuildRequested = false;
            generation = _requestedGeneration;
        }

        if (!fullRebuild && dirtyCount == 0)
        {
            if (dirtyKeys.Length > 0)
                ArrayPool<int>.Shared.Return(dirtyKeys, clearArray: false);
            return;
        }

        _gridSize = localGridSize;
        SparseVoxelSceneSource source = SparseVoxelSceneSource.FromWorld(
            world,
            localGridSize,
            generation,
            _buildScheduler.SceneId,
            fullRebuild,
            dirtyKeys,
            dirtyCount,
            dirtyChunkCountForStats,
            buildFocus);

        if (!_buildScheduler.TryStart(source))
            MarkAllDirty();
    }

    public void EnsureBuilt(GraphicsDevice graphicsDevice, ObservableCollection<BlockPiece>? pieces, int gridSize)
    {
        EnsureBuilt(graphicsDevice, pieces, gridSize, buildFocus: new Vector3(gridSize * 0.5f));
    }

    public void EnsureBuilt(GraphicsDevice graphicsDevice, ObservableCollection<BlockPiece>? pieces, int gridSize, Vector3 buildFocus)
    {
        ApplyPendingBuild(graphicsDevice);

        if (pieces is null || !_dirty || _buildScheduler.IsBusy)
            return;

        bool fullRebuild;
        int[] dirtyKeys;
        int dirtyCount;
        int dirtyChunkCountForStats;
        int generation;
        int localGridSize = Math.Clamp(gridSize, 1, 256);
        lock (_dirtyLock)
        {
            if (!_dirty)
                return;

            _dirty = false;
            fullRebuild = _fullRebuildRequested;
            dirtyCount = fullRebuild ? 0 : _dirtyChunks.Count;
            dirtyKeys = dirtyCount == 0 ? Array.Empty<int>() : ArrayPool<int>.Shared.Rent(dirtyCount);
            if (dirtyCount > 0)
            {
                int dirtyIndex = 0;
                foreach (int key in _dirtyChunks)
                    dirtyKeys[dirtyIndex++] = key;
                dirtyCount = dirtyIndex;
            }

            dirtyChunkCountForStats = fullRebuild ? -1 : dirtyCount;
            _lastDirtyChunkCount = dirtyChunkCountForStats;
            _dirtyChunks.Clear();
            _fullRebuildRequested = false;
            generation = _requestedGeneration;
        }

        if (!fullRebuild && dirtyCount == 0)
        {
            if (dirtyKeys.Length > 0)
                ArrayPool<int>.Shared.Return(dirtyKeys, clearArray: false);
            return;
        }

        _gridSize = localGridSize;
        SparseVoxelSceneSource source = SparseVoxelSceneSource.FromPieces(
            pieces,
            localGridSize,
            generation,
            _buildScheduler.SceneId,
            fullRebuild,
            dirtyKeys,
            dirtyCount,
            dirtyChunkCountForStats,
            buildFocus);

        if (!_buildScheduler.TryStart(source))
            MarkAllDirty();
    }

    public void Draw(GraphicsDevice graphicsDevice, BasicEffect effect, BoundingFrustum frustum, Matrix world, Vector3 workspaceOffset, bool enableFrustumCulling, out int visibleChunks, out int drawCalls)
        => DrawInternal(graphicsDevice, effect, frustum, world, enableFrustumCulling, surfaceFilterMask: 0, useSurfaceFilter: false, out visibleChunks, out drawCalls);

    /// <summary>
    /// Draws any chunk set with conservative camera-facing surface filtering.
    /// Chunks that only contain faces pointing away from the camera are skipped before
    /// DrawPrimitives. Chunks with special/authored geometry are marked SurfaceAll at
    /// build time and therefore remain unfiltered. This is draw-time only: it never
    /// changes VoxelWorld, picking, save/load, undo/redo, or editor commands.
    /// </summary>
    public void DrawCameraFiltered(GraphicsDevice graphicsDevice, BasicEffect effect, BoundingFrustum frustum, Matrix world, Vector3 directionFromGridToCamera, bool enableFrustumCulling, out int visibleChunks, out int drawCalls)
    {
        byte visibleSurfaceMask = CalculateVisibleSurfaceMask(directionFromGridToCamera);
        DrawInternal(graphicsDevice, effect, frustum, world, enableFrustumCulling, visibleSurfaceMask, useSurfaceFilter: true, out visibleChunks, out drawCalls);
    }

    /// <summary>
    /// Backwards-named wrapper for packed 256³ full-grid drawing. It now uses the
    /// same filtering path as normal chunk rendering.
    /// </summary>
    public void DrawProceduralFullGridCameraFiltered(GraphicsDevice graphicsDevice, BasicEffect effect, BoundingFrustum frustum, Matrix world, Vector3 directionFromGridToCamera, bool enableFrustumCulling, out int visibleChunks, out int drawCalls)
        => DrawCameraFiltered(graphicsDevice, effect, frustum, world, directionFromGridToCamera, enableFrustumCulling, out visibleChunks, out drawCalls);

    private void DrawInternal(GraphicsDevice graphicsDevice, BasicEffect effect, BoundingFrustum frustum, Matrix world, bool enableFrustumCulling, byte surfaceFilterMask, bool useSurfaceFilter, out int visibleChunks, out int drawCalls)
    {
        ApplyPendingBuild(graphicsDevice);

        visibleChunks = 0;
        drawCalls = 0;

        graphicsDevice.BlendState = BlendState.Opaque;
        graphicsDevice.DepthStencilState = DepthStencilState.Default;
        graphicsDevice.RasterizerState = RasterizerState.CullNone;
        graphicsDevice.SamplerStates[0] = SamplerState.LinearClamp;
        graphicsDevice.Textures[0] = null;
        graphicsDevice.Indices = null;

        effect.World = world;
        effect.VertexColorEnabled = true;
        effect.TextureEnabled = false;
        effect.LightingEnabled = false;
        effect.Alpha = 1f;
        effect.DiffuseColor = Vector3.One;

        _chunkStore.Draw(
            graphicsDevice,
            effect,
            frustum,
            enableFrustumCulling,
            surfaceFilterMask,
            useSurfaceFilter,
            out visibleChunks,
            out drawCalls);

        _stats = _stats with { VisibleChunks = visibleChunks, DrawCalls = drawCalls };
    }

    private static byte CalculateVisibleSurfaceMask(Vector3 directionFromGridToCamera)
    {
        if (directionFromGridToCamera.LengthSquared() > 0.000001f)
            directionFromGridToCamera.Normalize();
        else
            return SurfaceAll;

        const float axisEpsilon = 0.035f;
        byte mask = 0;

        if (directionFromGridToCamera.X > axisEpsilon) mask |= SurfacePosX;
        else if (directionFromGridToCamera.X < -axisEpsilon) mask |= SurfaceNegX;
        else mask |= SurfacePosX | SurfaceNegX;

        if (directionFromGridToCamera.Y > axisEpsilon) mask |= SurfacePosY;
        else if (directionFromGridToCamera.Y < -axisEpsilon) mask |= SurfaceNegY;
        else mask |= SurfacePosY | SurfaceNegY;

        if (directionFromGridToCamera.Z > axisEpsilon) mask |= SurfacePosZ;
        else if (directionFromGridToCamera.Z < -axisEpsilon) mask |= SurfaceNegZ;
        else mask |= SurfacePosZ | SurfaceNegZ;

        return mask == 0 ? SurfaceAll : mask;
    }

    private void ApplyPendingBuild(GraphicsDevice graphicsDevice)
    {
        int requestedGeneration;
        lock (_dirtyLock)
        {
            requestedGeneration = _requestedGeneration;
        }

        BuildResult? result = _buildScheduler.TakePendingResult(requestedGeneration, _uploadedGeneration);
        if (result is null)
            return;

        _uploadedGeneration = result.Generation;
        _lastBuildMilliseconds = result.BuildMilliseconds;
        _lastRebuiltChunkCount = result.RebuiltChunkKeys.Count;
        _lastBuildWasFullRebuild = result.FullRebuild;

        RenderChunkStoreStats storeStats = _chunkStore.ApplyBuildResult(graphicsDevice, result);
        RecalculateStats(result.BlockCount, storeStats);

        // The CPU build result contains transient vertex arrays that have now been
        // copied into GPU VertexBuffers. Do not keep them alive through renderer state.
        // GC policy is deliberately not controlled from the render/apply path.
        result.ClearTransientMeshData();
    }

    private void RecalculateStats(int blockCount, RenderChunkStoreStats storeStats)
    {
        _stats = new MeshStats(
            storeStats.LiveChunks,
            storeStats.LiveChunks,
            blockCount,
            storeStats.Vertices,
            storeStats.Vertices,
            storeStats.Triangles,
            storeStats.Quads,
            storeStats.LiveChunks,
            _lastDirtyChunkCount < 0 ? storeStats.LiveChunks : _lastDirtyChunkCount,
            _lastRebuiltChunkCount,
            _lastBuildWasFullRebuild,
            _lastBuildMilliseconds);
    }

}
