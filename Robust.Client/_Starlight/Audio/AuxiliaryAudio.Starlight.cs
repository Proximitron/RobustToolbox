using OpenTK.Audio.OpenAL;

// ReSharper disable once CheckNamespace
namespace Robust.Client.Audio.Effects;

internal sealed partial class AuxiliaryAudio
{
    private AudioEffect? _effect;

    internal bool IsAlive => Handle > 0;

    internal AuxiliaryAudio(IAudioInternal manager)
    {
        (manager as AudioManager)?.TrackAuxiliary(this);
    }

    /// <summary>
    /// Re-creates the effect slot on a new device. Effects must already be restored at this point.
    /// </summary>
    internal void RestoreDeviceState(bool efx)
    {
        if (!efx)
        {
            Handle = 0;
            return;
        }

        Handle = ALC.EFX.GenAuxiliaryEffectSlot();
        if (_effect is { IsAlive: true } effect)
            ALC.EFX.AuxiliaryEffectSlot(Handle, EffectSlotInteger.Effect, effect.Handle);

        AL.GetError();
    }
}
