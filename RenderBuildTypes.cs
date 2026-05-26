using System.Buffers;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace IsoBlockCharacterEditor;

/// <summary>
/// Small immutable value object for chunk coordinates. Used in renderer/build queues
/// instead of passing loose integer triples around.
/// </summary>
internal readonly record struct ChunkCoord(int X, int Y, int Z)
{
    public int Key => CoordinateHelper.ChunkKey(X, Y, Z);

    public static ChunkCoord FromKey(int key)
    {
        CoordinateHelper.DecodeChunkKey(key, out int x, out int y, out int z);
        return new ChunkCoord(x, y, z);
    }
}

/// <summary>Small immutable value object for cell coordinates.</summary>
internal readonly record struct CellCoord(int X, int Y, int Z)
{
    public int Key => CoordinateHelper.CellKey(X, Y, Z);
}

/// <summary>
/// One prioritized build request for a chunk. Lower Priority is built first.
/// </summary>
internal readonly record struct ChunkBuildRequest(int Key, ChunkCoord Coord, float Priority);

/// <summary>
/// Pooled read-only block-data snapshot. The background mesh builder owns this
/// object and must Dispose it when the build has completed or been cancelled.
/// </summary>
internal sealed class PooledBlockDataSnapshot : IDisposable
{
    public readonly BlockData[] Items;
    public readonly int Count;
    private readonly bool _pooled;
    private bool _disposed;

    public PooledBlockDataSnapshot(BlockData[] items, int count, bool pooled)
    {
        Items = items;
        Count = count;
        _pooled = pooled;
    }

    public static PooledBlockDataSnapshot Empty { get; } = new(Array.Empty<BlockData>(), 0, pooled: false);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_pooled && Items.Length > 0)
            ArrayPool<BlockData>.Shared.Return(Items, clearArray: false);
    }
}

/// <summary>
/// Immutable renderer snapshot for a single background build. It contains the
/// block data copied from VoxelWorld/UI plus the dirty-chunk keys and camera
/// focus needed for priority ordering. It never reads live VoxelWorld on the
/// worker thread.
/// </summary>
internal sealed class RenderStateSnapshot : IDisposable
{
    public readonly int GridSize;
    public readonly int Generation;
    public readonly long SceneId;
    public readonly bool FullRebuild;
    public readonly int TotalBlockCount;
    public readonly Vector3 BuildFocus;
    public readonly PooledBlockDataSnapshot Blocks;
    public readonly int[] DirtyChunkKeys;
    public readonly int DirtyChunkCount;
    private readonly bool _ownsDirtyChunkKeys;
    private bool _disposed;

    public RenderStateSnapshot(
        int gridSize,
        int generation,
        long sceneId,
        bool fullRebuild,
        int totalBlockCount,
        Vector3 buildFocus,
        PooledBlockDataSnapshot blocks,
        int[] dirtyChunkKeys,
        int dirtyChunkCount,
        bool ownsDirtyChunkKeys)
    {
        GridSize = gridSize;
        Generation = generation;
        SceneId = sceneId;
        FullRebuild = fullRebuild;
        TotalBlockCount = totalBlockCount;
        BuildFocus = buildFocus;
        Blocks = blocks;
        DirtyChunkKeys = dirtyChunkKeys;
        DirtyChunkCount = dirtyChunkCount;
        _ownsDirtyChunkKeys = ownsDirtyChunkKeys;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Blocks.Dispose();
        if (_ownsDirtyChunkKeys && DirtyChunkKeys.Length > 0)
            ArrayPool<int>.Shared.Return(DirtyChunkKeys, clearArray: false);
    }
}

/// <summary>CPU mesh data produced by a background build and later uploaded into a GPU RenderChunk.</summary>
internal sealed class ChunkMeshData
{
    public readonly int Key;
    public readonly int Cx;
    public readonly int Cy;
    public readonly int Cz;
    public readonly BoundingBox Bounds;
    public readonly VertexPositionColor[] Vertices;
    public readonly int Quads;
    public readonly byte SurfaceMask;

    public ChunkMeshData(int key, int cx, int cy, int cz, BoundingBox bounds, VertexPositionColor[] vertices, int quads, byte surfaceMask = 0)
    {
        Key = key;
        Cx = cx;
        Cy = cy;
        Cz = cz;
        Bounds = bounds;
        Vertices = vertices;
        Quads = quads;
        SurfaceMask = surfaceMask;
    }
}

/// <summary>Completed CPU build result waiting to be applied on the render thread.</summary>
internal sealed class BuildResult
{
    public readonly int Generation;
    public readonly long SceneId;
    public readonly int BlockCount;
    public readonly List<ChunkMeshData> Chunks;
    public readonly List<int> RebuiltChunkKeys;
    public readonly bool FullRebuild;
    public double BuildMilliseconds;

    public BuildResult(int generation, long sceneId, int blockCount, List<ChunkMeshData> chunks, List<int> rebuiltChunkKeys, bool fullRebuild)
    {
        Generation = generation;
        SceneId = sceneId;
        BlockCount = blockCount;
        Chunks = chunks;
        RebuiltChunkKeys = rebuiltChunkKeys;
        FullRebuild = fullRebuild;
    }

    public void ClearTransientMeshData()
    {
        Chunks.Clear();
        RebuiltChunkKeys.Clear();
    }
}
