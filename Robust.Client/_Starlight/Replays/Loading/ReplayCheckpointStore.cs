using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Robust.Shared.GameObjects;
using Robust.Shared.GameStates;
using Robust.Shared.Log;
using Robust.Shared.Replays;
using Robust.Shared.Serialization;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

// ReSharper disable once CheckNamespace
namespace Robust.Client.Replays.Loading;

/// <summary>
/// Looks up the cached implicit (prototype default) component states of a prototype.
/// Must be safe to call from worker threads.
/// </summary>
internal delegate bool ImplicitStateResolver(string prototype, out List<ComponentChange> states, out HashSet<ushort> netComps);

internal sealed class ReplayCheckpointStore : IReplayCheckpointLoader, IDisposable
{
    private readonly record struct Entry(long Offset, int Length, bool Keyframe, GameTick Tick);

    private readonly record struct EncodedEntry(byte[] Buffer, int Length);

    private static readonly GameTick MarkerTick = GameTick.MaxValue;

    private const ushort ImplicitNetCompsMarker = ushort.MaxValue;

    private readonly IRobustSerializer _serializer;
    private readonly ISawmill _sawmill;
    private readonly ImplicitStateResolver _implicit;
    private readonly ushort _metaNetId;
    private readonly Stream _storage;
    private readonly List<Entry> _entries = [];
    private readonly int _maxLoaded;
    private readonly int _maxPendingWrites;

    private readonly LinkedList<CheckpointState> _loaded = new();

    private int _cursorIndex = -1;
    private readonly Dictionary<NetEntity, EntityState> _cursorEnts = [];
    private SessionState[] _cursorPlayers = [];

    private readonly Queue<(int Index, Task<EncodedEntry> Task)> _pendingWrites = new();
    private ConcurrentBag<ZStdCompressionContext>? _zstdPool = [];
    private ConcurrentBag<MemoryStream>? _scratchPool = [];
    private ArrayPool<byte>? _bufferPool = ArrayPool<byte>.Create(1 << 30, 4);
    private bool _writesFinished;

    private int _lastKeyframeIndex = -1;
    private int _keyframeEntityCount;
    private int _deltaEntitiesSinceKeyframe;
    private bool _lastKeyframeWritten;
    private long _lastKeyframeBytes;
    private long _deltaBytesSinceKeyframe;

    private bool _disposed;

    public int Count => _entries.Count;
    public long BytesWritten => _storage.Length;
    public int KeyframeCount => _entries.Count(e => e.Keyframe);
    public bool OnDisk { get; }

    public ReplayCheckpointStore(
        IRobustSerializer serializer,
        ISawmill sawmill,
        int maxLoaded,
        ImplicitStateResolver implicitResolver,
        ushort metaNetId)
    {
        _serializer = serializer;
        _sawmill = sawmill;
        _implicit = implicitResolver;
        _metaNetId = metaNetId;
        _maxLoaded = Math.Max(1, maxLoaded);
        _maxPendingWrites = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

        try
        {
            var path = Path.Combine(Path.GetTempPath(), $"robust-replay-checkpoints-{Guid.NewGuid():N}.tmp");
            _storage = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1 << 16,
                FileOptions.DeleteOnClose | FileOptions.RandomAccess);
            OnDisk = true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _sawmill.Warning($"Could not create temporary checkpoint file, keeping compressed checkpoints in memory: {e.Message}");
            _storage = new MemoryStream();
        }
    }

    #region Writing

    /// <summary>
    /// Appends a new checkpoint to the store. The checkpoint is either a full keyframe or a delta from the previous
    /// checkpoint, depending on how much delta data has accumulated since the last keyframe.
    /// </summary>
    public int Append(
        GameTick tick,
        Dictionary<NetEntity, EntityState> entities,
        HashSet<NetEntity> changed,
        HashSet<NetEntity> deleted,
        SessionState[] players)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_writesFinished)
            throw new InvalidOperationException("Checkpoint store is already sealed.");

        var keyframe = _entries.Count == 0
                       || _deltaEntitiesSinceKeyframe >= _keyframeEntityCount
                       || _lastKeyframeWritten && _deltaBytesSinceKeyframe >= _lastKeyframeBytes;

        EntityState[] states;
        NetEntity[] deletions;
        if (keyframe)
        {
            states = entities.Values.ToArray();
            deletions = [];
            _lastKeyframeIndex = _entries.Count;
            _keyframeEntityCount = Math.Max(1, states.Length);
            _deltaEntitiesSinceKeyframe = 0;
            _lastKeyframeWritten = false;
            _deltaBytesSinceKeyframe = 0;
        }
        else
        {
            states = new EntityState[changed.Count];
            var i = 0;
            foreach (var ent in changed)
            {
                states[i++] = entities[ent];
            }

            deletions = deleted.ToArray();
            _deltaEntitiesSinceKeyframe += states.Length + deletions.Length;
        }

        var index = _entries.Count;
        _entries.Add(new Entry(-1, 0, keyframe, tick));

        while (_pendingWrites.Count >= _maxPendingWrites)
        {
            WriteNextPending();
        }

        _pendingWrites.Enqueue((index, Task.Run(() => Encode(tick, states, deletions, players))));
        return index;
    }

    /// <summary>
    /// Waits for all pending writes and releases the buffers used for writing. Must be called once all checkpoints
    /// have been appended. Loading a checkpoint does this implicitly.
    /// </summary>
    public void FinishWrites()
    {
        if (_writesFinished)
            return;

        while (_pendingWrites.Count > 0)
        {
            WriteNextPending();
        }

        _writesFinished = true;
        _storage.Flush();
        ReleaseWriteResources();
    }

    private void WriteNextPending()
    {
        var (index, task) = _pendingWrites.Dequeue();
        var encoded = task.GetAwaiter().GetResult();

        try
        {
            var offset = _storage.Seek(0, SeekOrigin.End);
            _storage.Write(encoded.Buffer, 0, encoded.Length);

            var entry = _entries[index] with { Offset = offset, Length = encoded.Length };
            _entries[index] = entry;

            if (entry.Keyframe)
            {
                if (index == _lastKeyframeIndex)
                {
                    _lastKeyframeWritten = true;
                    _lastKeyframeBytes = encoded.Length;
                }
            }
            else if (index > _lastKeyframeIndex)
            {
                _deltaBytesSinceKeyframe += encoded.Length;
            }
        }
        finally
        {
            _bufferPool?.Return(encoded.Buffer);
        }
    }

    private EncodedEntry Encode(GameTick tick, EntityState[] states, NetEntity[] deletions, SessionState[] players)
    {
        var bufferPool = _bufferPool!;
        var scratchPool = _scratchPool!;
        var zstdPool = _zstdPool!;

        var stripped = new EntityState[states.Length];
        for (var i = 0; i < states.Length; i++)
        {
            stripped[i] = Strip(states[i]);
        }

        var state = new GameState(GameTick.Zero, tick, default, stripped, players, deletions);

        if (!scratchPool.TryTake(out var scratch))
            scratch = new MemoryStream();

        if (!zstdPool.TryTake(out var zstd))
            zstd = new ZStdCompressionContext();

        try
        {
            scratch.SetLength(0);
            _serializer.SerializeDirect(scratch, state);
            var uncompressed = scratch.GetBuffer().AsSpan(0, (int) scratch.Length);

            var buffer = bufferPool.Rent(ZStd.CompressBound(uncompressed.Length) + sizeof(int));
            BitConverter.TryWriteBytes(buffer, uncompressed.Length);
            var compressed = zstd.Compress(buffer.AsSpan(sizeof(int)), uncompressed, compressionLevel: 1);
            return new EncodedEntry(buffer, compressed + sizeof(int));
        }
        finally
        {
            scratchPool.Add(scratch);
            zstdPool.Add(zstd);
        }
    }

    private void ReleaseWriteResources()
    {
        if (_zstdPool != null)
        {
            foreach (var ctx in _zstdPool)
            {
                ctx.Dispose();
            }
        }

        if (_scratchPool != null)
        {
            foreach (var scratch in _scratchPool)
            {
                scratch.Dispose();
            }
        }

        _zstdPool = null;
        _scratchPool = null;
        _bufferPool = null;
    }

    #endregion

    #region Implicit state stripping

    private bool TryGetImplicit(
        ReadOnlySpan<ComponentChange> comps,
        out List<ComponentChange> implicitStates,
        out HashSet<ushort> implicitNetComps)
    {
        foreach (var comp in comps)
        {
            if (comp.NetID != _metaNetId)
                continue;

            if (comp.State is MetaDataComponentState { PrototypeId: { } proto })
                return _implicit(proto, out implicitStates, out implicitNetComps);

            break;
        }

        implicitStates = default!;
        implicitNetComps = default!;
        return false;
    }

    private static bool IsImplicit(in ComponentChange comp, List<ComponentChange> implicitStates)
    {
        foreach (var implicitComp in implicitStates)
        {
            if (implicitComp.NetID != comp.NetID)
                continue;

            return ReferenceEquals(implicitComp.State, comp.State)
                   && implicitComp.LastModifiedTick == comp.LastModifiedTick;
        }

        return false;
    }

    private static bool IsMarker(in ComponentChange comp)
    {
        return comp.State == null && comp.LastModifiedTick == MarkerTick;
    }

    private EntityState Strip(EntityState state)
    {
        var comps = state.ComponentChanges.Span;
        if (!TryGetImplicit(comps, out var implicitStates, out var implicitNetComps))
            return state;

        var implicitNetCompsUsed = state.NetComponents != null && ReferenceEquals(state.NetComponents, implicitNetComps);

        List<ComponentChange>? stripped = null;
        for (var i = 0; i < comps.Length; i++)
        {
            ref readonly var comp = ref comps[i];
            if (comp.NetID == _metaNetId || !IsImplicit(comp, implicitStates))
            {
                stripped?.Add(comp);
                continue;
            }

            if (stripped == null)
            {
                stripped = new List<ComponentChange>(comps.Length + 1);
                for (var j = 0; j < i; j++)
                {
                    stripped.Add(comps[j]);
                }
            }

            stripped.Add(new ComponentChange(comp.NetID, null, MarkerTick));
        }

        if (stripped == null && !implicitNetCompsUsed)
            return state;

        stripped ??= [.. comps];
        if (implicitNetCompsUsed)
            stripped.Add(new ComponentChange(ImplicitNetCompsMarker, null, MarkerTick));

        return new EntityState(
            state.NetEntity,
            stripped,
            state.EntityLastModified,
            implicitNetCompsUsed ? null : state.NetComponents);
    }

    private EntityState Restore(EntityState state)
    {
        var comps = state.ComponentChanges.Span;

        var hasMarker = false;
        foreach (var comp in comps)
        {
            if (!IsMarker(comp))
                continue;

            hasMarker = true;
            break;
        }

        if (!hasMarker)
            return state;

        var found = TryGetImplicit(comps, out var implicitStates, out var implicitNetComps);
        if (!found)
            _sawmill.Error($"Missing implicit data while restoring checkpoint state of {state.NetEntity}.");

        var netComps = state.NetComponents;
        var restored = new List<ComponentChange>(comps.Length);
        foreach (var comp in comps)
        {
            if (!IsMarker(comp))
            {
                restored.Add(comp);
                continue;
            }

            if (!found)
                continue;

            if (comp.NetID == ImplicitNetCompsMarker)
            {
                netComps = implicitNetComps;
                continue;
            }

            var restoredComp = false;
            foreach (var implicitComp in implicitStates)
            {
                if (implicitComp.NetID != comp.NetID)
                    continue;

                restored.Add(implicitComp);
                restoredComp = true;
                break;
            }

            if (!restoredComp)
                _sawmill.Error($"Missing implicit state {comp.NetID} while restoring checkpoint state of {state.NetEntity}.");
        }

        return new EntityState(state.NetEntity, restored, state.EntityLastModified, netComps);
    }

    #endregion

    #region Loading

    public GameState Load(CheckpointState checkpoint)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        FinishWrites();

        var index = checkpoint.StorageIndex;
        var entry = _entries[index];

        var keyframe = index;
        while (!_entries[keyframe].Keyframe)
        {
            keyframe--;
        }

        byte[]? compressedBuf = null;
        byte[]? rawBuf = null;

        int next;
        if (_cursorIndex >= keyframe && _cursorIndex <= index)
        {
            next = _cursorIndex + 1;
        }
        else
        {
            _cursorEnts.Clear();
            _cursorIndex = -1;

            var full = Read(_entries[keyframe], ref compressedBuf, ref rawBuf);
            _cursorEnts.EnsureCapacity(full.EntityStates.Value.Count);
            foreach (var ent in full.EntityStates.Span)
            {
                _cursorEnts[ent.NetEntity] = Restore(ent);
            }

            _cursorPlayers = [.. full.PlayerStates.Value];
            next = keyframe + 1;
        }

        for (; next <= index; next++)
        {
            var delta = Read(_entries[next], ref compressedBuf, ref rawBuf);
            foreach (var ent in delta.EntityStates.Span)
            {
                _cursorEnts[ent.NetEntity] = Restore(ent);
            }

            foreach (var ent in delta.EntityDeletions.Span)
            {
                _cursorEnts.Remove(ent);
            }

            _cursorPlayers = [.. delta.PlayerStates.Value];
        }

        _cursorIndex = index;

        TrackLoaded(checkpoint);

        return new GameState(
            GameTick.Zero,
            entry.Tick,
            default,
            _cursorEnts.Values.ToArray(),
            _cursorPlayers.ToArray(),
            Array.Empty<NetEntity>());
    }

    private void TrackLoaded(CheckpointState checkpoint)
    {
        _loaded.Remove(checkpoint);
        _loaded.AddFirst(checkpoint);

        while (_loaded.Count > _maxLoaded)
        {
            var oldest = _loaded.Last!.Value;
            _loaded.RemoveLast();
            oldest.Unload();
        }
    }

    private GameState Read(in Entry entry, ref byte[]? compressedBuf, ref byte[]? rawBuf)
    {
        if (compressedBuf == null || compressedBuf.Length < entry.Length)
            compressedBuf = GC.AllocateUninitializedArray<byte>(entry.Length);

        _storage.Seek(entry.Offset, SeekOrigin.Begin);
        _storage.ReadExactly(compressedBuf, 0, entry.Length);

        var uncompressedSize = BitConverter.ToInt32(compressedBuf);
        if (rawBuf == null || rawBuf.Length < uncompressedSize)
            rawBuf = GC.AllocateUninitializedArray<byte>(uncompressedSize);

        var written = ZStdStarlight.Decompress(
            rawBuf.AsSpan(0, uncompressedSize),
            compressedBuf.AsSpan(sizeof(int), entry.Length - sizeof(int)));
        DebugTools.AssertEqual(written, uncompressedSize);

        using var stream = new MemoryStream(rawBuf, 0, uncompressedSize, writable: false);
        _serializer.DeserializeDirect(stream, out GameState state);
        return state;
    }

    #endregion

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        while (_pendingWrites.Count > 0)
        {
            var (_, task) = _pendingWrites.Dequeue();
            try
            {
                var encoded = task.GetAwaiter().GetResult();
                _bufferPool?.Return(encoded.Buffer);
            }
            catch (Exception)
            {
            }
        }

        ReleaseWriteResources();

        foreach (var checkpoint in _loaded)
        {
            checkpoint.Unload();
        }

        _loaded.Clear();
        _cursorEnts.Clear();
        _storage.Dispose();
    }
}
