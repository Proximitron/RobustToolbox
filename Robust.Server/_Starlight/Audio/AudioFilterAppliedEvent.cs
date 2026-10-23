using Robust.Shared.Audio.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Player;

namespace Robust.Server.Audio;

/// <summary>
/// Raised directed at an audio entity once a server <c>Play*</c> call has restricted it to <see cref="Filter"/>.
/// </summary>
/// <remarks>
/// Replays record every audio entity regardless of its filter, and <see cref="AudioComponent.IncludedEntities"/>
/// isn't networked, so a replay can't tell who a sound was meant for. This lets content record that.
/// <see cref="RecordReplay"/> is the flag the caller passed, which the server audio system doesn't otherwise act on.
/// </remarks>
[ByRefEvent]
public readonly record struct AudioFilterAppliedEvent(Filter Filter, bool RecordReplay);
