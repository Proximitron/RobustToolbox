using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
/// A checkpoint store that keeps only a small number of recently-used checkpoints loaded and lazily (re)reads checkpoints from a temporary file on demand.
/// </summary>
internal sealed class ReplayCheckpointStore : IReplayCheckpointLoader, IDisposable
{
    private readonly record struct Entry(long Offset, int Length, bool Keyframe, GameTick Tick);

    private readonly IRobustSerializer _serializer;
    private readonly ISawmill _sawmill;
    private readonly Stream _storage;
    private readonly ZStdCompressionContext _zstd = new();
    private readonly MemoryStream _scratch = new();
    private readonly List<Entry> _entries = [];
    private readonly int _maxLoaded;

    private readonly LinkedList<CheckpointState> _loaded = new();

    private int _cursorIndex = -1;
    private readonly Dictionary<NetEntity, EntityState> _cursorEnts = [];
    private SessionState[] _cursorPlayers = [];

    private long _lastKeyframeSize;
    private long _deltaBytesSinceKeyframe;

    private bool _disposed;

    public int Count => _entries.Count;
    public long BytesWritten => _storage.Length;
    public bool OnDisk { get; }

    public ReplayCheckpointStore(IRobustSerializer serializer, ISawmill sawmill, int maxLoaded)
    {
        _serializer = serializer;
        _sawmill = sawmill;
        _maxLoaded = Math.Max(2, maxLoaded);

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

    /// <summary>
    /// Appends a new checkpoint to the store. The checkpoint is either a full keyframe or a delta from the previous checkpoint, depending on how many bytes have been written since the last keyframe.
    /// </summary>
    public int Append(
        GameTick tick,
        Dictionary<NetEntity, EntityState> entities,
        HashSet<NetEntity> changed,
        HashSet<NetEntity> deleted,
        SessionState[] players)
    {
        var keyframe = _entries.Count == 0 || _deltaBytesSinceKeyframe >= _lastKeyframeSize;

        GameState state;
        if (keyframe)
        {
            state = new GameState(GameTick.Zero, tick, default, entities.Values.ToArray(), players, Array.Empty<NetEntity>());
        }
        else
        {
            var changedStates = new EntityState[changed.Count];
            var i = 0;
            foreach (var ent in changed)
            {
                changedStates[i++] = entities[ent];
            }

            state = new GameState(GameTick.Zero, tick, default, changedStates, players, deleted.ToArray());
        }

        var offset = _storage.Length;
        var length = Write(state);
        _entries.Add(new Entry(offset, length, keyframe, tick));

        if (keyframe)
        {
            _lastKeyframeSize = length;
            _deltaBytesSinceKeyframe = 0;
        }
        else
        {
            _deltaBytesSinceKeyframe += length;
        }

        return _entries.Count - 1;
    }

    public GameState Load(CheckpointState checkpoint)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var index = checkpoint.StorageIndex;
        var entry = _entries[index];

        var keyframe = index;
        while (!_entries[keyframe].Keyframe)
        {
            keyframe--;
        }

        int next;
        if (_cursorIndex >= keyframe && _cursorIndex <= index)
        {
            next = _cursorIndex + 1;
        }
        else
        {
            var full = Read(_entries[keyframe]);
            _cursorEnts.Clear();
            _cursorEnts.EnsureCapacity(full.EntityStates.Value.Count);
            foreach (var ent in full.EntityStates.Span)
            {
                _cursorEnts[ent.NetEntity] = ent;
            }

            _cursorPlayers = [.. full.PlayerStates.Value];
            next = keyframe + 1;
        }

        for (; next <= index; next++)
        {
            var delta = Read(_entries[next]);
            foreach (var ent in delta.EntityStates.Span)
            {
                _cursorEnts[ent.NetEntity] = ent;
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

    private int Write(GameState state)
    {
        _scratch.SetLength(0);
        _serializer.SerializeDirect(_scratch, state);
        var uncompressed = _scratch.GetBuffer().AsSpan(0, (int) _scratch.Length);

        var buffer = ArrayPool<byte>.Shared.Rent(ZStd.CompressBound(uncompressed.Length) + sizeof(int));
        try
        {
            BitConverter.TryWriteBytes(buffer, uncompressed.Length);

            var compressed = _zstd.Compress(buffer.AsSpan(sizeof(int)), uncompressed, compressionLevel: 1);

            _storage.Seek(0, SeekOrigin.End);
            _storage.Write(buffer, 0, compressed + sizeof(int));
            return compressed + sizeof(int);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private GameState Read(in Entry entry)
    {
        var compressed = new byte[entry.Length];
        _storage.Seek(entry.Offset, SeekOrigin.Begin);
        _storage.ReadExactly(compressed);

        var uncompressedSize = BitConverter.ToInt32(compressed);
        var uncompressed = new MemoryStream(uncompressedSize);
        using (var decompress = new ZStdDecompressStream(new MemoryStream(compressed, sizeof(int), entry.Length - sizeof(int))))
        {
            decompress.CopyTo(uncompressed);
        }

        DebugTools.AssertEqual(uncompressed.Length, uncompressedSize);
        uncompressed.Position = 0;
        _serializer.DeserializeDirect(uncompressed, out GameState state);
        return state;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        foreach (var checkpoint in _loaded)
        {
            checkpoint.Unload();
        }

        _loaded.Clear();
        _cursorEnts.Clear();
        _storage.Dispose();
        _zstd.Dispose();
        _scratch.Dispose();
    }
}
