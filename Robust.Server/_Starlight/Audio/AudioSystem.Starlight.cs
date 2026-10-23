using Robust.Shared.Audio.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Player;

namespace Robust.Server.Audio;

public sealed partial class AudioSystem
{
    private void RaiseAudioFilterApplied(Entity<AudioComponent> entity, Filter filter, bool recordReplay)
    {
        var ev = new AudioFilterAppliedEvent(filter, recordReplay);
        RaiseLocalEvent(entity, ref ev);
    }
}
