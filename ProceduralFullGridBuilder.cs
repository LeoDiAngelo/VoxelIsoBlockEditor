using Microsoft.Xna.Framework;

namespace IsoBlockCharacterEditor;

/// <summary>
/// Named CPU builder for the packed 256³ procedural full-grid strategy.
/// The implementation delegates to the shared greedy/procedural mesh primitives,
/// but callers no longer need packed-mode build details inside ChunkedVoxelRenderer.
/// </summary>
internal static class ProceduralFullGridBuilder
{
    public static BuildResult Build(
        int gridSize,
        int generation,
        long sceneId,
        bool fullRebuild,
        int[] dirtyChunkKeys,
        int dirtyChunkCount,
        int[] deletedCellKeys,
        int deletedCellCount,
        Vector3 buildFocus,
        CancellationToken cancellationToken)
    {
        return ChunkMeshBuilder.BuildProceduralFullGrid(
            gridSize,
            generation,
            sceneId,
            fullRebuild,
            dirtyChunkKeys,
            dirtyChunkCount,
            deletedCellKeys,
            deletedCellCount,
            buildFocus,
            cancellationToken);
    }
}
