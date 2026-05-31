using System;

namespace GxPT
{
    // Adapts the orchestrator's IToolLoopUi to simple delegates so MainForm can stream tool-call
    // turns into the existing transcript: model text and a minimal inline marker per tool call go to
    // appendText; Complete/OnError finalize the turn. (Tool results are not dumped into the bubble —
    // the model summarizes them; only errors get a short marker.)
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
            if (_appendText != null) _appendText("\r\n\r\n_[calling " + functionName + "]_\r\n\r\n");
        }

        public void OnToolResult(string functionName, string resultText, bool isError)
        {
            if (isError && _appendText != null) _appendText("\r\n\r\n_[" + functionName + " failed]_\r\n\r\n");
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
