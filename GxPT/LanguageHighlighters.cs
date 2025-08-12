// LanguageHighlighters.cs
// Language-specific syntax highlighters for .NET 3.5 compatibility
// Implements regex-based tokenization for C#, JavaScript, Python, and JSON

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace XpChat
{
    /// <summary>
    /// Base class for regex-based syntax highlighters
    /// </summary>
    public abstract class RegexHighlighterBase : ISyntaxHighlighter
    {
        protected struct TokenPattern
        {
            public Regex Regex;
            public TokenType Type;
            public int Priority; // Lower number = higher priority

            public TokenPattern(string pattern, TokenType type, int priority)
            {
                Regex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.Multiline);
                Type = type;
                Priority = priority;
            }
        }

        protected TokenPattern[] _patterns;

        public abstract string Language { get; }

        protected abstract TokenPattern[] GetPatterns();

        public RegexHighlighterBase()
        {
            _patterns = GetPatterns();
            // Sort patterns by priority (lower number = higher priority)
            Array.Sort(_patterns, (a, b) => a.Priority.CompareTo(b.Priority));
        }

        public virtual List<CodeToken> Tokenize(string sourceCode)
        {
            if (string.IsNullOrEmpty(sourceCode))
                return new List<CodeToken>();

            var tokens = new List<CodeToken>();
            var processed = new bool[sourceCode.Length];

            // Process patterns in priority order
            foreach (var pattern in _patterns)
            {
                var matches = pattern.Regex.Matches(sourceCode);
                foreach (Match match in matches)
                {
                    // Check if this region has already been processed by a higher priority pattern
                    bool canProcess = true;
                    for (int i = match.Index; i < match.Index + match.Length && canProcess; i++)
                    {
                        if (processed[i])
                            canProcess = false;
                    }

                    if (canProcess)
                    {
                        tokens.Add(new CodeToken(match.Value, pattern.Type, match.Index));
                        // Mark this region as processed
                        for (int i = match.Index; i < match.Index + match.Length; i++)
                        {
                            processed[i] = true;
                        }
                    }
                }
            }

            // Fill in gaps with normal text
            for (int i = 0; i < sourceCode.Length; )
            {
                if (!processed[i])
                {
                    int start = i;
                    while (i < sourceCode.Length && !processed[i])
                        i++;

                    string text = sourceCode.Substring(start, i - start);
                    tokens.Add(new CodeToken(text, TokenType.Normal, start));
                }
                else
                {
                    i++;
                }
            }

            // Sort tokens by start index
            tokens.Sort((a, b) => a.StartIndex.CompareTo(b.StartIndex));
            return tokens;
        }
    }

    /// <summary>
    /// C# syntax highlighter
    /// </summary>
    public class CSharpHighlighter : RegexHighlighterBase
    {
        public override string Language
        {
            get { return "cs"; }
        }

        protected override TokenPattern[] GetPatterns()
        {
            return new TokenPattern[]
            {
                // Single-line comments (highest priority to avoid conflicts)
                new TokenPattern(@"//.*$", TokenType.Comment, 1),
                
                // Multi-line comments
                new TokenPattern(@"/\*[\s\S]*?\*/", TokenType.Comment, 2),
                
                // String literals (including verbatim strings)
                new TokenPattern(@"@""(?:[^""]|"""")*""|""(?:[^""\\]|\\.)*""", TokenType.String, 3),
                
                // Character literals
                new TokenPattern(@"'(?:[^'\\]|\\.)'", TokenType.String, 4),
                
                // Numbers (integers, floats, hex)
                new TokenPattern(@"\b(?:0[xX][0-9a-fA-F]+|(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?[fFdDmM]?)\b", TokenType.Number, 5),
                
                // Keywords
                new TokenPattern(@"\b(?:abstract|as|base|bool|break|byte|case|catch|char|checked|class|const|continue|decimal|default|delegate|do|double|else|enum|event|explicit|extern|false|finally|fixed|float|for|foreach|goto|if|implicit|in|int|interface|internal|is|lock|long|namespace|new|null|object|operator|out|override|params|private|protected|public|readonly|ref|return|sbyte|sealed|short|sizeof|stackalloc|static|string|struct|switch|this|throw|true|try|typeof|uint|ulong|unchecked|unsafe|ushort|using|var|virtual|void|volatile|while)\b", TokenType.Keyword, 6),
                
                // Built-in types
                new TokenPattern(@"\b(?:bool|byte|char|decimal|double|float|int|long|object|sbyte|short|string|uint|ulong|ushort|void)\b", TokenType.Type, 7),
                
                // Method calls (identifier followed by opening parenthesis)
                new TokenPattern(@"\b[a-zA-Z_][a-zA-Z0-9_]*(?=\s*\()", TokenType.Method, 8),
                
                // Operators
                new TokenPattern(@"[+\-*/%=!<>&|^~?:]+|<<|>>|\+\+|--|&&|\|\||==|!=|<=|>=|\?\?", TokenType.Operator, 9),
                
                // Punctuation
                new TokenPattern(@"[{}()\[\];,.]", TokenType.Punctuation, 10)
            };
        }
    }

    /// <summary>
    /// JavaScript syntax highlighter
    /// </summary>
    public class JavaScriptHighlighter : RegexHighlighterBase
    {
        public override string Language
        {
            get { return "js"; }
        }

        protected override TokenPattern[] GetPatterns()
        {
            return new TokenPattern[]
            {
                // Single-line comments
                new TokenPattern(@"//.*$", TokenType.Comment, 1),
                
                // Multi-line comments
                new TokenPattern(@"/\*[\s\S]*?\*/", TokenType.Comment, 2),
                
                // String literals (single and double quotes, template literals)
                new TokenPattern(@"`(?:[^`\\]|\\.)*`|'(?:[^'\\]|\\.)*'|""(?:[^""\\]|\\.)*""", TokenType.String, 3),
                
                // Regular expressions
                new TokenPattern(@"/(?:[^/\\\n]|\\.)+/[gimuy]*", TokenType.String, 4),
                
                // Numbers
                new TokenPattern(@"\b(?:0[xX][0-9a-fA-F]+|(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?)\b", TokenType.Number, 5),
                
                // Keywords
                new TokenPattern(@"\b(?:async|await|break|case|catch|class|const|continue|debugger|default|delete|do|else|export|extends|false|finally|for|function|if|import|in|instanceof|let|new|null|return|super|switch|this|throw|true|try|typeof|undefined|var|void|while|with|yield)\b", TokenType.Keyword, 6),
                
                // Built-in types and objects
                new TokenPattern(@"\b(?:Array|Boolean|Date|Error|Function|JSON|Math|Number|Object|RegExp|String|Promise|Map|Set|Symbol)\b", TokenType.Type, 7),
                
                // Method calls
                new TokenPattern(@"\b[a-zA-Z_$][a-zA-Z0-9_$]*(?=\s*\()", TokenType.Method, 8),
                
                // Operators
                new TokenPattern(@"[+\-*/%=!<>&|^~?:]+|<<|>>|\+\+|--|&&|\|\||==|!=|<=|>=|===|!==|\?\?", TokenType.Operator, 9),
                
                // Punctuation
                new TokenPattern(@"[{}()\[\];,.]", TokenType.Punctuation, 10)
            };
        }
    }

    /// <summary>
    /// JSON syntax highlighter
    /// </summary>
    public class JsonHighlighter : RegexHighlighterBase
    {
        public override string Language
        {
            get { return "json"; }
        }

        protected override TokenPattern[] GetPatterns()
        {
            return new TokenPattern[]
            {
                // String literals (JSON keys and values)
                new TokenPattern(@"""(?:[^""\\]|\\.)*""", TokenType.String, 1),
                
                // Numbers
                new TokenPattern(@"-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?", TokenType.Number, 2),
                
                // Boolean and null values
                new TokenPattern(@"\b(?:true|false|null)\b", TokenType.Keyword, 3),
                
                // Punctuation
                new TokenPattern(@"[{}()\[\]:,]", TokenType.Punctuation, 4)
            };
        }
    }

    /// <summary>
    /// Python syntax highlighter
    /// </summary>
    public class PythonHighlighter : RegexHighlighterBase
    {
        public override string Language
        {
            get { return "python"; }
        }

        protected override TokenPattern[] GetPatterns()
        {
            return new TokenPattern[]
            {
                // Comments
                new TokenPattern(@"#.*$", TokenType.Comment, 1),
                
                // Triple-quoted strings (docstrings)
                new TokenPattern(@"(?:'''[\s\S]*?'''|""""""[\s\S]*?"""""")", TokenType.String, 2),
                
                // String literals
                new TokenPattern(@"(?:[fFrRuUbB]?'(?:[^'\\]|\\.)*'|[fFrRuUbB]?""(?:[^""\\]|\\.)*"")", TokenType.String, 3),
                
                // Numbers
                new TokenPattern(@"\b(?:0[xX][0-9a-fA-F]+|0[oO][0-7]+|0[bB][01]+|(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?[jJ]?)\b", TokenType.Number, 4),
                
                // Keywords
                new TokenPattern(@"\b(?:False|None|True|and|as|assert|async|await|break|class|continue|def|del|elif|else|except|finally|for|from|global|if|import|in|is|lambda|nonlocal|not|or|pass|raise|return|try|while|with|yield)\b", TokenType.Keyword, 5),
                
                // Built-in types and functions
                new TokenPattern(@"\b(?:bool|int|float|complex|str|list|tuple|dict|set|frozenset|bytes|bytearray|memoryview|range|enumerate|zip|map|filter|len|print|input|open|type|isinstance|hasattr|getattr|setattr|delattr)\b", TokenType.Type, 6),
                
                // Function definitions and calls
                new TokenPattern(@"\b[a-zA-Z_][a-zA-Z0-9_]*(?=\s*\()", TokenType.Method, 7),
                
                // Operators
                new TokenPattern(@"[+\-*/%=!<>&|^~@]+|\*\*|//|<<|>>|<=|>=|==|!=|\+=|-=|\*=|/=|%=|&=|\|=|\^=|<<=|>>=|\*\*=|//=", TokenType.Operator, 8),
                
                // Punctuation
                new TokenPattern(@"[{}()\[\]:;,.]", TokenType.Punctuation, 9)
            };
        }
    }
}
