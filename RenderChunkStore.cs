using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace IsoBlockCharacterEditor;

/// <summary>
/// Owns live GPU chunks: the lookup map, stable draw list and DynamicVertexBuffer lifetime.
/// CPU builders produce ChunkMeshData; this store is the only place that turns it into GPU buffers.
/// </summary>
internal sealed class RenderChunkStore : IDisposable
{
    private readonly List<RenderChunk> _chunks = new(512);
    private readonly Dictionary<int, RenderChunk> _chunkMap = new(512);

    public bool HasLiveChunks => _chunks.Count > 0;

    public RenderChunkStoreStats ApplyBuildResult(GraphicsDevice graphicsDevice, BuildResult result)
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
            chunk.QuadEstimate = data.Quads;
            chunk.SurfaceMask = data.SurfaceMask;
            chunk.PrimitiveCount = data.Vertices.Length / 3;
        }

        for (int i = _chunks.Count - 1; i >= 0; i--)
        {
            RenderChunk chunk = _chunks[i];
            int key = CoordinateHelper.ChunkKey(chunk.Cx, chunk.Cy, chunk.Cz);
            bool remove = result.FullRebuild ? !liveMeshKeys.Contains(key) : rebuiltKeys.Contains(key) && !liveMeshKeys.Contains(key);
            if (!remove)
                continue;

            chunk.Dispose();
            _chunks.RemoveAt(i);
            _chunkMap.Remove(key);
        }

        return CalculateStats();
    }

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
            if (chunk.VertexBuffer is null || chunk.VertexCount < 3 || chunk.PrimitiveCount <= 0)
                continue;

            if (useSurfaceFilter && chunk.SurfaceMask != 0 && (chunk.SurfaceMask & surfaceFilterMask) == 0)
                continue;

            if (enableFrustumCulling && frustum.Contains(chunk.Bounds) == ContainmentType.Disjoint)
                continue;

            visibleChunks++;
            graphicsDevice.SetVertexBuffer(chunk.VertexBuffer);

            for (int p = 0; p < effect.CurrentTechnique.Passes.Count; p++)
            {
                effect.CurrentTechnique.Passes[p].Apply();
                graphicsDevice.DrawPrimitives(PrimitiveType.TriangleList, 0, chunk.PrimitiveCount);
                drawCalls++;
            }
        }
    }

    public RenderChunkStoreStats CalculateStats()
    {
        int totalVertices = 0;
        int totalTriangles = 0;
        int totalQuads = 0;
        int liveChunks = 0;

        for (int i = 0; i < _chunks.Count; i++)
        {
            RenderChunk chunk = _chunks[i];
            if (chunk.VertexBuffer is null || chunk.VertexCount <= 0)
                continue;

            liveChunks++;
            totalVertices += chunk.VertexCount;
            totalTriangles += chunk.VertexCount / 3;
            totalQuads += chunk.QuadEstimate;
        }

        return new RenderChunkStoreStats(liveChunks, totalVertices, totalTriangles, totalQuads);
    }

    public void Clear()
    {
        for (int i = 0; i < _chunks.Count; i++)
            _chunks[i].Dispose();

        _chunks.Clear();
        _chunkMap.Clear();
    }

    public void Dispose() => Clear();

    private sealed class RenderChunk : IDisposable
    {
        public readonly int Cx;
        public readonly int Cy;
        public readonly int Cz;
        public DynamicVertexBuffer? VertexBuffer;
        public BoundingBox Bounds;
        public int VertexCount;
        public int PrimitiveCount;
        public int QuadEstimate;
        public int BufferCapacity;
        public byte SurfaceMask;

        public RenderChunk(int cx, int cy, int cz)
        {
            Cx = cx;
            Cy = cy;
            Cz = cz;
        }

        public void ReplaceBuffer(GraphicsDevice graphicsDevice, VertexPositionColor[] vertices)
        {
            VertexCount = vertices.Length;
            PrimitiveCount = vertices.Length / 3;

            if (vertices.Length == 0)
            {
                VertexCount = 0;
                PrimitiveCount = 0;
                QuadEstimate = 0;
                SurfaceMask = 0;
                return;
            }

            if (VertexBuffer is null || BufferCapacity < vertices.Length)
            {
                VertexBuffer?.Dispose();
                BufferCapacity = Math.Max(vertices.Length, BufferCapacity * 2);
                VertexBuffer = new DynamicVertexBuffer(graphicsDevice, VertexPositionColor.VertexDeclaration, BufferCapacity, BufferUsage.WriteOnly);
            }

            // Discard tells the driver it does not need to preserve the old buffer
            // contents. This reduces GPU stalls when a chunk buffer is rewritten.
            VertexBuffer.SetData(
                0,
                vertices,
                0,
                vertices.Length,
                VertexPositionColor.VertexDeclaration.VertexStride,
                SetDataOptions.Discard);
        }

        private void DisposeBufferOnly()
        {
            VertexBuffer?.Dispose();
            VertexBuffer = null;
            BufferCapacity = 0;
            VertexCount = 0;
            PrimitiveCount = 0;
            QuadEstimate = 0;
        }

        public void Dispose() => DisposeBufferOnly();
    }
}

internal readonly record struct RenderChunkStoreStats(int LiveChunks, int Vertices, int Triangles, int Quads);
