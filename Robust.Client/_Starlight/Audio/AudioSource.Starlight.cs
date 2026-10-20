// ReSharper disable once CheckNamespace
namespace Robust.Client.Audio.Sources;

internal sealed partial class AudioSource
{
    private protected override bool BindDeviceBuffers()
    {
        // The manager reloads every audio resource after a rebuild and gives us the replacement stream.
        if (!Master.TryGetMigratedStream(SourceStream, out var stream))
            return false;

        SourceStream = stream;
        return Master.TryBindStream(SourceHandle, stream);
    }
}
