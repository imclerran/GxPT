using GxPT;
using Xunit;

namespace GxPT.Tests
{
    public sealed class SkillFrontmatterTests
    {
        [Fact]
        public void Parse_ReadsNameAndDescriptionAndBody()
        {
            string text =
                "---\n" +
                "name: Release Notes\n" +
                "description: Draft release notes from the git log.\n" +
                "---\n" +
                "\n" +
                "1. Do the thing.\n";

            SkillFrontmatter fm = SkillFrontmatter.Parse(text);

            Assert.True(fm.HasFrontmatter);
            Assert.Equal("Release Notes", fm.Name);
            Assert.Equal("Draft release notes from the git log.", fm.Description);
            Assert.Equal("1. Do the thing.", fm.Body);
        }

        [Fact]
        public void Parse_IsCrlfAgnostic()
        {
            string text =
                "---\r\n" +
                "name: A\r\n" +
                "description: B\r\n" +
                "---\r\n" +
                "body line\r\n";

            SkillFrontmatter fm = SkillFrontmatter.Parse(text);

            Assert.Equal("A", fm.Name);
            Assert.Equal("B", fm.Description);
            Assert.Equal("body line", fm.Body);
        }

        [Fact]
        public void Parse_StripsLeadingBom()
        {
            string text = "\uFEFF---\nname: A\ndescription: B\n---\nbody\n";

            SkillFrontmatter fm = SkillFrontmatter.Parse(text);

            Assert.True(fm.HasFrontmatter);
            Assert.Equal("A", fm.Name);
            Assert.Equal("B", fm.Description);
        }

        [Fact]
        public void Parse_UnknownKeysIgnored_KnownKeysCaseInsensitive()
        {
            string text =
                "---\n" +
                "Name: A\n" +
                "DESCRIPTION: B\n" +
                "future_key: whatever\n" +
                "---\n" +
                "body\n";

            SkillFrontmatter fm = SkillFrontmatter.Parse(text);

            Assert.Equal("A", fm.Name);
            Assert.Equal("B", fm.Description);
        }

        [Fact]
        public void Parse_FirstValueWins_OnDuplicateKey()
        {
            string text = "---\ndescription: first\ndescription: second\n---\nbody\n";

            SkillFrontmatter fm = SkillFrontmatter.Parse(text);

            Assert.Equal("first", fm.Description);
        }

        [Fact]
        public void Parse_ValueWithColon_KeepsRemainder()
        {
            string text = "---\ndescription: a: b: c\n---\nbody\n";

            SkillFrontmatter fm = SkillFrontmatter.Parse(text);

            Assert.Equal("a: b: c", fm.Description);
        }

        [Fact]
        public void Parse_NoFrontmatter_WholeTextIsBody()
        {
            string text = "just a body\nwith two lines\n";

            SkillFrontmatter fm = SkillFrontmatter.Parse(text);

            Assert.False(fm.HasFrontmatter);
            Assert.Null(fm.Name);
            Assert.Null(fm.Description);
            Assert.Equal("just a body\nwith two lines", fm.Body);
        }

        [Fact]
        public void Parse_UnterminatedFrontmatter_IsLenient()
        {
            string text = "---\nname: A\ndescription: B\nno closing delimiter\n";

            SkillFrontmatter fm = SkillFrontmatter.Parse(text);

            Assert.False(fm.HasFrontmatter);
            Assert.Null(fm.Name);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void Parse_NullOrEmpty_YieldsEmptyBodyNoFrontmatter(string text)
        {
            SkillFrontmatter fm = SkillFrontmatter.Parse(text);

            Assert.False(fm.HasFrontmatter);
            Assert.Equal("", fm.Body);
        }
    }
}
