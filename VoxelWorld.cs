using System.Buffers;
using System.Runtime.CompilerServices;

namespace IsoBlockCharacterEditor;

/// <summary>
/// AAA editor core model: sparse chunk dictionary with horizontally laid out packed uint arrays.
/// This is the authoritative occupancy/source data for normal editor scenes.
/// UI still keeps lightweight BlockPiece view objects for selection/list/save compatibility,
/// but renderer, placement and support checks read from this VoxelWorld.
/// </summary>
public sealed class VoxelWorld
{
    public const int ChunkSize = CoordinateHelper.ChunkSize;
    public const int ChunkShift = CoordinateHelper.ChunkShift;
    public const int ChunkMask = CoordinateHelper.ChunkMask;
    public const int LayerStride = CoordinateHelper.ChunkLayerStride;
    public const int CellCount = CoordinateHelper.ChunkCellCount;

    private readonly Dictionary<int, VoxelChunk> _chunks = new(512);
    private readonly Dictionary<int, BlockPiece> _pieceByCell = new(4096);
    private const int MaxPaletteColors = 65535;
    private readonly Dictionary<int, ushort> _paletteIndexByColor = new(256);
    private readonly int[] _paletteColors = new int[MaxPaletteColors + 1];
    private int _paletteCount;
    private int _size;
    private int _blockCount;

    public VoxelWorld(int size) => _size = Math.Clamp(size, 1, 256);

    public int Size => _size;
    public int BlockCount => _blockCount;
    public IReadOnlyDictionary<int, VoxelChunk> Chunks => _chunks;

    public void Resize(int size)
    {
        _size = Math.Clamp(size, 1, 256);
        Clear();
    }

    public void Clear()
    {
        _chunks.Clear();
        _pieceByCell.Clear();
        _paletteIndexByColor.Clear();
        Array.Clear(_paletteColors, 0, _paletteColors.Length);
        _paletteCount = 0;
        _blockCount = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsInside(int x, int y, int z)
        => CoordinateHelper.IsInsideGrid(x, y, z, _size);

    public bool IsOccupied(int x, int y, int z)
    {
        if (!IsInside(x, y, z)) return false;
        int cx = x >> ChunkShift, cy = y >> ChunkShift, cz = z >> ChunkShift;
        if (!_chunks.TryGetValue(CoordinateHelper.ChunkKey(cx, cy, cz), out var chunk)) return false;
        return chunk.Blocks[CoordinateHelper.LocalIndex(x & ChunkMask, y & ChunkMask, z & ChunkMask)] != 0;
    }

    public bool TryGetBlock(int x, int y, int z, out uint block)
    {
        block = 0;
        if (!IsInside(x, y, z)) return false;
        int cx = x >> ChunkShift, cy = y >> ChunkShift, cz = z >> ChunkShift;
        if (!_chunks.TryGetValue(CoordinateHelper.ChunkKey(cx, cy, cz), out var chunk)) return false;
        block = chunk.Blocks[CoordinateHelper.LocalIndex(x & ChunkMask, y & ChunkMask, z & ChunkMask)];
        return block != 0;
    }

    public BlockPiece? GetPieceAt(int x, int y, int z)
    {
        if (!IsInside(x, y, z)) return null;
        _pieceByCell.TryGetValue(CoordinateHelper.CellKey(x, y, z), out var piece);
        return piece;
    }

    public bool TryAddPiece(BlockPiece piece)
    {
        if (!IsInside(piece.X, piece.Y, piece.Z)) return false;
        int key = piece.CellKey;
        if (_pieceByCell.ContainsKey(key)) return false;
        SetPiece(piece, overwrite: false);
        return true;
    }

    public void SetPiece(BlockPiece piece, bool overwrite = true)
    {
        if (!IsInside(piece.X, piece.Y, piece.Z)) return;
        int cx = piece.X >> ChunkShift, cy = piece.Y >> ChunkShift, cz = piece.Z >> ChunkShift;
        int chunkKey = CoordinateHelper.ChunkKey(cx, cy, cz);
        if (!_chunks.TryGetValue(chunkKey, out var chunk))
        {
            chunk = new VoxelChunk(cx, cy, cz);
            _chunks.Add(chunkKey, chunk);
        }

        int local = CoordinateHelper.LocalIndex(piece.X & ChunkMask, piece.Y & ChunkMask, piece.Z & ChunkMask);
        uint old = chunk.Blocks[local];
        if (old == 0) { chunk.NonEmptyCount++; _blockCount++; }
        else if (!overwrite) return;

        uint packed = PackPiece(piece);
        chunk.Blocks[local] = packed;
        chunk.SetOccupied(local, packed != 0);
        _pieceByCell[piece.CellKey] = piece;
    }

    public void UpdatePiece(BlockPiece piece) => SetPiece(piece, overwrite: true);

    public void RemovePiece(BlockPiece piece)
    {
        if (!IsInside(piece.X, piece.Y, piece.Z)) return;
        int cx = piece.X >> ChunkShift, cy = piece.Y >> ChunkShift, cz = piece.Z >> ChunkShift;
        int chunkKey = CoordinateHelper.ChunkKey(cx, cy, cz);
        if (!_chunks.TryGetValue(chunkKey, out var chunk)) return;

        int local = CoordinateHelper.LocalIndex(piece.X & ChunkMask, piece.Y & ChunkMask, piece.Z & ChunkMask);
        if (chunk.Blocks[local] != 0)
        {
            chunk.Blocks[local] = 0;
            chunk.SetOccupied(local, false);
            chunk.NonEmptyCount--;
            _blockCount--;
        }
        _pieceByCell.Remove(piece.CellKey);
        if (chunk.NonEmptyCount <= 0)
            _chunks.Remove(chunkKey);
    }

    public BlockData[] CreateRenderSnapshot(bool fullRebuild, HashSet<int>? dirtyChunks, int gridSize)
    {
        if (fullRebuild || dirtyChunks is null || dirtyChunks.Count == 0)
            return SnapshotChunks(null);

        int chunksPerAxis = CoordinateHelper.ChunksPerAxis(gridSize);
        var filter = new HashSet<int>(CoordinateHelper.EstimateChunkAndSixNeighbourCapacity(dirtyChunks, gridSize));
        foreach (int key in dirtyChunks)
        {
            CoordinateHelper.DecodeChunkKey(key, out int cx, out int cy, out int cz);
            AddChunkAndSixNeighboursIfInside(filter, cx, cy, cz, chunksPerAxis);
        }
        return SnapshotChunks(filter);
    }



    internal PooledBlockDataSnapshot CreatePooledRenderSnapshot(bool fullRebuild, int[] dirtyChunkKeys, int dirtyChunkCount, int gridSize)
    {
        HashSet<int>? filter = null;
        if (!fullRebuild && dirtyChunkCount > 0)
        {
            int chunksPerAxis = CoordinateHelper.ChunksPerAxis(gridSize);
            filter = new HashSet<int>(CoordinateHelper.EstimateChunkAndSixNeighbourCapacity(dirtyChunkKeys, dirtyChunkCount, gridSize));
            for (int i = 0; i < dirtyChunkCount; i++)
            {
                CoordinateHelper.DecodeChunkKey(dirtyChunkKeys[i], out int cx, out int cy, out int cz);
                AddChunkAndSixNeighboursIfInside(filter, cx, cy, cz, chunksPerAxis);
            }
        }

        int count = CountSnapshotBlocks(filter);
        if (count <= 0)
            return PooledBlockDataSnapshot.Empty;

        BlockData[] items = ArrayPool<BlockData>.Shared.Rent(count);
        int written = FillSnapshotBlocks(filter, items);
        return new PooledBlockDataSnapshot(items, written, pooled: true);
    }

    private int CountSnapshotBlocks(HashSet<int>? chunkFilter)
    {
        int count = 0;
        foreach (var kv in _chunks)
        {
            if (chunkFilter is not null && !chunkFilter.Contains(kv.Key)) continue;
            VoxelChunk chunk = kv.Value;
            if (chunk.NonEmptyCount <= 0) continue;
            int baseX = chunk.Cx << ChunkShift;
            int baseY = chunk.Cy << ChunkShift;
            int baseZ = chunk.Cz << ChunkShift;
            uint[] blocks = chunk.Blocks;
            for (int ly = 0; ly < ChunkSize; ly++)
            {
                int y = baseY + ly;
                if ((uint)y >= (uint)_size) break;
                int layerStart = ly * LayerStride;
                for (int lz = 0; lz < ChunkSize; lz++)
                {
                    int z = baseZ + lz;
                    if ((uint)z >= (uint)_size) break;
                    int row = layerStart + lz * ChunkSize;
                    for (int lx = 0; lx < ChunkSize; lx++)
                    {
                        int x = baseX + lx;
                        if ((uint)x >= (uint)_size) break;
                        if (blocks[row + lx] != 0)
                            count++;
                    }
                }
            }
        }
        return count;
    }

    private int FillSnapshotBlocks(HashSet<int>? chunkFilter, BlockData[] target)
    {
        int index = 0;
        foreach (var kv in _chunks)
        {
            if (chunkFilter is not null && !chunkFilter.Contains(kv.Key)) continue;
            VoxelChunk chunk = kv.Value;
            if (chunk.NonEmptyCount <= 0) continue;
            int baseX = chunk.Cx << ChunkShift;
            int baseY = chunk.Cy << ChunkShift;
            int baseZ = chunk.Cz << ChunkShift;
            uint[] blocks = chunk.Blocks;
            for (int ly = 0; ly < ChunkSize; ly++)
            {
                int y = baseY + ly;
                if ((uint)y >= (uint)_size) break;
                int layerStart = ly * LayerStride;
                for (int lz = 0; lz < ChunkSize; lz++)
                {
                    int z = baseZ + lz;
                    if ((uint)z >= (uint)_size) break;
                    int row = layerStart + lz * ChunkSize;
                    for (int lx = 0; lx < ChunkSize; lx++)
                    {
                        int x = baseX + lx;
                        if ((uint)x >= (uint)_size) break;
                        uint packed = blocks[row + lx];
                        if (packed == 0) continue;
                        target[index++] = UnpackBlockData(x, y, z, packed);
                    }
                }
            }
        }
        return index;
    }

    private BlockData[] SnapshotChunks(HashSet<int>? chunkFilter)
    {
        int estimate = chunkFilter is null ? _blockCount : Math.Min(_blockCount, chunkFilter.Count * CellCount);
        var result = new List<BlockData>(Math.Max(estimate, 16));
        foreach (var kv in _chunks)
        {
            if (chunkFilter is not null && !chunkFilter.Contains(kv.Key)) continue;
            VoxelChunk chunk = kv.Value;
            if (chunk.NonEmptyCount <= 0) continue;
            int baseX = chunk.Cx << ChunkShift;
            int baseY = chunk.Cy << ChunkShift;
            int baseZ = chunk.Cz << ChunkShift;
            uint[] blocks = chunk.Blocks;
            for (int ly = 0; ly < ChunkSize; ly++)
            {
                int y = baseY + ly;
                if ((uint)y >= (uint)_size) break;
                int layerStart = ly * LayerStride;
                for (int lz = 0; lz < ChunkSize; lz++)
                {
                    int z = baseZ + lz;
                    if ((uint)z >= (uint)_size) break;
                    int row = layerStart + lz * ChunkSize;
                    for (int lx = 0; lx < ChunkSize; lx++)
                    {
                        int x = baseX + lx;
                        if ((uint)x >= (uint)_size) break;
                        uint packed = blocks[row + lx];
                        if (packed == 0) continue;
                        result.Add(UnpackBlockData(x, y, z, packed));
                    }
                }
            }
        }
        return result.ToArray();
    }

    private uint PackPiece(BlockPiece p)
    {
        ushort colorIndex = EnsureColorIndex(p.PackedColor);
        int rotationQuarter = (((p.RotationY % 360) + 360) % 360) / 90;
        uint packed = colorIndex;
        packed |= (uint)((int)p.Shape & 0x0F) << 16;
        packed |= (uint)(rotationQuarter & 0x03) << 20;
        if (p.FlipHorizontal) packed |= 1u << 22;
        if (p.FlipVertical) packed |= 1u << 23;
        return packed; // color index starts at 1, so zero remains reserved for empty cells.
    }

    private ushort EnsureColorIndex(int packedColor)
    {
        if (_paletteIndexByColor.TryGetValue(packedColor, out ushort existing))
            return existing == 0 ? (ushort)1 : existing;

        if (_paletteCount >= MaxPaletteColors)
            throw new InvalidOperationException($"The voxel palette is full ({MaxPaletteColors} unique colors). Existing colors remain stable; no palette slot is overwritten.");

        _paletteCount++;
        ushort index = (ushort)_paletteCount;
        _paletteIndexByColor[packedColor] = index;
        _paletteColors[index] = packedColor;
        return index;
    }

    private BlockData UnpackBlockData(int x, int y, int z, uint packed)
    {
        int colorIndex = (int)(packed & 0xFFFF);
        int packedColor = colorIndex > 0 && colorIndex < _paletteColors.Length ? _paletteColors[colorIndex] : 0;
        if (packedColor == 0) packedColor = ColorUtil.PackColor("#4B88D8");
        var shape = (BlockShape)((packed >> 16) & 0x0F);
        int rotationY = (int)((packed >> 20) & 0x03) * 90;
        bool flipH = (packed & (1u << 22)) != 0;
        bool flipV = (packed & (1u << 23)) != 0;
        return new BlockData(x, y, z, shape, packedColor, rotationY, flipH, flipV);
    }

    private static void AddChunkAndSixNeighboursIfInside(HashSet<int> set, int cx, int cy, int cz, int chunksPerAxis)
    {
        AddIfInside(set, cx, cy, cz, chunksPerAxis);
        AddIfInside(set, cx - 1, cy, cz, chunksPerAxis);
        AddIfInside(set, cx + 1, cy, cz, chunksPerAxis);
        AddIfInside(set, cx, cy - 1, cz, chunksPerAxis);
        AddIfInside(set, cx, cy + 1, cz, chunksPerAxis);
        AddIfInside(set, cx, cy, cz - 1, chunksPerAxis);
        AddIfInside(set, cx, cy, cz + 1, chunksPerAxis);
    }

    private static void AddIfInside(HashSet<int> set, int cx, int cy, int cz, int chunksPerAxis)
    {
        if ((uint)cx >= (uint)chunksPerAxis ||
            (uint)cy >= (uint)chunksPerAxis ||
            (uint)cz >= (uint)chunksPerAxis)
        {
            return;
        }

        set.Add(CoordinateHelper.ChunkKeyUnsafe(cx, cy, cz));
    }
}

public sealed class VoxelChunk
{
    public readonly int Cx;
    public readonly int Cy;
    public readonly int Cz;
    public readonly uint[] Blocks = new uint[VoxelWorld.CellCount];
    public readonly ulong[] OccupancyBits = new ulong[VoxelWorld.CellCount / 64];
    public int NonEmptyCount;

    public VoxelChunk(int cx, int cy, int cz)
    {
        Cx = cx;
        Cy = cy;
        Cz = cz;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetOccupied(int localIndex, bool occupied)
    {
        int word = localIndex >> 6;
        int bit = localIndex & 63;
        ulong mask = 1UL << bit;
        if (occupied) OccupancyBits[word] |= mask;
        else OccupancyBits[word] &= ~mask;
    }
}
