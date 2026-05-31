using System.Collections.Generic;
using Mcp35.Core.Transport;
using Xunit;

namespace Mcp35.Core.Tests
{
    public class SseParserTests
    {
        private static List<SseEvent> Feed(SseParser p, params string[] lines)
        {
            var outp = new List<SseEvent>();
            foreach (var line in lines)
                outp.AddRange(p.PushLine(line));
            return outp;
        }

        [Fact]
        public void Single_data_line_emits_on_blank_line()
        {
            var p = new SseParser();
            var events = Feed(p, "data: {\"x\":1}", "");

            Assert.Single(events);
            Assert.Equal("{\"x\":1}", events[0].Data);
        }

        [Fact]
        public void Multiple_data_lines_join_with_newline()
        {
            var p = new SseParser();
            var events = Feed(p, "data: line1", "data: line2", "");

            Assert.Single(events);
            Assert.Equal("line1\nline2", events[0].Data);
        }

        [Fact]
        public void Comment_lines_are_ignored()
        {
            var p = new SseParser();
            var events = Feed(p, ": this is a comment", "data: hi", "");

            Assert.Single(events);
            Assert.Equal("hi", events[0].Data);
        }

        [Fact]
        public void Event_type_and_id_are_captured()
        {
            var p = new SseParser();
            var events = Feed(p, "event: message", "id: 42", "data: payload", "");

            Assert.Single(events);
            Assert.Equal("message", events[0].EventType);
            Assert.Equal("42", events[0].LastEventId);
            Assert.Equal("payload", events[0].Data);
        }

        [Fact]
        public void Last_event_id_is_sticky_across_events()
        {
            var p = new SseParser();
            var first = Feed(p, "id: 7", "data: a", "");
            var second = Feed(p, "data: b", "");

            Assert.Equal("7", first[0].LastEventId);
            Assert.Equal("7", second[0].LastEventId); // persists even though not repeated
        }

        [Fact]
        public void Value_without_leading_space_is_kept_verbatim()
        {
            var p = new SseParser();
            var events = Feed(p, "data:nospace", "");

            Assert.Equal("nospace", events[0].Data);
        }

        [Fact]
        public void Blank_line_with_no_data_emits_nothing()
        {
            var p = new SseParser();
            var events = Feed(p, "", "");
            Assert.Empty(events);
        }

        [Fact]
        public void Flush_emits_trailing_event_without_blank_line()
        {
            var p = new SseParser();
            var events = Feed(p, "data: trailing"); // no blank line
            Assert.Empty(events);

            SseEvent tail = p.Flush();
            Assert.NotNull(tail);
            Assert.Equal("trailing", tail.Data);
        }

        [Fact]
        public void Two_events_separated_by_blank_lines()
        {
            var p = new SseParser();
            var events = Feed(p, "data: one", "", "data: two", "");

            Assert.Equal(2, events.Count);
            Assert.Equal("one", events[0].Data);
            Assert.Equal("two", events[1].Data);
        }
    }
}
