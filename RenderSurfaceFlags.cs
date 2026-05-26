namespace IsoBlockCharacterEditor;

/// <summary>
/// Conservative outward-surface bit flags used by CPU mesh builders and draw-time camera filtering.
/// Keeping them in one place prevents sparse and procedural chunk paths from drifting apart.
/// </summary>
internal static class RenderSurfaceFlags
{
    public const byte SurfacePosX = 1 << 0;
    public const byte SurfaceNegX = 1 << 1;
    public const byte SurfacePosY = 1 << 2;
    public const byte SurfaceNegY = 1 << 3;
    public const byte SurfacePosZ = 1 << 4;
    public const byte SurfaceNegZ = 1 << 5;
    public const byte SurfaceAll = SurfacePosX | SurfaceNegX | SurfacePosY | SurfaceNegY | SurfacePosZ | SurfaceNegZ;
}
