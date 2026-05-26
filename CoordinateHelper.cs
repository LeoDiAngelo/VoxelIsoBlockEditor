using System.Runtime.CompilerServices;

namespace IsoBlockCharacterEditor;

/// <summary>
/// Central coordinate/key math for the entire editor.
/// Every CellKey, ChunkKey and local chunk index must go through this class.
/// This prevents renderer/world/picking/save code from silently drifting into
/// incompatible key layouts.
/// </summary>
public static class CoordinateHelper
{
    public const int GridSize = 256;
    public const int ChunkSize = 32;
    public const int ChunkShift = 5;
    public const int ChunkMask = ChunkSize - 1;
    public const int ChunkLayerStride = ChunkSize * ChunkSize;
    public const int ChunkCellCount = ChunkSize * ChunkSize * ChunkSize;
    public const int ChunkOccupancyWords = ChunkCellCount / 64;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsInsideGrid(int x, int y, int z, int gridSize)
        => (uint)x < (uint)gridSize && (uint)y < (uint)gridSize && (uint)z < (uint)gridSize;

    /// <summary>
    /// Safe cell key creation for coordinates that may come from neighbour checks,
    /// paste/move offsets or external/manual input. Prevents the old -1 -> 255
    /// wraparound behaviour caused by masking.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryCellKey(int x, int y, int z, out int key)
    {
        if ((uint)x >= GridSize || (uint)y >= GridSize || (uint)z >= GridSize)
        {
            key = 0;
            return false;
        }

        key = CellKeyUnsafe(x, y, z);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryCellKey(int x, int y, int z, int gridSize, out int key)
    {
        if (!IsInsideGrid(x, y, z, gridSize))
        {
            key = 0;
            return false;
        }

        key = CellKeyUnsafe(x, y, z);
        return true;
    }

    /// <summary>
    /// Fast cell key path. Caller must guarantee 0 <= x/y/z < 256.
    /// Use TryCellKey for neighbour checks and user-controlled coordinates.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CellKeyUnsafe(int x, int y, int z)
        => (x << 16) | (y << 8) | z;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CellKey(int x, int y, int z)
        => CellKeyUnsafe(x, y, z);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CellX(int cellKey) => (cellKey >> 16) & 0xFF;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CellY(int cellKey) => (cellKey >> 8) & 0xFF;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CellZ(int cellKey) => cellKey & 0xFF;

    /// <summary>
    /// Safe chunk key creation for chunk coordinates that may come from decoded,
    /// external or future variable-size input. The key layout itself remains an
    /// 8-bit-per-axis coordinate pack, matching CellKey's x/y/z packing style.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryChunkKey(int cx, int cy, int cz, int gridSize, out int key)
    {
        int chunksPerAxis = ChunksPerAxis(gridSize);
        if ((uint)cx >= (uint)chunksPerAxis || (uint)cy >= (uint)chunksPerAxis || (uint)cz >= (uint)chunksPerAxis)
        {
            key = 0;
            return false;
        }

        key = ChunkKeyUnsafe(cx, cy, cz);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryChunkKey(int cx, int cy, int cz, out int key)
        => TryChunkKey(cx, cy, cz, GridSize, out key);

    /// <summary>
    /// Fast chunk key path. Caller must guarantee that each chunk coordinate fits
    /// in one byte and is inside the active chunk grid. Use TryChunkKey for data
    /// that may be outside the current grid.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ChunkKeyUnsafe(int cx, int cy, int cz)
        => (cx << 16) | (cy << 8) | cz;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ChunkKey(int cx, int cy, int cz)
        => ChunkKeyUnsafe(cx, cy, cz);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ChunkKeyFromCell(int x, int y, int z)
        => ChunkKeyUnsafe(x >> ChunkShift, y >> ChunkShift, z >> ChunkShift);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void DecodeChunkKey(int key, out int cx, out int cy, out int cz)
    {
        cx = (key >> 16) & 0xFF;
        cy = (key >> 8) & 0xFF;
        cz = key & 0xFF;
    }

    public static int EstimateChunkAndSixNeighbourCapacity(IEnumerable<int> chunkKeys, int gridSize)
    {
        int chunksPerAxis = ChunksPerAxis(gridSize);
        int maxChunks = chunksPerAxis * chunksPerAxis * chunksPerAxis;
        int capacity = 0;

        foreach (int key in chunkKeys)
        {
            DecodeChunkKey(key, out int cx, out int cy, out int cz);
            capacity += CountChunkAndSixNeighboursInside(cx, cy, cz, chunksPerAxis);
            if (capacity >= maxChunks)
                return maxChunks;
        }

        return Math.Clamp(capacity, 4, Math.Max(4, maxChunks));
    }

    public static int EstimateChunkAndSixNeighbourCapacity(int[] chunkKeys, int count, int gridSize)
    {
        int chunksPerAxis = ChunksPerAxis(gridSize);
        int maxChunks = chunksPerAxis * chunksPerAxis * chunksPerAxis;
        int capacity = 0;

        for (int i = 0; i < count; i++)
        {
            DecodeChunkKey(chunkKeys[i], out int cx, out int cy, out int cz);
            capacity += CountChunkAndSixNeighboursInside(cx, cy, cz, chunksPerAxis);
            if (capacity >= maxChunks)
                return maxChunks;
        }

        return Math.Clamp(capacity, 4, Math.Max(4, maxChunks));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CountChunkAndSixNeighboursInside(int cx, int cy, int cz, int chunksPerAxis)
    {
        if ((uint)cx >= (uint)chunksPerAxis || (uint)cy >= (uint)chunksPerAxis || (uint)cz >= (uint)chunksPerAxis)
            return 0;

        int count = 1;
        if (cx > 0) count++;
        if (cx + 1 < chunksPerAxis) count++;
        if (cy > 0) count++;
        if (cy + 1 < chunksPerAxis) count++;
        if (cz > 0) count++;
        if (cz + 1 < chunksPerAxis) count++;
        return count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int LocalIndex(int lx, int ly, int lz)
        => ly * ChunkLayerStride + lz * ChunkSize + lx;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int LocalIndexFromCell(int x, int y, int z)
        => LocalIndex(x & ChunkMask, y & ChunkMask, z & ChunkMask);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int LayerStart(int ly) => ly * ChunkLayerStride;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ChunksPerAxis(int gridSize)
        => (Math.Clamp(gridSize, 1, GridSize) + ChunkSize - 1) >> ChunkShift;
}
