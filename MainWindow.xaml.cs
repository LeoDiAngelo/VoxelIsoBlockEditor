using Microsoft.Win32;
using System.Buffers;
using System.Collections;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace IsoBlockCharacterEditor;

public partial class MainWindow : Window
{
    private readonly BulkObservableCollection<BlockPiece> _pieces = new();
    private readonly HashSet<BlockPiece> _selection = new();
    private readonly SpatialGridIndex _spatialGrid = new(256); // Legacy UI/pick compatibility. VoxelWorld is authoritative.
    private readonly VoxelWorld _world = new(256);
    private readonly Dictionary<Guid, BlockPiece> _pieceById = new(4096);
    private readonly Stack<IEditorCommand> _undoStack = new();
    private readonly Stack<IEditorCommand> _redoStack = new();
    private readonly List<BlockPiece> _clipboard = new();
    private (int X, int Y, int Z) _clipboardOrigin;
    private BlockPiece? _selected;
    private bool _ready;
    private bool _isRestoringHistory;
    private int _gridSize = 256;
    private readonly DispatcherTimer _statsRefreshTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly StringBuilder _viewportInfoBuilder = new(512);
    private long _operationManagedLiveBaselineBytes = GC.GetTotalMemory(false);
    private long _operationPrivateBaselineBytes;
    private long _operationWorkingSetBaselineBytes;
    private int _operationGc0Baseline = GC.CollectionCount(0);
    private int _operationGc1Baseline = GC.CollectionCount(1);
    private int _operationGc2Baseline = GC.CollectionCount(2);
    private bool _compositionRenderingAttached;
    private bool _stressSceneSwitchInProgress;
    private readonly Stopwatch _compositionThrottleClock = Stopwatch.StartNew();
    private long _lastCompositionInvalidateTicks;
    private int GridMax => _gridSize - 1;


    public MainWindow()
    {
        InitializeComponent();
        ResetPerformanceBaseline();
        _statsRefreshTimer.Tick += (_, _) => RefreshEditorState();
        Activated += (_, _) =>
        {
            _statsRefreshTimer.Interval = TimeSpan.FromMilliseconds(500);
            EditorViewport?.InvalidateVisual();
        };
        Deactivated += (_, _) =>
        {
            _statsRefreshTimer.Interval = TimeSpan.FromMilliseconds(1000);
        };
        Closed += (_, _) =>
        {
            _statsRefreshTimer.Stop();
            DetachContinuousViewportRendering();
        };
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ShapeCombo.ItemsSource = BlockCatalog.Definitions;
        ShapeCombo.SelectedValue = BlockShape.FullCube;
        PiecesList.ItemsSource = _pieces;

        EditorViewport.AttachData(_pieces, _selection, _world);
        EditorViewport.AddBlockRequested += AddPiece;
        EditorViewport.SelectBlockRequested += SelectPiece;
        EditorViewport.ContextBlockRequested += ShowBlockContextMenu;
        ApplyViewportOptions();

        _ready = true;
        _statsRefreshTimer.Start();
        AttachContinuousViewportRendering();
        AddSampleCharacter();
        EditorViewport.InvalidateSceneGeometry();
        RefreshEditorState();
        EditorViewport.CenterOnModel();
    }


    private void AttachContinuousViewportRendering()
    {
        if (_compositionRenderingAttached)
            return;

        CompositionTarget.Rendering += OnCompositionRendering;
        _compositionRenderingAttached = true;
    }

    private void DetachContinuousViewportRendering()
    {
        if (!_compositionRenderingAttached)
            return;

        CompositionTarget.Rendering -= OnCompositionRendering;
        _compositionRenderingAttached = false;
    }

    private void OnCompositionRendering(object? sender, EventArgs e)
    {
        if (!_ready || !IsVisible || WindowState == WindowState.Minimized)
            return;

        // WPF/MonoGame interop is composition-driven. While the app is active we
        // allow one viewport invalidation per compositor tick. When the window is
        // inactive we throttle hard to avoid burning CPU/GPU in the background.
        long now = _compositionThrottleClock.ElapsedTicks;
        if (!IsActive)
        {
            long minTicks = Stopwatch.Frequency / 5; // 5 FPS background cap.
            if (now - _lastCompositionInvalidateTicks < minTicks)
                return;
        }

        _lastCompositionInvalidateTicks = now;
        EditorViewport.InvalidateVisual();
    }

    private static BlockPiece ClonePiece(BlockPiece p) => new()
    {
        Id = p.Id,
        X = p.X,
        Y = p.Y,
        Z = p.Z,
        Shape = p.Shape,
        ColorHex = p.ColorHex,
        RotationY = p.RotationY,
        FlipHorizontal = p.FlipHorizontal,
        FlipVertical = p.FlipVertical
    };

    private interface IEditorCommand
    {
        void Execute();
        void Undo();
    }

    private void ExecuteCommand(IEditorCommand command)
    {
        if (_isRestoringHistory) return;

        MarkCommandGeometryDirty(command);
        command.Execute();
        MarkCommandGeometryDirty(command);
        _undoStack.Push(command);
        _redoStack.Clear();

        TrimUndoHistory();
        RefreshEditorState();
    }

    private void Undo()
    {
        if (_undoStack.Count == 0) return;
        _isRestoringHistory = true;
        try
        {
            var command = _undoStack.Pop();
            MarkCommandGeometryDirty(command);
            command.Undo();
            MarkCommandGeometryDirty(command);
            _redoStack.Push(command);
            RefreshEditorState();
        }
        finally { _isRestoringHistory = false; }
    }

    private void Redo()
    {
        if (_redoStack.Count == 0) return;
        _isRestoringHistory = true;
        try
        {
            var command = _redoStack.Pop();
            MarkCommandGeometryDirty(command);
            command.Execute();
            MarkCommandGeometryDirty(command);
            _undoStack.Push(command);
            RefreshEditorState();
        }
        finally { _isRestoringHistory = false; }
    }



    private void MarkCommandGeometryDirty(IEditorCommand command)
    {
        switch (command)
        {
            case AddBlocksCommand add:
                MarkPiecesGeometryDirty(add._pieces);
                break;
            case DeleteBlocksCommand delete:
                MarkPiecesGeometryDirty(delete._pieces);
                break;
            case MoveBlocksCommand move:
                for (int i = 0; i < move._ids.Length; i++)
                {
                    var piece = FindPiece(move._ids[i]);
                    if (piece is not null)
                        EditorViewport.InvalidateSceneGeometryForCell(piece.X, piece.Y, piece.Z);
                }
                break;
            case PaintBlocksCommand paint:
                MarkIdsPaintDirty(paint._ids);
                break;
            case TransformBlocksCommand transform:
                MarkIdsGeometryDirty(transform._ids);
                break;
            default:
                EditorViewport.InvalidateSceneGeometry();
                break;
        }
    }

    private void MarkIdsGeometryDirty(Guid[] ids)
    {
        var pieces = new List<BlockPiece>(ids.Length);
        for (int i = 0; i < ids.Length; i++)
        {
            var piece = FindPiece(ids[i]);
            if (piece is not null)
                pieces.Add(piece);
        }
        if (pieces.Count > 0)
            EditorViewport.InvalidateSceneGeometryForPieces(pieces);
    }

    private void MarkIdsPaintDirty(Guid[] ids)
    {
        var pieces = new List<BlockPiece>(ids.Length);
        for (int i = 0; i < ids.Length; i++)
        {
            var piece = FindPiece(ids[i]);
            if (piece is not null)
                pieces.Add(piece);
        }
        if (pieces.Count > 0)
            EditorViewport.InvalidatePaintGeometryForPieces(pieces);
    }

    private void MarkPiecesGeometryDirty(IEnumerable<BlockPiece> pieces)
    {
        EditorViewport.InvalidateSceneGeometryForPieces(pieces);
    }

    private void TrimUndoHistory()
    {
        const int maxHistory = 2048;
        const int trimSlack = 256;

        // Avoid trimming on every command once the history limit is reached.
        // Stack<T> enumerates/pops newest-first; buffer[0] is the newest command.
        if (_undoStack.Count <= maxHistory + trimSlack) return;

        int count = _undoStack.Count;
        IEditorCommand[] buffer = ArrayPool<IEditorCommand>.Shared.Rent(count);
        try
        {
            int actualCount = 0;
            while (_undoStack.Count > 0)
                buffer[actualCount++] = _undoStack.Pop();

            int keep = Math.Min(maxHistory, actualCount);

            // Re-push from oldest kept to newest kept so the top of the stack
            // remains the newest command after trimming.
            for (int i = keep - 1; i >= 0; i--)
                _undoStack.Push(buffer[i]);

            for (int i = 0; i < actualCount; i++)
                buffer[i] = null!;
        }
        finally
        {
            ArrayPool<IEditorCommand>.Shared.Return(buffer, clearArray: false);
        }
    }

    private BlockPiece? FindPiece(Guid id)
    {
        _pieceById.TryGetValue(id, out var piece);
        return piece;
    }

    private void AddPieceInternal(BlockPiece piece)
    {
        if (_pieceById.ContainsKey(piece.Id)) return;
        if (EditorViewport.IsPackedFullGridMode)
            EditorViewport.ClearPackedFullGridDeletedCell(piece.X, piece.Y, piece.Z);
        _pieces.Add(piece);
        _pieceById[piece.Id] = piece;
        _world.SetPiece(piece);
        _spatialGrid.TryAdd(piece);
        VerifyCellStoresAgree(piece.X, piece.Y, piece.Z);
    }

    private void RemovePieceInternal(Guid id)
    {
        var piece = FindPiece(id);
        if (piece is null) return;

        int oldX = piece.X, oldY = piece.Y, oldZ = piece.Z;
        _world.RemovePiece(piece);
        _spatialGrid.Remove(piece);
        _pieceById.Remove(id);
        _pieces.Remove(piece);
        _selection.Remove(piece);
        if (ReferenceEquals(_selected, piece))
            _selected = LastSelected();
        VerifyCellStoresAgree(oldX, oldY, oldZ);
    }

    private void MovePiecesInternal(Guid[] ids, int dx, int dy, int dz)
    {
        if (ids.Length == 0 || (dx == 0 && dy == 0 && dz == 0))
            return;

        // Move as one transaction across the occupancy stores.
        // A multi-block selection is allowed to move into its own old cells
        // (for example a 1-cell shift, rotation-like manual edits, or swaps through undo/redo).
        // Moving piece-by-piece would temporarily leave the destination occupied by
        // another selected block and could desynchronise VoxelWorld/SpatialGridIndex.
        var moving = new List<BlockPiece>(ids.Length);
        for (int i = 0; i < ids.Length; i++)
        {
            BlockPiece? piece = FindPiece(ids[i]);
            if (piece is not null)
                moving.Add(piece);
        }

        if (moving.Count == 0)
            return;

        int count = moving.Count;
        int[] oldX = new int[count];
        int[] oldY = new int[count];
        int[] oldZ = new int[count];

        for (int i = 0; i < count; i++)
        {
            BlockPiece piece = moving[i];
            oldX[i] = piece.X;
            oldY[i] = piece.Y;
            oldZ[i] = piece.Z;

            _world.RemovePiece(piece);
            _spatialGrid.Remove(piece);
        }

        for (int i = 0; i < count; i++)
        {
            BlockPiece piece = moving[i];
            piece.X = oldX[i] + dx;
            piece.Y = oldY[i] + dy;
            piece.Z = oldZ[i] + dz;
        }

        for (int i = 0; i < count; i++)
        {
            BlockPiece piece = moving[i];
            _world.SetPiece(piece);
            bool addedToSpatialIndex = _spatialGrid.TryAdd(piece);
            Debug.Assert(addedToSpatialIndex,
                $"SpatialGridIndex rejected moved block at ({piece.X}, {piece.Y}, {piece.Z}). Move validation should have prevented this.");
        }

        for (int i = 0; i < count; i++)
        {
            BlockPiece piece = moving[i];
            VerifyCellStoresAgree(oldX[i], oldY[i], oldZ[i]);
            VerifyCellStoresAgree(piece.X, piece.Y, piece.Z);
        }
    }

    [Conditional("DEBUG")]
    private void VerifyCellStoresAgree(int x, int y, int z)
    {
        BlockPiece? worldPiece = _world.GetPieceAt(x, y, z);
        BlockPiece? spatialPiece = _spatialGrid.GetAt(x, y, z);
        Debug.Assert(ReferenceEquals(worldPiece, spatialPiece),
            $"VoxelWorld/SpatialGridIndex drift at ({x}, {y}, {z}).");
    }

    private sealed class AddBlocksCommand : IEditorCommand
    {
        private readonly MainWindow _owner;
        public readonly List<BlockPiece> _pieces;

        public AddBlocksCommand(MainWindow owner, IEnumerable<BlockPiece> pieces)
        {
            _owner = owner;
            _pieces = new List<BlockPiece>();
            foreach (var p in pieces) _pieces.Add(ClonePiece(p));
        }

        public void Execute()
        {
            for (int i = 0; i < _pieces.Count; i++)
                _owner.AddPieceInternal(ClonePiece(_pieces[i]));
        }

        public void Undo()
        {
            for (int i = 0; i < _pieces.Count; i++)
                _owner.RemovePieceInternal(_pieces[i].Id);
        }
    }

    private sealed class DeleteBlocksCommand : IEditorCommand
    {
        private readonly MainWindow _owner;
        public readonly List<BlockPiece> _pieces;

        public DeleteBlocksCommand(MainWindow owner, IEnumerable<BlockPiece> pieces)
        {
            _owner = owner;
            _pieces = new List<BlockPiece>();
            foreach (var p in pieces) _pieces.Add(ClonePiece(p));
        }

        public void Execute()
        {
            if (_owner.EditorViewport.IsPackedFullGridMode)
                _owner.EditorViewport.MarkPackedFullGridCellsDeleted(_pieces);

            for (int i = 0; i < _pieces.Count; i++)
                _owner.RemovePieceInternal(_pieces[i].Id);
        }

        public void Undo()
        {
            for (int i = 0; i < _pieces.Count; i++)
                _owner.AddPieceInternal(ClonePiece(_pieces[i]));
        }
    }

    private sealed class MoveBlocksCommand : IEditorCommand
    {
        private readonly MainWindow _owner;
        public readonly Guid[] _ids;
        private readonly int _dx;
        private readonly int _dy;
        private readonly int _dz;

        public MoveBlocksCommand(MainWindow owner, IEnumerable<BlockPiece> pieces, int dx, int dy, int dz)
        {
            _owner = owner;
            var ids = new List<Guid>();
            foreach (var p in pieces) ids.Add(p.Id);
            _ids = ids.ToArray();
            _dx = dx; _dy = dy; _dz = dz;
        }

        public void Execute() => Move(_dx, _dy, _dz);
        public void Undo() => Move(-_dx, -_dy, -_dz);

        private void Move(int dx, int dy, int dz)
        {
            _owner.MovePiecesInternal(_ids, dx, dy, dz);
        }
    }

    private sealed class PaintBlocksCommand : IEditorCommand
    {
        private readonly MainWindow _owner;
        public readonly Guid[] _ids;
        private readonly string[] _oldColors;
        private readonly string _newColor;

        public PaintBlocksCommand(MainWindow owner, IEnumerable<BlockPiece> pieces, string newColor)
        {
            _owner = owner;
            var ids = new List<Guid>();
            var old = new List<string>();
            foreach (var p in pieces)
            {
                ids.Add(p.Id);
                old.Add(p.ColorHex);
            }
            _ids = ids.ToArray();
            _oldColors = old.ToArray();
            _newColor = ColorUtil.NormalizeHex(newColor);
        }

        public void Execute()
        {
            for (int i = 0; i < _ids.Length; i++)
            {
                var piece = _owner.FindPiece(_ids[i]);
                if (piece is not null) { piece.ColorHex = _newColor; _owner._world.UpdatePiece(piece); }
            }
        }

        public void Undo()
        {
            for (int i = 0; i < _ids.Length; i++)
            {
                var piece = _owner.FindPiece(_ids[i]);
                if (piece is not null) { piece.ColorHex = _oldColors[i]; _owner._world.UpdatePiece(piece); }
            }
        }
    }



    private sealed class TransformBlocksCommand : IEditorCommand
    {
        private readonly MainWindow _owner;
        public readonly Guid[] _ids;
        private readonly int[] _oldRotations;
        private readonly bool[] _oldFlipH;
        private readonly bool[] _oldFlipV;
        private readonly int[] _newRotations;
        private readonly bool[] _newFlipH;
        private readonly bool[] _newFlipV;

        public TransformBlocksCommand(MainWindow owner, IEnumerable<BlockPiece> pieces, Func<BlockPiece, (int Rotation, bool FlipH, bool FlipV)> transform)
        {
            _owner = owner;
            var src = new List<BlockPiece>();
            foreach (var p in pieces) src.Add(p);
            int count = src.Count;
            _ids = new Guid[count];
            _oldRotations = new int[count];
            _oldFlipH = new bool[count];
            _oldFlipV = new bool[count];
            _newRotations = new int[count];
            _newFlipH = new bool[count];
            _newFlipV = new bool[count];
            for (int i = 0; i < count; i++)
            {
                var p = src[i];
                _ids[i] = p.Id;
                _oldRotations[i] = p.RotationY;
                _oldFlipH[i] = p.FlipHorizontal;
                _oldFlipV[i] = p.FlipVertical;
                var t = transform(p);
                _newRotations[i] = t.Rotation;
                _newFlipH[i] = t.FlipH;
                _newFlipV[i] = t.FlipV;
            }
        }

        public void Execute() => Apply(_newRotations, _newFlipH, _newFlipV);
        public void Undo() => Apply(_oldRotations, _oldFlipH, _oldFlipV);

        private void Apply(int[] rotations, bool[] flipH, bool[] flipV)
        {
            for (int i = 0; i < _ids.Length; i++)
            {
                var p = _owner.FindPiece(_ids[i]);
                if (p is null) continue;
                p.RotationY = rotations[i];
                p.FlipHorizontal = flipH[i];
                p.FlipVertical = flipV[i];
                _owner._world.UpdatePiece(p);
            }
        }
    }

    private void ResetPerformanceBaseline()
    {
        _operationManagedLiveBaselineBytes = GC.GetTotalMemory(false);
        using var process = Process.GetCurrentProcess();
        _operationPrivateBaselineBytes = process.PrivateMemorySize64;
        _operationWorkingSetBaselineBytes = process.WorkingSet64;
        _operationGc0Baseline = GC.CollectionCount(0);
        _operationGc1Baseline = GC.CollectionCount(1);
        _operationGc2Baseline = GC.CollectionCount(2);
    }

    private static string FormatSignedMb(long bytes)
    {
        long mb = bytes / 1024 / 1024;
        return mb >= 0 ? $"+{mb}" : mb.ToString(CultureInfo.InvariantCulture);
    }

    private static long ToMb(long bytes) => bytes / 1024 / 1024;

    private void RefreshEditorState()
    {
        if (!_ready) return;
        var rendererStats = EditorViewport.GetRenderStats();
        int displayBlockCount = Math.Max(_pieces.Count, rendererStats.Blocks);
        int triangleCount = rendererStats.Triangles;
        int vertexCount = rendererStats.Vertices;
        int drawVertexCount = rendererStats.DrawVertices;
        int quadEstimate = rendererStats.Quads;
        StatsText.Text = $"{displayBlockCount} blocks / {triangleCount} triangles\n{vertexCount} vertices / ~{quadEstimate} quads";
        if (ViewportInfoText is not null)
        {
            long managedLive = GC.GetTotalMemory(false);
            using var process = Process.GetCurrentProcess();
            long privateBytes = process.PrivateMemorySize64;
            long workingSetBytes = process.WorkingSet64;
            int gc0 = GC.CollectionCount(0);
            int gc1 = GC.CollectionCount(1);
            int gc2 = GC.CollectionCount(2);
            string buildType = rendererStats.FullRebuild ? "Full" : "Partial";
            var sb = _viewportInfoBuilder;
            sb.Clear();
            sb.Append("Blocks: ").Append(displayBlockCount)
              .Append("\nChunks: ").Append(rendererStats.Chunks).Append(" visible ").Append(rendererStats.VisibleChunks)
              .Append("\nBuild: ").Append(buildType).Append(", dirty ").Append(rendererStats.DirtyChunks).Append(", rebuilt ").Append(rendererStats.RebuiltChunks)
              .Append("\nDraw calls: ").Append(rendererStats.DrawCalls)
              .Append("\nVertices: ").Append(vertexCount)
              .Append("\nDraw vertices: ").Append(drawVertexCount)
              .Append("\nTriangles: ").Append(triangleCount)
              .Append("\nQuads est.: ").Append(quadEstimate)
              .Append("\nMesh build: ").Append(rendererStats.LastBuildMilliseconds.ToString("F2", CultureInfo.InvariantCulture)).Append(" ms")
              .Append("\nFrame: ").Append(EditorViewport.LastFrameMilliseconds.ToString("F2", CultureInfo.InvariantCulture)).Append(" ms")
              .Append("\nFPS: ").Append(EditorViewport.ActualFps.ToString("F1", CultureInfo.InvariantCulture))
              .Append("\nPicking: ").Append(EditorViewport.LastPickingMilliseconds.ToString("F3", CultureInfo.InvariantCulture)).Append(" ms")
              .Append("\nGC total: ").Append(gc0).Append('/').Append(gc1).Append('/').Append(gc2)
              .Append("  Δ ").Append(gc0 - _operationGc0Baseline).Append('/').Append(gc1 - _operationGc1Baseline).Append('/').Append(gc2 - _operationGc2Baseline)
              .Append("\nManaged live: ").Append(ToMb(managedLive)).Append(" MB  Δ ").Append(FormatSignedMb(managedLive - _operationManagedLiveBaselineBytes)).Append(" MB")
              .Append("\nPrivate: ").Append(ToMb(privateBytes)).Append(" MB  Δ ").Append(FormatSignedMb(privateBytes - _operationPrivateBaselineBytes)).Append(" MB")
              .Append("\nWorking set: ").Append(ToMb(workingSetBytes)).Append(" MB  Δ ").Append(FormatSignedMb(workingSetBytes - _operationWorkingSetBaselineBytes)).Append(" MB")
              .Append("\nGrid: ").Append(_gridSize).Append(" × ").Append(_gridSize).Append(" × ").Append(_gridSize)
              .Append("\nSelected: ").Append(_selection.Count);
            ViewportInfoText.Text = sb.ToString();
        }
        SelectionText.Text = _selection.Count == 0
            ? "None"
            : _selection.Count == 1 && _selected is not null
                ? _selected.Shape.ToString()
                : $"{_selection.Count} blocks selected";
        // The hidden list must not be refreshed for 100k/250k stress scenes.
        // Refreshing a WPF ItemsControl over a huge ObservableCollection is pure UI work
        // and can dominate memory/CPU even though the list is collapsed.
        if (PiecesList.Visibility == Visibility.Visible && _pieces.Count <= 5000)
            PiecesList.Items.Refresh();
        EditorViewport.InvalidateUiOnly();
    }


    private void ReplacePiecesBulk(IList<BlockPiece>? pieces)
    {
        EditorViewport.BeginBulkSceneUpdate();
        IEnumerable? oldItemsSource = PiecesList.ItemsSource;
        bool detachList = pieces is not null && pieces.Count > 5000;
        if (detachList)
            PiecesList.ItemsSource = null;

        try
        {
            _pieces.ReplaceAll(pieces);
            RebuildSpatialIndex();
        }
        finally
        {
            if (detachList)
                PiecesList.ItemsSource = oldItemsSource ?? _pieces;
            EditorViewport.EndBulkSceneUpdate(invalidateGeometry: false);
        }
    }

    private void RebuildSpatialIndex()
    {
        _spatialGrid.Resize(_gridSize);
        _world.Resize(_gridSize);
        _pieceById.Clear();
        for (int i = 0; i < _pieces.Count; i++)
        {
            BlockPiece p = _pieces[i];
            _spatialGrid.TryAdd(p);
            _world.SetPiece(p);
            _pieceById[p.Id] = p;
        }
    }

    private void ApplyViewportOptions()
    {
        if (!_ready && EditorViewport is null) return;
        EditorViewport.GridSize = _gridSize;
        EditorViewport.ShowEdges = ShowEdgesCheck?.IsChecked == true;
        EditorViewport.ShowFloorGrid = ShowFloorGridCheck?.IsChecked == true;
        EditorViewport.ShowLeftGrid = ShowBackGridCheck?.IsChecked == true;
        EditorViewport.ShowRightGrid = ShowSideGridCheck?.IsChecked == true;
        EditorViewport.EditorMode = BuildModeCheck?.IsChecked == true ? EditorMode.Build : EditorMode.Select;
        if (ViewportInfoOverlay is not null)
            ViewportInfoOverlay.Visibility = ShowInfoCheck?.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        EditorViewport.InvalidateOverlay();
    }

    private bool TryHandleShortcuts(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            if (key == Key.Z) { Undo(); return true; }
            if (key == Key.Y) { Redo(); return true; }
            if (key == Key.C) { CopySelectedToInternalClipboard(); return true; }
            if (key == Key.V) { PasteInternalClipboard(); return true; }
        }

        return false;
    }

    private static bool IsWorkspaceArrowKey(Key key) => key is Key.Left or Key.Right or Key.Up or Key.Down;

    private bool TryUpdateWorkspaceArrowKeyState(KeyEventArgs e, bool isPressed)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (!IsWorkspaceArrowKey(key))
            return false;

        EditorViewport.SetWorkspaceArrowKeyState(key, isPressed, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        return true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (TryHandleShortcuts(e))
        {
            e.Handled = true;
            return;
        }

        if (TryUpdateWorkspaceArrowKeyState(e, true))
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Delete)
        {
            DeleteSelected_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (_selection.Count == 0) return;
        int dx = 0, dy = 0, dz = 0;
        if (e.Key == Key.A) dx = -1;
        else if (e.Key == Key.D) dx = 1;
        else if (e.Key == Key.W) dz = -1;
        else if (e.Key == Key.S) dz = 1;
        else if (e.Key == Key.E) dy = 1;
        else if (e.Key == Key.Q) dy = -1;
        else return;
        MoveSelected(dx, dy, dz);
        e.Handled = true;
    }

    private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (TryUpdateWorkspaceArrowKeyState(e, false))
        {
            e.Handled = true;
        }
    }

    private void Mode_Checked(object sender, RoutedEventArgs e) => ApplyViewportOptions();
    private void ShapeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyViewportOptions();
    private void GridOption_Click(object sender, RoutedEventArgs e) => ApplyViewportOptions();

    private bool IsInsideGrid(int x, int y, int z) => _world.IsInside(x, y, z);
    private bool IsOccupied(int x, int y, int z)
    {
        if (EditorViewport.IsPackedFullGridMode && EditorViewport.IsPackedFullGridCellOccupied(x, y, z))
            return true;
        return _world.IsOccupied(x, y, z);
    }

    private bool IsSupportedPosition(int x, int y, int z, bool supportedByConstructionPlane)
    {
        if (!IsInsideGrid(x, y, z)) return false;
        if (y == 0) return true;
        if (supportedByConstructionPlane) return true;
        return IsOccupied(x, y - 1, z) ||
               IsOccupied(x, y + 1, z) ||
               IsOccupied(x - 1, y, z) ||
               IsOccupied(x + 1, y, z) ||
               IsOccupied(x, y, z - 1) ||
               IsOccupied(x, y, z + 1);
    }

    private void AddPiece(int x, int y, int z, bool supportedByConstructionPlane)
    {
        if (!IsInsideGrid(x, y, z))
        {
            MessageBox.Show($"Block is outside the {_gridSize} x {_gridSize} x {_gridSize} build grid.", "Outside grid", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (IsOccupied(x, y, z))
        {
            MessageBox.Show("That grid cell is already occupied.", "Occupied", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!IsSupportedPosition(x, y, z, supportedByConstructionPlane))
        {
            MessageBox.Show("Blocks cannot float in the air. Place the block on the floor, attach it to a wall grid, or attach it to another block.", "Needs support", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var piece = new BlockPiece
        {
            X = x,
            Y = y,
            Z = z,
            Shape = ShapeCombo.SelectedValue is BlockShape shape ? shape : BlockShape.FullCube,
            ColorHex = ColorUtil.NormalizeHex(ColorText.Text),
            RotationY = 0
        };
        ExecuteCommand(new AddBlocksCommand(this, new[] { piece }));
        var added = FindPiece(piece.Id);
        SelectPiece(added, false);
    }

    private BlockPiece? LastSelected()
    {
        BlockPiece? last = null;
        foreach (var p in _selection)
            last = p;
        return last;
    }

    private List<BlockPiece> GetSelectionSnapshot()
    {
        var list = new List<BlockPiece>(_selection.Count);
        foreach (var p in _selection)
            list.Add(p);
        return list;
    }

    private void FillSelectionCellKeys(HashSet<int> target, int dx = 0, int dy = 0, int dz = 0)
    {
        target.Clear();
        if (dx == 0 && dy == 0 && dz == 0)
        {
            foreach (var p in _selection)
                target.Add(p.CellKey);
            return;
        }

        foreach (var p in _selection)
        {
            if (CoordinateHelper.TryCellKey(p.X + dx, p.Y + dy, p.Z + dz, _gridSize, out int key))
                target.Add(key);
        }
    }

    private (int X, int Y, int Z) GetSelectionMin()
    {
        int minX = int.MaxValue, minY = int.MaxValue, minZ = int.MaxValue;
        foreach (var p in _selection)
        {
            if (p.X < minX) minX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.Z < minZ) minZ = p.Z;
        }
        return minX == int.MaxValue ? (0, 0, 0) : (minX, minY, minZ);
    }

    private void SelectPiece(BlockPiece? piece, bool append)
    {
        if (!append) _selection.Clear();
        if (piece is null)
        {
            _selected = null;
            PiecesList.SelectedItem = null;
            RefreshEditorState();
            return;
        }

        // In packed 256³ mode, a selected surface cell must stay virtual until
        // the user actually edits it. Materializing it on simple selection would
        // draw a sparse overlay block and visually recolor the selected voxel.

        if (append && _selection.Contains(piece)) _selection.Remove(piece); else _selection.Add(piece);
        _selected = LastSelected();
        PiecesList.SelectedItem = _selected;
        if (_selected is not null)
        {
            XText.Text = _selected.X.ToString(CultureInfo.InvariantCulture);
            YText.Text = _selected.Y.ToString(CultureInfo.InvariantCulture);
            ZText.Text = _selected.Z.ToString(CultureInfo.InvariantCulture);
            ColorText.Text = _selected.ColorHex;
            ColorPreview.Background = new SolidColorBrush(ColorUtil.ToWpfColor(_selected.ColorHex, Colors.SteelBlue));
        }
        RefreshEditorState();
    }

    private void MoveSelected(int dx, int dy, int dz)
    {
        if (_selection.Count == 0) return;
        var selectedKeys = new HashSet<int>(_selection.Count);
        var movedKeys = new HashSet<int>(_selection.Count);
        FillSelectionCellKeys(selectedKeys);
        FillSelectionCellKeys(movedKeys, dx, dy, dz);

        foreach (var p in _selection)
        {
            int nx = p.X + dx, ny = p.Y + dy, nz = p.Z + dz;
            if (!CoordinateHelper.TryCellKey(nx, ny, nz, _gridSize, out int next))
            {
                MessageBox.Show($"The move would place one or more blocks outside the {_gridSize} x {_gridSize} x {_gridSize} build grid.", "Outside grid", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (IsOccupied(nx, ny, nz) && !selectedKeys.Contains(next)) return;
            if (!IsSupportedAfterMove(nx, ny, nz, selectedKeys, movedKeys))
            {
                MessageBox.Show("The move would leave one or more blocks floating in the air.", "Needs support", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }

        ExecuteCommand(new MoveBlocksCommand(this, GetSelectionSnapshot(), dx, dy, dz));
    }

    private bool IsSupportedAfterMove(int x, int y, int z, HashSet<int> selectedOldKeys, HashSet<int> selectedMovedKeys)
    {
        if (!IsInsideGrid(x, y, z)) return false;
        if (y == 0) return true;
        bool OccupiedAfter(int ox, int oy, int oz)
        {
            if (!CoordinateHelper.TryCellKey(ox, oy, oz, _gridSize, out int key))
                return false;
            if (selectedMovedKeys.Contains(key)) return true;
            if (EditorViewport.IsPackedFullGridMode && EditorViewport.IsPackedFullGridCellOccupied(ox, oy, oz))
                return !selectedOldKeys.Contains(key);
            var existing = _world.GetPieceAt(ox, oy, oz);
            return existing is not null && !selectedOldKeys.Contains(existing.CellKey);
        }
        return OccupiedAfter(x, y - 1, z) || OccupiedAfter(x, y + 1, z) ||
               OccupiedAfter(x - 1, y, z) || OccupiedAfter(x + 1, y, z) ||
               OccupiedAfter(x, y, z - 1) || OccupiedAfter(x, y, z + 1);
    }

    private void MaterializePackedSelectionForEdit()
    {
        if (!EditorViewport.IsPackedFullGridMode || _selection.Count == 0) return;

        var selected = GetSelectionSnapshot();
        bool changed = false;
        for (int i = 0; i < selected.Count; i++)
        {
            BlockPiece p = selected[i];
            if (_pieceById.ContainsKey(p.Id)) continue;

            // The cell becomes a sparse overlay only when edited. AddPieceInternal
            // also clears a deleted-cell marker if this edit is rebuilding a hole.
            AddPieceInternal(p);
            changed = true;
        }

        if (changed)
        {
            _selection.Clear();
            for (int i = 0; i < selected.Count; i++)
                _selection.Add(selected[i]);
            _selected = LastSelected();
        }
    }

    private void PresetColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string hex }) return;
        ColorText.Text = ColorUtil.NormalizeHex(hex);
        ColorPreview.Background = new SolidColorBrush(ColorUtil.ToWpfColor(ColorText.Text, Colors.SteelBlue));
        if (_selection.Count == 0) return;
        MaterializePackedSelectionForEdit();
        ExecuteCommand(new PaintBlocksCommand(this, GetSelectionSnapshot(), ColorText.Text));
    }

    private void ColorText_LostFocus(object sender, RoutedEventArgs e)
    {
        ColorText.Text = ColorUtil.NormalizeHex(ColorText.Text);
        ColorPreview.Background = new SolidColorBrush(ColorUtil.ToWpfColor(ColorText.Text, Colors.SteelBlue));
    }

    private void PaintSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_selection.Count == 0) return;
        MaterializePackedSelectionForEdit();
        ExecuteCommand(new PaintBlocksCommand(this, GetSelectionSnapshot(), ColorText.Text));
    }

    private void RotateSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_selection.Count == 0) return;
        MaterializePackedSelectionForEdit();
        ExecuteCommand(new TransformBlocksCommand(this, _selection, p => ((p.RotationY + 90) % 360, p.FlipHorizontal, p.FlipVertical)));
    }

    private void FlipHorizontalSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_selection.Count == 0) return;
        MaterializePackedSelectionForEdit();
        ExecuteCommand(new TransformBlocksCommand(this, _selection, p => (p.RotationY, !p.FlipHorizontal, p.FlipVertical)));
    }

    private void FlipVerticalSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_selection.Count == 0) return;
        MaterializePackedSelectionForEdit();
        ExecuteCommand(new TransformBlocksCommand(this, _selection, p => (p.RotationY, p.FlipHorizontal, !p.FlipVertical)));
    }

    private void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_selection.Count == 0) return;
        var deleted = GetSelectionSnapshot();
        ExecuteCommand(new DeleteBlocksCommand(this, deleted));
        _selection.Clear();
        _selected = null;
        RefreshEditorState();
    }

    private void CopySelectedToInternalClipboard()
    {
        if (_selection.Count == 0) return;
        _clipboard.Clear();
        _clipboardOrigin = GetSelectionMin();
        var selected = GetSelectionSnapshot();
        selected.Sort(static (a, b) =>
        {
            int cmp = a.Y.CompareTo(b.Y);
            if (cmp != 0) return cmp;
            cmp = a.Z.CompareTo(b.Z);
            if (cmp != 0) return cmp;
            return a.X.CompareTo(b.X);
        });
        for (int i = 0; i < selected.Count; i++)
            _clipboard.Add(ClonePiece(selected[i]));
    }

    private void PasteInternalClipboard()
    {
        if (_clipboard.Count == 0) return;
        if (!TryFindPasteLocation(out int dx, out int dy, out int dz))
        {
            MessageBox.Show("Could not find a free supported position for the copied blocks inside the grid.", "Paste", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var pasted = new List<BlockPiece>(_clipboard.Count);
        foreach (var source in _clipboard)
        {
            var p = ClonePiece(source);
            p.Id = Guid.NewGuid();
            p.X += dx; p.Y += dy; p.Z += dz;
            pasted.Add(p);
        }

        ExecuteCommand(new AddBlocksCommand(this, pasted));
        _selection.Clear();
        for (int i = 0; i < pasted.Count; i++)
        {
            var added = FindPiece(pasted[i].Id);
            if (added is not null) _selection.Add(added);
        }
        _selected = LastSelected();
        RefreshEditorState();
    }

    private bool TryFindPasteLocation(out int dx, out int dy, out int dz)
    {
        var selectionMin = _selection.Count > 0 ? GetSelectionMin() : _clipboardOrigin;
        int baseDx = (selectionMin.X + 1) - _clipboardOrigin.X;
        int baseDy = selectionMin.Y - _clipboardOrigin.Y;
        int baseDz = selectionMin.Z - _clipboardOrigin.Z;
        for (int r = 0; r <= Math.Min(_gridSize, 32); r++)
        {
            foreach (var off in SpiralOffsets(r))
            {
                dx = baseDx + off.X; dy = baseDy; dz = baseDz + off.Z;
                if (CanPasteAtOffset(dx, dy, dz)) return true;
            }
        }
        dx = dy = dz = 0;
        return false;
    }

    private static IEnumerable<(int X, int Z)> SpiralOffsets(int r)
    {
        if (r == 0) { yield return (0, 0); yield break; }
        yield return (r, 0); yield return (-r, 0); yield return (0, r); yield return (0, -r);
        yield return (r, r); yield return (-r, r); yield return (r, -r); yield return (-r, -r);
    }

    private bool CanPasteAtOffset(int dx, int dy, int dz)
    {
        var pasteKeys = new HashSet<int>();
        foreach (var p in _clipboard)
        {
            int x = p.X + dx, y = p.Y + dy, z = p.Z + dz;
            if (!IsInsideGrid(x, y, z) || IsOccupied(x, y, z)) return false;
            pasteKeys.Add(CoordinateHelper.CellKey(x, y, z));
        }
        foreach (var p in _clipboard)
        {
            int x = p.X + dx, y = p.Y + dy, z = p.Z + dz;
            if (!IsSupportedPositionForPaste(x, y, z, pasteKeys)) return false;
        }
        return true;
    }

    private bool IsSupportedPositionForPaste(int x, int y, int z, HashSet<int> pasteKeys)
    {
        if (y == 0) return true;
        bool occupiedOrPasted(int ox, int oy, int oz)
        {
            if (!CoordinateHelper.TryCellKey(ox, oy, oz, _gridSize, out int key))
                return false;
            return pasteKeys.Contains(key) ||
                   (EditorViewport.IsPackedFullGridMode && EditorViewport.IsPackedFullGridCellOccupied(ox, oy, oz)) ||
                   _world.GetPieceAt(ox, oy, oz) is not null;
        }
        return occupiedOrPasted(x, y - 1, z) || occupiedOrPasted(x, y + 1, z) ||
               occupiedOrPasted(x - 1, y, z) || occupiedOrPasted(x + 1, y, z) ||
               occupiedOrPasted(x, y, z - 1) || occupiedOrPasted(x, y, z + 1);
    }

    private void AddAtTextPosition_Click(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(XText.Text, out var x) && int.TryParse(YText.Text, out var y) && int.TryParse(ZText.Text, out var z))
            AddPiece(x, y, z, false);
    }

    private void PiecesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PiecesList.SelectedItem is BlockPiece p && !ReferenceEquals(p, _selected))
            SelectPiece(p, false);
    }

    private void ShowBlockContextMenu(BlockPiece piece)
    {
        if (!_selection.Contains(piece)) SelectPiece(piece, false);
        var menu = new ContextMenu { PlacementTarget = EditorViewport, Placement = PlacementMode.MousePoint };
        AddMenuItem(menu, "Select mode", () => SelectModeCheck.IsChecked = true);
        AddMenuItem(menu, "Build mode", () => BuildModeCheck.IsChecked = true);
        menu.Items.Add(new Separator());
        AddMenuItem(menu, "Paint selected", () => PaintSelected_Click(this, new RoutedEventArgs()));
        AddMenuItem(menu, "Rotate 90°", () => RotateSelected_Click(this, new RoutedEventArgs()));
        AddMenuItem(menu, "Flip horizontal", () => FlipHorizontalSelected_Click(this, new RoutedEventArgs()));
        AddMenuItem(menu, "Flip vertical", () => FlipVerticalSelected_Click(this, new RoutedEventArgs()));
        AddMenuItem(menu, "Copy", CopySelectedToInternalClipboard);
        AddMenuItem(menu, "Paste", PasteInternalClipboard);
        AddMenuItem(menu, "Delete", () => DeleteSelected_Click(this, new RoutedEventArgs()));
        menu.IsOpen = true;
    }

    private static void AddMenuItem(ContextMenu menu, string text, Action action)
    {
        var item = new MenuItem { Header = text };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private void OpenLibrary_Click(object sender, RoutedEventArgs e)
    {
        var current = ShapeCombo.SelectedValue is BlockShape s ? s : BlockShape.FullCube;
        var window = new BlockLibraryWindow(current) { Owner = this };
        window.ShapeChosen += shape => { ShapeCombo.SelectedValue = shape; window.Close(); };
        window.ShowDialog();
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new NewSceneDialog(_gridSize) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        ResetPerformanceBaseline();
        EditorViewport.UseNormalPieceScene();
        _undoStack.Clear(); _redoStack.Clear();
        _gridSize = dialog.GridSize;

        EditorViewport.BeginBulkSceneUpdate();
        try
        {
            _pieces.Clear();
            _selection.Clear();
            _selected = null;
            RebuildSpatialIndex();
        }
        finally
        {
            EditorViewport.EndBulkSceneUpdate(invalidateGeometry: false);
        }

        EditorViewport.GridSize = _gridSize;
        EditorViewport.CenterOnGrid();
        EditorViewport.InvalidateSceneGeometry();
        RefreshEditorState();
    }

    private void Sample_Click(object sender, RoutedEventArgs e)
    {
        _undoStack.Clear(); _redoStack.Clear();
        AddSampleCharacter();
        EditorViewport.InvalidateSceneGeometry();
        RefreshEditorState();
        EditorViewport.CenterOnModel();
    }

    private void CenterCamera_Click(object sender, RoutedEventArgs e) => EditorViewport.CenterOnModel();

    private const uint BinarySceneMagic = 0x584F4249; // IBOX, little-endian
    private const int BinarySceneVersion = 2;

    private BlockPiece? FirstPieceOrNull() => _pieces.Count > 0 ? _pieces[0] : null;

    private List<BlockPiece> ClonePiecesForSave()
    {
        var list = new List<BlockPiece>(_pieces.Count);
        for (int i = 0; i < _pieces.Count; i++)
            list.Add(ClonePiece(_pieces[i]));
        return list;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "Iso block binary scene (*.isoblocks)|*.isoblocks|All files (*.*)|*.*",
            FileName = "character.isoblocks"
        };
        if (dlg.ShowDialog() != true) return;

        var scene = CaptureCurrentSceneForSave();
        WriteBinaryScene(dlg.FileName, scene);
    }

    private SavedScene CaptureCurrentSceneForSave()
    {
        return new SavedScene
        {
            GridSize = _gridSize,
            Pieces = ClonePiecesForSave(),
            IsPackedFullGrid = EditorViewport.IsPackedFullGridMode,
            PackedFullGridSize = _gridSize,
            PackedDeletedCellKeys = EditorViewport.IsPackedFullGridMode
                ? new List<int>(EditorViewport.GetPackedFullGridDeletedCellKeys())
                : new List<int>()
        };
    }

    private static void WriteBinaryScene(string path, SavedScene scene)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: false);

        writer.Write(BinarySceneMagic);
        writer.Write(BinarySceneVersion);
        writer.Write(scene.GridSize);
        writer.Write(scene.IsPackedFullGrid);
        writer.Write(scene.PackedFullGridSize);

        writer.Write(scene.PackedDeletedCellKeys?.Count ?? 0);
        if (scene.PackedDeletedCellKeys is not null)
        {
            for (int i = 0; i < scene.PackedDeletedCellKeys.Count; i++)
                writer.Write(scene.PackedDeletedCellKeys[i]);
        }

        writer.Write(scene.Pieces?.Count ?? 0);
        if (scene.Pieces is not null)
        {
            for (int i = 0; i < scene.Pieces.Count; i++)
                WriteBlockPiece(writer, scene.Pieces[i]);
        }
    }

    private static void WriteBlockPiece(BinaryWriter writer, BlockPiece piece)
    {
        writer.Write(piece.Id.ToByteArray());
        writer.Write(piece.X);
        writer.Write(piece.Y);
        writer.Write(piece.Z);
        writer.Write((int)piece.Shape);
        writer.Write(piece.ColorHex ?? "#4B88D8");
        writer.Write(piece.RotationY);
        writer.Write(piece.FlipHorizontal);
        writer.Write(piece.FlipVertical);
    }

    private static BlockPiece ReadBlockPiece(BinaryReader reader)
    {
        byte[] idBytes = reader.ReadBytes(16);
        return new BlockPiece
        {
            Id = new Guid(idBytes),
            X = reader.ReadInt32(),
            Y = reader.ReadInt32(),
            Z = reader.ReadInt32(),
            Shape = (BlockShape)reader.ReadInt32(),
            ColorHex = reader.ReadString(),
            RotationY = reader.ReadInt32(),
            FlipHorizontal = reader.ReadBoolean(),
            FlipVertical = reader.ReadBoolean()
        };
    }

    private static SavedScene? ReadSceneFile(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length >= 8)
        {
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            uint magic = reader.ReadUInt32();
            if (magic == BinarySceneMagic)
            {
                int version = reader.ReadInt32();
                if (version is < 1 or > BinarySceneVersion)
                    throw new InvalidDataException($"Unsupported IsoBlock binary scene version {version}.");

                var scene = new SavedScene
                {
                    GridSize = reader.ReadInt32(),
                    IsPackedFullGrid = reader.ReadBoolean(),
                    PackedFullGridSize = reader.ReadInt32()
                };

                int deletedCount = reader.ReadInt32();
                scene.PackedDeletedCellKeys = new List<int>(Math.Max(0, deletedCount));
                for (int i = 0; i < deletedCount; i++)
                    scene.PackedDeletedCellKeys.Add(reader.ReadInt32());

                int pieceCount = reader.ReadInt32();
                scene.Pieces = new List<BlockPiece>(Math.Max(0, pieceCount));
                for (int i = 0; i < pieceCount; i++)
                    scene.Pieces.Add(ReadBlockPiece(reader));

                return scene;
            }
        }

        // Backward compatibility for old JSON saves. New saves are binary.
        stream.Position = 0;
        using var textReader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
        string json = textReader.ReadToEnd();
        return JsonSerializer.Deserialize<SavedScene>(json);
    }

    private void Load_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Iso block scene (*.isoblocks;*.json)|*.isoblocks;*.json|All files (*.*)|*.*" };
        if (dlg.ShowDialog() != true) return;
        var scene = ReadSceneFile(dlg.FileName);
        if (scene is null) return;

        ApplyLoadedScene(scene);
    }

    private void ApplyLoadedScene(SavedScene scene)
    {
        ResetPerformanceBaseline();
        _undoStack.Clear();
        _redoStack.Clear();
        _selection.Clear();
        _selected = null;

        int loadedGridSize = Math.Clamp(scene.GridSize > 0 ? scene.GridSize : 256, 1, 256);
        if (scene.IsPackedFullGrid)
            loadedGridSize = Math.Clamp(scene.PackedFullGridSize > 0 ? scene.PackedFullGridSize : loadedGridSize, 1, 256);

        _gridSize = loadedGridSize;
        EditorViewport.GridSize = _gridSize;

        List<BlockPiece> validatedPieces = CreateValidatedLoadedPieces(scene.Pieces, _gridSize);

        // Loading is a whole-scene replacement. Do not call AddPieceInternal in a loop here:
        // that would raise one WPF CollectionChanged event per block and rebuild the indices
        // incrementally. ReplacePiecesBulk emits one Reset notification and rebuilds the
        // spatial grid, VoxelWorld and id map once at the end.
        ReplacePiecesBulk(validatedPieces);

        if (scene.IsPackedFullGrid)
        {
            EditorViewport.GridSize = _gridSize;
            EditorViewport.UsePackedFullGridStressScene(_gridSize);
            EditorViewport.RestorePackedFullGridDeletedCellKeys(scene.PackedDeletedCellKeys);
            EditorViewport.CenterOnGrid();
        }
        else
        {
            EditorViewport.UseNormalPieceScene();
            EditorViewport.CenterOnModel();
            EditorViewport.InvalidateSceneGeometry();
        }

        _selected = FirstPieceOrNull();
        if (_selected is not null)
            _selection.Add(_selected);
        RefreshEditorState();
    }

    private static List<BlockPiece> CreateValidatedLoadedPieces(IList<BlockPiece>? pieces, int gridSize)
    {
        int count = pieces?.Count ?? 0;
        var validated = new List<BlockPiece>(count);
        if (pieces is null || count == 0)
            return validated;

        var seenIds = new HashSet<Guid>(count);
        var seenCells = new HashSet<int>(count);

        for (int i = 0; i < count; i++)
        {
            BlockPiece piece = pieces[i];
            if (!CoordinateHelper.TryCellKey(piece.X, piece.Y, piece.Z, gridSize, out int cellKey))
                continue;
            if (!seenIds.Add(piece.Id))
                continue;
            if (!seenCells.Add(cellKey))
                continue;

            validated.Add(piece);
        }

        return validated;
    }

    private void AddSampleCharacter()
    {
        EditorViewport.UseNormalPieceScene();
        int cx = Math.Clamp(_gridSize / 2, 4, GridMax - 4);
        int cz = Math.Clamp(_gridSize / 2, 4, GridMax - 4);
        var sample = new List<BlockPiece>(14);
        void P(int x, int y, int z, BlockShape s, string c, int r = 0) => sample.Add(new BlockPiece { X = x, Y = y, Z = z, Shape = s, ColorHex = c, RotationY = r });
        P(cx, 6, cz, BlockShape.FullCube, "#D8B18C");
        P(cx, 5, cz, BlockShape.Slope50, "#4B88D8", 270);
        P(cx, 4, cz, BlockShape.FullCube, "#4B88D8");
        P(cx, 3, cz, BlockShape.DiagonalSlope50, "#4B88D8");
        P(cx - 1, 5, cz, BlockShape.Slope50, "#4B88D8");
        P(cx + 1, 5, cz, BlockShape.Slope50, "#4B88D8", 180);
        P(cx - 2, 4, cz, BlockShape.FullCube, "#D8B18C");
        P(cx + 2, 4, cz, BlockShape.FullCube, "#D8B18C");
        P(cx - 1, 2, cz, BlockShape.FullCube, "#2D3542");
        P(cx + 1, 2, cz, BlockShape.FullCube, "#2D3542");
        P(cx - 1, 1, cz, BlockShape.FullCube, "#202631");
        P(cx + 1, 1, cz, BlockShape.FullCube, "#202631");
        P(cx - 1, 0, cz, BlockShape.Slope50, "#202631", 90);
        P(cx + 1, 0, cz, BlockShape.Slope50, "#202631", 90);

        ReplacePiecesBulk(sample);
        _selection.Clear();
        _selected = FirstPieceOrNull();
        if (_selected is not null)
            _selection.Add(_selected);
        EditorViewport.CenterOnModel();
    }


    private void Generate10k_Click(object sender, RoutedEventArgs e) => GenerateStressScene(10_000);
    private void Generate50k_Click(object sender, RoutedEventArgs e) => GenerateStressScene(50_000);
    private void Generate100k_Click(object sender, RoutedEventArgs e) => GenerateStressScene(100_000);
    private void Generate250k_Click(object sender, RoutedEventArgs e) => GenerateStressScene(250_000);
    private void Generate256Full_Click(object sender, RoutedEventArgs e) => GenerateFull256StressScene();

    private void GenerateFull256StressScene()
    {
        if (_stressSceneSwitchInProgress)
            return;

        _stressSceneSwitchInProgress = true;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            ResetPerformanceBaseline();
            ScheduleLargeStressSceneCompaction();
            ResetPerformanceBaseline();

            _undoStack.Clear();
            _redoStack.Clear();
            _selection.Clear();
            _selected = null;
            _gridSize = 256;

            ReplacePiecesBulk(Array.Empty<BlockPiece>());

            EditorViewport.GridSize = _gridSize;
            EditorViewport.UsePackedFullGridStressScene(_gridSize);
            EditorViewport.CenterOnGrid();
            RefreshEditorState();
        }
        finally
        {
            Mouse.OverrideCursor = null;
            _stressSceneSwitchInProgress = false;
        }
    }

    private static void ScheduleLargeStressSceneCompaction()
    {
        // Intentionally no-op. Large transient arrays are released from renderer/build
        // ownership as soon as GPU buffers have been updated. The runtime should own
        // GC and LOH policy; editor interaction paths should not change process-wide
        // GC settings.
    }

    private void GenerateStressScene(int targetCount)
    {
        if (_stressSceneSwitchInProgress)
            return;

        _stressSceneSwitchInProgress = true;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            ResetPerformanceBaseline();
            ScheduleLargeStressSceneCompaction();
            ResetPerformanceBaseline();
            EditorViewport.UseNormalPieceScene();
            _undoStack.Clear();
            _redoStack.Clear();
            _selection.Clear();
            _selected = null;

            int side = Math.Min(_gridSize - 2, (int)Math.Ceiling(Math.Pow(targetCount, 1.0 / 3.0)));
            int startX = Math.Max(1, (_gridSize - side) / 2);
            int startZ = Math.Max(1, (_gridSize - side) / 2);
            string[] colors = new[] { "#4B88D8", "#60B35F", "#D8B18C", "#2D3542" };
            var generated = new List<BlockPiece>(targetCount);

            int count = 0;
            for (int y = 0; y < side && count < targetCount; y++)
            {
                for (int z = 0; z < side && count < targetCount; z++)
                {
                    for (int x = 0; x < side && count < targetCount; x++)
                    {
                        generated.Add(new BlockPiece
                        {
                            X = startX + x,
                            Y = y,
                            Z = startZ + z,
                            Shape = BlockShape.FullCube,
                            ColorHex = colors[(x + y + z) & 3]
                        });
                        count++;
                    }
                }
            }

            // Replace the ObservableCollection with a single Reset notification.
            // Adding 100k/250k items one by one makes WPF process thousands of
            // collection events and can make the window appear frozen during rapid
            // stress-test switching.
            ReplacePiecesBulk(generated);

            _selected = FirstPieceOrNull();
            if (_selected is not null)
                _selection.Add(_selected);

            EditorViewport.CenterOnModel();
            EditorViewport.InvalidateSceneGeometry();
            RefreshEditorState();
        }
        finally
        {
            Mouse.OverrideCursor = null;
            _stressSceneSwitchInProgress = false;
        }
    }

    private List<BlockPiece> GetVisibleStressPieces(int maxCount)
    {
        var result = new List<BlockPiece>(Math.Min(maxCount, _pieces.Count));
        if (maxCount <= 0 || _pieces.Count == 0) return result;

        int maxX = int.MinValue, maxY = int.MinValue, maxZ = int.MinValue;
        for (int i = 0; i < _pieces.Count; i++)
        {
            BlockPiece p = _pieces[i];
            if (p.X > maxX) maxX = p.X;
            if (p.Y > maxY) maxY = p.Y;
            if (p.Z > maxZ) maxZ = p.Z;
        }

        var used = new HashSet<Guid>();

        // First prefer the visually exposed top/right/front shell of stress cubes.
        // The previous implementation used the first 1000 pieces, which are often
        // buried inside or on the bottom of large generated blocks, so Paint/Select
        // looked like it did nothing even though data changed.
        for (int i = 0; i < _pieces.Count && result.Count < maxCount; i++)
        {
            BlockPiece p = _pieces[i];
            if (p.Y == maxY || p.X == maxX || p.Z == maxZ)
            {
                result.Add(p);
                used.Add(p.Id);
            }
        }

        // Then add any genuinely exposed pieces from arbitrary user scenes.
        for (int i = 0; i < _pieces.Count && result.Count < maxCount; i++)
        {
            BlockPiece p = _pieces[i];
            if (used.Contains(p.Id)) continue;
            if (!_world.IsOccupied(p.X + 1, p.Y, p.Z) ||
                !_world.IsOccupied(p.X - 1, p.Y, p.Z) ||
                !_world.IsOccupied(p.X, p.Y + 1, p.Z) ||
                !_world.IsOccupied(p.X, p.Y - 1, p.Z) ||
                !_world.IsOccupied(p.X, p.Y, p.Z + 1) ||
                !_world.IsOccupied(p.X, p.Y, p.Z - 1))
            {
                result.Add(p);
                used.Add(p.Id);
            }
        }

        // Last fallback for tiny scenes where everything is already represented.
        for (int i = 0; i < _pieces.Count && result.Count < maxCount; i++)
        {
            BlockPiece p = _pieces[i];
            if (used.Contains(p.Id)) continue;
            result.Add(p);
            used.Add(p.Id);
        }

        return result;
    }

    private BlockPiece CreatePackedSurfacePiece(int ordinal, string? colorHex = null)
    {
        int n = Math.Clamp(_gridSize, 1, 256);
        int layerCells = n * n;
        int face = ordinal / layerCells;
        int local = ordinal % layerCells;
        int a = local % n;
        int b = local / n;

        int x, y, z;
        switch (face % 3)
        {
            // Use the three outward faces that are normally easiest to see in the
            // editor camera. This keeps stress buttons visibly deterministic while
            // still using real 256^3 surface cells.
            case 0: x = a;     y = n - 1; z = b;     break; // top face
            case 1: x = n - 1; y = b;     z = a;     break; // right face
            default:x = a;     y = b;     z = n - 1; break; // front face
        }

        return new BlockPiece
        {
            X = x,
            Y = y,
            Z = z,
            Shape = BlockShape.FullCube,
            ColorHex = colorHex ?? PackedSurfaceColorHex(x, y, z),
            RotationY = 0
        };
    }

    private static string PackedSurfaceColorHex(int x, int y, int z)
    {
        return ((x + y + z) & 3) switch
        {
            0 => "#4B88D8",
            1 => "#60B35F",
            2 => "#D8B18C",
            _ => "#2D3542"
        };
    }

    private BlockPiece GetOrCreatePackedSurfacePiece(int ordinal, string? colorHex = null)
    {
        BlockPiece template = CreatePackedSurfacePiece(ordinal, colorHex);
        return GetOrCreatePackedSurfaceCell(template.X, template.Y, template.Z, colorHex ?? template.ColorHex, template.RotationY, template.FlipHorizontal, template.FlipVertical);
    }

    private BlockPiece GetOrCreatePackedSurfaceCell(int x, int y, int z, string colorHex, int rotationY = 0, bool flipH = false, bool flipV = false)
    {
        BlockPiece? existing = _world.GetPieceAt(x, y, z);
        if (existing is not null)
        {
            existing.ColorHex = ColorUtil.NormalizeHex(colorHex);
            existing.RotationY = rotationY;
            existing.FlipHorizontal = flipH;
            existing.FlipVertical = flipV;
            _world.UpdatePiece(existing);
            return existing;
        }

        var piece = new BlockPiece
        {
            X = x,
            Y = y,
            Z = z,
            Shape = BlockShape.FullCube,
            ColorHex = ColorUtil.NormalizeHex(colorHex),
            RotationY = rotationY,
            FlipHorizontal = flipH,
            FlipVertical = flipV
        };
        AddPieceInternal(piece);
        return piece;
    }

    private void PaintPackedSurface1000()
    {
        ResetPerformanceBaseline();
        int count = Math.Min(1000, _gridSize * _gridSize * 3);
        var changed = new List<BlockPiece>(count);

        EditorViewport.BeginBulkSceneUpdate();
        try
        {
            for (int i = 0; i < count; i++)
            {
                string color = i % 2 == 0 ? "#D84C4C" : "#E2A83C";
                changed.Add(GetOrCreatePackedSurfacePiece(i, color));
            }
        }
        finally
        {
            EditorViewport.EndBulkSceneUpdate(invalidateGeometry: false);
        }

        EditorViewport.InvalidatePaintGeometryForPieces(changed);
        RefreshEditorState();
    }

    private void SelectPackedSurface1000()
    {
        ResetPerformanceBaseline();
        int count = Math.Min(1000, _gridSize * _gridSize * 3);
        var selected = new List<BlockPiece>(count);

        EditorViewport.BeginBulkSceneUpdate();
        try
        {
            for (int i = 0; i < count; i++)
                selected.Add(GetOrCreatePackedSurfacePiece(i));
        }
        finally
        {
            EditorViewport.EndBulkSceneUpdate(invalidateGeometry: false);
        }

        _selection.Clear();
        for (int i = 0; i < selected.Count; i++)
            _selection.Add(selected[i]);
        _selected = LastSelected();

        EditorViewport.InvalidateSceneGeometryForPieces(selected);
        RefreshEditorState();
        EditorViewport.InvalidateOverlay();
    }

    private void RandomPackedSurfaceEditBurst()
    {
        ResetPerformanceBaseline();
        var random = new Random(12345);
        int max = Math.Max(1, _gridSize * _gridSize * 3);
        int count = Math.Min(1000, max);
        var changed = new List<BlockPiece>(count);

        EditorViewport.BeginBulkSceneUpdate();
        try
        {
            for (int i = 0; i < count; i++)
            {
                int ordinal = random.Next(max);
                BlockPiece template = CreatePackedSurfacePiece(ordinal, (i & 1) == 0 ? "#8D65DA" : "#4B88D8");
                int rotation = ((i + ordinal) & 3) * 90;
                changed.Add(GetOrCreatePackedSurfaceCell(template.X, template.Y, template.Z, template.ColorHex, rotation));
            }
        }
        finally
        {
            EditorViewport.EndBulkSceneUpdate(invalidateGeometry: false);
        }

        EditorViewport.InvalidateSceneGeometryForPieces(changed);
        RefreshEditorState();
    }

    private void Paint1000_Click(object sender, RoutedEventArgs e)
    {
        if (EditorViewport.IsPackedFullGridMode)
        {
            PaintPackedSurface1000();
            return;
        }

        ResetPerformanceBaseline();
        var changed = GetVisibleStressPieces(1000);
        for (int i = 0; i < changed.Count; i++)
        {
            BlockPiece p = changed[i];
            p.ColorHex = i % 2 == 0 ? "#D84C4C" : "#E2A83C";
            _world.UpdatePiece(p);
        }
        EditorViewport.InvalidatePaintGeometryForPieces(changed);
        RefreshEditorState();
    }

    private void Select1000_Click(object sender, RoutedEventArgs e)
    {
        if (EditorViewport.IsPackedFullGridMode)
        {
            SelectPackedSurface1000();
            return;
        }

        _selection.Clear();
        var selected = GetVisibleStressPieces(1000);
        for (int i = 0; i < selected.Count; i++)
            _selection.Add(selected[i]);
        _selected = LastSelected();
        RefreshEditorState();
        EditorViewport.InvalidateOverlay();
    }

    private void RandomEditBurst_Click(object sender, RoutedEventArgs e)
    {
        if (EditorViewport.IsPackedFullGridMode)
        {
            RandomPackedSurfaceEditBurst();
            return;
        }

        if (_pieces.Count == 0) return;
        ResetPerformanceBaseline();
        var random = new Random(12345);
        var candidates = GetVisibleStressPieces(Math.Min(5000, _pieces.Count));
        int count = Math.Min(1000, candidates.Count);
        var changed = new List<BlockPiece>(count);
        for (int i = 0; i < count; i++)
        {
            int index = random.Next(candidates.Count);
            BlockPiece p = candidates[index];
            p.RotationY = (p.RotationY + 90) % 360;
            p.ColorHex = (i & 1) == 0 ? "#8D65DA" : "#4B88D8";
            _world.UpdatePiece(p);
            changed.Add(p);
        }
        EditorViewport.InvalidateSceneGeometryForPieces(changed);
        RefreshEditorState();
    }
}

public sealed class NewSceneDialog : Window
{
    private readonly TextBox _sizeText = new();
    public int GridSize { get; private set; }

    public NewSceneDialog(int currentSize)
    {
        Title = "New build grid";
        Width = 410;
        Height = 250;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromRgb(18, 24, 33));
        Foreground = Brushes.White;
        GridSize = Math.Clamp(currentSize <= 0 ? 256 : currentSize, 1, 256);
        var root = new StackPanel { Margin = new Thickness(18) };
        Content = root;
        root.Children.Add(new TextBlock { Text = "Grid size", FontSize = 18, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 8) });
        root.Children.Add(new TextBlock { Text = "Choose a cubic build grid. Maximum is 256 × 256 × 256.", Foreground = new SolidColorBrush(Color.FromRgb(175, 188, 205)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        _sizeText.Text = GridSize.ToString(CultureInfo.InvariantCulture);
        _sizeText.SelectAll();
        root.Children.Add(_sizeText);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var ok = new Button { Content = "Create", Width = 92, IsDefault = true };
        var cancel = new Button { Content = "Cancel", Width = 92, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        ok.Click += (_, _) => Accept();
        buttons.Children.Add(ok); buttons.Children.Add(cancel); root.Children.Add(buttons);
    }

    private void Accept()
    {
        if (!int.TryParse(_sizeText.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int size) || size < 1 || size > 256)
        {
            MessageBox.Show("Grid size must be a number from 1 to 256.", "Invalid grid size", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        GridSize = size;
        DialogResult = true;
    }
}
