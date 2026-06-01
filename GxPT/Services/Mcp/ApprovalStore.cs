using System;
using System.Collections.Generic;

namespace GxPT
{
    // Remembered approvals for the current app session (approval spec §5): a set of Tool-scope
    // function names and a list of Argument-scope rules. Kept in memory only — not persisted, so
    // remembered choices last until the app closes. Abstracted behind an interface for testing.
    internal interface IApprovalStore
    {
        bool IsToolApproved(string functionName);
        void AddApprovedTool(string functionName);
        IList<ApprovalRule> RulesFor(string functionName);
        void AddRule(ApprovalRule rule);
    }

    // In-memory store: remembered approvals live for the app session only.
    internal class InMemoryApprovalStore : IApprovalStore
    {
        protected readonly Dictionary<string, bool> _approvedTools = new Dictionary<string, bool>(StringComparer.Ordinal);
        protected readonly List<ApprovalRule> _rules = new List<ApprovalRule>();

        public bool IsToolApproved(string functionName)
        {
            return functionName != null && _approvedTools.ContainsKey(functionName);
        }

        public virtual void AddApprovedTool(string functionName)
        {
            if (!string.IsNullOrEmpty(functionName)) _approvedTools[functionName] = true;
        }

        public IList<ApprovalRule> RulesFor(string functionName)
        {
            var outp = new List<ApprovalRule>();
            for (int i = 0; i < _rules.Count; i++)
                if (_rules[i] != null && _rules[i].FunctionName == functionName) outp.Add(_rules[i]);
            return outp;
        }

        public virtual void AddRule(ApprovalRule rule)
        {
            if (rule == null) return;
            // Skip exact duplicates.
            for (int i = 0; i < _rules.Count; i++)
            {
                ApprovalRule r = _rules[i];
                if (r.FunctionName == rule.FunctionName && r.Kind == rule.Kind &&
                    r.ArgPath == rule.ArgPath && r.Pattern == rule.Pattern) return;
            }
            _rules.Add(rule);
        }
    }
}
