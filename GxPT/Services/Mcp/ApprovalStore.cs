using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace GxPT
{
    // Persistence of remembered approvals (approval spec §5): a set of Tool-scope function names and
    // a list of Argument-scope rules. Abstracted so the policy is testable with an in-memory store.
    internal interface IApprovalStore
    {
        bool IsToolApproved(string functionName);
        void AddApprovedTool(string functionName);
        IList<ApprovalRule> RulesFor(string functionName);
        void AddRule(ApprovalRule rule);
    }

    // In-memory store (tests; also the base behavior the settings-backed store builds on).
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

    // settings.json-backed store via AppSettings (spec §5): mcp.approvedTools (string list) and
    // mcp.approvalRules (list of JSON-serialized ApprovalRule). AppSettings stays on
    // JavaScriptSerializer (D16); rules are serialized with Newtonsoft for a stable shape.
    internal sealed class SettingsApprovalStore : InMemoryApprovalStore
    {
        public const string ApprovedToolsKey = "mcp.approvedTools";
        public const string ApprovalRulesKey = "mcp.approvalRules";

        public SettingsApprovalStore()
        {
            Load();
        }

        private void Load()
        {
            try
            {
                IList<string> tools = AppSettings.GetList(ApprovedToolsKey);
                if (tools != null)
                    for (int i = 0; i < tools.Count; i++)
                        if (!string.IsNullOrEmpty(tools[i])) _approvedTools[tools[i]] = true;
            }
            catch { }

            try
            {
                IList<string> ruleJson = AppSettings.GetList(ApprovalRulesKey);
                if (ruleJson != null)
                {
                    for (int i = 0; i < ruleJson.Count; i++)
                    {
                        try
                        {
                            ApprovalRule r = JsonConvert.DeserializeObject<ApprovalRule>(ruleJson[i]);
                            if (r != null && !string.IsNullOrEmpty(r.FunctionName)) _rules.Add(r);
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        public override void AddApprovedTool(string functionName)
        {
            base.AddApprovedTool(functionName);
            Save();
        }

        public override void AddRule(ApprovalRule rule)
        {
            base.AddRule(rule);
            Save();
        }

        private void Save()
        {
            try
            {
                var tools = new List<string>(_approvedTools.Keys);
                AppSettings.SetList(ApprovedToolsKey, tools);

                var ruleJson = new List<string>();
                for (int i = 0; i < _rules.Count; i++)
                    ruleJson.Add(JsonConvert.SerializeObject(_rules[i]));
                AppSettings.SetList(ApprovalRulesKey, ruleJson);
            }
            catch { }
        }
    }
}
