using System.Buffers;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using XnaColor = Microsoft.Xna.Framework.Color;
using static IsoBlockCharacterEditor.RenderSurfaceFlags;

namespace IsoBlockCharacterEditor;

/// <summary>
/// CPU-only mesh build implementation for sparse editor scenes and packed procedural scenes.
/// It contains no GraphicsDevice usage and can be moved behind tests without touching WPF/MonoGame rendering.
/// </summary>
internal static class ChunkMeshBuilder
{
    private const int ChunkSize = CoordinateHelper.ChunkSize;
    private const int ChunkShift = CoordinateHelper.ChunkShift;
    private const int ChunkMask = CoordinateHelper.ChunkMask;
    private const int ChunkVoxelCount = CoordinateHelper.ChunkCellCount;

    internal static BuildResult BuildSnapshot(RenderStateSnapshot snapshot, CancellationToken cancellationToken)
    {
        BlockData[] pieces = snapshot.Blocks.Items;
        int pieceCount = snapshot.Blocks.Count;
        int gridSize = snapshot.GridSize;
        bool fullRebuild = snapshot.FullRebuild;

        HashSet<int>? dirtyChunks = null;
        if (!fullRebuild)
        {
            dirtyChunks = new HashSet<int>(Math.Max(4, snapshot.DirtyChunkCount));
            for (int i = 0; i < snapshot.DirtyChunkCount; i++)
                dirtyChunks.Add(snapshot.DirtyChunkKeys[i]);
        }

        HashSet<int>? occupancyChunkFilter = null;
        if (!fullRebuild && dirtyChunks is not null)
        {
            int chunksPerAxis = CoordinateHelper.ChunksPerAxis(gridSize);
            occupancyChunkFilter = new HashSet<int>(CoordinateHelper.EstimateChunkAndSixNeighbourCapacity(dirtyChunks, gridSize));
            foreach (int key in dirtyChunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CoordinateHelper.DecodeChunkKey(key, out int dcx, out int dcy, out int dcz);
                AddChunkAndSixNeighboursIfInside(occupancyChunkFilter, dcx, dcy, dcz, chunksPerAxis);
            }
        }

        int expectedOccupancy = fullRebuild || occupancyChunkFilter is null ? pieceCount * 2 : Math.Min(pieceCount, Math.Max(64, occupancyChunkFilter.Count * ChunkVoxelCount));
        int expectedInputs = fullRebuild || dirtyChunks is null ? Math.Max(64, pieceCount / 256) : Math.Max(4, dirtyChunks.Count);
        var occupancy = new Dictionary<int, ulong>(expectedOccupancy);
        var chunkInputs = new Dictionary<int, ChunkInput>(expectedInputs);

        for (int i = 0; i < pieceCount; i++)
        {
            if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            BlockData p = pieces[i];
            if ((uint)p.X >= (uint)gridSize || (uint)p.Y >= (uint)gridSize || (uint)p.Z >= (uint)gridSize)
                continue;

            int cx = p.X >> ChunkShift;
            int cy = p.Y >> ChunkShift;
            int cz = p.Z >> ChunkShift;
            int key = CoordinateHelper.ChunkKey(cx, cy, cz);

            bool isDirtyChunk = fullRebuild || dirtyChunks is null || dirtyChunks.Contains(key);
            bool isOccupancyChunk = fullRebuild || occupancyChunkFilter is null || occupancyChunkFilter.Contains(key);
            if (!isDirtyChunk && !isOccupancyChunk)
                continue;

            ulong packedBlock = PackBlock(p);
            if (isOccupancyChunk)
                occupancy[CoordinateHelper.CellKey(p.X, p.Y, p.Z)] = packedBlock;

            if (!isDirtyChunk)
                continue;

            if (!chunkInputs.TryGetValue(key, out ChunkInput? chunk))
            {
                chunk = new ChunkInput(key, cx, cy, cz);
                chunkInputs.Add(key, chunk);
            }

            chunk.SetCell(p.X & ChunkMask, p.Y & ChunkMask, p.Z & ChunkMask, packedBlock);
            if (!IsGreedyCube(p))
                chunk.SpecialPieces.Add(p);
        }

        var requests = new List<ChunkBuildRequest>(fullRebuild || dirtyChunks is null ? chunkInputs.Count : dirtyChunks.Count);
        if (fullRebuild || dirtyChunks is null)
        {
            foreach (var entry in chunkInputs)
            {
                ChunkInput input = entry.Value;
                if (input.NonEmptyCount == 0)
                    continue;
                requests.Add(CreateBuildRequest(input.Key, input.Cx, input.Cy, input.Cz, snapshot.BuildFocus));
            }
        }
        else
        {
            for (int i = 0; i < snapshot.DirtyChunkCount; i++)
            {
                int key = snapshot.DirtyChunkKeys[i];
                CoordinateHelper.DecodeChunkKey(key, out int cx, out int cy, out int cz);
                requests.Add(CreateBuildRequest(key, cx, cy, cz, snapshot.BuildFocus));
            }
        }
        SortBuildRequests(requests);

        var chunkMeshes = new List<ChunkMeshData>(requests.Count);
        var rebuiltKeys = new List<int>(requests.Count);

        for (int i = 0; i < requests.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ChunkBuildRequest request = requests[i];
            rebuiltKeys.Add(request.Key);
            if (!chunkInputs.TryGetValue(request.Key, out ChunkInput? input) || input.NonEmptyCount == 0)
                continue;

            ChunkMeshData mesh = BuildChunkCpu(input, occupancy, gridSize);
            if (mesh.Vertices.Length > 0)
                chunkMeshes.Add(mesh);
        }

        return new BuildResult(snapshot.Generation, snapshot.SceneId, snapshot.TotalBlockCount, chunkMeshes, rebuiltKeys, fullRebuild);
    }

    private static ChunkBuildRequest CreateBuildRequest(int key, int cx, int cy, int cz, Vector3 buildFocus)
    {
        var coord = new ChunkCoord(cx, cy, cz);
        Vector3 center = new(cx * ChunkSize + ChunkSize * 0.5f, cy * ChunkSize + ChunkSize * 0.5f, cz * ChunkSize + ChunkSize * 0.5f);
        float priority = Vector3.DistanceSquared(center, buildFocus);
        return new ChunkBuildRequest(key, coord, priority);
    }

    private static void SortBuildRequests(List<ChunkBuildRequest> requests)
    {
        requests.Sort(static (a, b) => a.Priority.CompareTo(b.Priority));
    }

    internal static BuildResult BuildProceduralFullGrid(int gridSize, int generation, long sceneId, bool fullRebuild, int[] dirtyKeys, int dirtyCount, int[] deletedKeys, int deletedCount, Vector3 buildFocus, CancellationToken cancellationToken)
    {
        int chunksPerAxis = CoordinateHelper.ChunksPerAxis(gridSize);
        int blockCount = gridSize * gridSize * gridSize - deletedCount;
        HashSet<int>? deleted = null;
        if (deletedCount > 0)
        {
            deleted = new HashSet<int>(deletedCount);
            for (int i = 0; i < deletedCount; i++)
            {
                if ((i & 4095) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                deleted.Add(deletedKeys[i]);
            }
        }

        int maxChunks = chunksPerAxis * chunksPerAxis * chunksPerAxis;
        int requestCapacity = fullRebuild ? maxChunks : Math.Max(1, dirtyCount);
        var requests = new List<ChunkBuildRequest>(requestCapacity);
        if (fullRebuild)
        {
            for (int cy = 0; cy < chunksPerAxis; cy++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int cz = 0; cz < chunksPerAxis; cz++)
                {
                    for (int cx = 0; cx < chunksPerAxis; cx++)
                    {
                        int key = CoordinateHelper.ChunkKey(cx, cy, cz);
                        requests.Add(CreateBuildRequest(key, cx, cy, cz, buildFocus));
                    }
                }
            }
        }
        else
        {
            for (int i = 0; i < dirtyCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int key = dirtyKeys[i];
                CoordinateHelper.DecodeChunkKey(key, out int cx, out int cy, out int cz);
                if (cx < 0 || cy < 0 || cz < 0 || cx >= chunksPerAxis || cy >= chunksPerAxis || cz >= chunksPerAxis)
                    continue;
                requests.Add(CreateBuildRequest(key, cx, cy, cz, buildFocus));
            }
        }
        SortBuildRequests(requests);

        int meshCapacity = fullRebuild
            ? Math.Max(1, maxChunks - Math.Max(0, chunksPerAxis - 2) * Math.Max(0, chunksPerAxis - 2) * Math.Max(0, chunksPerAxis - 2) + deletedCount)
            : Math.Max(1, requests.Count);
        var meshes = new List<ChunkMeshData>(meshCapacity);
        var rebuilt = new List<int>(requests.Count);

        for (int i = 0; i < requests.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ChunkBuildRequest request = requests[i];
            rebuilt.Add(request.Key);
            ChunkMeshData mesh = BuildProceduralFullGridSurfaceChunk(request.Coord.X, request.Coord.Y, request.Coord.Z, gridSize, chunksPerAxis, deleted, deletedKeys, deletedCount, cancellationToken);
            if (mesh.Vertices.Length > 0)
                meshes.Add(mesh);
        }

        return new BuildResult(generation, sceneId, blockCount, meshes, rebuilt, fullRebuild);
    }

    private static ChunkMeshData BuildProceduralFullGridSurfaceChunk(int cx, int cy, int cz, int gridSize, int chunksPerAxis, HashSet<int>? deleted, int[] deletedKeys, int deletedCount, CancellationToken cancellationToken)
    {
        using var vertices = new PooledVertexBuffer(32 * 1024);
        int quads = 0;
        byte surfaceMask = 0;
        int[] mask = ArrayPool<int>.Shared.Rent(ChunkSize * ChunkSize);

        try
        {
            int chunkX0 = cx * ChunkSize;
            int chunkY0 = cy * ChunkSize;
            int chunkZ0 = cz * ChunkSize;
            int sx = Math.Min(ChunkSize, gridSize - chunkX0);
            int sy = Math.Min(ChunkSize, gridSize - chunkY0);
            int sz = Math.Min(ChunkSize, gridSize - chunkZ0);

            if (cz == chunksPerAxis - 1)
            {
                surfaceMask |= SurfacePosZ;
                Array.Clear(mask, 0, mask.Length);
                int wz = gridSize - 1;
                for (int ly = 0; ly < sy; ly++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    for (int lx = 0; lx < sx; lx++)
                        if (!IsDeleted(deleted, chunkX0 + lx, chunkY0 + ly, wz))
                            mask[lx + ly * ChunkSize] = ProceduralColor(chunkX0 + lx, chunkY0 + ly, wz);
                }
                quads += EmitMask(mask, vertices, (x, y, w, h, packed) =>
                {
                    XnaColor c = UnpackColor(packed);
                    float x0 = chunkX0 + x, x1 = x0 + w;
                    float y0 = chunkY0 + y, y1 = y0 + h;
                    float z = gridSize;
                    AddQuad(vertices, new Vector3(x0, y0, z), new Vector3(x1, y0, z), new Vector3(x1, y1, z), new Vector3(x0, y1, z), c);
                });
            }

            if (cz == 0)
            {
                surfaceMask |= SurfaceNegZ;
                Array.Clear(mask, 0, mask.Length);
                int wz = 0;
                for (int ly = 0; ly < sy; ly++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    for (int lx = 0; lx < sx; lx++)
                        if (!IsDeleted(deleted, chunkX0 + lx, chunkY0 + ly, wz))
                            mask[lx + ly * ChunkSize] = ProceduralColor(chunkX0 + lx, chunkY0 + ly, wz);
                }
                quads += EmitMask(mask, vertices, (x, y, w, h, packed) =>
                {
                    XnaColor c = UnpackColor(packed);
                    float x0 = chunkX0 + x, x1 = x0 + w;
                    float y0 = chunkY0 + y, y1 = y0 + h;
                    float z = 0;
                    AddQuad(vertices, new Vector3(x1, y0, z), new Vector3(x0, y0, z), new Vector3(x0, y1, z), new Vector3(x1, y1, z), c);
                });
            }

            if (cx == chunksPerAxis - 1)
            {
                surfaceMask |= SurfacePosX;
                Array.Clear(mask, 0, mask.Length);
                int wx = gridSize - 1;
                for (int ly = 0; ly < sy; ly++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    for (int lz = 0; lz < sz; lz++)
                        if (!IsDeleted(deleted, wx, chunkY0 + ly, chunkZ0 + lz))
                            mask[lz + ly * ChunkSize] = ProceduralColor(wx, chunkY0 + ly, chunkZ0 + lz);
                }
                quads += EmitMask(mask, vertices, (zLocal, y, w, h, packed) =>
                {
                    XnaColor c = UnpackColor(packed);
                    float x = gridSize;
                    float z0 = chunkZ0 + zLocal, z1 = z0 + w;
                    float y0 = chunkY0 + y, y1 = y0 + h;
                    AddQuad(vertices, new Vector3(x, y0, z1), new Vector3(x, y0, z0), new Vector3(x, y1, z0), new Vector3(x, y1, z1), c);
                });
            }

            if (cx == 0)
            {
                surfaceMask |= SurfaceNegX;
                Array.Clear(mask, 0, mask.Length);
                int wx = 0;
                for (int ly = 0; ly < sy; ly++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    for (int lz = 0; lz < sz; lz++)
                        if (!IsDeleted(deleted, wx, chunkY0 + ly, chunkZ0 + lz))
                            mask[lz + ly * ChunkSize] = ProceduralColor(wx, chunkY0 + ly, chunkZ0 + lz);
                }
                quads += EmitMask(mask, vertices, (zLocal, y, w, h, packed) =>
                {
                    XnaColor c = UnpackColor(packed);
                    float x = 0;
                    float z0 = chunkZ0 + zLocal, z1 = z0 + w;
                    float y0 = chunkY0 + y, y1 = y0 + h;
                    AddQuad(vertices, new Vector3(x, y0, z0), new Vector3(x, y0, z1), new Vector3(x, y1, z1), new Vector3(x, y1, z0), c);
                });
            }

            if (cy == chunksPerAxis - 1)
            {
                surfaceMask |= SurfacePosY;
                Array.Clear(mask, 0, mask.Length);
                int wy = gridSize - 1;
                for (int lz = 0; lz < sz; lz++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    for (int lx = 0; lx < sx; lx++)
                        if (!IsDeleted(deleted, chunkX0 + lx, wy, chunkZ0 + lz))
                            mask[lx + lz * ChunkSize] = ProceduralColor(chunkX0 + lx, wy, chunkZ0 + lz);
                }
                quads += EmitMask(mask, vertices, (x, zLocal, w, h, packed) =>
                {
                    XnaColor c = UnpackColor(packed);
                    float x0 = chunkX0 + x, x1 = x0 + w;
                    float z0 = chunkZ0 + zLocal, z1 = z0 + h;
                    float y = gridSize;
                    AddQuad(vertices, new Vector3(x0, y, z0), new Vector3(x0, y, z1), new Vector3(x1, y, z1), new Vector3(x1, y, z0), c);
                });
            }

            if (cy == 0)
            {
                surfaceMask |= SurfaceNegY;
                Array.Clear(mask, 0, mask.Length);
                int wy = 0;
                for (int lz = 0; lz < sz; lz++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    for (int lx = 0; lx < sx; lx++)
                        if (!IsDeleted(deleted, chunkX0 + lx, wy, chunkZ0 + lz))
                            mask[lx + lz * ChunkSize] = ProceduralColor(chunkX0 + lx, wy, chunkZ0 + lz);
                }
                quads += EmitMask(mask, vertices, (x, zLocal, w, h, packed) =>
                {
                    XnaColor c = UnpackColor(packed);
                    float x0 = chunkX0 + x, x1 = x0 + w;
                    float z0 = chunkZ0 + zLocal, z1 = z0 + h;
                    float y = 0;
                    AddQuad(vertices, new Vector3(x0, y, z0), new Vector3(x1, y, z0), new Vector3(x1, y, z1), new Vector3(x0, y, z1), c);
                });
            }

            if (deleted is not null && deletedCount > 0)
            {
                int cavityQuads = AddDeletedCellCavityFaces(vertices, cx, cy, cz, gridSize, deleted, deletedKeys, deletedCount, cancellationToken);
                if (cavityQuads > 0)
                    surfaceMask = SurfaceAll; // cavity faces may be visible through a hole from many angles
                quads += cavityQuads;
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(mask, clearArray: true);
        }

        VertexPositionColor[] vertexArray = vertices.ToArray();
        BoundingBox bounds = CreateTightMeshBounds(vertexArray, cx, cy, cz, gridSize);
        return new ChunkMeshData(CoordinateHelper.ChunkKey(cx, cy, cz), cx, cy, cz, bounds, vertexArray, quads, surfaceMask);
    }

    private static bool IsDeleted(HashSet<int>? deleted, int x, int y, int z)
        => deleted is not null && deleted.Contains(CoordinateHelper.CellKey(x, y, z));

    private static bool IsSolidProceduralCell(int gridSize, HashSet<int> deleted, int x, int y, int z)
        => (uint)x < (uint)gridSize && (uint)y < (uint)gridSize && (uint)z < (uint)gridSize && !deleted.Contains(CoordinateHelper.CellKey(x, y, z));

    private static int AddDeletedCellCavityFaces(PooledVertexBuffer vertices, int cx, int cy, int cz, int gridSize, HashSet<int> deleted, int[] deletedKeys, int deletedCount, CancellationToken cancellationToken)
    {
        int quads = 0;
        int x0 = cx * ChunkSize, y0 = cy * ChunkSize, z0 = cz * ChunkSize;
        int x1 = Math.Min(x0 + ChunkSize, gridSize);
        int y1 = Math.Min(y0 + ChunkSize, gridSize);
        int z1 = Math.Min(z0 + ChunkSize, gridSize);

        for (int i = 0; i < deletedCount; i++)
        {
            if ((i & 4095) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            int key = deletedKeys[i];
            int dx = (key >> 16) & 0xFF;
            int dy = (key >> 8) & 0xFF;
            int dz = key & 0xFF;

            // For each deleted cell, emit the newly exposed face of each solid neighbour.
            quads += AddNeighbourFaceIfInChunk(vertices, gridSize, deleted, dx - 1, dy, dz, x0, y0, z0, x1, y1, z1, face: 0);
            quads += AddNeighbourFaceIfInChunk(vertices, gridSize, deleted, dx + 1, dy, dz, x0, y0, z0, x1, y1, z1, face: 1);
            quads += AddNeighbourFaceIfInChunk(vertices, gridSize, deleted, dx, dy - 1, dz, x0, y0, z0, x1, y1, z1, face: 2);
            quads += AddNeighbourFaceIfInChunk(vertices, gridSize, deleted, dx, dy + 1, dz, x0, y0, z0, x1, y1, z1, face: 3);
            quads += AddNeighbourFaceIfInChunk(vertices, gridSize, deleted, dx, dy, dz - 1, x0, y0, z0, x1, y1, z1, face: 4);
            quads += AddNeighbourFaceIfInChunk(vertices, gridSize, deleted, dx, dy, dz + 1, x0, y0, z0, x1, y1, z1, face: 5);
        }

        return quads;
    }

    private static int AddNeighbourFaceIfInChunk(PooledVertexBuffer vertices, int gridSize, HashSet<int> deleted, int x, int y, int z, int chunkX0, int chunkY0, int chunkZ0, int chunkX1, int chunkY1, int chunkZ1, int face)
    {
        if (x < chunkX0 || x >= chunkX1 || y < chunkY0 || y >= chunkY1 || z < chunkZ0 || z >= chunkZ1)
            return 0;
        if (!IsSolidProceduralCell(gridSize, deleted, x, y, z))
            return 0;

        XnaColor c = UnpackColor(ProceduralColor(x, y, z));
        float x0 = x, x1 = x + 1f, y0 = y, y1 = y + 1f, z0 = z, z1 = z + 1f;
        switch (face)
        {
            case 0: AddQuad(vertices, new Vector3(x1, y0, z0), new Vector3(x1, y0, z1), new Vector3(x1, y1, z1), new Vector3(x1, y1, z0), c); break; // neighbour left, exposed +X
            case 1: AddQuad(vertices, new Vector3(x0, y0, z1), new Vector3(x0, y0, z0), new Vector3(x0, y1, z0), new Vector3(x0, y1, z1), c); break; // neighbour right, exposed -X
            case 2: AddQuad(vertices, new Vector3(x0, y1, z0), new Vector3(x1, y1, z0), new Vector3(x1, y1, z1), new Vector3(x0, y1, z1), c); break; // below, exposed +Y
            case 3: AddQuad(vertices, new Vector3(x0, y0, z1), new Vector3(x1, y0, z1), new Vector3(x1, y0, z0), new Vector3(x0, y0, z0), c); break; // above, exposed -Y
            case 4: AddQuad(vertices, new Vector3(x0, y0, z1), new Vector3(x1, y0, z1), new Vector3(x1, y1, z1), new Vector3(x0, y1, z1), c); break; // back, exposed +Z
            case 5: AddQuad(vertices, new Vector3(x1, y0, z0), new Vector3(x0, y0, z0), new Vector3(x0, y1, z0), new Vector3(x1, y1, z0), c); break; // front, exposed -Z
        }
        return 1;
    }

    private static int ProceduralColor(int x, int y, int z)
    {
        return ((x + y + z) & 3) switch
        {
            0 => unchecked((int)0xFF4B88D8),
            1 => unchecked((int)0xFF60B35F),
            2 => unchecked((int)0xFFD8B18C),
            _ => unchecked((int)0xFF2D3542)
        };
    }

    private static ChunkMeshData BuildChunkCpu(ChunkInput chunk, Dictionary<int, ulong> occupancy, int gridSize)
    {
        // v94: chunk data is already stored as a horizontal Y-layer array:
        // index = localY * 32 * 32 + localZ * 32 + localX.
        // That removes the old nullable BlockData?[] fullCells allocation per rebuild
        // and keeps layer/slice access cache-friendly for 256³ editing.
        // Important: do NOT size this from NonEmptyCount * 18.
        // A dense 32^3 chunk can contain 32768 cells, but greedy meshing usually emits
        // only surface vertices. Oversizing here rents massive arrays from ArrayPool;
        // ArrayPool may keep them, which makes Task Manager show multi-GB memory after
        // repeated stress tests. Start modestly and grow only when the mesh really needs it.
        using var vertices = new PooledVertexBuffer(EstimateInitialVertexCapacity(chunk));
        int quads = 0;
        byte surfaceMask = 0;

        // Full cube faces are greedy merged. This removes hidden faces and merges
        // large visible planes with same color into one quad. Track which outward
        // face directions were emitted so normal editor/stress scenes can use the
        // same camera-facing draw filter as the 256³ procedural full-grid path.
        int axisQuads;

        axisQuads = GreedyAxisZ(chunk, occupancy, gridSize, vertices, positive: true);
        if (axisQuads > 0) surfaceMask |= SurfacePosZ;
        quads += axisQuads;

        axisQuads = GreedyAxisZ(chunk, occupancy, gridSize, vertices, positive: false);
        if (axisQuads > 0) surfaceMask |= SurfaceNegZ;
        quads += axisQuads;

        axisQuads = GreedyAxisX(chunk, occupancy, gridSize, vertices, positive: true);
        if (axisQuads > 0) surfaceMask |= SurfacePosX;
        quads += axisQuads;

        axisQuads = GreedyAxisX(chunk, occupancy, gridSize, vertices, positive: false);
        if (axisQuads > 0) surfaceMask |= SurfaceNegX;
        quads += axisQuads;

        axisQuads = GreedyAxisY(chunk, occupancy, gridSize, vertices, positive: true);
        if (axisQuads > 0) surfaceMask |= SurfacePosY;
        quads += axisQuads;

        axisQuads = GreedyAxisY(chunk, occupancy, gridSize, vertices, positive: false);
        if (axisQuads > 0) surfaceMask |= SurfaceNegY;
        quads += axisQuads;

        // Authored special pieces keep their exact shape and per-face lighting.
        // Mark the chunk as SurfaceAll so the conservative camera-facing filter never
        // hides custom/sloped geometry where back-side visibility may matter.
        for (int i = 0; i < chunk.SpecialPieces.Count; i++)
        {
            BlockData p = chunk.SpecialPieces[i];
            BlockMesh mesh = IsoBlockMeshBuilder.BuildWorld(p);
            XnaColor baseColor = UnpackColor(p.PackedColor);
            AppendMesh(vertices, mesh, baseColor);
            quads += mesh.TriangleCount / 2;
            surfaceMask = SurfaceAll;
        }

        VertexPositionColor[] vertexArray = vertices.ToArray();
        BoundingBox bounds = CreateTightMeshBounds(vertexArray, chunk.Cx, chunk.Cy, chunk.Cz, gridSize);
        return new ChunkMeshData(chunk.Key, chunk.Cx, chunk.Cy, chunk.Cz, bounds, vertexArray, quads, surfaceMask);
    }

    private static int GreedyAxisZ(ChunkInput chunk, Dictionary<int, ulong> occupancy, int gridSize, PooledVertexBuffer vertices, bool positive)
    {
        int quads = 0;
        int[] mask = ArrayPool<int>.Shared.Rent(ChunkSize * ChunkSize);

        try
        {
            for (int lz = 0; lz < ChunkSize; lz++)
        {
            Array.Clear(mask, 0, mask.Length);

            for (int ly = 0; ly < ChunkSize; ly++)
            {
                for (int lx = 0; lx < ChunkSize; lx++)
                {
                    ulong block = chunk.Blocks[CoordinateHelper.LocalIndex(lx, ly, lz)];
                    if (!IsGreedyCube(block)) continue;

                    int wx = chunk.Cx * ChunkSize + lx;
                    int wy = chunk.Cy * ChunkSize + ly;
                    int wz = chunk.Cz * ChunkSize + lz;
                    int nz = positive ? wz + 1 : wz - 1;
                    if (HasGreedyCube(occupancy, gridSize, wx, wy, nz))
                        continue;

                    mask[lx + ly * ChunkSize] = PackedColor(block);
                }
            }

            quads += EmitMask(mask, vertices, (x, y, w, h, packed) =>
            {
                XnaColor c = UnpackColor(packed);
                float x0 = chunk.Cx * ChunkSize + x;
                float x1 = x0 + w;
                float y0 = chunk.Cy * ChunkSize + y;
                float y1 = y0 + h;
                float z = chunk.Cz * ChunkSize + lz + (positive ? 1 : 0);
                if (positive)
                    AddQuad(vertices, new Vector3(x0, y0, z), new Vector3(x1, y0, z), new Vector3(x1, y1, z), new Vector3(x0, y1, z), c);
                else
                    AddQuad(vertices, new Vector3(x1, y0, z), new Vector3(x0, y0, z), new Vector3(x0, y1, z), new Vector3(x1, y1, z), c);
            });
        }

            return quads;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(mask, clearArray: true);
        }
    }

    private static int GreedyAxisX(ChunkInput chunk, Dictionary<int, ulong> occupancy, int gridSize, PooledVertexBuffer vertices, bool positive)
    {
        int quads = 0;
        int[] mask = ArrayPool<int>.Shared.Rent(ChunkSize * ChunkSize);

        try
        {
            for (int lx = 0; lx < ChunkSize; lx++)
        {
            Array.Clear(mask, 0, mask.Length);

            for (int ly = 0; ly < ChunkSize; ly++)
            {
                for (int lz = 0; lz < ChunkSize; lz++)
                {
                    ulong block = chunk.Blocks[CoordinateHelper.LocalIndex(lx, ly, lz)];
                    if (!IsGreedyCube(block)) continue;

                    int wx = chunk.Cx * ChunkSize + lx;
                    int wy = chunk.Cy * ChunkSize + ly;
                    int wz = chunk.Cz * ChunkSize + lz;
                    int nx = positive ? wx + 1 : wx - 1;
                    if (HasGreedyCube(occupancy, gridSize, nx, wy, wz))
                        continue;

                    mask[lz + ly * ChunkSize] = PackedColor(block);
                }
            }

            quads += EmitMask(mask, vertices, (zLocal, y, w, h, packed) =>
            {
                XnaColor c = UnpackColor(packed);
                float x = chunk.Cx * ChunkSize + lx + (positive ? 1 : 0);
                float z0 = chunk.Cz * ChunkSize + zLocal;
                float z1 = z0 + w;
                float y0 = chunk.Cy * ChunkSize + y;
                float y1 = y0 + h;
                if (positive)
                    AddQuad(vertices, new Vector3(x, y0, z1), new Vector3(x, y0, z0), new Vector3(x, y1, z0), new Vector3(x, y1, z1), c);
                else
                    AddQuad(vertices, new Vector3(x, y0, z0), new Vector3(x, y0, z1), new Vector3(x, y1, z1), new Vector3(x, y1, z0), c);
            });
        }

            return quads;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(mask, clearArray: true);
        }
    }

    private static int GreedyAxisY(ChunkInput chunk, Dictionary<int, ulong> occupancy, int gridSize, PooledVertexBuffer vertices, bool positive)
    {
        int quads = 0;
        int[] mask = ArrayPool<int>.Shared.Rent(ChunkSize * ChunkSize);

        try
        {
            for (int ly = 0; ly < ChunkSize; ly++)
        {
            Array.Clear(mask, 0, mask.Length);

            for (int lz = 0; lz < ChunkSize; lz++)
            {
                for (int lx = 0; lx < ChunkSize; lx++)
                {
                    ulong block = chunk.Blocks[CoordinateHelper.LocalIndex(lx, ly, lz)];
                    if (!IsGreedyCube(block)) continue;

                    int wx = chunk.Cx * ChunkSize + lx;
                    int wy = chunk.Cy * ChunkSize + ly;
                    int wz = chunk.Cz * ChunkSize + lz;
                    int ny = positive ? wy + 1 : wy - 1;
                    if (HasGreedyCube(occupancy, gridSize, wx, ny, wz))
                        continue;

                    mask[lx + lz * ChunkSize] = PackedColor(block);
                }
            }

            quads += EmitMask(mask, vertices, (x, zLocal, w, h, packed) =>
            {
                XnaColor c = UnpackColor(packed);
                float x0 = chunk.Cx * ChunkSize + x;
                float x1 = x0 + w;
                float z0 = chunk.Cz * ChunkSize + zLocal;
                float z1 = z0 + h;
                float y = chunk.Cy * ChunkSize + ly + (positive ? 1 : 0);
                if (positive)
                    AddQuad(vertices, new Vector3(x0, y, z0), new Vector3(x0, y, z1), new Vector3(x1, y, z1), new Vector3(x1, y, z0), c);
                else
                    AddQuad(vertices, new Vector3(x0, y, z0), new Vector3(x1, y, z0), new Vector3(x1, y, z1), new Vector3(x0, y, z1), c);
            });
        }

            return quads;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(mask, clearArray: true);
        }
    }

    private delegate void EmitQuadDelegate(int x, int y, int width, int height, int packedColor);

    private static int EmitMask(int[] mask, PooledVertexBuffer vertices, EmitQuadDelegate emit)
    {
        int quads = 0;

        for (int y = 0; y < ChunkSize; y++)
        {
            int x = 0;
            while (x < ChunkSize)
            {
                int packed = mask[x + y * ChunkSize];
                if (packed == 0)
                {
                    x++;
                    continue;
                }

                int width = 1;
                while (x + width < ChunkSize && mask[x + width + y * ChunkSize] == packed)
                    width++;

                int height = 1;
                bool done = false;
                while (y + height < ChunkSize && !done)
                {
                    for (int k = 0; k < width; k++)
                    {
                        if (mask[x + k + (y + height) * ChunkSize] != packed)
                        {
                            done = true;
                            break;
                        }
                    }

                    if (!done)
                        height++;
                }

                emit(x, y, width, height, packed);

                for (int yy = 0; yy < height; yy++)
                {
                    int row = (y + yy) * ChunkSize;
                    for (int xx = 0; xx < width; xx++)
                        mask[x + xx + row] = 0;
                }

                quads++;
                x += width;
            }
        }

        return quads;
    }

    private static int EstimateInitialVertexCapacity(ChunkInput chunk)
    {
        // Worst case remains handled by Grow(), but normal greedy chunks should not
        // rent LOH-sized staging arrays. 32k vertices is enough for the current 250k
        // stress cube chunks and keeps the pool from retaining huge buffers.
        int byOccupancy = Math.Max(1024, chunk.NonEmptyCount / 2);
        int bySpecials = chunk.SpecialPieces.Count * 36;
        return Math.Clamp(Math.Max(byOccupancy, bySpecials), 1024, 32 * 1024);
    }

    private static void AppendMesh(PooledVertexBuffer vertices, BlockMesh mesh, XnaColor baseColor)
    {
        for (int i = 0; i + 2 < mesh.Indices.Count; i += 3)
        {
            int ia = mesh.Indices[i];
            int ib = mesh.Indices[i + 1];
            int ic = mesh.Indices[i + 2];
            if ((uint)ia >= (uint)mesh.Positions.Count ||
                (uint)ib >= (uint)mesh.Positions.Count ||
                (uint)ic >= (uint)mesh.Positions.Count)
                continue;

            AddTriangle(vertices, mesh.Positions[ia], mesh.Positions[ib], mesh.Positions[ic], baseColor);
        }
    }

    private static void AddQuad(PooledVertexBuffer vertices, Vector3 a, Vector3 b, Vector3 c, Vector3 d, XnaColor baseColor)
    {
        XnaColor color = LitColor(a, b, c, baseColor);
        vertices.Add(new VertexPositionColor(a, color));
        vertices.Add(new VertexPositionColor(b, color));
        vertices.Add(new VertexPositionColor(c, color));
        vertices.Add(new VertexPositionColor(a, color));
        vertices.Add(new VertexPositionColor(c, color));
        vertices.Add(new VertexPositionColor(d, color));
    }

    private static void AddTriangle(PooledVertexBuffer vertices, Vector3 a, Vector3 b, Vector3 c, XnaColor baseColor)
    {
        if (Vector3.Cross(b - a, c - a).LengthSquared() < 0.00000001f)
            return;

        XnaColor color = LitColor(a, b, c, baseColor);
        vertices.Add(new VertexPositionColor(a, color));
        vertices.Add(new VertexPositionColor(b, color));
        vertices.Add(new VertexPositionColor(c, color));
    }

    private static XnaColor LitColor(Vector3 a, Vector3 b, Vector3 c, XnaColor baseColor)
    {
        Vector3 n = Vector3.Cross(b - a, c - a);
        if (n.LengthSquared() > 0.000001f)
            n.Normalize();
        else
            n = Vector3.Up;

        Vector3 key = Vector3.Normalize(new Vector3(0.55f, 0.78f, -0.35f));
        Vector3 fill = Vector3.Normalize(new Vector3(-0.6f, 0.3f, 0.55f));
        float k = MathF.Abs(Vector3.Dot(n, key));
        float f = MathF.Abs(Vector3.Dot(n, fill));
        float intensity = Math.Clamp(0.34f + k * 0.50f + f * 0.20f, 0.30f, 1.0f);

        return new XnaColor(
            (byte)Math.Clamp(baseColor.R * intensity, 0, 255),
            (byte)Math.Clamp(baseColor.G * intensity, 0, 255),
            (byte)Math.Clamp(baseColor.B * intensity, 0, 255),
            baseColor.A);
    }

    private static bool IsGreedyCube(BlockData p)
        => p.Shape == BlockShape.FullCube && p.RotationY == 0 && !p.FlipHorizontal && !p.FlipVertical;

    private static bool IsGreedyCube(ulong packedBlock)
        => packedBlock != 0 && ShapeFlags(packedBlock) == 0;

    private static bool HasGreedyCube(Dictionary<int, ulong> occupancy, int gridSize, int x, int y, int z)
    {
        if (x < 0 || y < 0 || z < 0 || x >= gridSize || y >= gridSize || z >= gridSize)
            return false;

        return occupancy.TryGetValue(CoordinateHelper.CellKey(x, y, z), out ulong block) && IsGreedyCube(block);
    }

    private static ulong PackBlock(BlockData p)
        => ((ulong)p.ShapeFlags << 32) | (uint)p.PackedColor;

    private static int PackedColor(ulong packedBlock)
        => unchecked((int)(packedBlock & 0xFFFFFFFFUL));

    private static ushort ShapeFlags(ulong packedBlock)
        => (ushort)(packedBlock >> 32);

    private static BoundingBox CreateChunkBounds(int cx, int cy, int cz, int gridSize)
    {
        float minX = cx * ChunkSize;
        float minY = cy * ChunkSize;
        float minZ = cz * ChunkSize;
        float maxX = Math.Min(gridSize, minX + ChunkSize);
        float maxY = Math.Min(gridSize, minY + ChunkSize);
        float maxZ = Math.Min(gridSize, minZ + ChunkSize);
        return new BoundingBox(new Vector3(minX, minY, minZ), new Vector3(maxX, maxY, maxZ));
    }

    /// <summary>
    /// Creates a tight culling box from the actual emitted mesh vertices.
    /// This avoids false-positive frustum hits where a sparse/small model sits
    /// inside a large 32³ world chunk but the whole chunk box is still considered
    /// visible after the camera has panned away. A tiny epsilon keeps purely planar
    /// procedural surface chunks from becoming zero-thickness boxes.
    /// </summary>
    private static BoundingBox CreateTightMeshBounds(VertexPositionColor[] vertices, int cx, int cy, int cz, int gridSize)
    {
        if (vertices.Length == 0)
            return CreateChunkBounds(cx, cy, cz, gridSize);

        Vector3 min = vertices[0].Position;
        Vector3 max = vertices[0].Position;
        for (int i = 1; i < vertices.Length; i++)
        {
            Vector3 p = vertices[i].Position;
            if (p.X < min.X) min.X = p.X;
            if (p.Y < min.Y) min.Y = p.Y;
            if (p.Z < min.Z) min.Z = p.Z;
            if (p.X > max.X) max.X = p.X;
            if (p.Y > max.Y) max.Y = p.Y;
            if (p.Z > max.Z) max.Z = p.Z;
        }

        const float epsilon = 0.0025f;
        min -= new Vector3(epsilon);
        max += new Vector3(epsilon);

        // Keep bounds sane but do not force them back to the whole chunk. The
        // epsilon may legitimately push the outside surface slightly past grid
        // extents; clamp only to a tiny margin around the valid grid.
        float lo = -epsilon;
        float hi = gridSize + epsilon;
        min.X = Math.Clamp(min.X, lo, hi);
        min.Y = Math.Clamp(min.Y, lo, hi);
        min.Z = Math.Clamp(min.Z, lo, hi);
        max.X = Math.Clamp(max.X, lo, hi);
        max.Y = Math.Clamp(max.Y, lo, hi);
        max.Z = Math.Clamp(max.Z, lo, hi);

        return new BoundingBox(min, max);
    }

    private static XnaColor UnpackColor(int packed)
    {
        return new XnaColor(
            (byte)((packed >> 16) & 0xFF),
            (byte)((packed >> 8) & 0xFF),
            (byte)(packed & 0xFF),
            (byte)((packed >> 24) & 0xFF));
    }

    private static void AddChunkAndSixNeighboursIfInside(HashSet<int> keys, int cx, int cy, int cz, int chunksPerAxis)
    {
        AddChunkKeyIfInside(keys, cx, cy, cz, chunksPerAxis);
        AddChunkKeyIfInside(keys, cx - 1, cy, cz, chunksPerAxis);
        AddChunkKeyIfInside(keys, cx + 1, cy, cz, chunksPerAxis);
        AddChunkKeyIfInside(keys, cx, cy - 1, cz, chunksPerAxis);
        AddChunkKeyIfInside(keys, cx, cy + 1, cz, chunksPerAxis);
        AddChunkKeyIfInside(keys, cx, cy, cz - 1, chunksPerAxis);
        AddChunkKeyIfInside(keys, cx, cy, cz + 1, chunksPerAxis);
    }

    private static void AddChunkKeyIfInside(HashSet<int> keys, int cx, int cy, int cz, int chunksPerAxis)
    {
        if ((uint)cx >= (uint)chunksPerAxis ||
            (uint)cy >= (uint)chunksPerAxis ||
            (uint)cz >= (uint)chunksPerAxis)
        {
            return;
        }

        keys.Add(CoordinateHelper.ChunkKeyUnsafe(cx, cy, cz));
    }

    private sealed class ChunkInput
    {
        public readonly int Key;
        public readonly int Cx;
        public readonly int Cy;
        public readonly int Cz;

        // v94: horizontal Y-layer array. This is the future 256³ layout:
        // y-layer 0 is contiguous, then y-layer 1, etc.
        // Empty cell = 0. Non-empty cell = packed color + shape/rotation/flip flags.
        public readonly ulong[] Blocks = new ulong[ChunkVoxelCount];
        public readonly List<BlockData> SpecialPieces = new(64);
        public int NonEmptyCount;

        public ChunkInput(int key, int cx, int cy, int cz)
        {
            Key = key;
            Cx = cx;
            Cy = cy;
            Cz = cz;
        }

        public void SetCell(int lx, int ly, int lz, ulong packedBlock)
        {
            int index = CoordinateHelper.LocalIndex(lx, ly, lz);
            if (Blocks[index] == 0)
                NonEmptyCount++;
            Blocks[index] = packedBlock;
        }
    }

    private sealed class PooledVertexBuffer : IDisposable
    {
        // Arrays larger than this are LOH-sized and should not be returned to the shared
        // ArrayPool. Returning very large transient build buffers lets ArrayPool retain
        // them and Task Manager can show multi-GB memory after repeated stress tests.
        private const int MaxRetainedArrayLength = 128 * 1024;

        private VertexPositionColor[] _items;
        public int Count { get; private set; }

        public PooledVertexBuffer(int initialCapacity)
        {
            int capacity = Math.Clamp(initialCapacity, 64, MaxRetainedArrayLength);
            _items = ArrayPool<VertexPositionColor>.Shared.Rent(capacity);
        }

        public void Add(VertexPositionColor item)
        {
            if (Count == _items.Length)
                Grow();
            _items[Count++] = item;
        }

        public VertexPositionColor[] ToArray()
        {
            var result = new VertexPositionColor[Count];
            _items.AsSpan(0, Count).CopyTo(result);
            return result;
        }

        private void Grow()
        {
            int newLength = checked(_items.Length * 2);
            VertexPositionColor[] bigger = newLength <= MaxRetainedArrayLength
                ? ArrayPool<VertexPositionColor>.Shared.Rent(newLength)
                : new VertexPositionColor[newLength];
            _items.AsSpan(0, Count).CopyTo(bigger);
            ReturnIfSmall(_items);
            _items = bigger;
        }

        private static void ReturnIfSmall(VertexPositionColor[] array)
        {
            if (array.Length <= MaxRetainedArrayLength)
                ArrayPool<VertexPositionColor>.Shared.Return(array, clearArray: false);
            // For huge arrays we deliberately do not return them to ArrayPool. The GC can
            // release them instead of the pool keeping them alive for the process lifetime.
        }

        public void Dispose()
        {
            ReturnIfSmall(_items);
            _items = Array.Empty<VertexPositionColor>();
            Count = 0;
        }
    }


}
