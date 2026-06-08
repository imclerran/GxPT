using System;
using System.Collections.Generic;

namespace GxPT
{
    // Built-in client commands: intercepted locally and executed against ISlashCommandContext rather
    // than sent to the model. Registered alongside the prompt commands by the host.
    internal static class ClientCommands
    {
        public static IList<ISlashCommand> BuiltIns()
        {
            List<ISlashCommand> list = new List<ISlashCommand>();
            list.Add(new ModelCommand());
            list.Add(new ToolCommand());
            list.Add(new NewCommand());
            list.Add(new ExportCommand());
            return list;
        }
    }

    // Shared boilerplate for client commands (empty alias/requires lists, Client kind, no path arg).
    internal abstract class ClientCommandBase : ISlashCommand
    {
        private static readonly IList<string> EmptyList = new List<string>().AsReadOnly();

        public abstract string Name { get; }
        public abstract string Description { get; }
        public virtual IList<string> Aliases { get { return EmptyList; } }
        public virtual string ArgumentHint { get { return string.Empty; } }
        public SlashCommandKind Kind { get { return SlashCommandKind.Client; } }
        public virtual IList<string> Requires { get { return EmptyList; } }
        public virtual IList<string> RequiresAny { get { return EmptyList; } }
        public virtual bool TakesPathArgument { get { return false; } }
        public abstract SlashCommandResult Invoke(string args, ISlashCommandContext ctx);
    }

    // /model <author/model> -- switch the active model, completing the OpenRouter slug in two levels
    // (author "/" first, then model).
    internal sealed class ModelCommand : ClientCommandBase, IArgumentCompleter
    {
        public override string Name { get { return "model"; } }
        public override string Description { get { return "Switch the active model"; } }
        public override string ArgumentHint { get { return "<author/model>"; } }

        public override SlashCommandResult Invoke(string args, ISlashCommandContext ctx)
        {
            string slug = (args ?? string.Empty).Trim();
            if (slug.Length == 0) return SlashCommandResult.Fail("Usage: /model <author/model>");
            ctx.SetModel(slug); // silent: the model combo reflects the change
            return SlashCommandResult.Handled();
        }

        public IList<ArgCompletion> CompleteArgument(string argText, ISlashCommandContext ctx)
        {
            List<ArgCompletion> result = new List<ArgCompletion>();
            IList<string> models = ctx != null ? ctx.GetModels() : null;
            if (models == null) return result;

            string a = argText ?? string.Empty;
            int slash = a.IndexOf('/');
            if (slash < 0)
            {
                // First level: distinct authors matching the prefix.
                Dictionary<string, bool> seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < models.Count; i++)
                {
                    string slug = models[i];
                    if (string.IsNullOrEmpty(slug)) continue;
                    int s = slug.IndexOf('/');
                    string author = s >= 0 ? slug.Substring(0, s) : slug;
                    if (a.Length > 0 && !author.StartsWith(a, StringComparison.OrdinalIgnoreCase)) continue;
                    if (seen.ContainsKey(author)) continue;
                    seen[author] = true;
                    result.Add(new ArgCompletion(author + "/", author + "/", true));
                }
            }
            else
            {
                // Second level: models under the typed author matching the partial model name.
                string author = a.Substring(0, slash);
                string rest = a.Substring(slash + 1);
                for (int i = 0; i < models.Count; i++)
                {
                    string slug = models[i];
                    if (string.IsNullOrEmpty(slug)) continue;
                    int s = slug.IndexOf('/');
                    if (s < 0) continue;
                    if (!string.Equals(slug.Substring(0, s), author, StringComparison.OrdinalIgnoreCase)) continue;
                    string model = slug.Substring(s + 1);
                    if (rest.Length > 0 && !model.StartsWith(rest, StringComparison.OrdinalIgnoreCase)) continue;
                    result.Add(new ArgCompletion(slug, slug, false));
                }
            }
            return result;
        }
    }

    // /tool <name> [on|off] -- enable/disable a built-in MCP tool server (omit on|off to toggle).
    internal sealed class ToolCommand : ClientCommandBase, IArgumentCompleter
    {
        public override string Name { get { return "tool"; } }
        public override string Description { get { return "Enable or disable a tool server"; } }
        public override string ArgumentHint { get { return "<name> [on|off]"; } }

        public override SlashCommandResult Invoke(string args, ISlashCommandContext ctx)
        {
            string a = (args ?? string.Empty).Trim();
            if (a.Length == 0) return SlashCommandResult.Fail("Usage: /tool <name> [on|off]");

            string name, rest;
            int sp = a.IndexOf(' ');
            if (sp < 0) { name = a; rest = string.Empty; }
            else { name = a.Substring(0, sp); rest = a.Substring(sp + 1).Trim(); }

            // Match (and canonicalize) the name against the known servers.
            IList<string> names = ctx.GetServerNames();
            bool known = false;
            if (names != null)
            {
                for (int i = 0; i < names.Count; i++)
                {
                    if (string.Equals(names[i], name, StringComparison.OrdinalIgnoreCase)) { name = names[i]; known = true; break; }
                }
            }
            if (!known) return SlashCommandResult.Fail("Unknown server: " + name);

            bool target;
            if (rest.Length == 0) target = !ctx.GetServerEnabled(name); // toggle
            else if (IsOn(rest)) target = true;
            else if (IsOff(rest)) target = false;
            else return SlashCommandResult.Fail("Use 'on' or 'off' (or omit to toggle).");

            string err = ctx.SetServerEnabled(name, target);
            if (err != null) return SlashCommandResult.Fail(err);
            // Silent: /tool completion shows each server's current on/off state.
            return SlashCommandResult.Handled();
        }

        public IList<ArgCompletion> CompleteArgument(string argText, ISlashCommandContext ctx)
        {
            List<ArgCompletion> result = new List<ArgCompletion>();
            if (ctx == null) return result;

            string a = argText ?? string.Empty;
            int sp = a.IndexOf(' ');
            if (sp < 0)
            {
                // First token: server names, annotated with their current state.
                IList<string> names = ctx.GetServerNames();
                if (names == null) return result;
                for (int i = 0; i < names.Count; i++)
                {
                    string n = names[i];
                    if (string.IsNullOrEmpty(n)) continue;
                    if (a.Length > 0 && !n.StartsWith(a, StringComparison.OrdinalIgnoreCase)) continue;
                    string state = ctx.GetServerEnabled(n) ? "on" : "off";
                    result.Add(new ArgCompletion(n + "  (" + state + ")", n + " ", true));
                }
            }
            else
            {
                // Second token: on / off.
                string name = a.Substring(0, sp);
                string rest = a.Substring(sp + 1).TrimStart();
                string[] choices = new string[] { "on", "off" };
                for (int i = 0; i < choices.Length; i++)
                {
                    if (rest.Length > 0 && !choices[i].StartsWith(rest, StringComparison.OrdinalIgnoreCase)) continue;
                    result.Add(new ArgCompletion(choices[i], name + " " + choices[i], false));
                }
            }
            return result;
        }

        private static bool IsOn(string s)
        {
            s = s.Trim().ToLowerInvariant();
            return s == "on" || s == "true" || s == "1" || s == "yes" || s == "enable" || s == "enabled";
        }

        private static bool IsOff(string s)
        {
            s = s.Trim().ToLowerInvariant();
            return s == "off" || s == "false" || s == "0" || s == "no" || s == "disable" || s == "disabled";
        }
    }

    // /new -- open a fresh conversation tab.
    internal sealed class NewCommand : ClientCommandBase
    {
        public override string Name { get { return "new"; } }
        public override string Description { get { return "Start a new conversation"; } }

        public override SlashCommandResult Invoke(string args, ISlashCommandContext ctx)
        {
            ctx.NewConversation();
            return SlashCommandResult.Handled();
        }
    }

    // /export -- export conversations to a zip archive.
    internal sealed class ExportCommand : ClientCommandBase
    {
        public override string Name { get { return "export"; } }
        public override string Description { get { return "Export conversations to a zip"; } }

        public override SlashCommandResult Invoke(string args, ISlashCommandContext ctx)
        {
            ctx.ExportConversations();
            return SlashCommandResult.Handled();
        }
    }
}
