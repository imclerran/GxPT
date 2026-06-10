using GxPT;
using Xunit;

namespace GxPT.Tests
{
    public sealed class SkillSlugTests
    {
        [Theory]
        [InlineData("release-notes", "release-notes")]
        [InlineData("Release Notes", "release-notes")]
        [InlineData("releaseNotes", "release-notes")]
        [InlineData("release_notes", "release-notes")]
        [InlineData("  Release   Notes  ", "release-notes")]
        [InlineData("PDF2Text", "pdf2-text")]
        [InlineData("HTTPServer", "httpserver")]
        [InlineData("a--b__c", "a-b-c")]
        [InlineData("v1.2.3", "v1-2-3")]
        public void Make_NormalizesToKebab(string input, string expected)
        {
            Assert.Equal(expected, SkillSlug.Make(input));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("---")]
        [InlineData("...")]
        public void Make_NoUsableChars_ReturnsNull(string input)
        {
            Assert.Null(SkillSlug.Make(input));
        }

        [Fact]
        public void Make_AlreadyKebab_IsIdempotent()
        {
            Assert.Equal("foo-bar-baz", SkillSlug.Make("foo-bar-baz"));
        }
    }
}
