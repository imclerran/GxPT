// SyntaxHighlighter.cs
// Syntax highlighting infrastructure for .NET 3.5 compatibility
// Provides interfaces and base classes for language-specific syntax highlighting

using System;
using System.Collections.Generic;
using System.Drawing;

namespace GxPT
{
    /// <summary>
    /// Represents different types of code tokens for syntax highlighting
    /// </summary>
    public enum TokenType
    {
        Normal,      // Regular text/identifiers
        Keyword,     // Language keywords (if, for, class, etc.)
        String,      // String literals
        Comment,     // Comments (single-line and multi-line)
        Number,      // Numeric literals
        Operator,    // Operators (+, -, =, etc.)
        Punctuation, // Braces, brackets, semicolons
        Type,        // Built-in types (int, string, bool)
        Method,      // Method/function names

        // Diff/patch tokens. Unlike the categories above (whose colors are theme-driven), these
        // map to a FIXED red/green family in GetTokenColorForTheme — a diff must always read as a
        // diff regardless of the active accent palette. They are also the only token types that
        // carry a background color (see GetTokenBackgroundColorForTheme).
        Addition,    // A '+' line in a unified diff (added)
        Deletion,    // A '-' line in a unified diff (removed)
        DiffMeta     // Hunk/file headers and metadata (@@, +++/---, diff --git, ...)
    }

    /// <summary>
    /// Represents a single highlighted token in source code
    /// </summary>
    public struct CodeToken
    {
        public string Text;
        public TokenType Type;
        public int StartIndex;
        public int Length;

        public CodeToken(string text, TokenType type, int startIndex)
        {
            Text = text;
            Type = type;
            StartIndex = startIndex;
            Length = text != null ? text.Length : 0;
        }
    }

    /// <summary>
    /// Interface for language-specific syntax highlighters
    /// </summary>
    public interface ISyntaxHighlighter
    {
        /// <summary>
        /// The primary language identifier (e.g., "cs", "js", "json", "python")
        /// </summary>
        string Language { get; }

        /// <summary>
        /// All accepted identifiers for this highlighter, including aliases (e.g., "cs", "csharp").
        /// Should include the primary <see cref="Language"/> as well. Case-insensitive matching.
        /// </summary>
        string[] Aliases { get; }

        /// <summary>
        /// Tokenizes the given source code into highlighted tokens
        /// </summary>
        /// <param name="sourceCode">The source code to tokenize</param>
        /// <returns>List of tokens with syntax highlighting information</returns>
        List<CodeToken> Tokenize(string sourceCode);
    }

    /// <summary>
    /// Main syntax highlighter that manages language-specific highlighters
    /// </summary>
    public static class SyntaxHighlighter
    {
        private static readonly Dictionary<string, ISyntaxHighlighter> _highlighters;

        static SyntaxHighlighter()
        {
            _highlighters = new Dictionary<string, ISyntaxHighlighter>(StringComparer.OrdinalIgnoreCase);
            RegisterDefaultHighlighters();
        }

        /// <summary>
        /// Registers the default set of language highlighters
        /// </summary>
        private static void RegisterDefaultHighlighters()
        {
            RegisterHighlighter(new AdaHighlighter());
            RegisterHighlighter(new AssemblyHighlighter());
            RegisterHighlighter(new BashHighlighter());
            RegisterHighlighter(new BasicHighlighter());
            RegisterHighlighter(new BatchHighlighter());
            RegisterHighlighter(new CHighlighter());
            RegisterHighlighter(new CppHighlighter());
            RegisterHighlighter(new CSharpHighlighter());
            RegisterHighlighter(new CssHighlighter());
            RegisterHighlighter(new CsvHighlighter());
            RegisterHighlighter(new DartHighlighter());
            RegisterHighlighter(new DiffHighlighter());
            RegisterHighlighter(new EbnfHighlighter());
            RegisterHighlighter(new ElixirHighlighter());
            RegisterHighlighter(new ErlangHighlighter());
            RegisterHighlighter(new FortranHighlighter());
            RegisterHighlighter(new FSharpHighlighter());
            RegisterHighlighter(new GoHighlighter());
            RegisterHighlighter(new GqlHighlighter());
            RegisterHighlighter(new HaskellHighlighter());
            RegisterHighlighter(new HtmlHighlighter());
            RegisterHighlighter(new JavaHighlighter());
            RegisterHighlighter(new JavaScriptHighlighter());
            RegisterHighlighter(new JsonHighlighter());
            RegisterHighlighter(new KotlinHighlighter());
            RegisterHighlighter(new LispHighlighter());
            RegisterHighlighter(new LuaHighlighter());
            RegisterHighlighter(new McfunctionHighlighter());
            RegisterHighlighter(new OcamlHighlighter());
            RegisterHighlighter(new PascalHighlighter());
            RegisterHighlighter(new PerlHighlighter());
            RegisterHighlighter(new PhpHighlighter());
            RegisterHighlighter(new PowerShellHighlighter());
            RegisterHighlighter(new PropertiesHighlighter());
            RegisterHighlighter(new PythonHighlighter());
            RegisterHighlighter(new RubyHighlighter());
            RegisterHighlighter(new RegexHighlighter());
            RegisterHighlighter(new RustHighlighter());
            RegisterHighlighter(new ScalaHighlighter());
            RegisterHighlighter(new SqlHighlighter());
            RegisterHighlighter(new SwiftHighlighter());
            RegisterHighlighter(new TypeScriptHighlighter());
            RegisterHighlighter(new VisualBasicHighlighter());
            RegisterHighlighter(new XmlHighlighter());
            RegisterHighlighter(new YamlHighlighter());
            RegisterHighlighter(new ZigHighlighter());
        }

        /// <summary>
        /// Registers a new language highlighter
        /// </summary>
        /// <param name="highlighter">The highlighter to register</param>
        public static void RegisterHighlighter(ISyntaxHighlighter highlighter)
        {
            if (highlighter == null)
                return;

            // Collect unique identifiers (primary + aliases)
            var ids = new List<string>();
            if (!string.IsNullOrEmpty(highlighter.Language))
                ids.Add(highlighter.Language);

            if (highlighter.Aliases != null)
            {
                for (int i = 0; i < highlighter.Aliases.Length; i++)
                {
                    var id = highlighter.Aliases[i];
                    if (!string.IsNullOrEmpty(id))
                        ids.Add(id);
                }
            }

            // Register under each identifier (dictionary is case-insensitive)
            for (int i = 0; i < ids.Count; i++)
            {
                string id = ids[i];
                if (!_highlighters.ContainsKey(id))
                {
                    _highlighters[id] = highlighter;
                }
                else
                {
                    // Overwrite only if mapping to the same highlighter type to avoid surprises
                    _highlighters[id] = highlighter;
                }
            }
        }

        /// <summary>
        /// Gets syntax highlighting for the specified language and source code
        /// </summary>
        /// <param name="language">The programming language (e.g., "cs", "js", "json")</param>
        /// <param name="sourceCode">The source code to highlight</param>
        /// <returns>List of highlighted tokens, or a single normal token if language is not supported</returns>
        public static List<CodeToken> Highlight(string language, string sourceCode)
        {
            if (string.IsNullOrEmpty(sourceCode))
            {
                return new List<CodeToken>();
            }

            if (string.IsNullOrEmpty(language) || !_highlighters.ContainsKey(language))
            {
                // Return the entire text as a single normal token
                return new List<CodeToken>
                {
                    new CodeToken(sourceCode, TokenType.Normal, 0)
                };
            }

            try
            {
                return _highlighters[language].Tokenize(sourceCode);
            }
            catch
            {
                // If highlighting fails, return as normal text
                return new List<CodeToken>
                {
                    new CodeToken(sourceCode, TokenType.Normal, 0)
                };
            }
        }

        /// <summary>
        /// Gets the color for a specific token type using the default (light) palette.
        /// This is used by non-chat contexts like the Settings JSON editor.
        /// </summary>
        public static Color GetTokenColor(TokenType tokenType)
        {
            return GetTokenColorForTheme(tokenType, false);
        }

        /// <summary>
        /// Gets the color for a specific token type for a chosen theme.
        /// When dark=true, uses Catppuccin Macchiato; otherwise Catppuccin Latte.
        /// </summary>
        public static Color GetTokenColorForTheme(TokenType tokenType, bool dark)
        {
            if (dark)
            {
                // Catppuccin Mocha palette mapping
                // https://github.com/catppuccin/catppuccin
                switch (tokenType)
                {
                    case TokenType.Keyword:
                        return ColorTranslator.FromHtml("#cba6f7"); // Mauve
                    case TokenType.String:
                        return ColorTranslator.FromHtml("#a6e3a1"); // Green
                    case TokenType.Comment:
                        return ColorTranslator.FromHtml("#7f849c"); // Overlay 1
                    case TokenType.Number:
                        return ColorTranslator.FromHtml("#fab387"); // Peach
                    case TokenType.Operator:
                        return ColorTranslator.FromHtml("#f5c2e7"); // Pink
                    case TokenType.Punctuation:
                        return ColorTranslator.FromHtml("#9399b2"); // Overlay 2
                    case TokenType.Type:
                        return ColorTranslator.FromHtml("#74c7ec"); // Sapphire
                    case TokenType.Method:
                        return ColorTranslator.FromHtml("#89b4fa"); // Blue
                    // Diff: fixed red/green family (NOT theme-arbitrary), tuned for the dark palette.
                    case TokenType.Addition:
                        return ColorTranslator.FromHtml("#a6e3a1"); // Green
                    case TokenType.Deletion:
                        return ColorTranslator.FromHtml("#f38ba8"); // Red
                    case TokenType.DiffMeta:
                        return ColorTranslator.FromHtml("#89dceb"); // Sky
                    case TokenType.Normal:
                    default:
                        return ColorTranslator.FromHtml("#cdd6f4"); // Text
                }
            }
            else
            {
                // Catppuccin Latte palette mapping (light)
                switch (tokenType)
                {
                    case TokenType.Keyword:
                        return ColorTranslator.FromHtml("#8839ef"); // mauve
                    case TokenType.String:
                        return ColorTranslator.FromHtml("#40a02b"); // green
                    case TokenType.Comment:
                        return ColorTranslator.FromHtml("#8c8fa1"); // overlay 1
                    case TokenType.Number:
                        return ColorTranslator.FromHtml("#fe640b");  // peach
                    case TokenType.Operator:
                        return ColorTranslator.FromHtml("#ea76cb"); // pink
                    case TokenType.Punctuation:
                        return ColorTranslator.FromHtml("#7c7f93"); // overlay 2
                    case TokenType.Type:
                        return ColorTranslator.FromHtml("#209fb5"); // sapphire
                    case TokenType.Method:
                        return ColorTranslator.FromHtml("#1e66f5"); // blue
                    // Diff: fixed red/green family (NOT theme-arbitrary), tuned for the light palette.
                    case TokenType.Addition:
                        return ColorTranslator.FromHtml("#40a02b"); // green
                    case TokenType.Deletion:
                        return ColorTranslator.FromHtml("#d20f39"); // red
                    case TokenType.DiffMeta:
                        return ColorTranslator.FromHtml("#209fb5"); // sapphire
                    case TokenType.Normal:
                    default:
                        return SystemColors.WindowText; // Align with renderer/default
                }
            }
        }

        /// <summary>
        /// Background color for a token type, or null for "no background" (the common case). Only the
        /// diff token types return a value: a desaturated red/green band behind added/removed lines so
        /// a diff reads at a glance even past the foreground color. Like the diff foreground colors,
        /// these are a fixed red/green family — theme-tuned shades, never the accent palette.
        /// </summary>
        public static Color? GetTokenBackgroundColorForTheme(TokenType tokenType, bool dark)
        {
            if (dark)
            {
                switch (tokenType)
                {
                    case TokenType.Addition:
                        return ColorTranslator.FromHtml("#28361f"); // muted green band on dark
                    case TokenType.Deletion:
                        return ColorTranslator.FromHtml("#3a2228"); // muted red band on dark
                    default:
                        return null;
                }
            }
            else
            {
                switch (tokenType)
                {
                    case TokenType.Addition:
                        return ColorTranslator.FromHtml("#e6f3e0"); // pale green band on light
                    case TokenType.Deletion:
                        return ColorTranslator.FromHtml("#fbe0e4"); // pale red band on light
                    default:
                        return null;
                }
            }
        }

        /// <summary>
        /// Gets a language-specific highlighter by language identifier
        /// </summary>
        /// <param name="language">The language identifier</param>
        /// <returns>The highlighter for the language, or null if not supported</returns>
        public static ISyntaxHighlighter GetHighlighter(string language)
        {
            if (string.IsNullOrEmpty(language))
                return null;

            ISyntaxHighlighter highlighter;
            return _highlighters.TryGetValue(language, out highlighter) ? highlighter : null;
        }

        /// <summary>
        /// Checks if a language is supported
        /// </summary>
        /// <param name="language">The language identifier to check</param>
        /// <returns>True if the language is supported, false otherwise</returns>
        public static bool IsLanguageSupported(string language)
        {
            return !string.IsNullOrEmpty(language) && _highlighters.ContainsKey(language);
        }

        /// <summary>
        /// Gets all supported language identifiers
        /// </summary>
        /// <returns>Array of supported language identifiers</returns>
        public static string[] GetSupportedLanguages()
        {
            var languages = new string[_highlighters.Count];
            _highlighters.Keys.CopyTo(languages, 0);
            return languages;
        }

        /// <summary>
        /// Resolves a highlighter language id from a file name's extension (e.g. "src/a.cs" -> "csharp").
        /// Returns null when the extension is unknown, in which case callers should render plain text.
        /// </summary>
        public static string GetLanguageForFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return null;
            string ext;
            try { ext = System.IO.Path.GetExtension(fileName); }
            catch { return null; }
            if (string.IsNullOrEmpty(ext)) return null;
            switch (ext.ToLowerInvariant())
            {
                case ".cs": case ".csx": return "csharp";
                case ".js": case ".jsx": case ".mjs": case ".cjs": return "javascript";
                case ".ts": case ".tsx": return "typescript";
                case ".json": case ".jsonc": case ".json5": return "json";
                case ".py": case ".pyw": case ".pyi": case ".pyx": return "python";
                case ".html": case ".htm": case ".xhtml": return "html";
                case ".css": case ".scss": case ".sass": case ".less": return "css";
                case ".xml": case ".xaml": case ".xsd": case ".xsl": case ".xslt": case ".svg":
                case ".csproj": case ".vbproj": case ".resx": case ".props": case ".targets": return "xml";
                case ".yml": case ".yaml": return "yaml";
                case ".sh": case ".bash": case ".zsh": case ".ksh": return "bash";
                case ".bat": case ".cmd": return "batch";
                case ".ps1": case ".psm1": case ".psd1": return "powershell";
                case ".c": case ".h": return "c";
                case ".cpp": case ".cxx": case ".cc": case ".hpp": case ".hxx": case ".hh": return "cpp";
                case ".go": return "go";
                case ".rs": return "rust";
                case ".java": case ".jav": return "java";
                case ".rb": case ".rake": case ".gemspec": return "ruby";
                case ".php": case ".phtml": return "php";
                case ".sql": return "sql";
                case ".swift": return "swift";
                case ".kt": case ".kts": return "kotlin";
                case ".lua": return "lua";
                case ".pl": case ".pm": return "perl";
                case ".scala": case ".sc": return "scala";
                case ".dart": return "dart";
                case ".ex": case ".exs": return "elixir";
                case ".erl": case ".hrl": return "erlang";
                case ".hs": case ".lhs": return "haskell";
                case ".fs": case ".fsi": case ".fsx": return "fsharp";
                case ".ml": case ".mli": return "ocaml";
                case ".pas": case ".pp": return "pascal";
                case ".vb": case ".vbs": case ".vba": return "visualbasic";
                case ".zig": return "zig";
                case ".diff": case ".patch": return "diff";
                case ".csv": return "csv";
                case ".gql": case ".graphql": return "gql";
                case ".ini": case ".cfg": case ".conf": case ".properties": case ".toml": return "properties";
                case ".asm": case ".s": case ".nasm": return "assembly";
                case ".ada": case ".adb": case ".ads": return "ada";
                case ".f": case ".for": case ".f90": case ".f95": return "fortran";
                case ".bas": return "basic";
                case ".mcfunction": return "mcfunction";
                default: return null;
            }
        }

        /// <summary>
        /// Returns a deduplicated set of file dialog patterns (e.g., "*.cs") contributed by
        /// built-in highlighters. This avoids reflection by referencing the known types.
        /// </summary>
        public static string[] GetAllHighlighterFilePatterns()
        {
            var list = new List<string>(256);
            try
            {
                // Add each highlighter's public static FileTypes if available
                AddPatterns(list, AdaHighlighter.FileTypes);
                AddPatterns(list, AssemblyHighlighter.FileTypes);
                AddPatterns(list, BashHighlighter.FileTypes);
                AddPatterns(list, BasicHighlighter.FileTypes);
                AddPatterns(list, BatchHighlighter.FileTypes);
                AddPatterns(list, CHighlighter.FileTypes);
                AddPatterns(list, CppHighlighter.FileTypes);
                AddPatterns(list, CSharpHighlighter.FileTypes);
                AddPatterns(list, CssHighlighter.FileTypes);
                AddPatterns(list, CsvHighlighter.FileTypes);
                AddPatterns(list, DartHighlighter.FileTypes);
                AddPatterns(list, EbnfHighlighter.FileTypes);
                AddPatterns(list, ElixirHighlighter.FileTypes);
                AddPatterns(list, ErlangHighlighter.FileTypes);
                AddPatterns(list, FortranHighlighter.FileTypes);
                AddPatterns(list, FSharpHighlighter.FileTypes);
                AddPatterns(list, GoHighlighter.FileTypes);
                AddPatterns(list, GqlHighlighter.FileTypes);
                AddPatterns(list, HaskellHighlighter.FileTypes);
                AddPatterns(list, HtmlHighlighter.FileTypes);
                AddPatterns(list, JavaHighlighter.FileTypes);
                AddPatterns(list, JavaScriptHighlighter.FileTypes);
                AddPatterns(list, JsonHighlighter.FileTypes);
                AddPatterns(list, KotlinHighlighter.FileTypes);
                AddPatterns(list, LispHighlighter.FileTypes);
                AddPatterns(list, LuaHighlighter.FileTypes);
                AddPatterns(list, McfunctionHighlighter.FileTypes);
                AddPatterns(list, OcamlHighlighter.FileTypes);
                AddPatterns(list, PascalHighlighter.FileTypes);
                AddPatterns(list, PerlHighlighter.FileTypes);
                AddPatterns(list, PhpHighlighter.FileTypes);
                AddPatterns(list, PowerShellHighlighter.FileTypes);
                AddPatterns(list, PropertiesHighlighter.FileTypes);
                AddPatterns(list, PythonHighlighter.FileTypes);
                AddPatterns(list, RegexHighlighter.FileTypes);
                AddPatterns(list, RubyHighlighter.FileTypes);
                AddPatterns(list, RustHighlighter.FileTypes);
                AddPatterns(list, ScalaHighlighter.FileTypes);
                AddPatterns(list, SqlHighlighter.FileTypes);
                AddPatterns(list, SwiftHighlighter.FileTypes);
                AddPatterns(list, TypeScriptHighlighter.FileTypes);
                AddPatterns(list, VisualBasicHighlighter.FileTypes);
                AddPatterns(list, XmlHighlighter.FileTypes);
                AddPatterns(list, YamlHighlighter.FileTypes);
                AddPatterns(list, ZigHighlighter.FileTypes);
            }
            catch { }

            // Dedup case-insensitively
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dedup = new List<string>(list.Count);
            for (int i = 0; i < list.Count; i++)
            {
                string p = list[i];
                if (string.IsNullOrEmpty(p)) continue;
                if (seen.Contains(p)) continue;
                seen.Add(p); dedup.Add(p);
            }
            return dedup.ToArray();
        }

        // Helper for GetAllHighlighterFilePatterns to append patterns safely
        private static void AddPatterns(List<string> dst, string[] arr)
        {
            if (dst == null || arr == null || arr.Length == 0)
                return;
            for (int i = 0; i < arr.Length; i++)
            {
                string s = arr[i];
                if (!string.IsNullOrEmpty(s))
                    dst.Add(s);
            }
        }
    }
}
