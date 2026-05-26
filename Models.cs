using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Microsoft.Xna.Framework;

namespace IsoBlockCharacterEditor;



public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IList<T>? items)
    {
        CheckReentrancy();

        Items.Clear();
        if (items is not null)
        {
            for (int i = 0; i < items.Count; i++)
                Items.Add(items[i]);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

public enum BlockShape
{
    FullCube = 0,
    Slope50 = 1,
    DiagonalSlope50 = 2,
    DiagonalSlopeInward = 3,
    CornerCut50 = 4
}

public enum AttachDirection
{
    Up,
    Down,
    LeftX,
    RightX,
    BackZ,
    FrontZ
}

public enum EditorMode
{
    Build,
    Select
}

public enum BuildGridPlane
{
    FloorXZ,
    LeftSideYZ,
    RightSideXY
}

public sealed class BlockPiece
{
    private string _colorHex = "#4B88D8";
    private int _x;
    private int _y;
    private int _z;

    public Guid Id { get; set; } = Guid.NewGuid();

    public int X
    {
        get => _x;
        set { _x = value; UpdateCellKey(); }
    }

    public int Y
    {
        get => _y;
        set { _y = value; UpdateCellKey(); }
    }

    public int Z
    {
        get => _z;
        set { _z = value; UpdateCellKey(); }
    }

    [JsonIgnore]
    public int CellKey { get; private set; }

    public BlockShape Shape { get; set; }

    public string ColorHex
    {
        get => _colorHex;
        set
        {
            _colorHex = ColorUtil.NormalizeHex(value);
            PackedColor = ColorUtil.PackColor(_colorHex);
        }
    }

    [JsonIgnore]
    public int PackedColor { get; private set; } = ColorUtil.PackColor("#4B88D8");

    public int RotationY { get; set; }
    public bool FlipHorizontal { get; set; }
    public bool FlipVertical { get; set; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void UpdateCellKey() => CellKey = PackCellKey(_x, _y, _z);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int PackCellKey(int x, int y, int z) => CoordinateHelper.CellKey(x, y, z);

    [JsonIgnore]
    public string Display => $"{Shape}  ({X},{Y},{Z})  R{RotationY}" + (FlipHorizontal ? " FlipH" : "") + (FlipVertical ? " FlipV" : "");

    public override string ToString() => Display;
}


public readonly struct BlockData
{
    // Compact worker/render representation. Position is packed as 8 bits per axis.
    // This avoids cloning full BlockPiece objects, Guid and ColorHex strings during mesh builds.
    public readonly int CellKey;
    public readonly int PackedColor;
    public readonly ushort ShapeFlags;

    public BlockData(int x, int y, int z, BlockShape shape, int packedColor, int rotationY, bool flipHorizontal, bool flipVertical)
    {
        CellKey = CoordinateHelper.CellKey(x, y, z);
        PackedColor = packedColor;

        int rotationQuarter = ((rotationY % 360) + 360) % 360 / 90;
        ShapeFlags = (ushort)(((int)shape & 0xFF) | ((rotationQuarter & 0x03) << 8) | (flipHorizontal ? 1 << 10 : 0) | (flipVertical ? 1 << 11 : 0));
    }

    public static BlockData FromPiece(BlockPiece p) => new(p.X, p.Y, p.Z, p.Shape, p.PackedColor, p.RotationY, p.FlipHorizontal, p.FlipVertical);


    public int X => CoordinateHelper.CellX(CellKey);
    public int Y => CoordinateHelper.CellY(CellKey);
    public int Z => CoordinateHelper.CellZ(CellKey);
    public BlockShape Shape => (BlockShape)(ShapeFlags & 0xFF);
    public int RotationY => ((ShapeFlags >> 8) & 0x03) * 90;
    public bool FlipHorizontal => (ShapeFlags & (1 << 10)) != 0;
    public bool FlipVertical => (ShapeFlags & (1 << 11)) != 0;
}

public sealed class SavedScene
{
    public int GridSize { get; set; } = 256;

    // Normal editor scenes store all visible/editor-created blocks here.
    // PackedFullGrid scenes use this as a sparse overlay only; the dense 256^3
    // base is represented by IsPackedFullGrid instead of 16.7M serialized objects.
    public List<BlockPiece> Pieces { get; set; } = new();

    public bool IsPackedFullGrid { get; set; }
    public int PackedFullGridSize { get; set; } = 256;
    public List<int> PackedDeletedCellKeys { get; set; } = new();
}

public sealed class BlockDefinition
{
    public BlockShape Shape { get; }
    public string Name { get; }
    public string Description { get; }

    // Shape-specific canonical orientation used only by Block Shape Library.
    // This does not change how blocks are placed, saved, loaded, rotated, or meshed
    // in the editor. It only makes asymmetric shapes face the user more clearly
    // in the preview window.
    public int LibraryPreviewRotationY { get; }

    public BlockDefinition(BlockShape shape, string name, string description, int libraryPreviewRotationY = 0)
    {
        Shape = shape;
        Name = name;
        Description = description;
        LibraryPreviewRotationY = NormalizeRotationY(libraryPreviewRotationY);
    }

    public override string ToString() => Name;

    private static int NormalizeRotationY(int degrees)
    {
        int normalized = ((degrees % 360) + 360) % 360;
        return normalized switch
        {
            0 or 90 or 180 or 270 => normalized,
            _ => 0
        };
    }
}

public static class BlockCatalog
{
    public static readonly BlockDefinition[] Definitions =
    [
        new(BlockShape.FullCube, "Full cube", "One full 1x1x1 isometric construction block."),

        // Library preview: rotate the simple slope so its ramp profile is visible
        // from the viewer-facing side instead of reading mostly as a rear-facing plane.
        new(BlockShape.Slope50, "Slope 50%", "One 50% ramp block. Rotate and flip to create every direction.", libraryPreviewRotationY: 90),

        // DiagonalSlope50 is rotated for library preview so its raised diagonal corner
        // faces the viewer instead of reading as a rear-facing diagonal.
        // DiagonalSlopeInward keeps its canonical orientation.
        new(BlockShape.DiagonalSlope50, "Diagonal slope 50%", "A raised-corner diagonal slope block.", libraryPreviewRotationY: 180),
        new(BlockShape.DiagonalSlopeInward, "Diagonal slope inward", "The older inward diagonal slope style.", libraryPreviewRotationY: 0),

        new(BlockShape.CornerCut50, "Corner cut 50%", "A vertical 45-degree corner cut block."),
    ];
}

public sealed class SpatialGridIndex
{
    // Sparse O(1) occupancy map. This avoids allocating a full 256^3 reference array
    // for mostly empty editor scenes and keeps placement/support tests fast.
    private readonly Dictionary<int, BlockPiece> _cells = new(4096);
    private int _size;

    public SpatialGridIndex(int size)
    {
        _size = Math.Clamp(size, 1, 256);
    }

    public int Size => _size;

    public void Resize(int size)
    {
        size = Math.Clamp(size, 1, 256);
        _size = size;
        Clear();
    }

    public void Clear() => _cells.Clear();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsInside(int x, int y, int z)
        => x >= 0 && x < _size && y >= 0 && y < _size && z >= 0 && z < _size;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BlockPiece? GetAt(int x, int y, int z)
    {
        if (!IsInside(x, y, z)) return null;
        _cells.TryGetValue(CoordinateHelper.CellKey(x, y, z), out var piece);
        return piece;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsOccupied(int x, int y, int z) => GetAt(x, y, z) is not null;

    public bool TryAdd(BlockPiece piece)
    {
        if (!IsInside(piece.X, piece.Y, piece.Z)) return false;
        return _cells.TryAdd(piece.CellKey, piece);
    }

    public void Remove(BlockPiece piece)
    {
        if (!IsInside(piece.X, piece.Y, piece.Z)) return;
        int key = piece.CellKey;
        if (_cells.TryGetValue(key, out var existing) && ReferenceEquals(existing, piece))
            _cells.Remove(key);
    }

}

public static class ColorUtil
{
    public static string NormalizeHex(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "#4B88D8";
        var text = value.Trim();
        if (!text.StartsWith('#')) text = "#" + text;
        if (text.Length != 7) return "#4B88D8";
        return text.ToUpperInvariant();
    }

    public static int PackColor(string value)
    {
        string text = NormalizeHex(value);
        if (!TryReadHexByte(text, 1, out byte r) ||
            !TryReadHexByte(text, 3, out byte g) ||
            !TryReadHexByte(text, 5, out byte b))
        {
            return (255 << 24) | (75 << 16) | (136 << 8) | 216;
        }

        return (255 << 24) | (r << 16) | (g << 8) | b;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryReadHexByte(string text, int offset, out byte value)
    {
        int hi = HexValue(text[offset]);
        int lo = HexValue(text[offset + 1]);
        if ((hi | lo) < 0)
        {
            value = 0;
            return false;
        }

        value = (byte)((hi << 4) | lo);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int HexValue(char c)
    {
        if ((uint)(c - '0') <= 9) return c - '0';
        c = char.ToUpperInvariant(c);
        if ((uint)(c - 'A') <= 5) return c - 'A' + 10;
        return -1;
    }

    public static Color ToXnaColor(int packedColor)
    {
        return new Color(
            (byte)((packedColor >> 16) & 0xFF),
            (byte)((packedColor >> 8) & 0xFF),
            (byte)(packedColor & 0xFF),
            (byte)((packedColor >> 24) & 0xFF));
    }

    public static Color ToXnaColor(string value, Color fallback)
    {
        return ToXnaColor(PackColor(value));
    }

    public static System.Windows.Media.Color ToWpfColor(string value, System.Windows.Media.Color fallback)
    {
        try
        {
            return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(NormalizeHex(value))!;
        }
        catch
        {
            return fallback;
        }
    }
}
