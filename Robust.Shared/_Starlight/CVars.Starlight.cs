using Robust.Shared.Configuration;
using Robust.Shared.Network;

// Resharper disable once CheckNamespace
namespace Robust.Shared;

public abstract partial class CVars
{
    /// <summary>
    /// Whether to enable granular metrics in the <see cref="SharedUserInterfaceSystem"/>.
    /// </summary>
    public static readonly CVarDef<bool> MetricsGranularUiSystem =
        CVarDef.Create("metrics.granular_ui_system", false, CVar.SERVERONLY);

    /// <summary>
    /// Mode with which to handle ADDITIONAL authentication on the server.
    /// </summary>
    public static readonly CVarDef<int> AdditionalAuthMode =
        CVarDef.Create("auth.additionalmode", (int)Network.AdditionalAuthModes.Disabled, CVar.SERVERONLY);

    /// <summary>
    /// Authentication server address for discord.
    /// </summary>
    public static readonly CVarDef<string> StarlightAPIServer =
        CVarDef.Create("auth.starlightapiserver", AuthManager.DefaultStarlightAPI, CVar.SERVERONLY);

    /// <summary>
    /// Speaker layout to render audio for. One of: auto, mono, stereo, headphones, quad, 5.1, 7.1.
    /// "auto" lets OpenAL pick based on the OS configuration. Forcing "stereo" or "headphones" fixes missing
    /// sounds (e.g. voices in the center channel) when the OS reports a surround layout that isn't really there.
    /// </summary>
    public static readonly CVarDef<string> AudioOutputMode =
        CVarDef.Create("audio.output_mode", "auto", CVar.CLIENTONLY | CVar.ARCHIVE);

    /// <summary>
    /// When no specific <see cref="AudioDevice"/> is selected, switch along when the OS default output changes
    /// (e.g. headphones plugged in).
    /// </summary>
    public static readonly CVarDef<bool> AudioFollowDefaultDevice =
        CVarDef.Create("audio.follow_default_device", true, CVar.CLIENTONLY | CVar.ARCHIVE);

    /// <summary>
    /// If true, replay checkpoints are stored compressed (keyframes + deltas) in a temporary file and only loaded
    /// on demand, instead of keeping every checkpoint as a full game state in RAM.
    /// </summary>
    public static readonly CVarDef<bool> ReplayCheckpointDiskCache =
        CVarDef.Create("replay.checkpoint_disk_cache", true, CVar.CLIENTONLY);

    /// <summary>
    /// How many checkpoints may be resident in memory at once when <see cref="ReplayCheckpointDiskCache"/> is on.
    /// </summary>
    public static readonly CVarDef<int> ReplayCheckpointsResident =
        CVarDef.Create("replay.checkpoints_resident", 3, CVar.CLIENTONLY);
}
