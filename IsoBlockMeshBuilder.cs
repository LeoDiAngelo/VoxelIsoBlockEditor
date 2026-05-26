using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace IsoBlockCharacterEditor;

public sealed class BlockMesh
{
    public readonly List<Vector3> Positions = new();
    public readonly List<int> Indices = new();

    public int TriangleCount => Indices.Count / 3;
}

public static class IsoBlockMeshBuilder
{
    private static readonly object s_localMeshCacheLock = new();
    private static readonly Dictionary<BlockShape, BlockMesh> s_localMeshCache = new();

    public static BlockMesh BuildLocal(BlockShape shape)
    {
        lock (s_localMeshCacheLock)
        {
            if (s_localMeshCache.TryGetValue(shape, out var cached))
                return cached;

            var mesh = CreateLocalUncached(shape);
            s_localMeshCache.Add(shape, mesh);
            return mesh;
        }
    }

    private static BlockMesh CreateLocalUncached(BlockShape shape)
    {
        return shape switch
        {
            BlockShape.Slope50 => SlopeX(highOnPositive: true),
            BlockShape.DiagonalSlope50 => DiagonalSlope(highX: true, highZ: true),
            BlockShape.DiagonalSlopeInward => DiagonalSlopeInward(lowX: true, lowZ: true),
            BlockShape.CornerCut50 => CornerCut(cutXPositive: true, cutZPositive: true),
            _ => Box(0, 0, 0, 1, 1, 1)
        };
    }

    public static BlockMesh BuildWorld(BlockPiece piece)
        => BuildWorld(piece.Shape, piece.RotationY, piece.FlipHorizontal, piece.FlipVertical, piece.X, piece.Y, piece.Z);

    public static BlockMesh BuildWorld(BlockData piece)
        => BuildWorld(piece.Shape, piece.RotationY, piece.FlipHorizontal, piece.FlipVertical, piece.X, piece.Y, piece.Z);

    private static BlockMesh BuildWorld(BlockShape shape, int rotationY, bool flipHorizontal, bool flipVertical, int x, int y, int z)
    {
        var local = BuildLocal(shape);
        var result = new BlockMesh();
        foreach (var p in local.Positions)
            result.Positions.Add(TransformLocal(p, rotationY, flipHorizontal, flipVertical, x, y, z));

        bool reverse = flipHorizontal ^ flipVertical;
        for (int i = 0; i + 2 < local.Indices.Count; i += 3)
        {
            int a = local.Indices[i];
            int b = local.Indices[i + 1];
            int c = local.Indices[i + 2];
            if (reverse)
            {
                result.Indices.Add(a);
                result.Indices.Add(c);
                result.Indices.Add(b);
            }
            else
            {
                result.Indices.Add(a);
                result.Indices.Add(b);
                result.Indices.Add(c);
            }
        }
        return result;
    }

    public static int VertexCountFor(BlockShape shape) => BuildLocal(shape).Indices.Count;

    public static VertexPositionColor[] ToVertices(BlockMesh mesh, Color baseColor)
    {
        var vertices = new VertexPositionColor[mesh.Indices.Count];
        AppendVertices(mesh, baseColor, vertices, 0);
        return vertices;
    }

    public static int AppendVertices(BlockMesh mesh, Color baseColor, VertexPositionColor[] target, int startIndex)
    {
        int write = startIndex;
        for (int i = 0; i + 2 < mesh.Indices.Count; i += 3)
        {
            var a = mesh.Positions[mesh.Indices[i]];
            var b = mesh.Positions[mesh.Indices[i + 1]];
            var c = mesh.Positions[mesh.Indices[i + 2]];
            var color = LitColor(a, b, c, baseColor);
            target[write++] = new VertexPositionColor(a, color);
            target[write++] = new VertexPositionColor(b, color);
            target[write++] = new VertexPositionColor(c, color);
        }
        return write - startIndex;
    }

    private static Color LitColor(Vector3 a, Vector3 b, Vector3 c, Color baseColor)
    {
        Vector3 n = Vector3.Cross(b - a, c - a);
        if (n.LengthSquared() > 0.000001f)
            n.Normalize();
        else
            n = Vector3.Up;

        // Keep immediate/overlay pieces visually identical to chunk-baked pieces.
        // Earlier versions used a different light model here, which made sparse
        // edited cells look like they had two colors after paint/rotate in 256³ mode.
        Vector3 key = Vector3.Normalize(new Vector3(0.55f, 0.78f, -0.35f));
        Vector3 fill = Vector3.Normalize(new Vector3(-0.6f, 0.3f, 0.55f));
        float k = MathF.Abs(Vector3.Dot(n, key));
        float f = MathF.Abs(Vector3.Dot(n, fill));
        float intensity = Math.Clamp(0.34f + k * 0.50f + f * 0.20f, 0.30f, 1.0f);
        return Scale(baseColor, intensity);
    }

    public static bool IsCompletelySolid(BlockPiece? piece) => piece is not null && piece.Shape == BlockShape.FullCube;

    public readonly record struct MeshStats(int Triangles, int Positions, int DrawVertices, int Quads);

    public static MeshStats EstimateCulledStats(IEnumerable<BlockPiece> pieces)
    {
        IList<BlockPiece> list;
        if (pieces is IList<BlockPiece> existingList)
        {
            list = existingList;
        }
        else
        {
            var builtList = new List<BlockPiece>();
            foreach (BlockPiece piece in pieces)
                builtList.Add(piece);
            list = builtList;
        }

        var occupancy = new Dictionary<int, BlockPiece>(list.Count);
        for (int i = 0; i < list.Count; i++)
        {
            BlockPiece piece = list[i];
            occupancy[CoordinateHelper.CellKey(piece.X, piece.Y, piece.Z)] = piece;
        }

        int triangles = 0;
        int positions = 0;
        int drawVertices = 0;
        int quads = 0;

        foreach (var piece in list)
        {
            if (piece.Shape == BlockShape.FullCube)
            {
                int exposedFaces = 0;
                if (!HasSolidNeighbor(occupancy, piece.X + 1, piece.Y, piece.Z)) exposedFaces++;
                if (!HasSolidNeighbor(occupancy, piece.X - 1, piece.Y, piece.Z)) exposedFaces++;
                if (!HasSolidNeighbor(occupancy, piece.X, piece.Y + 1, piece.Z)) exposedFaces++;
                if (!HasSolidNeighbor(occupancy, piece.X, piece.Y - 1, piece.Z)) exposedFaces++;
                if (!HasSolidNeighbor(occupancy, piece.X, piece.Y, piece.Z + 1)) exposedFaces++;
                if (!HasSolidNeighbor(occupancy, piece.X, piece.Y, piece.Z - 1)) exposedFaces++;

                quads += exposedFaces;
                triangles += exposedFaces * 2;
                positions += exposedFaces * 4;
                drawVertices += exposedFaces * 6;
            }
            else
            {
                var mesh = BuildWorld(piece);
                triangles += mesh.TriangleCount;
                positions += mesh.Positions.Count;
                drawVertices += mesh.Indices.Count;
                quads += mesh.TriangleCount / 2;
            }
        }

        return new MeshStats(triangles, positions, drawVertices, quads);
    }

    private static bool HasSolidNeighbor(Dictionary<int, BlockPiece> occupancy, int x, int y, int z)
        => CoordinateHelper.TryCellKey(x, y, z, out int key) &&
           occupancy.TryGetValue(key, out var neighbor) &&
           IsCompletelySolid(neighbor);


    private static Color Scale(Color c, float s)
        => new((byte)Math.Clamp(c.R * s, 0, 255), (byte)Math.Clamp(c.G * s, 0, 255), (byte)Math.Clamp(c.B * s, 0, 255), c.A);

    private static BlockMesh Box(float x, float y, float z, float sx, float sy, float sz)
    {
        var m = new BlockMesh();
        Vector3 p000 = new(x, y, z), p100 = new(x + sx, y, z), p110 = new(x + sx, y + sy, z), p010 = new(x, y + sy, z);
        Vector3 p001 = new(x, y, z + sz), p101 = new(x + sx, y, z + sz), p111 = new(x + sx, y + sy, z + sz), p011 = new(x, y + sy, z + sz);
        AddQuad(m, p001, p101, p111, p011); // front
        AddQuad(m, p100, p000, p010, p110); // back
        AddQuad(m, p000, p001, p011, p010); // left
        AddQuad(m, p101, p100, p110, p111); // right
        AddQuad(m, p010, p011, p111, p110); // top
        AddQuad(m, p000, p100, p101, p001); // bottom
        return m;
    }

    private static BlockMesh SlopeX(bool highOnPositive)
    {
        var m = new BlockMesh();
        float yLeft = highOnPositive ? 0f : 1f;
        float yRight = highOnPositive ? 1f : 0f;
        Vector3 p000 = new(0, 0, 0), p100 = new(1, 0, 0), p101 = new(1, 0, 1), p001 = new(0, 0, 1);
        Vector3 t010 = new(0, yLeft, 0), t110 = new(1, yRight, 0), t111 = new(1, yRight, 1), t011 = new(0, yLeft, 1);
        Vector3 center = new(0.5f, 0.5f, 0.5f);
        AddQuadOutward(m, p000, p100, p101, p001, center);
        AddQuadOutward(m, p001, p101, t111, t011, center);
        AddQuadOutward(m, p100, p000, t010, t110, center);
        if (!highOnPositive) AddQuadOutward(m, p000, p001, t011, t010, center);
        if (highOnPositive) AddQuadOutward(m, p101, p100, t110, t111, center);
        AddQuadOutward(m, t010, t011, t111, t110, center);
        return m;
    }

    private static BlockMesh DiagonalSlope(bool highX, bool highZ)
    {
        var m = new BlockMesh();
        Vector3 p000 = new(0, 0, 0), p100 = new(1, 0, 0), p101 = new(1, 0, 1), p001 = new(0, 0, 1);
        Vector3 apex = new(highX ? 1 : 0, 1, highZ ? 1 : 0);
        Vector3 center = new(0.5f, 0.25f, 0.5f);
        AddQuadOutward(m, p000, p100, p101, p001, center);
        AddTriangleOutward(m, p000, p100, apex, center);
        AddTriangleOutward(m, p100, p101, apex, center);
        AddTriangleOutward(m, p101, p001, apex, center);
        AddTriangleOutward(m, p001, p000, apex, center);
        return m;
    }

    private static BlockMesh DiagonalSlopeInward(bool lowX, bool lowZ)
    {
        var m = new BlockMesh();
        Vector3 b00 = new(0, 0, 0), b10 = new(1, 0, 0), b11 = new(1, 0, 1), b01 = new(0, 0, 1);
        float y00 = !lowX && !lowZ ? 0f : 1f;
        float y10 = lowX && !lowZ ? 0f : 1f;
        float y11 = lowX && lowZ ? 0f : 1f;
        float y01 = !lowX && lowZ ? 0f : 1f;
        Vector3 t00 = new(0, y00, 0), t10 = new(1, y10, 0), t11 = new(1, y11, 1), t01 = new(0, y01, 1);
        Vector3 center = new(0.5f, 0.5f, 0.5f);
        AddQuadOutward(m, b00, b10, b11, b01, center);
        AddSideIfVisibleOutward(m, b00, b10, t10, t00, center);
        AddSideIfVisibleOutward(m, b10, b11, t11, t10, center);
        AddSideIfVisibleOutward(m, b11, b01, t01, t11, center);
        AddSideIfVisibleOutward(m, b01, b00, t00, t01, center);
        AddQuadOutward(m, t00, t10, t11, t01, center);
        return m;
    }

    private static BlockMesh CornerCut(bool cutXPositive, bool cutZPositive)
    {
        var m = new BlockMesh();
        var footprint = BuildCornerCutFootprint(cutXPositive, cutZPositive);
        var center = new Vector3(0.5f, 0.5f, 0.5f);
        var bottom = new List<Vector3>(footprint.Count);
        var top = new List<Vector3>(footprint.Count);
        for (int i = 0; i < footprint.Count; i++)
        {
            FootprintVertex v = footprint[i];
            bottom.Add(new Vector3(v.X, 0, v.Z));
            top.Add(new Vector3(v.X, 1, v.Z));
        }
        AddPolygonOutward(m, bottom, center);
        AddPolygonOutward(m, top, center);
        for (int i = 0; i < footprint.Count; i++)
        {
            int j = (i + 1) % footprint.Count;
            AddQuadOutward(m, bottom[i], bottom[j], top[j], top[i], center);
        }
        return m;
    }

    private readonly record struct FootprintVertex(float X, float Z);

    private static List<FootprintVertex> BuildCornerCutFootprint(bool cutXPositive, bool cutZPositive)
    {
        var polygon = new List<FootprintVertex> { new(0, 0), new(1, 0), new(1, 1), new(0, 1) };
        float ax = cutXPositive ? 1f : -1f;
        float bz = cutZPositive ? 1f : -1f;
        float c = (cutXPositive ? 0f : 1f) + (cutZPositive ? 0f : 1f) - 1.5f;
        static float Eval(FootprintVertex p, float ax, float bz, float c) => ax * p.X + bz * p.Z + c;
        static FootprintVertex Intersect(FootprintVertex a, FootprintVertex b, float ea, float eb)
        {
            float t = ea / (ea - eb);
            return new FootprintVertex(a.X + (b.X - a.X) * t, a.Z + (b.Z - a.Z) * t);
        }
        var output = new List<FootprintVertex>();
        for (int i = 0; i < polygon.Count; i++)
        {
            var current = polygon[i];
            var next = polygon[(i + 1) % polygon.Count];
            float ec = Eval(current, ax, bz, c);
            float en = Eval(next, ax, bz, c);
            bool ic = ec <= 0.0001f;
            bool inn = en <= 0.0001f;
            if (ic && inn) output.Add(next);
            else if (ic && !inn) output.Add(Intersect(current, next, ec, en));
            else if (!ic && inn)
            {
                output.Add(Intersect(current, next, ec, en));
                output.Add(next);
            }
        }
        return output;
    }

    private static Vector3 TransformLocal(Vector3 p, int degrees, bool flipHorizontal, bool flipVertical, int gx, int gy, int gz)
    {
        float px = flipHorizontal ? 1f - p.X : p.X;
        float py = flipVertical ? 1f - p.Y : p.Y;
        float pz = p.Z;
        float x = px - 0.5f;
        float z = pz - 0.5f;
        int r = ((degrees % 360) + 360) % 360;
        float rx = x, rz = z;
        switch (r)
        {
            case 90: rx = z; rz = -x; break;
            case 180: rx = -x; rz = -z; break;
            case 270: rx = -z; rz = x; break;
        }
        return new Vector3(gx + rx + 0.5f, gy + py, gz + rz + 0.5f);
    }

    private static void AddSideIfVisibleOutward(BlockMesh m, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 center)
    {
        if (Math.Abs(c.Y) < 0.0001f && Math.Abs(d.Y) < 0.0001f) return;
        if ((c - b).LengthSquared() < 0.000001f)
        {
            AddTriangleOutward(m, a, b, d, center);
            return;
        }
        if ((d - a).LengthSquared() < 0.000001f)
        {
            AddTriangleOutward(m, a, b, c, center);
            return;
        }
        AddQuadOutward(m, a, b, c, d, center);
    }

    private static void AddTriangle(BlockMesh mesh, Vector3 a, Vector3 b, Vector3 c)
    {
        if (Vector3.Cross(b - a, c - a).LengthSquared() < 0.00000001f) return;
        int i = mesh.Positions.Count;
        mesh.Positions.Add(a); mesh.Positions.Add(b); mesh.Positions.Add(c);
        mesh.Indices.Add(i); mesh.Indices.Add(i + 1); mesh.Indices.Add(i + 2);
    }

    private static void AddTriangleOutward(BlockMesh mesh, Vector3 a, Vector3 b, Vector3 c, Vector3 solidCenter)
    {
        if (Vector3.Cross(b - a, c - a).LengthSquared() < 0.00000001f) return;
        var n = Vector3.Cross(b - a, c - a);
        var faceCenter = (a + b + c) / 3f;
        var outward = faceCenter - solidCenter;
        if (Vector3.Dot(n, outward) < 0) AddTriangle(mesh, a, c, b); else AddTriangle(mesh, a, b, c);
    }

    private static void AddQuad(BlockMesh mesh, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        int i = mesh.Positions.Count;
        mesh.Positions.Add(a); mesh.Positions.Add(b); mesh.Positions.Add(c); mesh.Positions.Add(d);
        mesh.Indices.Add(i); mesh.Indices.Add(i + 1); mesh.Indices.Add(i + 2);
        mesh.Indices.Add(i); mesh.Indices.Add(i + 2); mesh.Indices.Add(i + 3);
    }

    private static void AddQuadOutward(BlockMesh mesh, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 solidCenter)
    {
        AddTriangleOutward(mesh, a, b, c, solidCenter);
        AddTriangleOutward(mesh, a, c, d, solidCenter);
    }

    private static void AddPolygonOutward(BlockMesh mesh, IReadOnlyList<Vector3> points, Vector3 solidCenter)
    {
        int pointCount = points.Count;
        if (pointCount < 3) return;

        float sumX = 0f;
        float sumY = 0f;
        float sumZ = 0f;
        for (int i = 0; i < pointCount; i++)
        {
            Vector3 point = points[i];
            sumX += point.X;
            sumY += point.Y;
            sumZ += point.Z;
        }

        float invCount = 1f / pointCount;
        var center = new Vector3(sumX * invCount, sumY * invCount, sumZ * invCount);
        for (int i = 0; i < pointCount; i++)
        {
            AddTriangleOutward(mesh, center, points[i], points[(i + 1) % pointCount], solidCenter);
        }
    }
}
