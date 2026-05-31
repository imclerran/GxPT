using System;
using System.Collections.Generic;
using System.Text;

namespace GxPT
{
    // Reassembles fragmented streamed tool calls (phase 4, §5). Providers stream a tool call in
    // pieces: id/name usually in the first fragment, arguments dribbled across many, correlated by
    // index. The assembler consumes ChatCompletionChunks (via OnChunk), forwards plain text deltas
    // to the UI as they arrive, and at stream end exposes either the accumulated assistant text or
    // the rebuilt list of tool calls.
    //
    // Usage: client.StreamRawChunks(body, asm.OnChunk, onError); asm.Finish();
    //   then read ProducedToolCalls / Calls / Text.
    internal sealed class ToolCallAssembler
    {
        private sealed class Acc
        {
            public string Id;
            public string Type;
            public readonly StringBuilder Name = new StringBuilder();
            public readonly StringBuilder Args = new StringBuilder();
        }

        private readonly Action<string> _onTextDelta;
        private readonly Dictionary<int, Acc> _accs = new Dictionary<int, Acc>();
        private readonly StringBuilder _text = new StringBuilder();

        private bool _finalized;
        private bool _producedToolCalls;
        private List<ToolCall> _calls;
        private string _finishReason;
        private bool _truncated;

        public ToolCallAssembler(Action<string> onTextDelta)
        {
            _onTextDelta = onTextDelta;
        }

        public bool ProducedToolCalls { get { return _producedToolCalls; } }
        public List<ToolCall> Calls { get { return _calls != null ? _calls : new List<ToolCall>(); } }
        public string Text { get { return _text.ToString(); } }
        public string FinishReason { get { return _finishReason; } }
        // A tool call cut off by finish_reason == "length": its arguments are incomplete.
        public bool Truncated { get { return _truncated; } }

        public void OnChunk(ChatCompletionChunk chunk)
        {
            if (chunk == null || chunk.choices == null || chunk.choices.Count == 0) return;

            // Streaming chat completions use n=1; the first choice carries this turn's deltas.
            ChatCompletionChunk.Choice choice = chunk.choices[0];
            if (choice == null) return;

            ChatCompletionChunk.Delta delta = choice.delta;
            if (delta != null)
            {
                if (!string.IsNullOrEmpty(delta.content))
                {
                    _text.Append(delta.content);
                    if (_onTextDelta != null) _onTextDelta(delta.content);
                }

                if (delta.tool_calls != null)
                {
                    for (int i = 0; i < delta.tool_calls.Count; i++)
                    {
                        ToolCallDelta f = delta.tool_calls[i];
                        if (f == null) continue;

                        Acc a;
                        if (!_accs.TryGetValue(f.index, out a))
                        {
                            a = new Acc();
                            _accs[f.index] = a;
                        }
                        if (f.id != null) a.Id = f.id;       // last non-null wins
                        if (f.type != null) a.Type = f.type;
                        if (f.function != null)
                        {
                            if (f.function.name != null) a.Name.Append(f.function.name);        // concat-safe
                            if (f.function.arguments != null) a.Args.Append(f.function.arguments);
                        }
                    }
                }
            }

            if (choice.finish_reason != null)
                Finalize(choice.finish_reason);
        }

        // Idempotent; call after the stream ends in case no finish_reason arrived.
        public void Finish()
        {
            Finalize(_finishReason);
        }

        private void Finalize(string reason)
        {
            if (_finalized) return;
            _finalized = true;
            _finishReason = reason;

            if (_accs.Count == 0)
            {
                // No tool calls — a plain assistant answer.
                _producedToolCalls = false;
                return;
            }

            // Rebuild calls ordered by their streamed index for stable ordering.
            List<int> indexes = new List<int>(_accs.Keys);
            indexes.Sort();

            _calls = new List<ToolCall>(indexes.Count);
            for (int i = 0; i < indexes.Count; i++)
            {
                Acc a = _accs[indexes[i]];
                string args = a.Args.Length > 0 ? a.Args.ToString() : "{}";
                _calls.Add(new ToolCall(a.Id, a.Name.ToString(), args));
            }
            _producedToolCalls = true;
            // A call cut off by the model's max length leaves arguments JSON incomplete.
            _truncated = (reason == "length");
        }
    }
}
