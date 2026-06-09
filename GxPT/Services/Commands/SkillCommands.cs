using System;
using System.Collections.Generic;
using System.Text;

namespace GxPT
{
    // Slash commands for the skills feature (design sec.6), built on the existing ISlashCommand framework:
    //   /skills [on|off|reset] [here|global]            -- list, or toggle/reset the whole feature
    //   /skill <slug> [on|off|reset] [here|global]      -- toggle/reset one skill (bare slug toggles)
    //   /use <slug> [text]                              -- use a skill (body attached as hidden context)
    // Management commands are Client (local, no LLM send); /use sends a short "Use the X skill" ask and
    // attaches the skill body as a hidden system message (it never enters the transcript). Scope
    // defaults to "here" (this conversation); "global" edits skills.json. Conversation overrides are
    // read/written through ISlashCommandContext; the global default through SkillEnablement directly.
    internal static class SkillCommands
    {
        public static IList<ISlashCommand> BuiltIns()
        {
            List<ISlashCommand> list = new List<ISlashCommand>();
            list.Add(new SkillsCommand());
            list.Add(new SkillCommand());
            list.Add(new UseCommand());
            return list;
        }

        // ---- shared helpers ----

        internal static SkillCatalog BuildCatalog(ISlashCommandContext ctx)
        {
            string workdir = ctx != null ? ctx.WorkingDir : null;
            return SkillInjection.BuildCatalog(AppDomain.CurrentDomain.BaseDirectory, workdir);
        }

        // Splits raw args on whitespace into non-empty tokens.
        internal static string[] Tokens(string args)
        {
            if (string.IsNullOrEmpty(args)) return new string[0];
            return args.Trim().Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        }

        // true = on, false = off, null = not an on/off word.
        internal static bool? ParseOnOff(string token)
        {
            return SlashArgs.ParseOnOff(token);
        }

        // Recognizes the trailing scope word. Returns false for anything that isn't here/global.
        internal static bool TryScope(string token, out bool isGlobal)
        {
            isGlobal = false;
            if (string.IsNullOrEmpty(token)) return false;
            string t = token.Trim().ToLowerInvariant();
            if (t == "global") { isGlobal = true; return true; }
            if (t == "here") { isGlobal = false; return true; }
            return false;
        }

        // Resolve a user-typed slug (exact, else kebab-normalized) against the catalog.
        internal static bool ResolveSkill(SkillCatalog cat, string typed, out Skill skill)
        {
            skill = null;
            if (cat == null || string.IsNullOrEmpty(typed)) return false;
            if (cat.TryGet(typed, out skill)) return true;
            string norm = SkillSlug.Make(typed);
            return !string.IsNullOrEmpty(norm) && cat.TryGet(norm, out skill);
        }

        internal static bool? ConvOverrideFor(ISlashCommandContext ctx, string slug)
        {
            IDictionary<string, bool> ov = ctx.GetConversationSkillOverrides();
            bool v;
            if (ov != null && slug != null && ov.TryGetValue(slug, out v)) return v;
            return null;
        }
    }

    // /skills [on|off|reset] [here|global]
    internal sealed class SkillsCommand : ClientCommandBase, IArgumentCompleter
    {
        public override string Name { get { return "skills"; } }
        public override string Description { get { return "List skills, or turn the feature on/off"; } }
        public override string ArgumentHint { get { return "[on|off|reset] [here|global]"; } }

        public override SlashCommandResult Invoke(string args, ISlashCommandContext ctx)
        {
            string[] tok = SkillCommands.Tokens(args);
            SkillCatalog cat = SkillCommands.BuildCatalog(ctx);
            SkillEnablement global = SkillEnablement.LoadGlobal();

            if (tok.Length == 0)
            {
                ctx.WriteInfo(BuildList(cat, global, ctx));
                return SlashCommandResult.Handled();
            }
            if (tok.Length > 2) // verb + optional scope; reject trailing junk rather than silently ignore
                return SlashCommandResult.Fail("Usage: /skills [on|off|reset] [here|global]");

            bool isGlobal = false;
            if (tok.Length >= 2 && !SkillCommands.TryScope(tok[1], out isGlobal))
                return SlashCommandResult.Fail("Unknown scope '" + tok[1] + "'. Use 'here' or 'global'.");

            string verb = tok[0].ToLowerInvariant();
            if (verb == "reset")
            {
                if (isGlobal)
                {
                    global.FeatureOff = false;
                    global.ClearSkillOverrides();
                    global.SaveGlobal();
                    ctx.WriteInfo("Skills: global defaults reset (feature on, no per-skill settings).");
                }
                else
                {
                    ctx.ResetConversationSkills();
                    ctx.WriteInfo("Skills: cleared this conversation's overrides.");
                }
                ctx.RefreshSkillsServer(); // the Skills MCP server follows skill enablement
                return SlashCommandResult.Handled();
            }

            bool? onoff = SkillCommands.ParseOnOff(verb);
            if (!onoff.HasValue)
                return SlashCommandResult.Fail("Usage: /skills [on|off|reset] [here|global]");

            bool on = onoff.Value;
            string where = isGlobal ? "globally" : "for this conversation";
            if (isGlobal)
            {
                global.FeatureOff = !on;
                global.SaveGlobal();
            }
            else
            {
                ctx.SetConversationSkillsFeatureOff(on ? (bool?)false : (bool?)true);
            }
            ctx.RefreshSkillsServer(); // the Skills MCP server follows skill enablement
            ctx.WriteInfo("Skills turned " + (on ? "on" : "off") + " " + where + ".");
            return SlashCommandResult.Handled();
        }

        private static string BuildList(SkillCatalog cat, SkillEnablement global, ISlashCommandContext ctx)
        {
            bool? convFeatureOff = ctx.GetConversationSkillsFeatureOff();
            IDictionary<string, bool> convOv = ctx.GetConversationSkillOverrides();

            StringBuilder sb = new StringBuilder();
            sb.Append("Skills \u2014 most specific setting wins.");
            // The feature toggle (rungs 3-4) = the default for any skill with no per-skill setting. The
            // "here" half only shows when this conversation has set it (otherwise it inherits global).
            sb.Append("\nDefault: ").Append(global.FeatureOff ? "OFF" : "ON").Append(" globally");
            if (convFeatureOff.HasValue)
                sb.Append(" \u00b7 ").Append(convFeatureOff.Value ? "OFF" : "ON").Append(" here");

            IList<Skill> skills = cat.Skills;
            if (skills == null || skills.Count == 0)
            {
                sb.Append("\nNo skills found.");
                return sb.ToString();
            }

            int width = 0;
            for (int i = 0; i < skills.Count; i++)
                if (skills[i].Slug != null && skills[i].Slug.Length > width) width = skills[i].Slug.Length;

            sb.Append("\n"); // blank line before the list
            for (int i = 0; i < skills.Count; i++)
            {
                Skill s = skills[i];
                bool v;
                bool? ov = (convOv != null && convOv.TryGetValue(s.Slug, out v)) ? (bool?)v : null;
                SkillRule rule;
                bool enabled = SkillResolve.Resolve(global, s.Slug, ov, convFeatureOff, out rule);
                sb.Append("\n- ").Append((s.Slug != null ? s.Slug : "").PadRight(width)).Append("  ")
                  .Append(enabled ? "ON " : "OFF").Append("  (").Append(ReasonText(rule, enabled)).Append(")");
            }
            return sb.ToString();
        }

        // Human-readable form of the rule that decided a skill's state (for the /skills list).
        private static string ReasonText(SkillRule rule, bool enabled)
        {
            string st = enabled ? "on" : "off";
            switch (rule)
            {
                case SkillRule.SkillHere: return st + " here";
                case SkillRule.SkillGlobal: return st + " globally";
                case SkillRule.FeatureHere: return "all skills " + st + " here";
                case SkillRule.FeatureGlobal: return "all skills " + st;
                default: return "default";
            }
        }

        public IList<ArgCompletion> CompleteArgument(string argText, ISlashCommandContext ctx)
        {
            List<ArgCompletion> result = new List<ArgCompletion>();
            string a = argText ?? string.Empty;
            int sp = a.IndexOf(' ');
            if (sp < 0)
            {
                // Offer "run with no arguments" (list) as the default entry, so the bare /skills command
                // is selectable from the popup; then the verb choices.
                if (a.Length == 0)
                    result.Add(new ArgCompletion("(list current skills)", "", false));
                AddMatching(result, new string[] { "on", "off", "reset" }, a, "", true);
            }
            else
            {
                string first = a.Substring(0, sp);
                string rest = a.Substring(sp + 1).TrimStart();
                AddMatching(result, new string[] { "here", "global" }, rest, first + " ", false);
            }
            return result;
        }

        // Adds choices that match the partial token. InsertArg = prefix + choice, plus a trailing space
        // when there is a further level (cont) so accepting it advances the popup to that next level
        // immediately (matching name-mode / the /tool completer) instead of waiting for a typed space.
        internal static void AddMatching(List<ArgCompletion> into, string[] choices, string partial,
            string prefix, bool cont)
        {
            for (int i = 0; i < choices.Length; i++)
            {
                if (partial.Length > 0 && !choices[i].StartsWith(partial, StringComparison.OrdinalIgnoreCase))
                    continue;
                into.Add(new ArgCompletion(choices[i], prefix + choices[i] + (cont ? " " : ""), cont));
            }
        }
    }

    // /skill <slug> [on|off|reset] [here|global]   (bare "/skill <slug>" toggles for this conversation)
    internal sealed class SkillCommand : ClientCommandBase, IArgumentCompleter
    {
        public override string Name { get { return "skill"; } }
        public override string Description { get { return "Enable or disable one skill"; } }
        public override string ArgumentHint { get { return "<slug> [on|off|reset] [here|global]"; } }

        public override SlashCommandResult Invoke(string args, ISlashCommandContext ctx)
        {
            string[] tok = SkillCommands.Tokens(args);
            if (tok.Length == 0 || tok.Length > 3) // slug + optional verb + optional scope; no trailing junk
                return SlashCommandResult.Fail("Usage: /skill <slug> [on|off|reset] [here|global]");

            SkillCatalog cat = SkillCommands.BuildCatalog(ctx);
            Skill skill;
            if (!SkillCommands.ResolveSkill(cat, tok[0], out skill))
                return SlashCommandResult.Fail("Unknown skill: " + tok[0]);
            string slug = skill.Slug;
            SkillEnablement global = SkillEnablement.LoadGlobal();

            // Bare "/skill <slug>": toggle the effective state for this conversation.
            if (tok.Length == 1)
            {
                bool current = SkillResolve.IsEnabled(global, slug,
                    SkillCommands.ConvOverrideFor(ctx, slug), ctx.GetConversationSkillsFeatureOff());
                ctx.SetConversationSkillOverride(slug, !current);
                ctx.RefreshSkillsServer(); // the Skills MCP server follows skill enablement
                ctx.WriteInfo("Skill '" + slug + "' " + (!current ? "enabled" : "disabled") + " for this conversation.");
                return SlashCommandResult.Handled();
            }

            bool isGlobal = false;
            if (tok.Length >= 3 && !SkillCommands.TryScope(tok[2], out isGlobal))
                return SlashCommandResult.Fail("Unknown scope '" + tok[2] + "'. Use 'here' or 'global'.");

            string verb = tok[1].ToLowerInvariant();
            if (verb == "reset")
            {
                if (isGlobal) { global.SetSkillOverride(slug, null); global.SaveGlobal(); ctx.WriteInfo("Skill '" + slug + "': global setting cleared."); }
                else { ctx.SetConversationSkillOverride(slug, null); ctx.WriteInfo("Skill '" + slug + "': conversation override cleared."); }
                ctx.RefreshSkillsServer(); // the Skills MCP server follows skill enablement
                return SlashCommandResult.Handled();
            }

            bool? onoff = SkillCommands.ParseOnOff(verb);
            if (!onoff.HasValue)
                return SlashCommandResult.Fail("Usage: /skill <slug> [on|off|reset] [here|global]");

            bool on = onoff.Value;
            string where = isGlobal ? "globally" : "for this conversation";
            if (isGlobal) { global.SetSkillOverride(slug, on); global.SaveGlobal(); }
            else { ctx.SetConversationSkillOverride(slug, on); }
            ctx.RefreshSkillsServer(); // the Skills MCP server follows skill enablement
            ctx.WriteInfo("Skill '" + slug + "' " + (on ? "enabled" : "disabled") + " " + where + ".");
            return SlashCommandResult.Handled();
        }

        public IList<ArgCompletion> CompleteArgument(string argText, ISlashCommandContext ctx)
        {
            List<ArgCompletion> result = new List<ArgCompletion>();
            string a = argText ?? string.Empty;
            int sp = a.IndexOf(' ');
            if (sp < 0)
            {
                // First token: skill slugs, annotated with their effective state.
                SkillCatalog cat = SkillCommands.BuildCatalog(ctx);
                SkillEnablement global = SkillEnablement.LoadGlobal();
                bool? convFeatureOff = ctx.GetConversationSkillsFeatureOff();
                IList<Skill> skills = cat.Skills;
                for (int i = 0; i < skills.Count; i++)
                {
                    string slug = skills[i].Slug;
                    if (a.Length > 0 && !slug.StartsWith(a, StringComparison.OrdinalIgnoreCase)) continue;
                    bool enabled = SkillResolve.IsEnabled(global, slug, SkillCommands.ConvOverrideFor(ctx, slug), convFeatureOff);
                    result.Add(new ArgCompletion(slug + "  (" + (enabled ? "on" : "off") + ")", slug + " ", true));
                }
            }
            else
            {
                int sp2 = a.IndexOf(' ', sp + 1);
                if (sp2 < 0)
                {
                    string slug = a.Substring(0, sp);
                    string rest = a.Substring(sp + 1).TrimStart();
                    SkillsCommand.AddMatching(result, new string[] { "on", "off", "reset" }, rest, slug + " ", true);
                }
                else
                {
                    string head = a.Substring(0, sp2);
                    string rest = a.Substring(sp2 + 1).TrimStart();
                    SkillsCommand.AddMatching(result, new string[] { "here", "global" }, rest, head + " ", false);
                }
            }
            return result;
        }
    }

    // /use <slug> [text] -- invoke a skill explicitly, regardless of its enabled state. Sends a short
    // user message ("Use the <slug> skill. [text]") and attaches the skill's full instructions as a
    // HIDDEN system message (context the model sees but the transcript never shows) - so the body never
    // clutters the user transcript. Custom behavior, not a generic prompt expansion.
    internal sealed class UseCommand : ISlashCommand, IArgumentCompleter
    {
        private static readonly IList<string> EmptyList = new List<string>().AsReadOnly();

        public string Name { get { return "use"; } }
        public IList<string> Aliases { get { return EmptyList; } }
        public string Description { get { return "Use a skill (loads it as context)"; } }
        public string ArgumentHint { get { return "<slug> [message]"; } }
        public SlashCommandKind Kind { get { return SlashCommandKind.Client; } }
        public IList<string> Requires { get { return EmptyList; } }
        public IList<string> RequiresAny { get { return EmptyList; } }
        public bool TakesPathArgument { get { return false; } }

        public SlashCommandResult Invoke(string args, ISlashCommandContext ctx)
        {
            string a = (args ?? string.Empty).Trim();
            if (a.Length == 0) return SlashCommandResult.Fail("Usage: /use <slug> [message]");

            string slugArg, rest;
            int sp = a.IndexOf(' ');
            if (sp < 0) { slugArg = a; rest = string.Empty; }
            else { slugArg = a.Substring(0, sp); rest = a.Substring(sp + 1).Trim(); }

            SkillCatalog cat = SkillCommands.BuildCatalog(ctx);
            Skill skill;
            if (!SkillCommands.ResolveSkill(cat, slugArg, out skill))
                return SlashCommandResult.Fail("Unknown skill: " + slugArg);

            // The skill body rides as a hidden system message (committed at send, not now), so the
            // transcript shows only the short ask and an early return can't orphan it.
            string systemContext =
                "The user invoked this skill with /use. Follow its instructions for their request.\n\n"
                + SkillTools.RenderSkill(skill);

            string msg = "Use the " + skill.Slug + " skill.";
            if (rest.Length > 0) msg += " " + rest;
            return SlashCommandResult.Send(msg, systemContext);
        }

        public IList<ArgCompletion> CompleteArgument(string argText, ISlashCommandContext ctx)
        {
            List<ArgCompletion> result = new List<ArgCompletion>();
            string a = argText ?? string.Empty;
            if (a.IndexOf(' ') >= 0) return result; // only complete the first token (the slug)

            SkillCatalog cat = SkillCommands.BuildCatalog(ctx);
            IList<Skill> skills = cat.Skills;
            for (int i = 0; i < skills.Count; i++)
            {
                string slug = skills[i].Slug;
                if (a.Length > 0 && !slug.StartsWith(a, StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(new ArgCompletion(slug + " - " + skills[i].Description, slug + " ", false));
            }
            return result;
        }
    }
}
