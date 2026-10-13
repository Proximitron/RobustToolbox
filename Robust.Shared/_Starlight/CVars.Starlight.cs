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
    /// Each one is a full copy of the world's entity states. The store additionally keeps a decoding cursor at the most
    /// recently loaded checkpoint, so jumping back to it again is cheap even with 1.
    /// </summary>
    public static readonly CVarDef<int> ReplayCheckpointsResident =
        CVarDef.Create("replay.checkpoints_resident", 1, CVar.CLIENTONLY);

    /// <summary>
    /// Minimum time (in milliseconds) the replay loading job may run per frame before yielding to render the loading
    /// screen. Content may request a shorter slice (e.g. one 60 FPS frame), which leaves the loader idle for most of
    /// the time while vsync/rendering runs.
    /// </summary>
    public static readonly CVarDef<int> ReplayLoadTimeSlice =
        CVarDef.Create("replay.load_time_slice_ms", 100, CVar.CLIENTONLY | CVar.ARCHIVE);

    /// <summary>
    /// Fixes vertex colors passed through <c>DrawPrimitives</c> (circles, polygons, gradients) being converted from
    /// sRGB to linear twice, which made them come out much darker than the requested color.
    /// </summary>
    public static readonly CVarDef<bool> DisplayPrimitiveColorFix =
        CVarDef.Create("display.primitive_color_fix", true, CVar.CLIENTONLY | CVar.ARCHIVE);
}
