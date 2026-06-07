using System.Collections.Generic;

namespace GxPT
{
    // A slash command that expands a template into a message sent to the model. The template may contain
    // the placeholder {args}; every occurrence is replaced with the supplied argument text. If the
    // template has no placeholder and an argument is supplied, the argument is appended on its own line
    // so nothing the user typed is silently dropped.
    internal sealed class PromptCommand : ISlashCommand
    {
        public const string ArgsPlaceholder = "{args}";

        private readonly string _name;
        private readonly IList<string> _aliases;
        private readonly string _description;
        private readonly string _argumentHint;
        private readonly IList<string> _requires;
        private readonly IList<string> _requiresAny;
        private readonly bool _takesPath;
        private readonly string _template;

        public PromptCommand(string name, string description, string template,
            string argumentHint, bool takesPathArgument,
            IList<string> aliases, IList<string> requires, IList<string> requiresAny)
        {
            _name = name ?? string.Empty;
            _description = description ?? string.Empty;
            _template = template ?? string.Empty;
            _argumentHint = argumentHint ?? string.Empty;
            _takesPath = takesPathArgument;
            _aliases = aliases ?? new List<string>();
            _requires = requires ?? new List<string>();
            _requiresAny = requiresAny ?? new List<string>();
        }

        public string Name { get { return _name; } }
        public IList<string> Aliases { get { return _aliases; } }
        public string Description { get { return _description; } }
        public string ArgumentHint { get { return _argumentHint; } }
        public SlashCommandKind Kind { get { return SlashCommandKind.Prompt; } }
        public IList<string> Requires { get { return _requires; } }
        public IList<string> RequiresAny { get { return _requiresAny; } }
        public bool TakesPathArgument { get { return _takesPath; } }
        public string Template { get { return _template; } }

        public SlashCommandResult Invoke(string args, ISlashCommandContext ctx)
        {
            string a = (args ?? string.Empty).Trim();
            string expanded;
            if (_template.IndexOf(ArgsPlaceholder, System.StringComparison.Ordinal) >= 0)
            {
                expanded = _template.Replace(ArgsPlaceholder, a);
            }
            else if (a.Length > 0)
            {
                expanded = _template + "\n\n" + a;
            }
            else
            {
                expanded = _template;
            }
            return SlashCommandResult.Send(expanded);
        }
    }
}
