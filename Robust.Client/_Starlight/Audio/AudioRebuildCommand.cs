using Robust.Shared.Console;
using Robust.Shared.IoC;

// ReSharper disable once CheckNamespace
namespace Robust.Client.Audio;

/// <summary>
/// Re-creates the OpenAL device the way an unplugged/replugged output does, to check that audio survives it.
/// </summary>
internal sealed partial class AudioRebuildCommand : IConsoleCommand
{
    [Dependency] private IAudioInternal _audio = default!;

    public string Command => "audio_rebuild";
    public string Description => "Re-creates the audio device, moving all playing sounds over.";
    public string Help => "audio_rebuild [loopback] - with 'loopback' switches to silent output first, it returns to the real device within a few seconds.";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (_audio is not AudioManager manager)
        {
            shell.WriteError("No OpenAL audio on this client.");
            return;
        }

        manager.DebugRebuild(args.Length > 0 && args[0] == "loopback");
        shell.WriteLine($"Audio device re-created, generation {manager.DeviceGeneration}.");
    }
}
