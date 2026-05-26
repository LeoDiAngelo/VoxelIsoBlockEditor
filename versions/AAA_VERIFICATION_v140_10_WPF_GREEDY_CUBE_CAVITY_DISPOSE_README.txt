Voxel Iso Block Editor v140.10 verification note

Changes:
- FullCube now stays on the greedy-meshing path regardless of stored RotationY/FlipHorizontal/FlipVertical flags. A cube is geometrically invariant under these transforms, so rotation/flip metadata must not force it into the authored-special-mesh path.
- Packed full-grid deleted-cell cavity generation now builds a chunk-local deleted-cell index. Rebuilt chunks only scan deleted cells that can expose faces into that chunk instead of scanning every deleted cell for every rebuilt chunk.
- Added explicit viewport resource lifetime cleanup on WPF Unloaded: input handler detach, collection handler detach, chunk renderer disposal, packed renderer disposal, BasicEffect/SpriteBatch/Texture2D disposal, and rasterizer state disposal.
- Added InputHandler.Detach() for symmetrical WPF event unsubscription.
- Renderer build preparation is now also kicked from Update(), so dirty snapshot/build startup is not only initiated from DrawBlocks(). Draw still calls the safe ensure methods as a no-op/apply-pending guard.
- README wording now says logical packed 256^3 grid surface-rendered with chunked greedy/procedural meshing, avoiding the misleading impression that 16.7M individual cube meshes are drawn.

Not changed:
- No pan/scroll experiment from v141-v144.
- No editor behavior changes for placement, selection, paint/delete, undo/redo, save/load, block shape orientation, or packed/full-grid semantics.
- No LINQ or tuple/offset-array hot-path rewrite added.

Build note:
- dotnet SDK was not available in the patching environment; Visual Studio build verification is still required.
