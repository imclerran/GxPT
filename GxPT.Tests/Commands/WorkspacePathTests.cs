using GxPT;
using Xunit;

namespace GxPT.Tests.Commands
{
    public class WorkspacePathTests
    {
        [Theory]
        [InlineData("src/Foo.cs")]
        [InlineData("src\\Foo.cs")]
        [InlineData("Foo.cs")]
        [InlineData("a/b/c/d.txt")]
        [InlineData("folder")]
        public void Accepts_relative_paths(string rel)
        {
            Assert.True(WorkspacePath.IsValid(rel));
            Assert.Null(WorkspacePath.Validate(rel));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Empty_argument_is_accepted_as_no_path(string rel)
        {
            // An absent path argument is the caller's concern, not a validation error.
            Assert.True(WorkspacePath.IsValid(rel));
        }

        [Theory]
        [InlineData("/etc/passwd")]
        [InlineData("\\\\server\\share")]
        [InlineData("\\windows")]
        public void Rejects_absolute_paths(string rel)
        {
            Assert.False(WorkspacePath.IsValid(rel));
        }

        [Theory]
        [InlineData("C:\\Windows")]
        [InlineData("c:/temp")]
        [InlineData("foo:bar")]
        public void Rejects_drive_or_colon_paths(string rel)
        {
            Assert.False(WorkspacePath.IsValid(rel));
        }

        [Theory]
        [InlineData("..")]
        [InlineData("../secrets")]
        [InlineData("..\\secrets")]
        [InlineData("src/../../etc")]
        [InlineData("a/b/../../../c")]
        public void Rejects_parent_traversal(string rel)
        {
            Assert.False(WorkspacePath.IsValid(rel));
        }

        [Fact]
        public void Does_not_reject_dotdot_inside_a_filename()
        {
            // "..foo" is a legitimate filename; only a whole ".." segment is refused.
            Assert.True(WorkspacePath.IsValid("a/..foo"));
            Assert.True(WorkspacePath.IsValid("foo..bar/baz"));
        }
    }
}
