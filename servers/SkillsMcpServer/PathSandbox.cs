using System;
using System.IO;

namespace SkillsMcpServer
{
    /// <summary>Thrown when a requested path would escape the sandbox root.</summary>
    internal sealed class SandboxException : Exception
    {
        public SandboxException(string message) : base(message) { }
    }

    /// <summary>
    /// Confines every file path to a single skill folder. Copied from FilesMcpServer (servers are
    /// independent consumers of the SDK): resolve the relative path against the root, canonicalize
    /// <c>.</c>/<c>..</c>, and verify the result is inside the root with a <b>directory-boundary</b>
    /// check - so "/root" does not match "/root-evil". Absolute / drive-relative inputs are rejected.
    /// </summary>
    internal sealed class PathSandbox
    {
        private readonly string _root;          // canonical, no trailing separator
        private readonly string _rootWithSep;   // canonical + separator, for boundary checks

        public PathSandbox(string root)
        {
            if (string.IsNullOrEmpty(root)) throw new ArgumentException("root is required", "root");
            string full = Path.GetFullPath(root);
            _root = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            _rootWithSep = _root + Path.DirectorySeparatorChar;
        }

        public string Root { get { return _root; } }

        /// <summary>Resolve a caller-supplied relative path to a full path guaranteed inside the root.</summary>
        public string Resolve(string rel)
        {
            if (rel == null) throw new SandboxException("path is required");
            if (rel.Length == 0) throw new SandboxException("path is required");
            if (Path.IsPathRooted(rel)) throw new SandboxException("absolute paths are not allowed");
            if (rel.IndexOf(':') >= 0) throw new SandboxException("invalid path");

            string combined = Path.Combine(_root, rel);
            string full = Path.GetFullPath(combined); // collapses . and ..

            if (!IsWithin(full)) throw new SandboxException("path escapes the skill folder");
            return full;
        }

        public bool IsWithin(string full)
        {
            if (string.IsNullOrEmpty(full)) return false;
            string trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(trimmed, _root, StringComparison.OrdinalIgnoreCase)) return true;
            return full.StartsWith(_rootWithSep, StringComparison.OrdinalIgnoreCase);
        }
    }
}
