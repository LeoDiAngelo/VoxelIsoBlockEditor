using System.Buffers;
using System.Collections.ObjectModel;
using Microsoft.Xna.Framework;

namespace IsoBlockCharacterEditor;

/// <summary>
/// CPU-build source used by the renderer scheduler. Implementations are immutable
/// ownership objects: once queued, the worker owns the snapshot and Dispose() returns
/// any pooled arrays. This separates scene representation from GPU rendering.
/// </summary>
internal interface IVoxelSceneSource : IDisposable
{
    int GridSize { get; }
    int Generation { get; }
    long SceneId { get; }
    bool FullRebuild { get; }
    int TotalBlockCount { get; }
    int DirtyChunkCountForStats { get; }

    BuildResult Build(CancellationToken cancellationToken);
}

/// <summary>Scene-source strategy for normal sparse editor worlds.</summary>
internal sealed class SparseVoxelSceneSource : IVoxelSceneSource
{
    private readonly RenderStateSnapshot _snapshot;
    private bool _disposed;

    private SparseVoxelSceneSource(RenderStateSnapshot snapshot, int dirtyChunkCountForStats)
    {
        _snapshot = snapshot;
        DirtyChunkCountForStats = dirtyChunkCountForStats;
    }

    public int GridSize => _snapshot.GridSize;
    public int Generation => _snapshot.Generation;
    public long SceneId => _snapshot.SceneId;
    public bool FullRebuild => _snapshot.FullRebuild;
    public int TotalBlockCount => _snapshot.TotalBlockCount;
    public int DirtyChunkCountForStats { get; }

    public static SparseVoxelSceneSource FromWorld(
        VoxelWorld world,
        int gridSize,
        int generation,
        long sceneId,
        bool fullRebuild,
        int[] dirtyChunkKeys,
        int dirtyChunkCount,
        int dirtyChunkCountForStats,
        Vector3 buildFocus)
    {
        PooledBlockDataSnapshot blocks = world.CreatePooledRenderSnapshot(fullRebuild, dirtyChunkKeys, dirtyChunkCount, gridSize);
        var snapshot = new RenderStateSnapshot(gridSize, generation, sceneId, fullRebuild, world.BlockCount, buildFocus, blocks, dirtyChunkKeys, dirtyChunkCount, dirtyChunkKeys.Length > 0);
        return new SparseVoxelSceneSource(snapshot, dirtyChunkCountForStats);
    }

    public static SparseVoxelSceneSource FromPieces(
        ObservableCollection<BlockPiece> pieces,
        int gridSize,
        int generation,
        long sceneId,
        bool fullRebuild,
        int[] dirtyChunkKeys,
        int dirtyChunkCount,
        int dirtyChunkCountForStats,
        Vector3 buildFocus)
    {
        BlockData[] blockBuffer = pieces.Count == 0 ? Array.Empty<BlockData>() : ArrayPool<BlockData>.Shared.Rent(pieces.Count);
        for (int i = 0; i < pieces.Count; i++)
            blockBuffer[i] = BlockData.FromPiece(pieces[i]);

        var blocks = new PooledBlockDataSnapshot(blockBuffer, pieces.Count, pooled: blockBuffer.Length > 0);
        var snapshot = new RenderStateSnapshot(gridSize, generation, sceneId, fullRebuild, pieces.Count, buildFocus, blocks, dirtyChunkKeys, dirtyChunkCount, dirtyChunkKeys.Length > 0);
        return new SparseVoxelSceneSource(snapshot, dirtyChunkCountForStats);
    }

    public BuildResult Build(CancellationToken cancellationToken)
        => ChunkMeshBuilder.BuildSnapshot(_snapshot, cancellationToken);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _snapshot.Dispose();
    }
}

/// <summary>Scene-source strategy for the packed 256³ procedural full-grid stress scene.</summary>
internal sealed class PackedFullGridSceneSource : IVoxelSceneSource
{
    private readonly int[] _dirtyChunkKeys;
    private readonly int _dirtyChunkCount;
    private readonly int[] _deletedCellKeys;
    private readonly int _deletedCellCount;
    private readonly bool _ownsDirtyChunkKeys;
    private readonly bool _ownsDeletedCellKeys;
    private readonly Vector3 _buildFocus;
    private bool _disposed;

    public PackedFullGridSceneSource(
        int gridSize,
        int generation,
        long sceneId,
        bool fullRebuild,
        int[] dirtyChunkKeys,
        int dirtyChunkCount,
        int dirtyChunkCountForStats,
        int[] deletedCellKeys,
        int deletedCellCount,
        bool ownsDeletedCellKeys,
        Vector3 buildFocus)
    {
        GridSize = gridSize;
        Generation = generation;
        SceneId = sceneId;
        FullRebuild = fullRebuild;
        _dirtyChunkKeys = dirtyChunkKeys;
        _dirtyChunkCount = dirtyChunkCount;
        _ownsDirtyChunkKeys = dirtyChunkKeys.Length > 0;
        DirtyChunkCountForStats = dirtyChunkCountForStats;
        _deletedCellKeys = deletedCellKeys;
        _deletedCellCount = deletedCellCount;
        _ownsDeletedCellKeys = ownsDeletedCellKeys;
        _buildFocus = buildFocus;
        TotalBlockCount = gridSize * gridSize * gridSize - deletedCellCount;
    }

    public int GridSize { get; }
    public int Generation { get; }
    public long SceneId { get; }
    public bool FullRebuild { get; }
    public int TotalBlockCount { get; }
    public int DirtyChunkCountForStats { get; }

    public BuildResult Build(CancellationToken cancellationToken)
        => ProceduralFullGridBuilder.Build(GridSize, Generation, SceneId, FullRebuild, _dirtyChunkKeys, _dirtyChunkCount, _deletedCellKeys, _deletedCellCount, _buildFocus, cancellationToken);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsDirtyChunkKeys)
            ArrayPool<int>.Shared.Return(_dirtyChunkKeys, clearArray: false);
        if (_ownsDeletedCellKeys)
            ArrayPool<int>.Shared.Return(_deletedCellKeys, clearArray: false);
    }
}
