using System;

namespace GxPT
{
    // Shared formatting for tool activity shown in the transcript, so the live stream and the
    // reloaded view render identically. The qualified function name ("web__search") is displayed
    // as "web / search" — this also avoids the double-underscore being mangled by markdown emphasis.
    internal static class McpMarkers
    {
        public static string Display(string functionName)
        {
            return string.IsNullOrEmpty(functionName) ? string.Empty : functionName.Replace("__", " / ");
        }

        // A compact "using <tool>" marker, plain (not italic) for consistency with the collapsible
        // tool records. Display() turns the qualified name into "web / search" with no underscores.
        public static string Call(string functionName)
        {
            return "using " + Display(functionName);
        }

        // For files__edit / command__run, the generic "using" marker is replaced by a collapsible
        // record, anchored by this content-free sentinel (resolved to the record by the transcript).
        public static string EditDiff(string key)
        {
            return MarkdownParser.EditDiffSentinel(key);
        }

        // The tool-result content the orchestrator returns when the user denies a call. Centralized
        // so the transcript can recognize a denied call (live and on reload) and render it as denied
        // rather than as an applied edit/diff.
        public const string DeniedResult = "[Call denied by user.]";

        public static bool IsDenied(string resultText)
        {
            return string.Equals(resultText, DeniedResult, StringComparison.Ordinal);
        }

        // Shown in place of the edit/diff record when a call was denied, so a denied edit reads as
        // "denied" instead of looking like it was applied.
        public static string Denied(string functionName)
        {
            return "denied: " + Display(functionName);
        }
    }

    // Adapts the orchestrator's IToolLoopUi to simple delegates so MainForm can render a tool-call
    // turn as a chrome-less "tool activity" message plus a separate answer bubble: model text ->
    // appendText, each completed tool call -> onToolActivity (its own message), Complete/OnError
    // finalize.
    //
    // The tool record is rendered on the RESULT, not the call: OnToolCall fires before the approval
    // gate, so rendering there would show an edit/diff (and persist it) even when the user denies the
    // call. We stash the arguments at call time and emit the record once the outcome is known, so the
    // transcript reflects what actually happened (applied / denied / errored).
    internal sealed class DelegateToolLoopUi : IToolLoopUi
    {
        private readonly Action<string> _appendText;
        // (functionName, argumentsJson, resultText, isError) — emitted once a call's outcome is known.
        private readonly Action<string, string, string, bool> _onToolActivity;
        private readonly Action _complete;
        private readonly Action<string> _error;

        private string _pendingArgs; // arguments of the in-flight call, awaiting its result

        public DelegateToolLoopUi(Action<string> appendText, Action<string, string, string, bool> onToolActivity, Action complete, Action<string> error)
        {
            _appendText = appendText;
            _onToolActivity = onToolActivity;
            _complete = complete;
            _error = error;
        }

        public void AppendTextDelta(string text)
        {
            if (!string.IsNullOrEmpty(text) && _appendText != null) _appendText(text);
        }

        public void OnToolCall(string functionName, string argumentsJson)
        {
            // Defer rendering until the result: calls are serial, so a single pending slot suffices.
            _pendingArgs = argumentsJson;
        }

        public void OnToolResult(string functionName, string resultText, bool isError)
        {
            if (_onToolActivity != null) _onToolActivity(functionName, _pendingArgs, resultText, isError);
            _pendingArgs = null;
        }

        public void OnError(string message)
        {
            if (_error != null) _error(message);
        }

        public void Complete()
        {
            if (_complete != null) _complete();
        }
    }
}

