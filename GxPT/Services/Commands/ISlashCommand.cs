using System.Collections.Generic;

namespace GxPT
{
    // One registered slash command. The dispatch pipeline (SlashCommandProcessor) and the autocomplete
    // popup only ever see this interface, so adding new command kinds is registration-only.
    //
    // Server gating is data, not code: a command is available when every name in Requires is present
    // AND (RequiresAny is empty OR at least one of its names is present). See SlashCommandGate.
    internal interface ISlashCommand
    {
        // Invocation name without the leading slash, lower-case (e.g. "commit").
        string Name { get; }

        // Optional alternative names that also resolve to this command (lower-case).
        IList<string> Aliases { get; }

        // One-line description shown in the autocomplete popup.
        string Description { get; }

        // Human hint for the argument, shown in the popup (e.g. "[path]"). May be empty.
        string ArgumentHint { get; }

        SlashCommandKind Kind { get; }

        // All of these server toolsets must be available for the command to run. May be empty.
        IList<string> Requires { get; }

        // At least one of these server toolsets must be available (used for alternatives such as
        // build-via-msbuild OR build-via-command). Empty means "no alternative constraint".
        IList<string> RequiresAny { get; }

        // True when the (single) argument is a workspace path. Drives path-aware autocomplete and the
        // pre-send path validation in the processor.
        bool TakesPathArgument { get; }

        // Run the command. args is the raw text after the command name (may be empty).
        SlashCommandResult Invoke(string args, ISlashCommandContext ctx);
    }
}
