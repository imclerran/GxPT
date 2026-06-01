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

        // A compact, markdown-safe "using <tool>" marker (italic, no internal underscores).
        public static string Call(string functionName)
        {
            return "_using " + Display(functionName) + "_";
        }
    }

    // Adapts the orchestrator's IToolLoopUi to simple delegates so MainForm can stream tool-call
    // turns into the existing transcript: model text and a compact per-call marker go to appendText;
    // Complete/OnError finalize the turn. Tool results are never shown (the model summarizes them).
    internal sealed class DelegateToolLoopUi : IToolLoopUi
    {
        private readonly Action<string> _appendText;
        private readonly Action _complete;
        private readonly Action<string> _error;

        public DelegateToolLoopUi(Action<string> appendText, Action complete, Action<string> error)
        {
            _appendText = appendText;
            _complete = complete;
            _error = error;
        }

        public void AppendTextDelta(string text)
        {
            if (!string.IsNullOrEmpty(text) && _appendText != null) _appendText(text);
        }

        public void OnToolCall(string functionName, string argumentsJson)
        {
            if (_appendText != null) _appendText("\r\n\r\n" + McpMarkers.Call(functionName) + "\r\n\r\n");
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

