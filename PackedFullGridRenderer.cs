using System.Buffers;
using System.Collections.Generic;
using System.Collections.Specialized;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using XnaColor = Microsoft.Xna.Framework.Color;

namespace IsoBlockCharacterEditor;

internal sealed class PackedFullGridRenderer : IDisposable
{
    private readonly HashSet<int> _deletedCells = new();
    private readonly HashSet<int> _occludedCells = new();
    private bool _occludedCellsDirty = true;

    private DynamicVertexBuffer? _overlayVertexBuffer;
    private int _overlayVertexCapacity;
    private int _overlayVertexCount;
    private int _overlayTriangleCount;
    private bool _overlayDirty = true;

    private readonly List<VertexPositionColor> _lineScratch = new(4096);
    private VertexPositionColor[] _scratchLineArray = new VertexPositionColor[512];

    public bool IsActive { get; private set; }
    public int GridSize { get; private set; } = 256;

    public void UseNormalScene()
    {
        IsActive = false;
        _deletedCells.Clear();
        _occludedCells.Clear();
        _occludedCellsDirty = true;
        _overlayDirty = true;
        DisposeOverlayBuffer();
    }

    public int UsePackedFullGridStressScene(int gridSize)
    {
        IsActive = true;
        GridSize = Math.Clamp(gridSize, 1, 256);
        _deletedCells.Clear();
        _occludedCells.Clear();
        _occludedCellsDirty = true;
        _overlayDirty = true;
        DisposeOverlayBuffer();
        return GridSize;
    }

    public void OnGraphicsDeviceReset()
    {
        _overlayDirty = true;
        DisposeOverlayBuffer();
    }

    public void MarkOverlayDirty()
    {
        _overlayDirty = true;
    }

    public void MarkOcclusionAndOverlayDirty()
    {
        _occludedCellsDirty = true;
        _overlayDirty = true;
    }

    public bool IsCellOccupied(int x, int y, int z)
    {
        if (!IsActive) return false;
        if ((uint)x >= (uint)GridSize || (uint)y >= (uint)GridSize || (uint)z >= (uint)GridSize)
            return false;
        return !_deletedCells.Contains(CoordinateHelper.CellKey(x, y, z));
    }

    public bool IsCellDeleted(int x, int y, int z)
    {
        if ((uint)x >= (uint)GridSize || (uint)y >= (uint)GridSize || (uint)z >= (uint)GridSize)
            return false;
        return _deletedCells.Contains(CoordinateHelper.CellKey(x, y, z));
    }

    public int[] GetDeletedCellKeys()
    {
        if (_deletedCells.Count == 0)
            return Array.Empty<int>();

        int[] result = new int[_deletedCells.Count];
        int index = 0;
        foreach (int key in _deletedCells)
            result[index++] = key;
        return result;
    }

    public void RestoreDeletedCellKeys(IEnumerable<int>? keys, ChunkedVoxelRenderer renderer)
    {
        _deletedCells.Clear();
        if (keys is not null)
        {
            foreach (int key in keys)
                _deletedCells.Add(key);
        }

        _occludedCellsDirty = true;
        _overlayDirty = true;

        if (IsActive)
            renderer.MarkProceduralFullGridDirty(GridSize);
    }

    public bool MarkCellsDeleted(IEnumerable<BlockPiece> pieces, ChunkedVoxelRenderer renderer)
    {
        if (!IsActive) return false;

        bool changed = false;
        foreach (BlockPiece p in pieces)
        {
            if ((uint)p.X < (uint)GridSize &&
                (uint)p.Y < (uint)GridSize &&
                (uint)p.Z < (uint)GridSize)
            {
                changed |= _deletedCells.Add(CoordinateHelper.CellKey(p.X, p.Y, p.Z));
            }
        }

        if (!changed)
            return false;

        _occludedCellsDirty = true;
        MarkGeometryDirtyForPieces(pieces, renderer);
        return true;
    }

    public bool ClearDeletedCell(int x, int y, int z, ChunkedVoxelRenderer renderer)
    {
        if (!IsActive) return false;
        if (!_deletedCells.Remove(CoordinateHelper.CellKey(x, y, z)))
            return false;

        _occludedCellsDirty = true;
        renderer.MarkProceduralFullGridCellDirty(x, y, z, GridSize);
        return true;
    }

    public void MarkGeometryDirtyForPieces(IEnumerable<BlockPiece> pieces, ChunkedVoxelRenderer renderer)
    {
        foreach (BlockPiece p in pieces)
        {
            if ((uint)p.X < (uint)GridSize &&
                (uint)p.Y < (uint)GridSize &&
                (uint)p.Z < (uint)GridSize)
            {
                renderer.MarkProceduralFullGridCellDirty(p.X, p.Y, p.Z, GridSize);
            }
        }
    }

    public void MarkGeometryDirtyForNotifyArgs(NotifyCollectionChangedEventArgs e, ChunkedVoxelRenderer renderer)
    {
        bool marked = false;
        if (e.OldItems is not null)
        {
            for (int i = 0; i < e.OldItems.Count; i++)
            {
                if (e.OldItems[i] is BlockPiece p)
                {
                    renderer.MarkProceduralFullGridCellDirty(p.X, p.Y, p.Z, GridSize);
                    marked = true;
                }
            }
        }

        if (e.NewItems is not null)
        {
            for (int i = 0; i < e.NewItems.Count; i++)
            {
                if (e.NewItems[i] is BlockPiece p)
                {
                    renderer.MarkProceduralFullGridCellDirty(p.X, p.Y, p.Z, GridSize);
                    marked = true;
                }
            }
        }

        if (!marked)
            renderer.MarkProceduralFullGridDirty(GridSize);
    }

    public IReadOnlyCollection<int> GetOccludedCells(IList<BlockPiece>? pieces)
    {
        if (!_occludedCellsDirty)
            return _occludedCells;

        _occludedCells.Clear();
        foreach (int key in _deletedCells)
            _occludedCells.Add(key);

        if (pieces is not null)
        {
            for (int i = 0; i < pieces.Count; i++)
            {
                BlockPiece p = pieces[i];
                if ((uint)p.X < (uint)GridSize &&
                    (uint)p.Y < (uint)GridSize &&
                    (uint)p.Z < (uint)GridSize)
                {
                    _occludedCells.Add(CoordinateHelper.CellKey(p.X, p.Y, p.Z));
                }
            }
        }

        _occludedCellsDirty = false;
        return _occludedCells;
    }

    public void Draw(
        GraphicsDevice graphicsDevice,
        BasicEffect effect,
        ChunkedVoxelRenderer chunkRenderer,
        BoundingFrustum frustum,
        Matrix blockWorld,
        Vector3 directionFromGridToCamera,
        Vector3 buildPriorityFocus,
        bool showEdges,
        IList<BlockPiece>? pieces,
        IEnumerable<BlockPiece>? selection,
        RasterizerState edgeRasterizer)
    {
        chunkRenderer.EnsureProceduralFullGridBuilt(graphicsDevice, GridSize, GetOccludedCells(pieces), buildPriorityFocus);
        chunkRenderer.DrawProceduralFullGridCameraFiltered(graphicsDevice, effect, frustum, blockWorld, directionFromGridToCamera, true, out _, out _);

        if (pieces is not null && pieces.Count > 0)
            DrawOverlayCached(graphicsDevice, effect, blockWorld, pieces);

        if (showEdges)
            DrawEdges(graphicsDevice, effect, selection, edgeRasterizer);
    }

    private void DrawOverlayCached(GraphicsDevice graphicsDevice, BasicEffect effect, Matrix blockWorld, IList<BlockPiece> pieces)
    {
        EnsureOverlayBuilt(graphicsDevice, pieces);
        if (_overlayVertexBuffer is null || _overlayVertexCount < 3)
            return;

        Matrix previousWorld = effect.World;
        effect.World = blockWorld;

        graphicsDevice.SetVertexBuffer(_overlayVertexBuffer);
        for (int passIndex = 0; passIndex < effect.CurrentTechnique.Passes.Count; passIndex++)
        {
            effect.CurrentTechnique.Passes[passIndex].Apply();
            graphicsDevice.DrawPrimitives(PrimitiveType.TriangleList, 0, _overlayVertexCount / 3);
        }
        graphicsDevice.SetVertexBuffer(null);

        effect.World = previousWorld;
    }

    private void EnsureOverlayBuilt(GraphicsDevice graphicsDevice, IList<BlockPiece> pieces)
    {
        if (!_overlayDirty)
            return;

        _overlayDirty = false;
        _overlayVertexCount = 0;
        _overlayTriangleCount = 0;

        if (pieces.Count == 0)
        {
            DisposeOverlayBuffer();
            return;
        }

        int totalVertices = 0;
        for (int i = 0; i < pieces.Count; i++)
            totalVertices += IsoBlockMeshBuilder.VertexCountFor(pieces[i].Shape);

        if (totalVertices < 3)
        {
            DisposeOverlayBuffer();
            return;
        }

        VertexPositionColor[] rented = ArrayPool<VertexPositionColor>.Shared.Rent(totalVertices);
        int written = 0;
        try
        {
            for (int i = 0; i < pieces.Count; i++)
            {
                BlockPiece piece = pieces[i];
                BlockMesh mesh = IsoBlockMeshBuilder.BuildWorld(piece);
                XnaColor baseColor = ColorUtil.ToXnaColor(piece.PackedColor);
                written += IsoBlockMeshBuilder.AppendVertices(mesh, baseColor, rented, written);
            }

            if (written < 3)
            {
                DisposeOverlayBuffer();
                return;
            }

            if (_overlayVertexBuffer is null || _overlayVertexCapacity < written)
            {
                DisposeOverlayBuffer();
                _overlayVertexCapacity = NextVertexCapacity(written);
                _overlayVertexBuffer = new DynamicVertexBuffer(graphicsDevice, typeof(VertexPositionColor), _overlayVertexCapacity, BufferUsage.WriteOnly);
            }

            _overlayVertexBuffer.SetData(rented, 0, written, SetDataOptions.Discard);
            _overlayVertexCount = written;
            _overlayTriangleCount = written / 3;
        }
        finally
        {
            ArrayPool<VertexPositionColor>.Shared.Return(rented, clearArray: false);
        }
    }

    private void DrawEdges(GraphicsDevice graphicsDevice, BasicEffect effect, IEnumerable<BlockPiece>? selection, RasterizerState edgeRasterizer)
    {
        _lineScratch.Clear();
        XnaColor color = new(57, 255, 20, 245);
        int n = GridSize;
        const float lift = 0.035f;
        float min = -lift;
        float max = n + lift;

        for (int i = 0; i <= n; i++)
        {
            float f = i;

            AddLine(_lineScratch, new Vector3(0, max, f), new Vector3(n, max, f), color);
            AddLine(_lineScratch, new Vector3(f, max, 0), new Vector3(f, max, n), color);
            AddLine(_lineScratch, new Vector3(0, min, f), new Vector3(n, min, f), color);
            AddLine(_lineScratch, new Vector3(f, min, 0), new Vector3(f, min, n), color);

            AddLine(_lineScratch, new Vector3(0, f, min), new Vector3(n, f, min), color);
            AddLine(_lineScratch, new Vector3(f, 0, min), new Vector3(f, n, min), color);
            AddLine(_lineScratch, new Vector3(0, f, max), new Vector3(n, f, max), color);
            AddLine(_lineScratch, new Vector3(f, 0, max), new Vector3(f, n, max), color);

            AddLine(_lineScratch, new Vector3(min, f, 0), new Vector3(min, f, n), color);
            AddLine(_lineScratch, new Vector3(min, 0, f), new Vector3(min, n, f), color);
            AddLine(_lineScratch, new Vector3(max, f, 0), new Vector3(max, f, n), color);
            AddLine(_lineScratch, new Vector3(max, 0, f), new Vector3(max, n, f), color);
        }

        if (selection is not null)
        {
            XnaColor selectedEdge = new(255, 230, 70, 255);
            const float g = 0.035f;
            foreach (BlockPiece p in selection)
            {
                AddBoxLines(
                    _lineScratch,
                    new BoundingBox(
                        new Vector3(p.X - g, p.Y - g, p.Z - g),
                        new Vector3(p.X + 1f + g, p.Y + 1f + g, p.Z + 1f + g)),
                    selectedEdge);
            }
        }

        graphicsDevice.BlendState = BlendState.NonPremultiplied;
        graphicsDevice.DepthStencilState = DepthStencilState.DepthRead;
        graphicsDevice.RasterizerState = edgeRasterizer;
        DrawLineList(graphicsDevice, effect, _lineScratch);
    }

    private void DrawLineList(GraphicsDevice graphicsDevice, BasicEffect effect, List<VertexPositionColor> lines)
    {
        if (lines.Count < 2) return;
        if (_scratchLineArray.Length < lines.Count)
            Array.Resize(ref _scratchLineArray, Math.Max(lines.Count, _scratchLineArray.Length * 2));
        for (int i = 0; i < lines.Count; i++)
            _scratchLineArray[i] = lines[i];

        foreach (EffectPass pass in effect.CurrentTechnique.Passes)
        {
            pass.Apply();
            graphicsDevice.DrawUserPrimitives(PrimitiveType.LineList, _scratchLineArray, 0, lines.Count / 2);
        }
    }

    private void DisposeOverlayBuffer()
    {
        _overlayVertexBuffer?.Dispose();
        _overlayVertexBuffer = null;
        _overlayVertexCapacity = 0;
        _overlayVertexCount = 0;
        _overlayTriangleCount = 0;
    }

    public void Dispose()
    {
        DisposeOverlayBuffer();
    }

    private static int NextVertexCapacity(int needed)
    {
        int capacity = 256;
        while (capacity < needed && capacity < 1_048_576)
            capacity <<= 1;
        return capacity < needed ? needed : capacity;
    }

    private static void AddLine(List<VertexPositionColor> list, Vector3 a, Vector3 b, XnaColor c)
    {
        if ((a - b).LengthSquared() < 0.000001f) return;
        list.Add(new VertexPositionColor(a, c));
        list.Add(new VertexPositionColor(b, c));
    }

    private static void AddBoxLines(List<VertexPositionColor> list, BoundingBox box, XnaColor color)
    {
        Vector3[] c = box.GetCorners();
        int[] e = [0, 1, 1, 2, 2, 3, 3, 0, 4, 5, 5, 6, 6, 7, 7, 4, 0, 4, 1, 5, 2, 6, 3, 7];
        for (int i = 0; i < e.Length; i += 2)
            AddLine(list, c[e[i]], c[e[i + 1]], color);
    }
}
