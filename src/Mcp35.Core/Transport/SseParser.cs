using System.Collections.Generic;
using System.Text;

namespace Mcp35.Core.Transport
{
    /// <summary>One Server-Sent Event. For MCP Streamable HTTP each <see cref="Data"/> is a complete JSON-RPC message.</summary>
    public sealed class SseEvent
    {
        public string EventType;
        public string Data;
        public string LastEventId;
    }

    /// <summary>
    /// A line-oriented text/event-stream parser (matches a curl ReadLine loop). Push one line at a
    /// time; a complete event is emitted on the blank-line boundary. See mcp35-core-spec.md §7.
    /// </summary>
    public sealed class SseParser
    {
        private readonly StringBuilder _data = new StringBuilder();
        private string _eventType;
        private string _lastEventId; // sticky across events, per the SSE spec
        private bool _hasData;

        /// <summary>
        /// Feed one line (without its line terminator). A null line is treated as end-of-stream and
        /// flushes any buffered event. Returns the event(s) completed by this line (usually 0 or 1).
        /// </summary>
        public IEnumerable<SseEvent> PushLine(string line)
        {
            if (line == null)
            {
                SseEvent tail = Emit();
                if (tail != null) yield return tail;
                yield break;
            }

            if (line.Length == 0)
            {
                SseEvent ev = Emit();
                if (ev != null) yield return ev;
                yield break;
            }

            if (line[0] == ':') yield break; // comment line

            string field, value;
            int colon = line.IndexOf(':');
            if (colon < 0)
            {
                field = line;
                value = string.Empty;
            }
            else
            {
                field = line.Substring(0, colon);
                value = line.Substring(colon + 1);
                if (value.Length > 0 && value[0] == ' ') value = value.Substring(1);
            }

            switch (field)
            {
                case "data":
                    if (_hasData) _data.Append('\n');
                    _data.Append(value);
                    _hasData = true;
                    break;
                case "event":
                    _eventType = value;
                    break;
                case "id":
                    _lastEventId = value;
                    break;
                // "retry" and unknown fields are ignored.
            }
        }

        /// <summary>Emit any buffered event at end of stream (no trailing blank line).</summary>
        public SseEvent Flush()
        {
            return Emit();
        }

        private SseEvent Emit()
        {
            // Per SSE, a dispatch with no data buffered produces no event.
            if (!_hasData)
            {
                _eventType = null;
                return null;
            }

            SseEvent ev = new SseEvent();
            ev.Data = _data.ToString();
            ev.EventType = _eventType;
            ev.LastEventId = _lastEventId;

            _data.Length = 0;
            _hasData = false;
            _eventType = null;
            return ev;
        }
    }
}
