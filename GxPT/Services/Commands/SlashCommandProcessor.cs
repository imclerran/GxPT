namespace GxPT
{
    // Parses and dispatches a slash command from the input text. Per the design constraint, a command is
    // only recognized when the slash is at position 0 of the field (the input never contains text before
    // the command), which makes parsing trivial and avoids hijacking slashes inside ordinary messages.
    //
    // Process returns:
    //   * null                          -> not a command; send the original text unchanged.
    //   * SlashCommandResult.Send(...)   -> a prompt command expanded; send TextToSend instead.
    //   * SlashCommandResult.Fail(...)   -> a precondition failed; show Error, do not send.
    //   * SlashCommandResult.Handled()   -> a client command ran locally; do not send.
    internal sealed class SlashCommandProcessor
    {
        private readonly SlashCommandRegistry _registry;

        public SlashCommandProcessor(SlashCommandRegistry registry)
        {
            _registry = registry;
        }

        public SlashCommandResult Process(string raw, ISlashCommandContext ctx)
        {
            if (_registry == null) return null;
            if (string.IsNullOrEmpty(raw)) return null;
            if (raw[0] != '/') return null; // position-0 rule: only a leading slash is a command

            string body = raw.Substring(1);
            string name, args;
            SplitFirstToken(body, out name, out args);
            if (name.Length == 0) return null; // a bare "/" is just literal text

            ISlashCommand cmd;
            if (!_registry.TryResolve(name, out cmd))
                return null; // unknown token -> not a command; send the text as typed

            // Gate on server availability so a command that cannot work is refused with a clear reason
            // rather than silently producing a message the model can't act on.
            string reason = SlashCommandGate.UnavailableReason(cmd, ctx);
            if (reason != null)
                return SlashCommandResult.Fail("/" + cmd.Name + " " + reason + ".");

            // Validate a path argument before anything is sent, using the same rule the file server
            // enforces (relative, no drive letter, no "..").
            if (cmd.TakesPathArgument)
            {
                string pathError = WorkspacePath.Validate(args);
                if (pathError != null)
                    return SlashCommandResult.Fail(pathError);
            }

            return cmd.Invoke(args, ctx);
        }

        // Splits "name rest of the args" into the first whitespace-delimited token and the remainder.
        private static void SplitFirstToken(string body, out string name, out string args)
        {
            name = string.Empty;
            args = string.Empty;
            if (body == null) return;

            int i = 0;
            int n = body.Length;
            // Skip nothing at the front: the command name starts immediately after the slash.
            int start = i;
            while (i < n && !char.IsWhiteSpace(body[i])) i++;
            name = body.Substring(start, i - start);
            // Skip the whitespace separating name from args.
            while (i < n && char.IsWhiteSpace(body[i])) i++;
            if (i < n) args = body.Substring(i);
        }
    }
}
