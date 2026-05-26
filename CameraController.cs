using System.Collections.Generic;
using System.Windows.Input;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace IsoBlockCharacterEditor;

internal sealed class CameraController
{
    private const float LibraryPreviewModelScale = 1.0f;

    // Canonical preview framing used by Block Shape Library.
    // Higher orthographic height means more empty space around the 1x1x1 block.
    // 2.0f keeps all 1x1 shape previews safely inside the viewport, including
    // slopes whose upper wireframe corner can otherwise touch/crop at the top edge.
    private const float LibraryPreviewOrthoHeight = 2.0f;

    public float Yaw { get; set; } = MathHelper.ToRadians(45);
    public float Pitch { get; set; } = MathHelper.ToRadians(28);
    public float Distance { get; set; } = 40f;
    public Vector3 Target { get; set; } = new(64, 32, 64);
    public Vector3 WorkspaceOffset { get; set; } = Vector3.Zero;

    public bool IsPreviewMode { get; set; }
    public bool IsLibraryPreviewControl { get; private set; }
    public float PreviewModelScale { get; set; } = LibraryPreviewModelScale;
    public float PreviewOrthoHeight { get; set; } = LibraryPreviewOrthoHeight;
    public Vector3 PreviewScaleCenter { get; set; } = Vector3.Zero;

    private Vector3 _panVelocity = Vector3.Zero;
    private bool _panLeftPressed;
    private bool _panRightPressed;
    private bool _panUpPressed;
    private bool _panDownPressed;
    private bool _panFastPressed;

    public void ConfigureAsLibraryPreviewControl()
    {
        IsLibraryPreviewControl = true;
        IsPreviewMode = true;
    }

    public void CenterOnGrid(int gridSize)
    {
        WorkspaceOffset = Vector3.Zero;
        IsPreviewMode = false;
        PreviewModelScale = 1f;
        float center = (gridSize - 1) / 2f;
        Target = new Vector3(center, MathF.Min(16, gridSize / 4f), center);
        Distance = Math.Clamp(gridSize * 0.88f, 18, MaxDistance(gridSize));
    }

    public void CenterOnPackedFullGrid(int packedFullGridSize, int gridSize)
    {
        WorkspaceOffset = Vector3.Zero;
        IsPreviewMode = false;
        PreviewModelScale = 1f;

        float size = Math.Max(1, packedFullGridSize);
        float center = size * 0.5f;
        Target = new Vector3(center, center, center);

        float radius = MathF.Sqrt(size * size * 3f) * 0.5f;
        float desiredDistance = radius * 1.65f + 16f;
        Distance = Math.Clamp(desiredDistance, MinimumCameraDistanceOutsideModel(gridSize, true, packedFullGridSize), MaxDistance(gridSize));
    }

    public void CenterOnModel(IList<BlockPiece> pieces, int gridSize)
    {
        if (pieces.Count == 0)
        {
            CenterOnGrid(gridSize);
            return;
        }

        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        for (int i = 0; i < pieces.Count; i++)
        {
            BlockPiece p = pieces[i];
            if (p.X < minX) minX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.Z < minZ) minZ = p.Z;
            if (p.X + 1 > maxX) maxX = p.X + 1;
            if (p.Y + 1 > maxY) maxY = p.Y + 1;
            if (p.Z + 1 > maxZ) maxZ = p.Z + 1;
        }

        var center = new Vector3((minX + maxX) * 0.5f, (minY + maxY) * 0.5f, (minZ + maxZ) * 0.5f);
        var size = new Vector3(maxX - minX, maxY - minY, maxZ - minZ);
        float radius = MathF.Max(2.5f, size.Length() * 0.55f);

        WorkspaceOffset = Vector3.Zero;
        IsPreviewMode = false;
        PreviewModelScale = 1f;
        Target = center;
        Distance = Math.Clamp(radius * 1.55f + 2.4f, 4.8f, MaxDistance(gridSize));
    }

    public void CenterOnPreviewBlock()
    {
        WorkspaceOffset = Vector3.Zero;
        IsPreviewMode = true;
        PreviewModelScale = LibraryPreviewModelScale;
        PreviewOrthoHeight = LibraryPreviewOrthoHeight;
        PreviewScaleCenter = new Vector3(0.5f, 0.5f, 0.5f);

        Target = PreviewScaleCenter;
        Distance = 4.0f;
        Yaw = MathHelper.ToRadians(45);
        Pitch = MathHelper.ToRadians(22);
    }

    public void SetWorkspaceArrowKeyState(Key key, bool isPressed, bool fast)
    {
        if (key == Key.Left) _panLeftPressed = isPressed;
        else if (key == Key.Right) _panRightPressed = isPressed;
        else if (key == Key.Up) _panUpPressed = isPressed;
        else if (key == Key.Down) _panDownPressed = isPressed;
        _panFastPressed = fast;
    }

    public void ClearWorkspaceArrowKeyState()
    {
        _panLeftPressed = false;
        _panRightPressed = false;
        _panUpPressed = false;
        _panDownPressed = false;
        _panFastPressed = false;
        _panVelocity = Vector3.Zero;
    }

    public void UpdateWorkspacePan(float elapsed, int gridSize, bool packedFullGridMode, int packedFullGridSize)
    {
        if (elapsed <= 0) return;

        float dt = Math.Min(elapsed, 0.1f);
        float xAxis = (_panLeftPressed ? 1f : 0f) - (_panRightPressed ? 1f : 0f);
        float yAxis = (_panDownPressed ? 1f : 0f) - (_panUpPressed ? 1f : 0f);

        var viewInv = Matrix.Invert(GetViewMatrix());
        var right = Vector3.Normalize(new Vector3(viewInv.M11, viewInv.M12, viewInv.M13));
        var up = Vector3.Normalize(new Vector3(viewInv.M21, viewInv.M22, viewInv.M23));

        Vector3 desiredVelocity = Vector3.Zero;
        if (Math.Abs(xAxis) > 0.001f || Math.Abs(yAxis) > 0.001f)
        {
            Vector3 direction = right * xAxis + up * yAxis;
            if (direction.LengthSquared() > 0.000001f)
                direction.Normalize();

            float zoomScale = Math.Clamp(Distance / 42f, 0.35f, 4.0f);
            bool fast = _panFastPressed || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            float speed = (fast ? 60f : 24f) * zoomScale;
            desiredVelocity = direction * speed;
        }

        float lerpFactor = 1f - MathF.Exp(-16f * dt);
        _panVelocity = Vector3.Lerp(_panVelocity, desiredVelocity, lerpFactor);

        if (_panVelocity.LengthSquared() > 0.0001f)
        {
            WorkspaceOffset += _panVelocity * dt;
            if (packedFullGridMode)
                Distance = Math.Clamp(Distance, MinimumCameraDistanceOutsideModel(gridSize, true, packedFullGridSize), MaxDistance(gridSize));

            if (desiredVelocity.LengthSquared() < 0.0001f)
                _panVelocity *= MathF.Exp(-12f * dt);
        }
        else
        {
            _panVelocity = Vector3.Zero;
        }
    }

    public void RotateByMouseDelta(double dx, double dy, int gridSize, bool packedFullGridMode, int packedFullGridSize)
    {
        Yaw += (float)(dx * 0.0065);
        Pitch = Math.Clamp(Pitch - (float)(dy * 0.0045), MathHelper.ToRadians(-72), MathHelper.ToRadians(86));
        if (packedFullGridMode)
            Distance = Math.Clamp(Distance, MinimumCameraDistanceOutsideModel(gridSize, true, packedFullGridSize), MaxDistance(gridSize));
    }

    public void ZoomByMouseWheel(int delta, int gridSize, bool packedFullGridMode, int packedFullGridSize)
    {
        if (IsPreviewMode)
        {
            PreviewOrthoHeight *= delta > 0 ? 0.90f : 1.10f;
            PreviewOrthoHeight = Math.Clamp(PreviewOrthoHeight, 0.35f, 3.50f);
        }
        else
        {
            Distance *= delta > 0 ? 0.88f : 1.14f;
            Distance = Math.Clamp(Distance, MinimumCameraDistanceOutsideModel(gridSize, packedFullGridMode, packedFullGridSize), MaxDistance(gridSize));
        }
    }

    public Matrix GetViewMatrix()
    {
        Vector3 dir = ViewDirectionFromTargetToCamera();
        var pos = Target + dir * Distance;
        return Matrix.CreateLookAt(pos, Target, Vector3.Up);
    }

    public Matrix GetProjectionMatrix(Viewport viewport, int gridSize)
    {
        float aspect = viewport.AspectRatio;
        if (float.IsNaN(aspect) || aspect <= 0) aspect = 1;

        if (IsPreviewMode)
        {
            float height = PreviewOrthoHeight;
            float width = height * aspect;
            return Matrix.CreateOrthographic(width, height, 0.01f, 100f);
        }

        return Matrix.CreatePerspectiveFieldOfView(MathHelper.ToRadians(44), aspect, 0.05f, Math.Max(5000, gridSize * 80));
    }

    public Matrix GetBlockWorldMatrix()
    {
        if (!IsPreviewMode || Math.Abs(PreviewModelScale - 1f) < 0.0001f)
            return Matrix.CreateTranslation(WorkspaceOffset);

        return Matrix.CreateTranslation(-PreviewScaleCenter) *
               Matrix.CreateScale(PreviewModelScale) *
               Matrix.CreateTranslation(PreviewScaleCenter + WorkspaceOffset);
    }

    public Ray ToLogicalRay(Ray visualRay)
    {
        return new Ray(visualRay.Position - WorkspaceOffset, visualRay.Direction);
    }

    public Vector3 BuildPriorityFocus(int gridSize)
    {
        Vector3 focus = Target - WorkspaceOffset;
        focus.X = Math.Clamp(focus.X, 0, gridSize);
        focus.Y = Math.Clamp(focus.Y, 0, gridSize);
        focus.Z = Math.Clamp(focus.Z, 0, gridSize);
        return focus;
    }

    public Vector3 ViewDirectionFromTargetToCamera()
    {
        var dir = new Vector3(MathF.Cos(Pitch) * MathF.Cos(Yaw), MathF.Sin(Pitch), MathF.Cos(Pitch) * MathF.Sin(Yaw));
        if (dir.LengthSquared() > 0.000001f)
            dir.Normalize();
        return dir;
    }

    public float MinimumCameraDistanceOutsideModel(int gridSize, bool packedFullGridMode, int packedFullGridSize)
    {
        if (packedFullGridMode)
        {
            Vector3 dir = ViewDirectionFromTargetToCamera();
            Vector3 logicalTarget = Target - WorkspaceOffset;
            var box = new BoundingBox(Vector3.Zero, new Vector3(packedFullGridSize, packedFullGridSize, packedFullGridSize));

            if (ContainsStrict(box, logicalTarget))
            {
                float exit = DistanceToExitBox(logicalTarget, dir, box);
                return Math.Clamp(exit + 10.0f, 12.0f, MaxDistance(gridSize));
            }

            Vector3 logicalCamera = logicalTarget + dir * Distance;
            if (ContainsStrict(box, logicalCamera))
            {
                float exit = DistanceToExitBox(logicalTarget, dir, box);
                return Math.Clamp(exit + 10.0f, 12.0f, MaxDistance(gridSize));
            }
        }

        return 3.0f;
    }

    public float MaxDistance(int gridSize) => Math.Max(260f, gridSize * 4f);

    public void PanSceneByScreenDelta(float dx, float dy, int gridSize, bool packedFullGridMode, int packedFullGridSize)
    {
        var viewInv = Matrix.Invert(GetViewMatrix());
        var right = Vector3.Normalize(new Vector3(viewInv.M11, viewInv.M12, viewInv.M13));
        var up = Vector3.Normalize(new Vector3(viewInv.M21, viewInv.M22, viewInv.M23));
        float scale = Math.Max(0.05f, Distance / 150f);
        var move = right * (dx * scale) + up * (-dy * scale);
        WorkspaceOffset += move;
        if (packedFullGridMode)
            Distance = Math.Clamp(Distance, MinimumCameraDistanceOutsideModel(gridSize, true, packedFullGridSize), MaxDistance(gridSize));
    }

    private static bool ContainsStrict(BoundingBox box, Vector3 p)
        => p.X > box.Min.X && p.X < box.Max.X &&
           p.Y > box.Min.Y && p.Y < box.Max.Y &&
           p.Z > box.Min.Z && p.Z < box.Max.Z;

    private static float DistanceToExitBox(Vector3 origin, Vector3 direction, BoundingBox box)
    {
        float best = float.PositiveInfinity;
        const float eps = 0.000001f;

        static void TestPlane(float plane, float originComponent, float dirComponent, ref float best)
        {
            if (MathF.Abs(dirComponent) < eps) return;
            float t = (plane - originComponent) / dirComponent;
            if (t > 0f && t < best) best = t;
        }

        TestPlane(box.Min.X, origin.X, direction.X, ref best);
        TestPlane(box.Max.X, origin.X, direction.X, ref best);
        TestPlane(box.Min.Y, origin.Y, direction.Y, ref best);
        TestPlane(box.Max.Y, origin.Y, direction.Y, ref best);
        TestPlane(box.Min.Z, origin.Z, direction.Z, ref best);
        TestPlane(box.Max.Z, origin.Z, direction.Z, ref best);

        return float.IsFinite(best) ? best : 0f;
    }
}
