using System;
using System.IO;
using System.Text;

namespace SkillsMcpServer
{
    /// <summary>A tool-level failure (relayed to the model as isError), never an exception out of a handler.</summary>
    internal sealed class SkillWriteException : Exception
    {
        public SkillWriteException(string message) : base(message) { }
    }

    /// <summary>
    /// The skill authoring file operations (design S16): create a new skill's SKILL.md, write supporting
    /// files/scripts into a skill folder, and partial-update an existing SKILL.md. Targets the WRITABLE
    /// roots only (project `<workdir>/.gxpt/skills`, later user-global); the bundled install dir is never
    /// a write target. Atomic writes, UTF-8 (no BOM), CRLF for `.bat`/`.cmd` (XP cmd.exe).
    /// </summary>
    internal sealed class SkillWriter
    {
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        private readonly string _projectRoot; // <workdir>/.gxpt/skills, or null when no workspace
        private readonly string _userRoot;    // %AppData%/GxPT/skills, or null until wired

        public SkillWriter(string projectRoot, string userRoot)
        {
            _projectRoot = projectRoot;
            _userRoot = userRoot;
        }

        // create_skill: a NEW skill (refuses if it exists); assembles a guaranteed-loadable SKILL.md.
        public string CreateSkill(string scope, string slugIn, string name, string description, string body)
        {
            string root = RootFor(scope);
            string slug = RequireSlug(slugIn);
            if (IsBlank(name)) throw new SkillWriteException("name is required");
            if (IsBlank(description)) throw new SkillWriteException("description is required");

            string file = Path.Combine(Path.Combine(root, slug), "SKILL.md");
            if (File.Exists(file))
                throw new SkillWriteException("skill '" + slug + "' already exists; use update_skill to change it");

            AtomicWrite(file, BuildSkillMd(name, description, body));
            return "Created skill '" + slug + "'. It will be available on your next message.";
        }

        // write_skill_file: a supporting reference file or script (NOT SKILL.md - that's create/update).
        public string WriteFile(string scope, string slugIn, string relpath, string content)
        {
            string root = RootFor(scope);
            string slug = RequireSlug(slugIn);
            string dir = Path.Combine(root, slug);
            if (!File.Exists(Path.Combine(dir, "SKILL.md")))
                throw new SkillWriteException("skill '" + slug + "' does not exist yet; create_skill first");

            string full;
            try { full = new PathSandbox(dir).Resolve(relpath); }
            catch (SandboxException ex) { throw new SkillWriteException(ex.Message); }

            if (string.Equals(Path.GetFileName(full), "SKILL.md", StringComparison.OrdinalIgnoreCase))
                throw new SkillWriteException("write SKILL.md with create_skill / update_skill, not write_skill_file");

            string text = content != null ? content : string.Empty;
            string ext = (Path.GetExtension(full) ?? string.Empty).ToLowerInvariant();
            if (ext == ".bat" || ext == ".cmd") text = ToCrlf(text); // XP cmd.exe wants CRLF
            AtomicWrite(full, text);
            return "Wrote " + relpath + " into skill '" + slug + "'.";
        }

        // update_skill: partial edit of the main file (null field = leave unchanged).
        public string UpdateSkill(string scope, string slugIn, string name, string description, string body)
        {
            string root = RootFor(scope);
            string slug = RequireSlug(slugIn);
            string file = Path.Combine(Path.Combine(root, slug), "SKILL.md");
            if (!File.Exists(file))
                throw new SkillWriteException("skill '" + slug + "' does not exist; create_skill first");

            string existing;
            try { existing = File.ReadAllText(file, Encoding.UTF8); }
            catch (Exception ex) { throw new SkillWriteException("could not read SKILL.md: " + ex.Message); }

            SkillFrontmatter fm = SkillFrontmatter.Parse(existing);
            string newName = name != null ? name : (fm.Name != null ? fm.Name : slug);
            string newDesc = description != null ? description : fm.Description;
            string newBody = body != null ? body : fm.Body;
            if (IsBlank(newDesc)) throw new SkillWriteException("description is required");

            AtomicWrite(file, BuildSkillMd(newName, newDesc, newBody));
            return "Updated skill '" + slug + "'. Changes apply on your next message.";
        }

        // ---- internals ----

        private string RootFor(string scope)
        {
            string s = (scope == null ? "project" : scope.Trim().ToLowerInvariant());
            if (s.Length == 0) s = "project";
            if (s == "project")
            {
                if (string.IsNullOrEmpty(_projectRoot))
                    throw new SkillWriteException("no workspace folder is set for this conversation");
                return _projectRoot;
            }
            if (s == "user")
            {
                if (string.IsNullOrEmpty(_userRoot))
                    throw new SkillWriteException("user-global skills are not enabled yet");
                return _userRoot;
            }
            throw new SkillWriteException("unknown scope '" + scope + "' (use 'project' or 'user')");
        }

        private static string RequireSlug(string slugIn)
        {
            string slug = SkillSlug.Make(slugIn);
            if (string.IsNullOrEmpty(slug)) throw new SkillWriteException("a valid slug is required");
            return slug;
        }

        private static string BuildSkillMd(string name, string description, string body)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("---\n");
            sb.Append("name: ").Append(name.Trim()).Append('\n');
            sb.Append("description: ").Append(description.Trim()).Append('\n');
            sb.Append("---\n\n");
            string b = body != null ? body : string.Empty;
            sb.Append(b);
            if (!b.EndsWith("\n")) sb.Append('\n');
            return sb.ToString();
        }

        private static string ToCrlf(string s)
        {
            return s.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
        }

        private static bool IsBlank(string s)
        {
            return s == null || s.Trim().Length == 0;
        }

        // Atomic write (mirrors MemoryStore): temp file then replace/move; creates parent dirs.
        private static void AtomicWrite(string path, string content)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            File.WriteAllText(tmp, content, Utf8NoBom);
            if (File.Exists(path))
            {
                try { File.Replace(tmp, path, null); return; }
                catch { File.Delete(path); }
            }
            File.Move(tmp, path);
        }
    }
}
