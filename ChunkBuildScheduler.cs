using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace IsoBlockCharacterEditor;

/// <summary>
/// Owns the renderer's single background CPU-build lane. ChunkedVoxelRenderer remains
/// responsible for GPU buffers and drawing; this class owns build cancellation,
/// scene-boundary invalidation and pending-result publication.
/// </summary>
internal sealed class ChunkBuildScheduler : IDisposable
{
    private readonly object _sync = new();
    private CancellationTokenSource _sceneCancellation = new();
    private BuildResult? _pendingResult;
    private int _buildRunning;
    private int _buildSerial;
    private long _sceneId;
    private bool _disposed;

    public bool IsBusy => Volatile.Read(ref _buildRunning) != 0;

    public long SceneId
    {
        get
        {
            lock (_sync)
                return _sceneId;
        }
    }

    public BuildResult? ResetForNewScene()
    {
        CancellationTokenSource oldCancellation;
        BuildResult? droppedPending;
        lock (_sync)
        {
            oldCancellation = _sceneCancellation;
            _sceneCancellation = new CancellationTokenSource();
            _sceneId++;

            // New scene owns the build lane. Old workers are cancelled and their
            // finally blocks are prevented from reopening/closing the current lane.
            Interlocked.Increment(ref _buildSerial);
            Interlocked.Exchange(ref _buildRunning, 0);

            droppedPending = _pendingResult;
            _pendingResult = null;
        }

        oldCancellation.Cancel();
        oldCancellation.Dispose();
        return droppedPending;
    }

    public bool TryStart(IVoxelSceneSource source)
    {
        if (_disposed)
        {
            source.Dispose();
            return false;
        }

        if (Volatile.Read(ref _buildRunning) != 0)
        {
            source.Dispose();
            return false;
        }

        if (Interlocked.CompareExchange(ref _buildRunning, 1, 0) != 0)
        {
            source.Dispose();
            return false;
        }

        CancellationToken token;
        long currentSceneId;
        lock (_sync)
        {
            token = _sceneCancellation.Token;
            currentSceneId = _sceneId;
        }

        if (token.IsCancellationRequested || source.SceneId != currentSceneId)
        {
            source.Dispose();
            Interlocked.Exchange(ref _buildRunning, 0);
            return false;
        }

        int buildSerial = Interlocked.Increment(ref _buildSerial);
        Task.Run(() => RunBuild(source, token, buildSerial));
        return true;
    }

    public BuildResult? TakePendingResult(int requestedGeneration, int uploadedGeneration)
    {
        lock (_sync)
        {
            if (_pendingResult is null)
                return null;

            if (_pendingResult.SceneId == _sceneId &&
                _pendingResult.Generation == requestedGeneration &&
                _pendingResult.Generation >= uploadedGeneration)
            {
                BuildResult result = _pendingResult;
                _pendingResult = null;
                return result;
            }

            if (_pendingResult.SceneId != _sceneId || _pendingResult.Generation < requestedGeneration)
            {
                _pendingResult.ClearTransientMeshData();
                _pendingResult = null;
            }

            return null;
        }
    }

    private void RunBuild(IVoxelSceneSource source, CancellationToken token, int buildSerial)
    {
        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            token.ThrowIfCancellationRequested();
            BuildResult result = source.Build(token);
            sw.Stop();
            result.BuildMilliseconds = sw.Elapsed.TotalMilliseconds;
            if (!TryPublish(result, token))
                result.ClearTransientMeshData();
        }
        catch (OperationCanceledException)
        {
            // Scene/test switched while this build was running.
        }
        finally
        {
            source.Dispose();
            if (Volatile.Read(ref _buildSerial) == buildSerial)
                Interlocked.Exchange(ref _buildRunning, 0);
        }
    }

    private bool TryPublish(BuildResult result, CancellationToken token)
    {
        if (token.IsCancellationRequested)
            return false;

        lock (_sync)
        {
            if (token.IsCancellationRequested || result.SceneId != _sceneId)
                return false;

            if (_pendingResult is not null)
            {
                if (result.Generation < _pendingResult.Generation)
                    return false;

                _pendingResult.ClearTransientMeshData();
            }

            _pendingResult = result;
            return true;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        BuildResult? droppedPending;
        CancellationTokenSource cancellation;
        lock (_sync)
        {
            cancellation = _sceneCancellation;
            Interlocked.Increment(ref _buildSerial);
            Interlocked.Exchange(ref _buildRunning, 0);
            droppedPending = _pendingResult;
            _pendingResult = null;
        }

        cancellation.Cancel();
        cancellation.Dispose();
        droppedPending?.ClearTransientMeshData();
    }
}
