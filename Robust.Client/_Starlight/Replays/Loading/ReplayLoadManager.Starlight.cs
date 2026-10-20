using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Robust.Shared.GameStates;
using Robust.Shared.Replays;
using Robust.Shared.Utility;
using static Robust.Shared.Replays.ReplayConstants;

// ReSharper disable once CheckNamespace
namespace Robust.Client.Replays.Loading;

public sealed partial class ReplayLoadManager
{
    private sealed record DecodedBlock(ResPath FileName, List<GameState> States, List<ReplayMessage> Messages);

    private IEnumerable<(GameState State, ReplayMessage Messages)> StreamHistoryParallel(
        IReplayFileReader fileReader,
        int totalData,
        List<BufferedReplayDataProvider.BlockMeta> blocks,
        HistoryStreamStats stats)
    {
        var names = new List<ResPath>(totalData);
        while (true)
        {
            var name = new ResPath($"{DataFilePrefix}{names.Count}.{Ext}");
            if (!fileReader.Exists(name))
                break;

            names.Add(name);
        }

        if (names.Count != totalData)
            throw new Exception("Could not read expected amount of data files from replay");

        var maxInFlight = Math.Clamp(Environment.ProcessorCount - 1, 1, 8);
        var pending = new Queue<Task<DecodedBlock>>(maxInFlight);
        var nextToStart = 0;

        try
        {
            for (var b = 0; b < names.Count; b++)
            {
                while (pending.Count < maxInFlight && nextToStart < names.Count)
                {
                    var name = names[nextToStart++];
                    pending.Enqueue(Task.Run(() => DecodeBlock(fileReader, name)));
                }

                var block = pending.Dequeue().GetAwaiter().GetResult();
                var blockStart = stats.TickCount;

                for (var i = 0; i < block.States.Count; i++)
                {
                    var state = block.States[i];
                    if (stats.TickCount == 0)
                        stats.FirstTick = state.ToSequence;
                    stats.LastTick = state.ToSequence;
                    stats.TickCount++;

                    yield return (state, block.Messages[i]);
                }

                var blockCount = stats.TickCount - blockStart;
                if (blockCount > 0)
                    blocks.Add(new BufferedReplayDataProvider.BlockMeta(block.FileName, blockStart, blockCount));
                stats.BlocksRead++;
            }
        }
        finally
        {
            foreach (var task in pending)
            {
                try
                {
                    task.GetAwaiter().GetResult();
                }
                catch (Exception)
                {
                }
            }
        }
    }

    private DecodedBlock DecodeBlock(IReplayFileReader fileReader, ResPath name)
    {
        byte[] compressed;
        lock (fileReader)
        {
            using var fileStream = fileReader.Open(name);
            compressed = fileStream.CopyToArray();
        }

        var uncompressedSize = BitConverter.ToInt32(compressed);
        var uncompressed = new MemoryStream(uncompressedSize);
        using (var source = new MemoryStream(compressed, sizeof(int), compressed.Length - sizeof(int), writable: false))
        using (var decompressStream = new ZStdDecompressStream(source))
        {
            decompressStream.CopyTo(uncompressed);
        }

        DebugTools.Assert(uncompressedSize == uncompressed.Length);
        uncompressed.Position = 0;

        var states = new List<GameState>();
        var messages = new List<ReplayMessage>();
        while (uncompressed.Position < uncompressed.Length)
        {
            _serializer.DeserializeDirect(uncompressed, out GameState state);
            _serializer.DeserializeDirect(uncompressed, out ReplayMessage msg);
            states.Add(state);
            messages.Add(msg);
        }

        return new DecodedBlock(name, states, messages);
    }
}
