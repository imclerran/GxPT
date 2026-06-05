using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxPT
{
    // The real approval gate (approval spec §3) behind the orchestrator's IToolApprovalPolicy.Check.
    // Classifies the tool, consults remembered approvals (tool-name set + argument rules), and only
    // prompts (via IToolApprovalPrompt) when not already allowed; persists the user's remembered
    // choice. Pure logic + an injected prompt + an injected store — no UI here, so it's unit-testable.
    internal sealed class ToolApprovalPolicy : IToolApprovalPolicy
    {
        // Identifies first-party servers (whose tools use the hardcoded classification table).
        private static readonly Dictionary<string, bool> _firstPartyServers = BuildFirstPartySet();

        private readonly IToolClassifier _classifier;
        private readonly IToolApprovalPrompt _prompt;
        private readonly IApprovalStore _store;

        public ToolApprovalPolicy(IToolClassifier classifier, IToolApprovalPrompt prompt, IApprovalStore store)
        {
            _classifier = classifier != null ? classifier : new ToolClassifier();
            _prompt = prompt;
            _store = store != null ? store : new InMemoryApprovalStore();
        }

        // The orchestrator calls this. functionName is the qualified name; args already parsed.
        public ApprovalDecision Check(string functionName, JObject args)
        {
            string server = ServerOf(functionName);
            bool firstParty = server != null && _firstPartyServers.ContainsKey(server);
            JObject annotations = null; // third-party annotations not yet plumbed; unknown -> Write/Tool
            ToolPolicy pol = _classifier.Classify(functionName, annotations, firstParty);

            // Read-only tools never modify anything -> always allowed, no prompt. Driven by the
            // classified tier (not a name list), so every ReadOnly tool auto-allows: the read-only
            // built-ins (files read/list/search, git status/diff/log/fetch, web search/extract,
            // memory read_memory) and any future ReadOnly tool. Third-party tools can't currently
            // reach ReadOnly (their annotations aren't plumbed; unknown -> Write); if that changes and
            // you don't want to trust a server's self-declared readOnlyHint, add "&& firstParty" here.
            if (pol.Tier == ToolTier.ReadOnly)
                return ApprovalDecision.Allow;

            // Already-remembered fast paths.
            if (pol.Scope == RememberScope.Tool && _store.IsToolApproved(functionName))
                return ApprovalDecision.Allow;
            if (pol.Scope == RememberScope.Argument && MatchesAnyRule(functionName, pol, args))
                return ApprovalDecision.Allow;

            // Not remembered (or Scope==None) -> prompt.
            if (_prompt == null) return ApprovalDecision.Deny; // no UI available -> safe default

            ApprovalRequest req = new ApprovalRequest();
            req.ServerName = server;
            req.FunctionName = functionName;
            req.ToolName = ToolOf(functionName);
            req.Policy = pol;
            req.Arguments = args;
            req.Preview = BuildPreview(functionName, pol, args);

            ApprovalChoice choice = _prompt.Ask(req);
            Persist(req, choice);
            return choice == ApprovalChoice.Deny ? ApprovalDecision.Deny : ApprovalDecision.Allow;
        }

        // ---- remembered-rule matching (approval spec §3) ----

        private bool MatchesAnyRule(string functionName, ToolPolicy pol, JObject args)
        {
            string val = ArgValue(args, pol.ScopeArgPath);
            if (val == null) return false;

            IList<ApprovalRule> rules = _store.RulesFor(functionName);
            for (int i = 0; i < rules.Count; i++)
            {
                ApprovalRule r = rules[i];
                if (r == null || r.ArgPath != pol.ScopeArgPath) continue;
                if (r.Kind == RuleKind.ExactArgs)
                {
                    if (string.Equals(val, r.Pattern, StringComparison.Ordinal)) return true;
                }
                else // Prefix
                {
                    bool isPath = pol.ScopeArgPath == "path";
                    if (PrefixMatches(val, r.Pattern, isPath)) return true;
                }
            }
            return false;
        }

        // Boundary-aware prefix match (security, spec §3):
        //  - path: directory-boundary aware ("/a/b" matches "/a/b/c", not "/a/bc")
        //  - command: token-aware ("git status" matches "git status -s", not "git status-hack")
        internal static bool PrefixMatches(string value, string pattern, bool isPath)
        {
            if (value == null || pattern == null) return false;

            if (isPath)
            {
                // Normalize separators so a rule stored with one separator (e.g. '\' from
                // Path.GetDirectoryName on Windows) still matches a value using the other ('/' from
                // the model). Compare case-insensitively (Windows paths).
                value = NormalizePath(value);
                pattern = NormalizePath(pattern);

                // An empty pattern is the workspace root: matches any relative path under it.
                if (pattern.Length == 0) return true;
                if (value.Equals(pattern, StringComparison.OrdinalIgnoreCase)) return true;
                if (!value.StartsWith(pattern, StringComparison.OrdinalIgnoreCase)) return false;
                // Directory boundary: the char after the pattern must be a separator
                // ("sub" matches "sub/a", not "subdir/a").
                return value[pattern.Length] == '/';
            }

            // command: token-aware prefix.
            if (value.Equals(pattern, StringComparison.Ordinal)) return true;
            if (!value.StartsWith(pattern, StringComparison.Ordinal)) return false;
            char next = value[pattern.Length];
            return next == ' ' || next == '\t';
        }

        // Canonical relative-path form for comparison: '\' -> '/', collapse a leading './', drop any
        // trailing separator.
        private static string NormalizePath(string p)
        {
            if (string.IsNullOrEmpty(p)) return string.Empty;
            p = p.Replace('\\', '/');
            while (p.StartsWith("./", StringComparison.Ordinal)) p = p.Substring(2);
            while (p.Length > 1 && p[p.Length - 1] == '/') p = p.Substring(0, p.Length - 1);
            return p;
        }

        // ---- persistence of the user's choice ----

        private void Persist(ApprovalRequest req, ApprovalChoice choice)
        {
            switch (choice)
            {
                case ApprovalChoice.RememberTool:
                    _store.AddApprovedTool(req.FunctionName);
                    break;
                case ApprovalChoice.RememberExactArg:
                    _store.AddRule(new ApprovalRule(req.FunctionName, RuleKind.ExactArgs,
                        req.Policy.ScopeArgPath, ArgValue(req.Arguments, req.Policy.ScopeArgPath)));
                    break;
                case ApprovalChoice.RememberPrefixArg:
                    _store.AddRule(new ApprovalRule(req.FunctionName, RuleKind.Prefix,
                        req.Policy.ScopeArgPath, PrefixPattern(req)));
                    break;
                // AllowOnce / Deny: nothing persisted.
            }
        }

        // The structured prefix for a "base+subcommand" command rule or a "directory and below" path
        // rule, derived from the actual argument (no free-form entry — spec §3/§4).
        private static string PrefixPattern(ApprovalRequest req)
        {
            string val = ArgValue(req.Arguments, req.Policy.ScopeArgPath);
            if (val == null) return string.Empty;
            if (req.Policy.ScopeArgPath == "path")
            {
                // "directory and below": the rule is the file's PARENT directory, so other files in
                // the same folder match. Compute it with normalized '/' separators (NOT
                // Path.GetDirectoryName, which yields '\' on Windows and wouldn't match the model's
                // forward-slash paths). A root-level file yields "" — the workspace root.
                string norm = NormalizePath(val);
                int slash = norm.LastIndexOf('/');
                return slash < 0 ? string.Empty : norm.Substring(0, slash);
            }
            // command: base + first subcommand (first two whitespace tokens)
            string trimmed = val.Trim();
            string[] parts = trimmed.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length <= 1) return parts.Length == 1 ? parts[0] : trimmed;
            return parts[0] + " " + parts[1];
        }

        // ---- helpers ----

        private static string BuildPreview(string functionName, ToolPolicy pol, JObject args)
        {
            if (pol.Scope == RememberScope.Argument && pol.ScopeArgPath != null)
            {
                string v = ArgValue(args, pol.ScopeArgPath);
                if (v != null) return v;
            }
            return args != null ? args.ToString(Formatting.None) : string.Empty;
        }

        private static string ArgValue(JObject args, string path)
        {
            if (args == null || string.IsNullOrEmpty(path)) return null;
            JToken t = args[path];
            if (t == null || t.Type == JTokenType.Null) return null;
            return t.Type == JTokenType.String ? (string)t : t.ToString(Formatting.None);
        }

        private static string ServerOf(string functionName)
        {
            if (string.IsNullOrEmpty(functionName)) return null;
            int i = functionName.IndexOf("__", StringComparison.Ordinal);
            return i > 0 ? functionName.Substring(0, i) : null;
        }

        private static string ToolOf(string functionName)
        {
            if (string.IsNullOrEmpty(functionName)) return functionName;
            int i = functionName.IndexOf("__", StringComparison.Ordinal);
            return i >= 0 ? functionName.Substring(i + 2) : functionName;
        }

        private static Dictionary<string, bool> BuildFirstPartySet()
        {
            var s = new Dictionary<string, bool>(StringComparer.Ordinal);
            s[McpConfig.WebName] = true;
            s[McpConfig.FilesName] = true;
            s[McpConfig.GitName] = true;
            s[McpConfig.CommandName] = true;
            s[McpConfig.MsBuildName] = true;
            s[McpConfig.MemoryName] = true;
            // GitHub is a first-party-managed HTTP server but a third-party tool surface; classify
            // its tools via annotations (not the hardcoded table).
            return s;
        }
    }
}
