using Robust.Client.Graphics.Clyde;
using Robust.Shared.Console;

namespace Robust.Client.Graphics;

internal sealed class RenderDocCaptureCommand : LocalizedCommands
{
    public override string Command => "renderdoc_capture";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (!RenderDoc.IsLoaded)
        {
            shell.WriteError(Loc.GetString("cmd-renderdoc-not-loaded"));
            return;
        }

        var frames = 1u;
        if (args.Length > 0 && (!uint.TryParse(args[0], out frames) || frames == 0))
        {
            shell.WriteError(Help);
            return;
        }

        RenderDoc.TriggerCapture(frames);
        shell.WriteLine(Loc.GetString("cmd-renderdoc_capture-triggered", ("frames", frames)));
    }
}

internal sealed class RenderDocUiCommand : LocalizedCommands
{
    public override string Command => "renderdoc_ui";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (!RenderDoc.IsLoaded)
        {
            shell.WriteError(Loc.GetString("cmd-renderdoc-not-loaded"));
            return;
        }

        foreach (var capture in RenderDoc.GetCaptures())
            shell.WriteLine(capture);

        if (RenderDoc.OpenReplayUI() == 0)
            shell.WriteError(Loc.GetString("cmd-renderdoc_ui-failed"));
    }
}
