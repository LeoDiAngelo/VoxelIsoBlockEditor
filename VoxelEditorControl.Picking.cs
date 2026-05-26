using System.Diagnostics;
using System.Windows.Input;
using Microsoft.Xna.Framework;
using WpfPoint = System.Windows.Point;

namespace IsoBlockCharacterEditor;

public sealed partial class VoxelEditorControl
{
    private void HandleLeftClick(WpfPoint p)
    {
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        if (TryPickBlock(p, out var piece, out var hitPoint, out var faceNormal))
        {
            if (EditorMode == EditorMode.Build)
            {
                var target = AdjacentCell(piece, faceNormal);
                AddBlockRequested?.Invoke(target.X, target.Y, target.Z, false);
            }
            else
            {
                SelectBlockRequested?.Invoke(piece, shift);
            }
            return;
        }

        // A packed full 256³ volume is solid. If the ray did not hit a valid
        // exposed surface, do not fall back to floor/grid building; that would
        // allow placing blocks inside the apparent hollow/culled interior.
        if (_packedFullGridStressMode)
        {
            if (EditorMode == EditorMode.Select && !shift)
                SelectBlockRequested?.Invoke(null, false);
            return;
        }

        if (EditorMode == EditorMode.Build && TryPickGrid(p, out int x, out int y, out int z))
        {
            AddBlockRequested?.Invoke(x, y, z, true);
            return;
        }

        if (EditorMode == EditorMode.Select && !shift)
            SelectBlockRequested?.Invoke(null, false);
    }

    private static (int X, int Y, int Z) AdjacentCell(BlockPiece piece, Vector3 normal)
    {
        int dx = 0, dy = 0, dz = 0;
        float ax = Math.Abs(normal.X), ay = Math.Abs(normal.Y), az = Math.Abs(normal.Z);
        if (ay >= ax && ay >= az) dy = normal.Y >= 0 ? 1 : -1;
        else if (ax >= az) dx = normal.X >= 0 ? 1 : -1;
        else dz = normal.Z >= 0 ? 1 : -1;
        return (piece.X + dx, piece.Y + dy, piece.Z + dz);
    }

    // v104: keep the packed 256^3 pick ray and normal voxel pick ray in separate local names.
    // This avoids CS0136 and makes the coordinate-space conversion explicit.
    private bool TryPickBlock(WpfPoint p, out BlockPiece piece, out Vector3 hitPoint, out Vector3 normal)
    {
        var pickWatch = Stopwatch.StartNew();
        try
        {
            piece = null!;
            hitPoint = default;
            normal = Vector3.Up;

            if (_packedFullGridStressMode)
            {
                Ray packedRay = ToLogicalRay(GetPickRay(p));
                return RaycastPackedFullGrid(packedRay, out piece, out hitPoint, out normal);
            }

            if (_pieces is null || _pieces.Count == 0) return false;

            if (_isPreviewMode)
                return TryPickBlockLinear(p, out piece, out hitPoint, out normal);

            RebuildPickGridIfNeeded();
            Ray voxelRay = ToLogicalRay(GetPickRay(p));
            return RaycastVoxelGrid(voxelRay, out piece, out hitPoint, out normal);
        }
        finally
        {
            pickWatch.Stop();
            _lastPickingMilliseconds = pickWatch.Elapsed.TotalMilliseconds;
        }
    }

    private bool TryPickBlockLinear(WpfPoint p, out BlockPiece piece, out Vector3 hitPoint, out Vector3 normal)
    {
        piece = null!;
        hitPoint = default;
        normal = Vector3.Up;
        if (_pieces is null || _pieces.Count == 0) return false;
        var ray = GetPickRay(p);
        float best = float.MaxValue;
        for (int i = 0; i < _pieces.Count; i++)
        {
            BlockPiece candidate = _pieces[i];
            var box = new BoundingBox(new Vector3(candidate.X, candidate.Y, candidate.Z) + _workspaceOffset, new Vector3(candidate.X + 1, candidate.Y + 1, candidate.Z + 1) + _workspaceOffset);
            float? t = ray.Intersects(box);
            if (t is float dist && dist < best)
            {
                best = dist;
                piece = candidate;
                hitPoint = ray.Position + ray.Direction * dist - _workspaceOffset;
                normal = EstimateFaceNormal(candidate, hitPoint);
            }
        }
        return best < float.MaxValue;
    }

    private static string PackedFullGridColorHex(int x, int y, int z)
    {
        return ((x + y + z) & 3) switch
        {
            0 => "#4B88D8",
            1 => "#60B35F",
            2 => "#D8B18C",
            _ => "#2D3542"
        };
    }

    private bool RaycastPackedFullGrid(Ray ray, out BlockPiece piece, out Vector3 hitPoint, out Vector3 normal)
    {
        piece = null!;
        hitPoint = default;
        normal = Vector3.Up;

        var gridBox = new BoundingBox(Vector3.Zero, new Vector3(_packedFullGridSize, _packedFullGridSize, _packedFullGridSize));

        // The packed-full test represents a truly solid volume. If the camera
        // somehow starts inside it, do not pick internal cells. This prevents
        // edit operations from happening inside the culled/hollow-looking interior.
        if (ContainsStrict(gridBox, ray.Position))
            return false;

        float? entry = ray.Intersects(gridBox);
        if (entry is null)
            return false;

        float t = MathF.Max(entry.Value, 0f) + 0.0001f;
        Vector3 pos = ray.Position + ray.Direction * t;
        int x = ClampPackedCell(pos.X);
        int y = ClampPackedCell(pos.Y);
        int z = ClampPackedCell(pos.Z);

        int stepX = ray.Direction.X > 0 ? 1 : ray.Direction.X < 0 ? -1 : 0;
        int stepY = ray.Direction.Y > 0 ? 1 : ray.Direction.Y < 0 ? -1 : 0;
        int stepZ = ray.Direction.Z > 0 ? 1 : ray.Direction.Z < 0 ? -1 : 0;

        float tDeltaX = stepX == 0 ? float.PositiveInfinity : MathF.Abs(1f / ray.Direction.X);
        float tDeltaY = stepY == 0 ? float.PositiveInfinity : MathF.Abs(1f / ray.Direction.Y);
        float tDeltaZ = stepZ == 0 ? float.PositiveInfinity : MathF.Abs(1f / ray.Direction.Z);

        float nextBoundaryX = stepX > 0 ? x + 1f : x;
        float nextBoundaryY = stepY > 0 ? y + 1f : y;
        float nextBoundaryZ = stepZ > 0 ? z + 1f : z;

        float tMaxX = stepX == 0 ? float.PositiveInfinity : (nextBoundaryX - ray.Position.X) / ray.Direction.X;
        float tMaxY = stepY == 0 ? float.PositiveInfinity : (nextBoundaryY - ray.Position.Y) / ray.Direction.Y;
        float tMaxZ = stepZ == 0 ? float.PositiveInfinity : (nextBoundaryZ - ray.Position.Z) / ray.Direction.Z;

        tMaxX = MathF.Max(tMaxX, t);
        tMaxY = MathF.Max(tMaxY, t);
        tMaxZ = MathF.Max(tMaxZ, t);

        int maxSteps = _packedFullGridSize * 3 + 8;
        for (int i = 0; i < maxSteps; i++)
        {
            if ((uint)x >= (uint)_packedFullGridSize || (uint)y >= (uint)_packedFullGridSize || (uint)z >= (uint)_packedFullGridSize)
                return false;

            // Deleted cells are holes in the packed full base. Continue through
            // them so Build can place a block back into the first exposed hole.
            if (!_packedRenderer.IsCellDeleted(x, y, z))
            {
                BlockPiece? existing = null;
                if (_pieces is not null)
                {
                    for (int pi = 0; pi < _pieces.Count; pi++)
                    {
                        BlockPiece p = _pieces[pi];
                        if (p.X == x && p.Y == y && p.Z == z)
                        {
                            existing = p;
                            break;
                        }
                    }
                }

                piece = existing ?? new BlockPiece
                {
                    X = x,
                    Y = y,
                    Z = z,
                    Shape = BlockShape.FullCube,
                    ColorHex = PackedFullGridColorHex(x, y, z)
                };

                var box = new BoundingBox(new Vector3(x, y, z), new Vector3(x + 1, y + 1, z + 1));
                float? hitT = ray.Intersects(box);
                if (hitT is float dist)
                {
                    hitPoint = ray.Position + ray.Direction * dist;
                    normal = EstimateFaceNormal(piece, hitPoint);
                    return true;
                }
            }

            if (tMaxX <= tMaxY && tMaxX <= tMaxZ)
            {
                x += stepX;
                tMaxX += tDeltaX;
            }
            else if (tMaxY <= tMaxZ)
            {
                y += stepY;
                tMaxY += tDeltaY;
            }
            else
            {
                z += stepZ;
                tMaxZ += tDeltaZ;
            }
        }

        return false;
    }

    private static bool ContainsStrict(BoundingBox box, Vector3 p)
        => p.X > box.Min.X && p.X < box.Max.X &&
           p.Y > box.Min.Y && p.Y < box.Max.Y &&
           p.Z > box.Min.Z && p.Z < box.Max.Z;

    private bool RaycastVoxelGrid(Ray ray, out BlockPiece piece, out Vector3 hitPoint, out Vector3 normal)
    {
        piece = null!;
        hitPoint = default;
        normal = Vector3.Up;

        var gridBox = new BoundingBox(Vector3.Zero, new Vector3(_gridSize, _gridSize, _gridSize));
        float? entry = ray.Intersects(gridBox);
        if (entry is null)
            return false;

        float t = MathF.Max(entry.Value, 0f) + 0.0001f;
        Vector3 pos = ray.Position + ray.Direction * t;
        int x = ClampCell(pos.X);
        int y = ClampCell(pos.Y);
        int z = ClampCell(pos.Z);

        int stepX = ray.Direction.X > 0 ? 1 : ray.Direction.X < 0 ? -1 : 0;
        int stepY = ray.Direction.Y > 0 ? 1 : ray.Direction.Y < 0 ? -1 : 0;
        int stepZ = ray.Direction.Z > 0 ? 1 : ray.Direction.Z < 0 ? -1 : 0;

        float tDeltaX = stepX == 0 ? float.PositiveInfinity : MathF.Abs(1f / ray.Direction.X);
        float tDeltaY = stepY == 0 ? float.PositiveInfinity : MathF.Abs(1f / ray.Direction.Y);
        float tDeltaZ = stepZ == 0 ? float.PositiveInfinity : MathF.Abs(1f / ray.Direction.Z);

        float nextBoundaryX = stepX > 0 ? x + 1f : x;
        float nextBoundaryY = stepY > 0 ? y + 1f : y;
        float nextBoundaryZ = stepZ > 0 ? z + 1f : z;

        float tMaxX = stepX == 0 ? float.PositiveInfinity : (nextBoundaryX - ray.Position.X) / ray.Direction.X;
        float tMaxY = stepY == 0 ? float.PositiveInfinity : (nextBoundaryY - ray.Position.Y) / ray.Direction.Y;
        float tMaxZ = stepZ == 0 ? float.PositiveInfinity : (nextBoundaryZ - ray.Position.Z) / ray.Direction.Z;

        tMaxX = MathF.Max(tMaxX, t);
        tMaxY = MathF.Max(tMaxY, t);
        tMaxZ = MathF.Max(tMaxZ, t);

        int maxSteps = _gridSize * 3 + 8;
        for (int i = 0; i < maxSteps; i++)
        {
            if ((uint)x >= (uint)_gridSize || (uint)y >= (uint)_gridSize || (uint)z >= (uint)_gridSize)
                return false;

            if (_blocksByCell.TryGetValue(CoordinateHelper.CellKey(x, y, z), out BlockPiece? hitPiece))
            {
                var box = new BoundingBox(new Vector3(hitPiece.X, hitPiece.Y, hitPiece.Z), new Vector3(hitPiece.X + 1, hitPiece.Y + 1, hitPiece.Z + 1));
                float? hitT = ray.Intersects(box);
                if (hitT is float dist)
                {
                    piece = hitPiece;
                    hitPoint = ray.Position + ray.Direction * dist;
                    normal = EstimateFaceNormal(hitPiece, hitPoint);
                    return true;
                }
            }

            if (tMaxX <= tMaxY && tMaxX <= tMaxZ)
            {
                x += stepX;
                tMaxX += tDeltaX;
            }
            else if (tMaxY <= tMaxZ)
            {
                y += stepY;
                tMaxY += tDeltaY;
            }
            else
            {
                z += stepZ;
                tMaxZ += tDeltaZ;
            }
        }

        return false;
    }

    private void RebuildPickGridIfNeeded()
    {
        if (!_pickGridDirty) return;
        _blocksByCell.Clear();
        if (_pieces is not null)
        {
            for (int i = 0; i < _pieces.Count; i++)
            {
                BlockPiece p = _pieces[i];
                if ((uint)p.X < (uint)_gridSize && (uint)p.Y < (uint)_gridSize && (uint)p.Z < (uint)_gridSize)
                    _blocksByCell[p.CellKey] = p;
            }
        }
        _pickGridDirty = false;
    }

    private bool TryPickGrid(WpfPoint p, out int x, out int y, out int z)
    {
        x = y = z = 0;
        var ray = GetPickRay(p);

        bool found = false;
        float bestDistance = float.MaxValue;
        int bestX = 0, bestY = 0, bestZ = 0;

        // The visual grid is rendered with _effect.World = Translate(_workspaceOffset).
        // Therefore picking must test the translated visual planes, then convert the
        // resulting hit point back into logical grid coordinates by subtracting the
        // same workspace offset. If we ignore the offset here, clicks after arrow-key
        // panning can map to seemingly random cells and trigger false Occupied popups.
        void TestPlaneIntersection(Vector3 planeNormal, float planeD, BuildGridPlane plane)
        {
            float denom = Vector3.Dot(ray.Direction, planeNormal);
            if (Math.Abs(denom) < 0.00001f) return;

            float dist = (planeD - Vector3.Dot(ray.Position, planeNormal)) / denom;
            if (dist <= 0.0001f || dist >= bestDistance) return;

            Vector3 visualHit = ray.Position + ray.Direction * dist;
            Vector3 hit = visualHit - _workspaceOffset;

            const float eps = 0.001f;
            if (hit.X < -eps || hit.X > _gridSize + eps ||
                hit.Y < -eps || hit.Y > _gridSize + eps ||
                hit.Z < -eps || hit.Z > _gridSize + eps)
            {
                return;
            }

            int tx = SnapCell(hit.X);
            int ty = SnapCell(hit.Y);
            int tz = SnapCell(hit.Z);

            if (plane == BuildGridPlane.FloorXZ) ty = 0;
            else if (plane == BuildGridPlane.LeftSideYZ) tx = 0;
            else if (plane == BuildGridPlane.RightSideXY) tz = 0;

            bestX = tx;
            bestY = ty;
            bestZ = tz;
            bestDistance = dist;
            found = true;
        }

        if (_showFloorGrid)
            TestPlaneIntersection(Vector3.Up, _workspaceOffset.Y, BuildGridPlane.FloorXZ);
        if (_showLeftGrid)
            TestPlaneIntersection(Vector3.UnitX, _workspaceOffset.X, BuildGridPlane.LeftSideYZ);
        if (_showRightGrid)
            TestPlaneIntersection(Vector3.UnitZ, _workspaceOffset.Z, BuildGridPlane.RightSideXY);

        if (!found) return false;

        x = bestX;
        y = bestY;
        z = bestZ;
        return true;
    }

    private int SnapCell(float v)
    {
        // Clamp before flooring so tiny floating-point overshoot at the grid edge
        // never wraps into an invalid or visually unexpected cell.
        float clamped = Math.Clamp(v, 0f, MathF.Max(0f, _gridSize - 0.0001f));
        return Math.Clamp((int)MathF.Floor(clamped), 0, _gridSize - 1);
    }

    private int ClampCell(float v)
    {
        // Used by DDA ray entry. The ray can enter at exactly gridSize due to
        // floating-point precision, so clamp to the last valid cell.
        float clamped = Math.Clamp(v, 0f, MathF.Max(0f, _gridSize - 0.0001f));
        return Math.Clamp((int)MathF.Floor(clamped), 0, _gridSize - 1);
    }

    private int ClampPackedCell(float v)
    {
        float clamped = Math.Clamp(v, 0f, MathF.Max(0f, _packedFullGridSize - 0.0001f));
        return Math.Clamp((int)MathF.Floor(clamped), 0, _packedFullGridSize - 1);
    }


    private static Vector3 EstimateFaceNormal(BlockPiece piece, Vector3 hit)
    {
        float lx = hit.X - piece.X;
        float ly = hit.Y - piece.Y;
        float lz = hit.Z - piece.Z;

        float best = Math.Abs(lx);
        Vector3 normal = -Vector3.UnitX;

        float d = Math.Abs(lx - 1f);
        if (d < best) { best = d; normal = Vector3.UnitX; }
        d = Math.Abs(ly);
        if (d < best) { best = d; normal = -Vector3.UnitY; }
        d = Math.Abs(ly - 1f);
        if (d < best) { best = d; normal = Vector3.UnitY; }
        d = Math.Abs(lz);
        if (d < best) { best = d; normal = -Vector3.UnitZ; }
        d = Math.Abs(lz - 1f);
        if (d < best) normal = Vector3.UnitZ;

        return normal;
    }

    private Ray ToLogicalRay(Ray visualRay)
    {
        return _camera.ToLogicalRay(visualRay);
    }

    private Ray GetPickRay(WpfPoint p)
    {
        var viewport = GraphicsDevice.Viewport;
        float sx = ActualWidth > 0 ? viewport.Width / (float)ActualWidth : 1f;
        float sy = ActualHeight > 0 ? viewport.Height / (float)ActualHeight : 1f;
        var near = viewport.Unproject(new Vector3((float)p.X * sx, (float)p.Y * sy, 0f), GetProjectionMatrix(), GetViewMatrix(), Matrix.Identity);
        var far = viewport.Unproject(new Vector3((float)p.X * sx, (float)p.Y * sy, 1f), GetProjectionMatrix(), GetViewMatrix(), Matrix.Identity);
        var dir = far - near;
        dir.Normalize();
        return new Ray(near, dir);
    }

}
