using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows.Input;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using MonoGame.Framework.WpfInterop;
using MonoGame.Framework.WpfInterop.Input;
using XnaColor = Microsoft.Xna.Framework.Color;
using WpfPoint = System.Windows.Point;

namespace IsoBlockCharacterEditor;

public sealed partial class VoxelEditorControl : WpfGame, IVoxelEditorInputTarget
{
    public VoxelEditorControl()
    {
        // WpfGame/D3D11Host exposes IsFixedTimeStep as read-only and already
        // reports false because WPF controls the render cadence. Do not assign it;
        // the safe FPS pass is the measured Draw-call FPS counter below.
    }

    private WpfGraphicsDeviceService? _graphics;
    private BasicEffect? _effect;
    private WpfMouse? _mouse;
    private WpfKeyboard? _keyboard;
    private SpriteBatch? _spriteBatch;
    private Texture2D? _pixel;
    private int _lastRenderWidth;
    private int _lastRenderHeight;
    private readonly RasterizerState _solidRasterizer = new() { CullMode = CullMode.None, MultiSampleAntiAlias = true };
    private readonly RasterizerState _wireRasterizer = new() { CullMode = CullMode.None, MultiSampleAntiAlias = true };

    // Green bevel outlines are drawn as real 3D lines around each block.
    // A tiny depth bias pulls the line overlay just in front of the block face,
    // removing z-fighting without drawing hidden edges through the whole model.
    private readonly RasterizerState _edgeRasterizer = new()
    {
        CullMode = CullMode.None,
        MultiSampleAntiAlias = true,
        DepthBias = -0.00025f,
        SlopeScaleDepthBias = -0.75f
    };

    private ObservableCollection<BlockPiece>? _pieces;
    private HashSet<BlockPiece>? _selection;
    private VoxelWorld? _voxelWorld;
    private int _suspendCollectionInvalidation;

    private readonly ChunkedVoxelRenderer _chunkRenderer = new();
    private readonly PackedFullGridRenderer _packedRenderer = new();
    private readonly CameraController _camera = new();
    private InputHandler? _inputHandler;

    private Vector3 _workspaceOffset { get => _camera.WorkspaceOffset; set => _camera.WorkspaceOffset = value; }
    private bool _isPreviewMode { get => _camera.IsPreviewMode; set => _camera.IsPreviewMode = value; }
    private bool _isLibraryPreviewControl => _camera.IsLibraryPreviewControl;
    private bool _packedFullGridStressMode => _packedRenderer.IsActive;
    private int _packedFullGridSize => _packedRenderer.GridSize;

    private readonly List<VertexPositionColor> _gridLines = new();
    private readonly List<VertexPositionColor> _selectionLines = new(512);
    private VertexPositionColor[] _gridLineArray = Array.Empty<VertexPositionColor>();
    private VertexPositionColor[] _edgeLineArray = Array.Empty<VertexPositionColor>();
    private VertexPositionColor[] _scratchLineArray = new VertexPositionColor[512];
    private readonly Dictionary<int, BlockPiece> _blocksByCell = new(4096);
    private NotifyCollectionChangedEventHandler? _piecesChangedHandler;
    private bool _gridDirty = true;
    private bool _pickGridDirty = true;
    private bool _edgeLinesDirty = true;
    private double _lastFrameMilliseconds;
    private double _lastPickingMilliseconds;
    private double _actualFps;
    private double _fpsAccumulatedSeconds;
    private int _fpsFrameCount;
    private int _lastGridStep = -1;
    private int _lastMajorGridStep = -1;

    private int _gridSize = 256;
    private bool _showFloorGrid = true;
    private bool _showLeftGrid = true;
    private bool _showRightGrid = true;
    private bool _showEdges;
    public bool IsPackedFullGridMode => _packedFullGridStressMode;

    // Last rendered preview stats. The main editor stats are calculated by the
    // chunk renderer / editor state, but the Block Shape Library uses direct
    // immediate drawing for one preview block so it needs its own counters.
    private int _lastVertexCount;
    private int _lastDrawVertexCount;
    private int _lastTriangleCount;
    private int _lastQuadEstimate;

    public EditorMode EditorMode { get; set; } = EditorMode.Build;
    public AttachDirection AttachDirection { get; set; } = AttachDirection.Up;

    public event Action<int, int, int, bool>? AddBlockRequested;
    public event Action<BlockPiece?, bool>? SelectBlockRequested;
    public event Action<BlockPiece>? ContextBlockRequested;

    public int GridSize
    {
        get => _gridSize;
        set
        {
            int newSize = Math.Clamp(value, 1, 256);
            if (newSize == _gridSize)
            {
                _gridDirty = true;
                return;
            }

            _gridSize = newSize;
            _gridDirty = true;
            _pickGridDirty = true;
            _edgeLinesDirty = true;
            _chunkRenderer.MarkAllDirty();

            if (_isLibraryPreviewControl)
                CenterOnPreviewBlock();
            else
                CenterOnGrid();
        }
    }

    public bool ShowFloorGrid { get => _showFloorGrid; set { _showFloorGrid = value; _gridDirty = true; } }
    public bool ShowLeftGrid { get => _showLeftGrid; set { _showLeftGrid = value; _gridDirty = true; } }
    public bool ShowRightGrid { get => _showRightGrid; set { _showRightGrid = value; _gridDirty = true; } }
    public bool ShowEdges { get => _showEdges; set { _showEdges = value; } }

    protected override void Initialize()
    {
        _graphics = new WpfGraphicsDeviceService(this);
        ConfigureHighQualityBackBuffer();

        _mouse = new WpfMouse(this);
        _keyboard = new WpfKeyboard(this);
        RecreateDeviceResources();
        SizeChanged += (_, _) => ConfigureHighQualityBackBuffer();


        _inputHandler = new InputHandler(this, this);
        _inputHandler.Attach();
        Focusable = true;
        IsHitTestVisible = true;
        Loaded += (_, _) => Focus();
        LostKeyboardFocus += (_, _) => ClearWorkspaceArrowKeyState();
        Unloaded += (_, _) => ClearWorkspaceArrowKeyState();

        GraphicsDevice.RasterizerState = _solidRasterizer;
        GraphicsDevice.DepthStencilState = DepthStencilState.Default;
        GraphicsDevice.BlendState = BlendState.Opaque;

        if (_isLibraryPreviewControl)
            CenterOnPreviewBlock();
        else
            CenterOnGrid();

        base.Initialize();
    }

    private void ConfigureHighQualityBackBuffer()
    {
        if (_graphics is null) return;
        try
        {
            _graphics.PreferMultiSampling = true;
            var dpi = _graphics.SystemDpiScalingFactor;
            if (!double.IsNaN(dpi) && dpi > 0)
                _graphics.DpiScalingFactor = dpi;

            int width = Math.Max(1, (int)Math.Ceiling(ActualWidth));
            int height = Math.Max(1, (int)Math.Ceiling(ActualHeight));
            if (width == _lastRenderWidth && height == _lastRenderHeight)
                return;

            _lastRenderWidth = width;
            _lastRenderHeight = height;
            _graphics.ApplyChanges();
            RecreateDeviceResources();
        }
        catch
        {
            // WPF may report zero size while the control is being initialized. The next SizeChanged/Draw pass fixes it.
        }
    }

    private void RecreateDeviceResources()
    {
        if (GraphicsDevice is null) return;

        _effect?.Dispose();
        _effect = new BasicEffect(GraphicsDevice)
        {
            VertexColorEnabled = true,
            LightingEnabled = false,
            TextureEnabled = false
        };

        _spriteBatch?.Dispose();
        _spriteBatch = new SpriteBatch(GraphicsDevice);

        _pixel?.Dispose();
        _pixel = new Texture2D(GraphicsDevice, 1, 1);
        _pixel.SetData(new[] { XnaColor.White });

        _gridDirty = true;
        _packedRenderer.OnGraphicsDeviceReset();
        _chunkRenderer.MarkAllDirty();
    }

    public ChunkedVoxelRenderer.MeshStats GetRenderStats() => _chunkRenderer.Stats;
    public double LastFrameMilliseconds => _lastFrameMilliseconds;
    public double LastPickingMilliseconds => _lastPickingMilliseconds;
    public double ActualFps => _actualFps;

    public void AttachData(ObservableCollection<BlockPiece> pieces, HashSet<BlockPiece> selection, VoxelWorld? voxelWorld = null)
    {
        if (_pieces is not null && _piecesChangedHandler is not null)
            _pieces.CollectionChanged -= _piecesChangedHandler;

        _pieces = pieces;
        _selection = selection;
        _voxelWorld = voxelWorld;
        _piecesChangedHandler ??= OnPiecesCollectionChanged;
        _pieces.CollectionChanged += _piecesChangedHandler;
        InvalidateSceneGeometry();
    }

    public void BeginBulkSceneUpdate()
    {
        _suspendCollectionInvalidation++;
    }

    public void EndBulkSceneUpdate(bool invalidateGeometry)
    {
        if (_suspendCollectionInvalidation > 0)
            _suspendCollectionInvalidation--;

        if (invalidateGeometry)
            InvalidateSceneGeometry();
    }

    public void UseNormalPieceScene()
    {
        _packedRenderer.UseNormalScene();

        // Treat every explicit scene/test switch as a hard renderer boundary,
        // even when the previous scene was also a normal VoxelWorld scene. This
        // clears old stats/chunks immediately, prevents stale async uploads, and
        // makes New/10k/50k/100k/250k all start from a clean viewport state.
        _chunkRenderer.ResetForNewScene(markDirty: true);

        _pickGridDirty = true;
        _edgeLinesDirty = true;
        _gridDirty = true;
    }

    public void UsePackedFullGridStressScene(int gridSize)
    {
        _gridSize = _packedRenderer.UsePackedFullGridStressScene(gridSize);

        // Scene boundary: discard normal-scene builds/chunks before entering the
        // procedural full-grid renderer. The following MarkProcedural... starts
        // the correct build for this mode.
        _chunkRenderer.ResetForNewScene(markDirty: false);
        _chunkRenderer.MarkProceduralFullGridDirty(_packedFullGridSize);
        _pickGridDirty = true;
        _edgeLinesDirty = true;
        _gridDirty = true;
    }

    public bool IsPackedFullGridCellOccupied(int x, int y, int z)
    {
        return _packedRenderer.IsCellOccupied(x, y, z);
    }

    public int[] GetPackedFullGridDeletedCellKeys()
    {
        return _packedRenderer.GetDeletedCellKeys();
    }

    public void RestorePackedFullGridDeletedCellKeys(IEnumerable<int>? keys)
    {
        _packedRenderer.RestoreDeletedCellKeys(keys, _chunkRenderer);
        _pickGridDirty = true;
        _edgeLinesDirty = true;
        _gridDirty = true;
    }

    public void MarkPackedFullGridCellsDeleted(IEnumerable<BlockPiece> pieces)
    {
        if (!_packedRenderer.MarkCellsDeleted(pieces, _chunkRenderer))
            return;

        _pickGridDirty = true;
        _edgeLinesDirty = true;
        _gridDirty = true;
    }

    public void ClearPackedFullGridDeletedCell(int x, int y, int z)
    {
        if (!_packedRenderer.ClearDeletedCell(x, y, z, _chunkRenderer))
            return;

        _pickGridDirty = true;
        _edgeLinesDirty = true;
        _gridDirty = true;
    }

    private void MarkPackedFullGridGeometryDirtyForPieces(IEnumerable<BlockPiece> pieces)
    {
        _packedRenderer.MarkGeometryDirtyForPieces(pieces, _chunkRenderer);
    }

    private void MarkPackedFullGridGeometryDirtyForNotifyArgs(NotifyCollectionChangedEventArgs e)
    {
        _packedRenderer.MarkGeometryDirtyForNotifyArgs(e, _chunkRenderer);
    }

    private void OnPiecesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_suspendCollectionInvalidation > 0)
            return;

        if (_packedFullGridStressMode)
        {
            // Sparse overlay cells replace cells in the packed full base. Only the
            // affected procedural chunks need to be rebuilt; a full 256³ rebuild
            // here makes paint/delete/undo feel delayed.
            _packedRenderer.MarkOcclusionAndOverlayDirty();
            MarkPackedFullGridGeometryDirtyForNotifyArgs(e);
            _pickGridDirty = true;
            _edgeLinesDirty = true;
            _gridDirty = true;
            return;
        }

        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            _chunkRenderer.MarkAllDirty();
            _pickGridDirty = true;
            _edgeLinesDirty = true;
            _gridDirty = true;
            return;
        }

        bool markedSpecificChunks = false;
        if (e.OldItems is not null)
        {
            var oldPieces = new List<BlockPiece>(e.OldItems.Count);
            for (int i = 0; i < e.OldItems.Count; i++)
                if (e.OldItems[i] is BlockPiece p) oldPieces.Add(p);
            if (oldPieces.Count > 0)
            {
                _chunkRenderer.MarkChunksDirtyForPieces(oldPieces, includeNeighbors: true);
                markedSpecificChunks = true;
            }
        }

        if (e.NewItems is not null)
        {
            var newPieces = new List<BlockPiece>(e.NewItems.Count);
            for (int i = 0; i < e.NewItems.Count; i++)
                if (e.NewItems[i] is BlockPiece p) newPieces.Add(p);
            if (newPieces.Count > 0)
            {
                _chunkRenderer.MarkChunksDirtyForPieces(newPieces, includeNeighbors: true);
                markedSpecificChunks = true;
            }
        }

        if (!markedSpecificChunks)
            _chunkRenderer.MarkAllDirty();

        _pickGridDirty = true;
        _edgeLinesDirty = true;
        _gridDirty = true;
    }

    public void InvalidateVisualScene() => InvalidateSceneGeometry();

    public void InvalidateSceneGeometry()
    {
        _packedRenderer.UseNormalScene();
        _gridDirty = true;
        _pickGridDirty = true;
        _edgeLinesDirty = true;
        _chunkRenderer.MarkAllDirty();
    }

    public void InvalidateSceneGeometryForCell(int x, int y, int z)
    {
        _pickGridDirty = true;
        _edgeLinesDirty = true;
        if (_packedFullGridStressMode)
        {
            _packedRenderer.MarkOcclusionAndOverlayDirty();
            _chunkRenderer.MarkProceduralFullGridCellDirty(x, y, z, _packedFullGridSize);
            return;
        }

        _chunkRenderer.MarkChunkAndNeighborsDirtyForCell(x, y, z);
    }

    public void InvalidateSceneGeometryForPieces(IEnumerable<BlockPiece> pieces)
    {
        _pickGridDirty = true;
        _edgeLinesDirty = true;
        if (_packedFullGridStressMode)
        {
            _packedRenderer.MarkOcclusionAndOverlayDirty();
            MarkPackedFullGridGeometryDirtyForPieces(pieces);
            return;
        }

        _chunkRenderer.MarkChunksDirtyForPieces(pieces, includeNeighbors: true);
    }

    public void InvalidatePaintGeometryForPieces(IEnumerable<BlockPiece> pieces)
    {
        _edgeLinesDirty = true;
        if (_packedFullGridStressMode)
        {
            _pickGridDirty = true;
            _edgeLinesDirty = true;
            _packedRenderer.MarkOverlayDirty();
            return;
        }

        // Color-only edits never reveal/hide neighbour faces, so only the owning
        // chunk needs a rebuild. This is the critical difference that keeps
        // Paint 1000 from dirtying every adjacent chunk on chunk boundaries.
        _chunkRenderer.MarkChunksDirtyForPieces(pieces, includeNeighbors: false);
    }

    public void InvalidateOverlay()
    {
        _gridDirty = true;
    }

    public void InvalidateUiOnly()
    {
        // Intentionally no geometry invalidation.
    }

    public void ConfigureAsLibraryPreviewControl()
    {
        // This control instance is hosted by BlockLibraryWindow, not by the main editor.
        // WpfGame can initialize after the WPF window Loaded event, so the normal
        // Initialize() path must also know that it should not call CenterOnGrid().
        _camera.ConfigureAsLibraryPreviewControl();
        _gridDirty = true;
    }

    public void CenterOnGrid()
    {
        _camera.CenterOnGrid(_gridSize);
        _gridDirty = true;
    }

    private void CenterOnPackedFullGrid()
    {
        _camera.CenterOnPackedFullGrid(_packedFullGridSize, _gridSize);
        _gridDirty = true;
    }

    public void CenterOnModel()
    {
        if (_packedFullGridStressMode)
        {
            CenterOnPackedFullGrid();
            return;
        }

        if (_pieces is null || _pieces.Count == 0)
            _camera.CenterOnGrid(_gridSize);
        else
            _camera.CenterOnModel(_pieces, _gridSize);

        _gridDirty = true;
    }

    public void CenterOnPreviewBlock()
    {
        _camera.CenterOnPreviewBlock();
        _gridDirty = true;
    }

    public void SetWorkspaceArrowKeyState(System.Windows.Input.Key key, bool isPressed, bool fast)
    {
        _camera.SetWorkspaceArrowKeyState(key, isPressed, fast);
    }

    public void ClearWorkspaceArrowKeyState()
    {
        _camera.ClearWorkspaceArrowKeyState();
    }

    protected override void Update(GameTime gameTime)
    {
        // WpfMouse/WpfKeyboard stay initialized for future input migration, but
        // continuous workspace panning is state-polled here instead of relying on
        // Windows key-repeat timing. This removes the first-repeat delay and the
        // small stutter that happened while holding arrow keys.
        _ = _mouse?.GetState();
        _ = _keyboard?.GetState();

        float elapsed = (float)gameTime.ElapsedGameTime.TotalSeconds;
        UpdateWorkspacePan(elapsed);

        base.Update(gameTime);
    }

    private void UpdateWorkspacePan(float elapsed)
    {
        _camera.UpdateWorkspacePan(elapsed, _gridSize, _packedFullGridStressMode, _packedFullGridSize);
    }

    protected override void Draw(GameTime gameTime)
    {
        var frameWatch = Stopwatch.StartNew();
        if (_effect is null) return;

        GraphicsDevice.Clear(new XnaColor(11, 15, 21));
        GraphicsDevice.SamplerStates[0] = SamplerState.LinearClamp;
        GraphicsDevice.DepthStencilState = DepthStencilState.Default;
        GraphicsDevice.RasterizerState = _solidRasterizer;
        GraphicsDevice.BlendState = BlendState.Opaque;

        var view = GetViewMatrix();
        var projection = GetProjectionMatrix();
        var blockWorld = GetBlockWorldMatrix();

        // The chunk renderer culls against chunk-local bounds. Therefore the
        // frustum must be created from the same world/view/projection transform
        // that is later used when drawing the chunk buffers. Do not also add
        // _workspaceOffset to chunk bounds inside ChunkedVoxelRenderer, or the
        // culling space and draw space drift apart and chunks disappear.
        var frustum = new BoundingFrustum(blockWorld * view * projection);

        _effect.World = Matrix.CreateTranslation(_workspaceOffset);
        _effect.View = view;
        _effect.Projection = projection;

        int currentGridStep = GridLineStep(_gridSize);
        int currentMajorStep = MajorGridStep(_gridSize);
        if (_lastGridStep != currentGridStep || _lastMajorGridStep != currentMajorStep)
            _gridDirty = true;

        if (_gridDirty)
        {
            RebuildGridLines();
            _lastGridStep = currentGridStep;
            _lastMajorGridStep = currentMajorStep;
            _gridDirty = false;
        }

        // Draw the voxel-style guide grid as true 3D wire lines.
        // v45 used screen-projected lines; when the camera looked almost parallel to a grid plane,
        // endpoints behind/near the camera projected into long radial strokes. This is stable:
        // no planes, no projected starburst, just real world-space jailbar grid lines.
        GraphicsDevice.BlendState = BlendState.NonPremultiplied;
        GraphicsDevice.DepthStencilState = DepthStencilState.None;
        GraphicsDevice.RasterizerState = _wireRasterizer;
        DrawLineArray(_gridLineArray, _gridLineArray.Length);

        GraphicsDevice.BlendState = BlendState.Opaque;
        GraphicsDevice.DepthStencilState = DepthStencilState.Default;
        GraphicsDevice.RasterizerState = _solidRasterizer;
        _effect.World = blockWorld;
        DrawBlocks(frustum);

        GraphicsDevice.BlendState = BlendState.NonPremultiplied;
        GraphicsDevice.DepthStencilState = DepthStencilState.DepthRead;
        _effect.World = blockWorld;
        DrawSelection();

        base.Draw(gameTime);
        frameWatch.Stop();
        _lastFrameMilliseconds = frameWatch.Elapsed.TotalMilliseconds;
        UpdateActualFpsCounter(gameTime);
    }

    private void UpdateActualFpsCounter(GameTime gameTime)
    {
        double elapsedSeconds = gameTime.ElapsedGameTime.TotalSeconds;
        if (elapsedSeconds <= 0 || double.IsNaN(elapsedSeconds) || double.IsInfinity(elapsedSeconds))
            return;

        _fpsAccumulatedSeconds += elapsedSeconds;
        _fpsFrameCount++;

        // Half-second window gives a stable number without making the UI feel stale.
        if (_fpsAccumulatedSeconds >= 0.5)
        {
            _actualFps = _fpsFrameCount / _fpsAccumulatedSeconds;
            _fpsFrameCount = 0;
            _fpsAccumulatedSeconds = 0;
        }
    }

    private Matrix GetBlockWorldMatrix()
    {
        return _camera.GetBlockWorldMatrix();
    }

    private void DrawBlocks(BoundingFrustum frustum)
    {
        if (_effect is null) return;

        if (_packedFullGridStressMode)
        {
            _packedRenderer.Draw(
                GraphicsDevice,
                _effect,
                _chunkRenderer,
                frustum,
                GetBlockWorldMatrix(),
                ViewDirectionFromTargetToCamera(),
                BuildPriorityFocus(),
                _showEdges,
                _pieces,
                _selection,
                _edgeRasterizer);
            return;
        }

        if (_pieces is null || _pieces.Count == 0) return;

        if (_isPreviewMode)
        {
            // Library preview is deliberately rendered directly. It is only one block,
            // so this avoids stale chunk buffers/frustum edge cases and guarantees the
            // colored block faces are visible. The high-performance chunk renderer is
            // still used by the main editor viewport.
            DrawPreviewBlocksImmediate();
            if (_showEdges)
                DrawBlockWires();
            return;
        }

        // Build only when the editor data changes. During normal frames this is a no-op.
        if (_voxelWorld is not null)
            _chunkRenderer.EnsureBuilt(GraphicsDevice, _voxelWorld, _gridSize, BuildPriorityFocus());
        else
            _chunkRenderer.EnsureBuilt(GraphicsDevice, _pieces, _gridSize, BuildPriorityFocus());

        // Draw with real chunk-level frustum culling. Do not fall back to
        // unculled drawing when drawCalls == 0: that can simply mean the model
        // is outside the viewport after panning/scrolling, and Viewport Info
        // must then report 0 visible chunks / 0 draw calls.
        Vector3 normalDirectionFromGridToCamera = ViewDirectionFromTargetToCamera();
        _chunkRenderer.DrawCameraFiltered(GraphicsDevice, _effect, frustum, GetBlockWorldMatrix(), normalDirectionFromGridToCamera, true, out _, out _);

        if (_showEdges)
            DrawBlockWires();
    }

    private void DrawPreviewBlocksImmediate()
    {
        if (_effect is null || _pieces is null || _pieces.Count == 0) return;

        Matrix previousWorld = _effect.World;
        _effect.World = GetBlockWorldMatrix();

        int verticesTotal = 0;
        int trianglesTotal = 0;

        for (int i = 0; i < _pieces.Count; i++)
        {
            BlockPiece piece = _pieces[i];
            BlockMesh mesh = IsoBlockMeshBuilder.BuildWorld(piece);
            XnaColor baseColor = ColorUtil.ToXnaColor(piece.PackedColor);
            VertexPositionColor[] vertices = IsoBlockMeshBuilder.ToVertices(mesh, baseColor);
            if (vertices.Length < 3)
                continue;

            for (int passIndex = 0; passIndex < _effect.CurrentTechnique.Passes.Count; passIndex++)
            {
                _effect.CurrentTechnique.Passes[passIndex].Apply();
                GraphicsDevice.DrawUserPrimitives(PrimitiveType.TriangleList, vertices, 0, vertices.Length / 3);
            }

            verticesTotal += vertices.Length;
            trianglesTotal += vertices.Length / 3;
        }

        _lastVertexCount = verticesTotal;
        _lastDrawVertexCount = verticesTotal;
        _lastTriangleCount = trianglesTotal;
        _lastQuadEstimate = trianglesTotal / 2;
        _effect.World = previousWorld;
    }

    private void DrawSelection()
    {
        if (_selection is null || _selection.Count == 0) return;
        _selectionLines.Clear();
        foreach (var piece in _selection)
            AddBoxLines(_selectionLines, new BoundingBox(new Vector3(piece.X - 0.045f, piece.Y - 0.045f, piece.Z - 0.045f), new Vector3(piece.X + 1.045f, piece.Y + 1.045f, piece.Z + 1.045f)), new XnaColor(255, 230, 70));
        DrawLineList(_selectionLines);
    }

    private void DrawBlockWires()
    {
        if (_pieces is null || _pieces.Count == 0) return;

        if (_edgeLinesDirty)
            RebuildEdgeLines();

        if (_edgeLineArray.Length == 0) return;

        GraphicsDevice.BlendState = BlendState.NonPremultiplied;
        GraphicsDevice.DepthStencilState = DepthStencilState.DepthRead;
        GraphicsDevice.RasterizerState = _edgeRasterizer;
        DrawLineArray(_edgeLineArray, _edgeLineArray.Length);
    }

    private void RebuildEdgeLines()
    {
        _edgeLinesDirty = false;
        if (_pieces is null || _pieces.Count == 0)
        {
            _edgeLineArray = Array.Empty<VertexPositionColor>();
            return;
        }

        _selectionLines.Clear();
        XnaColor color = new(57, 255, 20, 245);
        const float edgeGrow = 0.003f;

        for (int i = 0; i < _pieces.Count; i++)
        {
            BlockPiece p = _pieces[i];
            AddBoxLines(
                _selectionLines,
                new BoundingBox(
                    new Vector3(p.X - edgeGrow, p.Y - edgeGrow, p.Z - edgeGrow),
                    new Vector3(p.X + 1f + edgeGrow, p.Y + 1f + edgeGrow, p.Z + 1f + edgeGrow)),
                color);
        }

        if (_edgeLineArray.Length != _selectionLines.Count)
            _edgeLineArray = new VertexPositionColor[_selectionLines.Count];

        for (int i = 0; i < _selectionLines.Count; i++)
            _edgeLineArray[i] = _selectionLines[i];
    }

    private void DrawProjectedGridLines(List<VertexPositionColor> lines, Matrix view, Matrix projection)
    {
        if (_spriteBatch is null || _pixel is null || lines.Count < 2) return;

        var viewport = GraphicsDevice.Viewport;
        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone);
        for (int i = 0; i + 1 < lines.Count; i += 2)
        {
            var a3 = viewport.Project(lines[i].Position + _workspaceOffset, projection, view, Matrix.Identity);
            var b3 = viewport.Project(lines[i + 1].Position + _workspaceOffset, projection, view, Matrix.Identity);

            if ((a3.Z < 0 && b3.Z < 0) || (a3.Z > 1 && b3.Z > 1))
                continue;

            var a = new Vector2(a3.X, a3.Y);
            var b = new Vector2(b3.X, b3.Y);
            var delta = b - a;
            float length = delta.Length();
            if (length < 0.75f) continue;

            bool major = lines[i].Color.A >= 120;
            float thickness = major ? 1.05f : 0.62f;
            var color = lines[i].Color;
            DrawScreenLine(a, b, color, thickness);
        }
        _spriteBatch.End();
    }

    private void DrawScreenLine(Vector2 a, Vector2 b, XnaColor color, float thickness)
    {
        if (_spriteBatch is null || _pixel is null) return;
        var delta = b - a;
        float length = delta.Length();
        if (length <= 0.001f) return;
        float rotation = MathF.Atan2(delta.Y, delta.X);
        _spriteBatch.Draw(_pixel, a, null, color, rotation, new Vector2(0f, 0.5f), new Vector2(length, thickness), SpriteEffects.None, 0f);
    }

    private void DrawLineArray(VertexPositionColor[] lines, int count)
    {
        if (_effect is null || count < 2) return;
        foreach (var pass in _effect.CurrentTechnique.Passes)
        {
            pass.Apply();
            GraphicsDevice.DrawUserPrimitives(PrimitiveType.LineList, lines, 0, count / 2);
        }
    }

    private void DrawLineList(List<VertexPositionColor> lines)
    {
        if (lines.Count < 2) return;
        if (_scratchLineArray.Length < lines.Count)
            Array.Resize(ref _scratchLineArray, Math.Max(lines.Count, _scratchLineArray.Length * 2));
        for (int i = 0; i < lines.Count; i++)
            _scratchLineArray[i] = lines[i];
        DrawLineArray(_scratchLineArray, lines.Count);
    }

    private void RebuildGridLines()
    {
        _gridLines.Clear();
        int size = _gridSize;

        // voxel-style: one thin wire line per block boundary.
        // No translucent wall/floor planes and no screen-space projection tricks.
        // Minor lines are subtle; every 8th line and the outer frame are stronger.
        if (_showFloorGrid)
            AddFloorGridLines(size, new XnaColor(150, 160, 172, 54), new XnaColor(230, 236, 244, 168));
        if (_showLeftGrid)
            AddLeftGridLines(size, new XnaColor(35, 170, 150, 48), new XnaColor(94, 230, 210, 142));
        if (_showRightGrid)
            AddRightGridLines(size, new XnaColor(178, 118, 54, 48), new XnaColor(246, 180, 86, 142));

        _gridLineArray = _gridLines.Count == 0 ? Array.Empty<VertexPositionColor>() : _gridLines.ToArray();
    }

    private int GridLineStep(int size)
    {
        // Always keep real block cells visible. The line alpha is subtle enough that the
        // full 256 grid stays readable without becoming a solid wall.
        return 1;
    }

    private int MajorGridStep(int size)
    {
        return 8;
    }

    private float EstimatePixelsPerCell()
    {
        var viewport = GraphicsDevice.Viewport;
        var view = GetViewMatrix();
        var projection = GetProjectionMatrix();

        var basePoint = _camera.Target - _workspaceOffset;
        basePoint.X = Math.Clamp(basePoint.X, 0, _gridSize - 1);
        basePoint.Y = 0;
        basePoint.Z = Math.Clamp(basePoint.Z, 0, _gridSize - 1);

        var a = viewport.Project(basePoint + _workspaceOffset, projection, view, Matrix.Identity);
        var b = viewport.Project(basePoint + Vector3.Right + _workspaceOffset, projection, view, Matrix.Identity);
        var c = viewport.Project(basePoint + Vector3.Forward + _workspaceOffset, projection, view, Matrix.Identity);

        float px = Vector2.Distance(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y));
        float pz = Vector2.Distance(new Vector2(a.X, a.Y), new Vector2(c.X, c.Y));
        return MathF.Max(px, pz);
    }

    private void AddFloorGridLines(int size, XnaColor minor, XnaColor major)
    {
        int step = GridLineStep(size);
        int majorStep = MajorGridStep(size);
        for (int i = 0; i <= size; i += step)
        {
            var c = (i == 0 || i == size || i % majorStep == 0) ? major : minor;
            AddLine(_gridLines, new Vector3(0, 0, i), new Vector3(size, 0, i), c);
            AddLine(_gridLines, new Vector3(i, 0, 0), new Vector3(i, 0, size), c);
        }
        if (size % step != 0)
        {
            AddLine(_gridLines, new Vector3(0, 0, size), new Vector3(size, 0, size), major);
            AddLine(_gridLines, new Vector3(size, 0, 0), new Vector3(size, 0, size), major);
        }
    }

    private void AddLeftGridLines(int size, XnaColor minor, XnaColor major)
    {
        int step = GridLineStep(size);
        int majorStep = MajorGridStep(size);
        for (int i = 0; i <= size; i += step)
        {
            var c = (i == 0 || i == size || i % majorStep == 0) ? major : minor;
            AddLine(_gridLines, new Vector3(0, 0, i), new Vector3(0, size, i), c);
            AddLine(_gridLines, new Vector3(0, i, 0), new Vector3(0, i, size), c);
        }
        if (size % step != 0)
        {
            AddLine(_gridLines, new Vector3(0, 0, size), new Vector3(0, size, size), major);
            AddLine(_gridLines, new Vector3(0, size, 0), new Vector3(0, size, size), major);
        }
    }

    private void AddRightGridLines(int size, XnaColor minor, XnaColor major)
    {
        int step = GridLineStep(size);
        int majorStep = MajorGridStep(size);
        for (int i = 0; i <= size; i += step)
        {
            var c = (i == 0 || i == size || i % majorStep == 0) ? major : minor;
            AddLine(_gridLines, new Vector3(0, i, 0), new Vector3(size, i, 0), c);
            AddLine(_gridLines, new Vector3(i, 0, 0), new Vector3(i, size, 0), c);
        }
        if (size % step != 0)
        {
            AddLine(_gridLines, new Vector3(0, size, 0), new Vector3(size, size, 0), major);
            AddLine(_gridLines, new Vector3(size, 0, 0), new Vector3(size, size, 0), major);
        }
    }

    private static void AddLine(List<VertexPositionColor> list, Vector3 a, Vector3 b, XnaColor c)
    {
        if ((a - b).LengthSquared() < 0.000001f) return;
        list.Add(new VertexPositionColor(a, c));
        list.Add(new VertexPositionColor(b, c));
    }

    private static void AddBoxLines(List<VertexPositionColor> list, BoundingBox box, XnaColor color)
    {
        var c = box.GetCorners();
        int[] e = [0, 1, 1, 2, 2, 3, 3, 0, 4, 5, 5, 6, 6, 7, 7, 4, 0, 4, 1, 5, 2, 6, 3, 7];
        for (int i = 0; i < e.Length; i += 2) AddLine(list, c[e[i]], c[e[i + 1]], color);
    }

    void IVoxelEditorInputTarget.FocusViewport()
    {
        Focus();
    }

    void IVoxelEditorInputTarget.RotateCameraByMouseDelta(double dx, double dy)
    {
        RotateCameraByMouseDelta(dx, dy);
    }

    void IVoxelEditorInputTarget.HandleLeftClick(WpfPoint point)
    {
        HandleLeftClick(point);
    }

    void IVoxelEditorInputTarget.HandleRightClick(WpfPoint point)
    {
        if (TryPickBlock(point, out BlockPiece piece, out _, out _))
            ContextBlockRequested?.Invoke(piece);
    }

    private void RotateCameraByMouseDelta(double dx, double dy)
    {
        _camera.RotateByMouseDelta(dx, dy, _gridSize, _packedFullGridStressMode, _packedFullGridSize);
    }

    void IVoxelEditorInputTarget.HandleMouseWheel(int delta)
    {
        _camera.ZoomByMouseWheel(delta, _gridSize, _packedFullGridStressMode, _packedFullGridSize);
        _gridDirty = true;
    }

    private Matrix GetViewMatrix()
    {
        return _camera.GetViewMatrix();
    }

    private Matrix GetProjectionMatrix()
    {
        return _camera.GetProjectionMatrix(GraphicsDevice.Viewport, _gridSize);
    }

    private float MinimumCameraDistanceOutsideModel()
    {
        return _camera.MinimumCameraDistanceOutsideModel(_gridSize, _packedFullGridStressMode, _packedFullGridSize);
    }

    private Vector3 BuildPriorityFocus()
    {
        return _camera.BuildPriorityFocus(_gridSize);
    }

    private Vector3 ViewDirectionFromTargetToCamera()
    {
        return _camera.ViewDirectionFromTargetToCamera();
    }

    private float MaxDistance() => _camera.MaxDistance(_gridSize);
}
