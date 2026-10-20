using OpenTK.Audio.OpenAL;
using OpenTK.Mathematics;

// ReSharper disable once CheckNamespace
namespace Robust.Client.Audio.Effects;

/// <summary>
/// Carries reverb parameters over a full OpenAL device rebuild, see <see cref="AudioManager"/>.
/// </summary>
internal sealed partial class AudioEffect
{
    private static readonly EffectFloat[] ReverbFloats =
    [
        EffectFloat.EaxReverbDensity,
        EffectFloat.EaxReverbDiffusion,
        EffectFloat.EaxReverbGain,
        EffectFloat.EaxReverbGainHF,
        EffectFloat.EaxReverbGainLF,
        EffectFloat.EaxReverbDecayTime,
        EffectFloat.EaxReverbDecayHFRatio,
        EffectFloat.EaxReverbDecayLFRatio,
        EffectFloat.EaxReverbReflectionsGain,
        EffectFloat.EaxReverbReflectionsDelay,
        EffectFloat.EaxReverbLateReverbGain,
        EffectFloat.EaxReverbLateReverbDelay,
        EffectFloat.EaxReverbEchoTime,
        EffectFloat.EaxReverbEchoDepth,
        EffectFloat.EaxReverbModulationTime,
        EffectFloat.EaxReverbModulationDepth,
        EffectFloat.EaxReverbAirAbsorptionGainHF,
        EffectFloat.EaxReverbHFReference,
        EffectFloat.EaxReverbLFReference,
        EffectFloat.EaxReverbRoomRolloffFactor,
    ];

    private static readonly EffectVector3[] ReverbVectors =
    [
        EffectVector3.EaxReverbReflectionsPan,
        EffectVector3.EaxReverbLateReverbPan,
    ];

    internal sealed class DeviceSnapshot
    {
        public readonly float[] Floats = new float[ReverbFloats.Length];
        public readonly Vector3[] Vectors = new Vector3[ReverbVectors.Length];
        public int DecayHFLimit;
    }

    internal bool IsAlive => Handle > 0;

    internal DeviceSnapshot CaptureDeviceState()
    {
        var snapshot = new DeviceSnapshot();
        for (var i = 0; i < ReverbFloats.Length; i++)
        {
            ALC.EFX.GetEffect(Handle, ReverbFloats[i], out snapshot.Floats[i]);
        }

        for (var i = 0; i < ReverbVectors.Length; i++)
        {
            ALC.EFX.GetEffect(Handle, ReverbVectors[i], out snapshot.Vectors[i]);
        }

        ALC.EFX.GetEffect(Handle, EffectInteger.EaxReverbDecayHFLimit, out snapshot.DecayHFLimit);
        AL.GetError();
        return snapshot;
    }

    /// <param name="efx">Whether the new device has EFX at all. Without it the effect turns into a no-op.</param>
    internal void RestoreDeviceState(DeviceSnapshot? snapshot, bool efx)
    {
        if (!efx)
        {
            Handle = 0;
            return;
        }

        Handle = ALC.EFX.GenEffect();
        ALC.EFX.Effect(Handle, EffectInteger.EffectType, (int) EffectType.EaxReverb);
        if (snapshot != null)
        {
            for (var i = 0; i < ReverbFloats.Length; i++)
            {
                ALC.EFX.Effect(Handle, ReverbFloats[i], snapshot.Floats[i]);
            }

            for (var i = 0; i < ReverbVectors.Length; i++)
            {
                ALC.EFX.Effect(Handle, ReverbVectors[i], ref snapshot.Vectors[i]);
            }

            ALC.EFX.Effect(Handle, EffectInteger.EaxReverbDecayHFLimit, snapshot.DecayHFLimit);
        }

        AL.GetError();
    }
}
