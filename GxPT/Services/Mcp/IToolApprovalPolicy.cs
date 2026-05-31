using Newtonsoft.Json.Linq;

namespace GxPT
{
    // The outcome of an approval check for a single tool call.
    internal enum ApprovalDecision
    {
        Allow,
        Deny
    }

    // The host's approval gate, called by McpChatOrchestrator before every tool invocation.
    // Phase 4 ships an allow-all stub (AllowAllApprovalPolicy); phase 6 replaces it with the
    // tiered allowlist / remember-scope / always-confirm-destructive model. The loop already
    // calls it at the right point, so swapping the implementation needs no loop changes.
    internal interface IToolApprovalPolicy
    {
        ApprovalDecision Check(string functionName, JObject args);
    }

    // Phase-4 stub: allow every call. (reveal_tools is handled by the host directly and never
    // reaches the policy.) Replaced wholesale in phase 6.
    internal sealed class AllowAllApprovalPolicy : IToolApprovalPolicy
    {
        public ApprovalDecision Check(string functionName, JObject args)
        {
            return ApprovalDecision.Allow;
        }
    }
}
