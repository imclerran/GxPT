// SyntaxHighlighter.cs
// Syntax highlighting infrastructure for .NET 3.5 compatibility
// Provides interfaces and base classes for language-specific syntax highlighting

using System;
using System.Collections.Generic;
using System.Drawing;

namespace XpChat
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
        /// The language identifier (e.g., "cs", "js", "json", "python")
        /// </summary>
        string Language { get; }

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
            RegisterHighlighter(new CSharpHighlighter());
            RegisterHighlighter(new JavaScriptHighlighter());
            RegisterHighlighter(new JsonHighlighter());
            RegisterHighlighter(new PythonHighlighter());
        }

        /// <summary>
        /// Registers a new language highlighter
        /// </summary>
        /// <param name="highlighter">The highlighter to register</param>
        public static void RegisterHighlighter(ISyntaxHighlighter highlighter)
        {
            if (highlighter != null && !string.IsNullOrEmpty(highlighter.Language))
            {
                _highlighters[highlighter.Language] = highlighter;
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
        /// Gets the color for a specific token type
        /// </summary>
        /// <param name="tokenType">The type of token</param>
        /// <returns>The color to use for rendering this token type</returns>
        public static Color GetTokenColor(TokenType tokenType)
        {
            switch (tokenType)
            {
                case TokenType.Keyword:
                    return Color.Blue;
                case TokenType.String:
                    return Color.FromArgb(163, 21, 21); // Dark red
                case TokenType.Comment:
                    return Color.Green;
                case TokenType.Number:
                    return Color.FromArgb(0, 0, 255); // Blue
                case TokenType.Operator:
                    return Color.FromArgb(128, 128, 128); // Gray
                case TokenType.Punctuation:
                    return Color.FromArgb(128, 128, 128); // Gray
                case TokenType.Type:
                    return Color.FromArgb(43, 145, 175); // Teal
                case TokenType.Method:
                    return Color.FromArgb(128, 0, 128); // Purple
                case TokenType.Normal:
                default:
                    return Color.Black;
            }
        }

        /// <summary>
        /// Gets the font style for a specific token type
        /// </summary>
        /// <param name="tokenType">The type of token</param>
        /// <returns>The font style to use for rendering this token type</returns>
        public static FontStyle GetTokenFontStyle(TokenType tokenType)
        {
            switch (tokenType)
            {
                case TokenType.Keyword:
                case TokenType.Type:
                    return FontStyle.Bold;
                case TokenType.Comment:
                    return FontStyle.Italic;
                default:
                    return FontStyle.Regular;
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
