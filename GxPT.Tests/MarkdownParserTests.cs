using System.Collections.Generic;
using System.Text;
using GxPT;
using Xunit;

namespace GxPT.Tests
{
    public class MarkdownParserTests
    {
        private static string InlineText(IEnumerable<InlineRun> runs)
        {
            var sb = new StringBuilder();
            if (runs != null)
                foreach (var r in runs)
                    if (r != null && r.Text != null) sb.Append(r.Text);
            return sb.ToString();
        }

        private static T FirstBlock<T>(List<Block> blocks) where T : Block
        {
            foreach (var b in blocks)
            {
                var t = b as T;
                if (t != null) return t;
            }
            return null;
        }

        [Fact]
        public void EmptyInput_ReturnsSingleEmptyParagraph()
        {
            var blocks = MarkdownParser.ParseMarkdown("");
            Assert.Single(blocks);
            var p = Assert.IsType<ParagraphBlock>(blocks[0]);
            Assert.Empty(p.Inlines);
        }

        [Fact]
        public void Heading_ParsesLevelAndText()
        {
            var blocks = MarkdownParser.ParseMarkdown("## Title Here");
            var h = FirstBlock<HeadingBlock>(blocks);
            Assert.NotNull(h);
            Assert.Equal(2, h.Level);
            Assert.Equal("Title Here", InlineText(h.Inlines));
        }

        [Fact]
        public void FencedCodeBlock_CapturesLanguageAndText()
        {
            var blocks = MarkdownParser.ParseMarkdown("```csharp\nint x = 1;\n```");
            var code = FirstBlock<CodeBlock>(blocks);
            Assert.NotNull(code);
            Assert.Equal("csharp", code.Language);
            Assert.Equal("int x = 1;", code.Text);
        }

        [Fact]
        public void BulletList_CollectsItems()
        {
            var blocks = MarkdownParser.ParseMarkdown("- one\n- two");
            var list = FirstBlock<BulletListBlock>(blocks);
            Assert.NotNull(list);
            Assert.Equal(2, list.Items.Count);
            Assert.Equal("one", InlineText(list.Items[0].Content));
            Assert.Equal("two", InlineText(list.Items[1].Content));
        }

        [Fact]
        public void NestedBullet_HasIndentLevel()
        {
            var blocks = MarkdownParser.ParseMarkdown("- a\n  - b");
            var list = FirstBlock<BulletListBlock>(blocks);
            Assert.NotNull(list);
            Assert.Equal(2, list.Items.Count);
            Assert.Equal(0, list.Items[0].IndentLevel);
            Assert.Equal(1, list.Items[1].IndentLevel);
        }

        [Fact]
        public void NumberedList_PreservesNumbers()
        {
            var blocks = MarkdownParser.ParseMarkdown("1. first\n2. second");
            var list = FirstBlock<NumberedListBlock>(blocks);
            Assert.NotNull(list);
            Assert.Equal(2, list.Items.Count);
            Assert.Equal(1, list.Items[0].Number);
            Assert.Equal(2, list.Items[1].Number);
        }

        [Fact]
        public void Table_ParsesHeaderAndRow()
        {
            var blocks = MarkdownParser.ParseMarkdown("| A | B |\n| --- | --- |\n| 1 | 2 |");
            var table = FirstBlock<TableBlock>(blocks);
            Assert.NotNull(table);
            Assert.Equal(2, table.Header.Count);
            Assert.Equal("A", InlineText(table.Header[0]));
            Assert.Equal("B", InlineText(table.Header[1]));
            Assert.Single(table.Rows);
            Assert.Equal(2, table.Rows[0].Count);
            Assert.Equal("1", InlineText(table.Rows[0][0]));
            Assert.Equal("2", InlineText(table.Rows[0][1]));
        }

        [Fact]
        public void Inline_Bold_SetsBoldStyle()
        {
            var runs = MarkdownParser.ParseInlines("**bold**");
            Assert.Contains(runs, r => r.Text == "bold" && (r.Style & RunStyle.Bold) != 0);
        }

        [Fact]
        public void Inline_Italic_SetsItalicStyle()
        {
            var runs = MarkdownParser.ParseInlines("*italic*");
            Assert.Contains(runs, r => r.Text == "italic" && (r.Style & RunStyle.Italic) != 0);
        }

        [Fact]
        public void Inline_Code_SetsCodeStyle()
        {
            var runs = MarkdownParser.ParseInlines("`x = 1`");
            Assert.Contains(runs, r => r.Text == "x = 1" && (r.Style & RunStyle.Code) != 0);
        }

        [Fact]
        public void Inline_Link_SetsLinkUrl()
        {
            var runs = MarkdownParser.ParseInlines("[Google](https://google.com)");
            Assert.Contains(runs, r => r.Text == "Google"
                && (r.Style & RunStyle.Link) != 0
                && r.LinkUrl == "https://google.com");
        }

        [Fact]
        public void Inline_BareUrl_BecomesLink()
        {
            var runs = MarkdownParser.ParseInlines("see https://example.com now");
            Assert.Contains(runs, r => r.LinkUrl == "https://example.com" && (r.Style & RunStyle.Link) != 0);
        }
    }
}
