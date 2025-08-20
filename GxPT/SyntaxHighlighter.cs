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
        Method       // Method/function names
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
            RegisterHighlighter(new BashHighlighter());
            RegisterHighlighter(new BasicHighlighter());
            RegisterHighlighter(new BatchHighlighter());
            RegisterHighlighter(new CHighlighter());
            RegisterHighlighter(new CppHighlighter());
            RegisterHighlighter(new CssHighlighter());
            RegisterHighlighter(new CSharpHighlighter());
            RegisterHighlighter(new EbnfHighlighter());
            RegisterHighlighter(new GoHighlighter());
            RegisterHighlighter(new HtmlHighlighter());
            RegisterHighlighter(new JavaHighlighter());
            RegisterHighlighter(new JavaScriptHighlighter());
            RegisterHighlighter(new JsonHighlighter());
            RegisterHighlighter(new PerlHighlighter());
            RegisterHighlighter(new PowerShellHighlighter());
            RegisterHighlighter(new PythonHighlighter());
            RegisterHighlighter(new RubyHighlighter());
            RegisterHighlighter(new RegexHighlighter());
            RegisterHighlighter(new RustHighlighter());
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
                    case TokenType.Normal:
                    default:
                        return SystemColors.WindowText; // Align with renderer/default
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
    }
}
