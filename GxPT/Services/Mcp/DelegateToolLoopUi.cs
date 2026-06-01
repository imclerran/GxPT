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
    }

    // Adapts the orchestrator's IToolLoopUi to simple delegates so MainForm can render a tool-call
    // turn as a chrome-less "tool activity" message plus a separate answer bubble: model text ->
    // appendText, each tool call -> onToolCall (its own message), Complete/OnError finalize. Tool
    // results are never shown (the model summarizes them).
    internal sealed class DelegateToolLoopUi : IToolLoopUi
    {
        private readonly Action<string> _appendText;
        private readonly Action<string, string> _onToolCall; // (functionName, argumentsJson)
        private readonly Action _complete;
        private readonly Action<string> _error;

        public DelegateToolLoopUi(Action<string> appendText, Action<string, string> onToolCall, Action complete, Action<string> error)
        {
            _appendText = appendText;
            _onToolCall = onToolCall;
            _complete = complete;
            _error = error;
        }

        public void AppendTextDelta(string text)
        {
            if (!string.IsNullOrEmpty(text) && _appendText != null) _appendText(text);
        }

        public void OnToolCall(string functionName, string argumentsJson)
        {
            if (_onToolCall != null) _onToolCall(functionName, argumentsJson);
        }

        public void OnToolResult(string functionName, string resultText, bool isError)
        {
            // Results are not rendered — the model incorporates them into its next message.
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

