# AAA Core Status v89

This version is based on the clean v79 block editor line. Marching Cubes / morph code is intentionally not included.

## Implemented

- Spatial hash / chunk partitioning
  - `SpatialGridIndex` is a sparse O(1) coordinate map.
  - `ChunkedVoxelRenderer` groups scene data by chunk key and never searches the whole model while rebuilding one chunk.

- Greedy meshing for full cubes
  - `GreedyAxisX`, `GreedyAxisY`, and `GreedyAxisZ` merge adjacent exposed full-cube faces with the same packed color.
  - Hidden faces between neighboring full cubes are culled.
  - Special authored blocks keep exact geometry.

- No string comparisons in hot render/mesh loops
  - Block shape is enum-based.
  - Color is cached as `PackedColor` integer on `BlockPiece`.

- Command Pattern undo/redo
  - Add, delete, move, paint, rotate and flips are stored as small command objects.
  - No full-scene snapshots are used for normal editor history.

- Async background baking
  - `EnsureBuilt` snapshots WPF-owned data once per edit batch.
  - CPU mesh build runs in `Task.Run`.
  - GPU buffer upload happens on the MonoGame/UI thread.

- Reduced allocations in chunk meshing
  - Full cube cell buffers use `ArrayPool<BlockPiece?>`.
  - Greedy masks use `ArrayPool<int>`.
  - Chunk vertex staging uses `PooledVertexBuffer` backed by `ArrayPool<VertexPositionColor>`.
  - The only required allocation after baking is the final exact vertex array handed to GPU upload.

## Not implemented / intentionally deferred

- GPU instancing for helper markers and tools.
  - The solid world renderer does not need instancing because it draws one vertex buffer per visible chunk.
  - Selection/outline helpers can be instanced later if marker counts become large.

## v91 targeted performance patch

Implemented in this package:

- Dirty chunk API on `ChunkedVoxelRenderer`:
  - `MarkAllDirty()` for full rebuilds.
  - `MarkChunkDirty()` and `MarkChunkAndNeighborsDirtyForCell()` for local edits.
- Partial chunk uploads:
  - dirty chunk rebuild results update only the rebuilt chunks.
  - chunks emptied by a delete/move are removed without discarding unrelated chunks.
- Separated editor invalidation:
  - geometry invalidation is separate from overlay/UI-only refresh.
  - selection/UI refresh no longer intentionally marks the solid mesh dirty.
- Fixed `CollectionChanged` event unsubscription by using a stored handler.
- Added sparse pick-grid cache and DDA voxel raycast for normal viewport block picking.
- Cached green edge overlay array so ShowEdges no longer rebuilds the line list every frame.
- Reused per-chunk `VertexBuffer` capacity where possible instead of always destroying/recreating buffers.
- Cached local authored shape meshes in `IsoBlockMeshBuilder`.
- Added extra viewport counters: dirty chunks, mesh build ms, frame ms, picking ms, GC counters and total allocated MB.
- Added stress-test buttons for 10k/50k/100k/250k scenes and 1000-item paint/select/edit bursts.

Compatibility goal: keep editor behaviour the same while making normal editing paths incremental.

## v94 Horizontal Array Memory Pass

- Chunk CPU bake data now uses a horizontal Y-layer packed array:
  - `index = localY * 32 * 32 + localZ * 32 + localX`.
  - All X/Z cells for the same Y layer are contiguous.
- Removed the old per-rebuild nullable `BlockData?[] fullCells` allocation.
- Chunk cells are stored as packed `ulong` values during baking:
  - lower 32 bits = packed ARGB color
  - upper bits = shape/rotation/flip flags
  - zero = empty cell
- Special authored blocks are kept in a small side-list per chunk; full cube greedy meshing reads directly from the horizontal packed array.
- This is a transitional step toward a full `VoxelWorld`/`VoxelChunk` model where the editor data itself is stored as packed horizontal arrays instead of one object per voxel.

## v95 viewport counters and dirty-paint fix

- Paint/color-only invalidation now marks only owning chunks, not neighbour chunks.
- Add/remove/move-style geometry invalidation still marks neighbour chunks when boundary faces may change.
- Partial rebuilds build occupancy only for dirty chunks plus adjacent chunks instead of allocating a full-world occupancy dictionary for every edit.
- Stress-test generation suppresses per-item viewport collection invalidation and performs one full geometry invalidation at the end.
- Viewport info now separates total allocation from per-operation allocation delta and shows managed live memory, private memory, working set, GC delta, build type, dirty chunks and rebuilt chunks.
