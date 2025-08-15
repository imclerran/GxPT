// LanguageHighlighters.cs
// Language-specific syntax highlighters for .NET 3.5 compatibility
// Implements regex-based tokenization for C#, JavaScript, Python, and JSON

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace GxPT
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

        // Optional aliases; base returns empty list to keep compatibility
        public virtual string[] Aliases
        {
            get { return new string[0]; }
        }

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
    /// C syntax highlighter
    /// </summary>
    public class CHighlighter : RegexHighlighterBase
    {
        public override string Language
        {
            get { return "c"; }
        }

        public override string[] Aliases
        {
            get { return new string[] { "ansi-c", "c99", "c11", "c17" }; }
        }

        protected override TokenPattern[] GetPatterns()
        {
            return new TokenPattern[]
            {
                // Single-line comments (C99 style)
                new TokenPattern(@"//.*$", TokenType.Comment, 1),
                
                // Multi-line comments (traditional C style)
                new TokenPattern(@"/\*[\s\S]*?\*/", TokenType.Comment, 2),
                
                // Preprocessor directives
                new TokenPattern(@"#\s*(?:include|define|undef|ifdef|ifndef|if|elif|else|endif|pragma|line|error|warning)\b.*$", TokenType.Comment, 3),
                
                // String literals
                new TokenPattern(@"""(?:[^""\\]|\\.)*""", TokenType.String, 4),
                
                // Character literals
                new TokenPattern(@"'(?:[^'\\]|\\.)+'", TokenType.String, 5),
                
                // Numbers (integers, floats, hex, octal, binary)
                new TokenPattern(@"\b(?:0[xX][0-9a-fA-F]+[uUlL]*|0[bB][01]+[uUlL]*|0[0-7]+[uUlL]*|(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?[fFlL]?[uUlL]*)\b", TokenType.Number, 6),
                
                // C keywords
                new TokenPattern(@"\b(?:auto|break|case|char|const|continue|default|do|double|else|enum|extern|float|for|goto|if|inline|int|long|register|restrict|return|short|signed|sizeof|static|struct|switch|typedef|union|unsigned|void|volatile|while|_Alignas|_Alignof|_Atomic|_Static_assert|_Noreturn|_Thread_local|_Generic|_Imaginary|_Complex|_Bool)\b", TokenType.Keyword, 7),
                
                // Built-in types and common typedefs
                new TokenPattern(@"\b(?:char|int|short|long|float|double|void|signed|unsigned|size_t|ptrdiff_t|wchar_t|bool|FILE|NULL|true|false)\b", TokenType.Type, 8),
                
                // Standard library functions (common ones)
                new TokenPattern(@"\b(?:printf|scanf|fprintf|fscanf|sprintf|sscanf|fgets|fputs|fopen|fclose|malloc|calloc|realloc|free|strlen|strcpy|strcat|strcmp|strncmp|memcpy|memset|memmove|memcmp|atoi|atof|exit|abort|system)\b(?=\s*\()", TokenType.Method, 9),
                
                // Function calls (identifier followed by opening parenthesis)
                new TokenPattern(@"\b[a-zA-Z_][a-zA-Z0-9_]*(?=\s*\()", TokenType.Method, 10),
                
                // Operators
                new TokenPattern(@"[+\-*/%=!<>&|^~?:]+|<<|>>|\+\+|--|&&|\|\||==|!=|<=|>=|->|\.\.\.", TokenType.Operator, 11),
                
                // Punctuation
                new TokenPattern(@"[{}()\[\];,.]", TokenType.Punctuation, 12)
            };
        }
    }

    /// <summary>
    /// C++ syntax highlighter
    /// </summary>
    public class CppHighlighter : RegexHighlighterBase
    {
        public override string Language
        {
            get { return "cpp"; }
        }

        public override string[] Aliases
        {
            get { return new string[] { "c++", "cplusplus" }; }
        }

        protected override TokenPattern[] GetPatterns()
        {
            return new TokenPattern[]
            {
                // Single-line comments
                new TokenPattern(@"//.*$", TokenType.Comment, 1),
                
                // Multi-line comments
                new TokenPattern(@"/\*[\s\S]*?\*/", TokenType.Comment, 2),
                
                // String literals
                new TokenPattern(@"""(?:[^""\\]|\\.)*""", TokenType.String, 3),
                
                // Character literals
                new TokenPattern(@"'(?:[^'\\]|\\.)'", TokenType.String, 4),
                
                // Numbers (integers, floats, hex)
                new TokenPattern(@"\b(?:0[xX][0-9a-fA-F]+|(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?)\b", TokenType.Number, 5),
                
                // Keywords
                new TokenPattern(@"\b(?:alignas|alignof|asm|auto|bool|break|case|catch|char|char16_t|char32_t|class|const|constexpr|const_cast|continue|decltype|default|delete|do|double|dynamic_cast|else|enum|explicit|export|extern|false|final|float|for|friend|goto|if|inline|int|long|mutable|namespace|new|noexcept|nullptr|operator|override|private|protected|public|register|reinterpret_cast|return|short|signed|sizeof|static|static_assert|static_cast|struct|switch|template|this|thread_local|throw|true|try|typedef|typeid|typename|union|unsigned|using|virtual|void|volatile|wchar_t|while)\b", TokenType.Keyword, 6),
                
                // Built-in types
                new TokenPattern(@"\b(?:bool|char|double|float|int|long|short|signed|unsigned|void|wchar_t)\b", TokenType.Type, 7),
                
                // Function definitions and calls
                new TokenPattern(@"\b[a-zA-Z_]\w*(?=\s*\()", TokenType.Method, 8),
                
                // Operators
                new TokenPattern(@"[+\-*/%=!<>&|^~?:]+|<<|>>|\+\+|--|&&|\|\||==|!=|<=|>=|\b(?:and|or|not)\b", TokenType.Operator, 9),
                
                // Punctuation
                new TokenPattern(@"[{}()\[\];,.]", TokenType.Punctuation, 10)
            };
        }
    }

    /// <summary>
    /// C# syntax highlighter
    /// </summary>
    public class CSharpHighlighter : RegexHighlighterBase
    {
        public override string Language
        {
            get { return "csharp"; }
        }

        public override string[] Aliases
        {
            get { return new string[] { "cs", "c#", "dotnet", "c-sharp" }; }
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
    /// Go (Golang) syntax highlighter
    /// </summary>
    public class GoHighlighter : RegexHighlighterBase
    {
        public override string Language
        {
            get { return "go"; }
        }

        public override string[] Aliases
        {
            get { return new string[] { "golang" }; }
        }

        protected override TokenPattern[] GetPatterns()
        {
            return new TokenPattern[]
            {
                // Single-line comments
                new TokenPattern(@"//.*$", TokenType.Comment, 1),

                // Multi-line comments
                new TokenPattern(@"/\*[\s\S]*?\*/", TokenType.Comment, 2),

                // String literals (including raw strings with backticks)
                new TokenPattern(@"`(?:[^`]|``)*`|""(?:[^""\\]|\\.)*""", TokenType.String, 3),

                // Character literals
                new TokenPattern(@"'\\?.'", TokenType.String, 4),

                // Numbers (integers, floats, hex, octal)
                new TokenPattern(@"\b(?:0[xX][0-9a-fA-F]+|0[oO]?[0-7]+|\d+(?:_\d+)*(\.\d+(?:_\d+)*)?(?:[eE][+-]?\d+)?[fF]?)\b", TokenType.Number, 5),

                // Keywords
                new TokenPattern(@"\b(?:break|case|chan|const|continue|default|defer|else|fallthrough|for|func|go|goto|if|import|interface|map|package|range|return|select|struct|switch|type|var)\b", TokenType.Keyword, 6),

                // Built-in types
                new TokenPattern(@"\b(?:bool|byte|complex64|complex128|error|float32|float64|int|int8|int16|int32|int64|rune|string|uint|uint8|uint16|uint32|uint64|uintptr)\b", TokenType.Type, 7),

                // Function and method calls
                new TokenPattern(@"\b[a-zA-Z_][a-zA-Z0-9_]*(?=\s*\()", TokenType.Method, 8),

                // Operators
                new TokenPattern(@"[+\-*/%=!<>&|^~:.?]+|\+\+|--|&&|\|\||==|!=|<=|>=|\.\.\.", TokenType.Operator, 9),

                // Punctuation
                new TokenPattern(@"[{}()\[\];,.]", TokenType.Punctuation, 10)
            };
        }
    }

    /// <summary>
    /// Java syntax highlighter
    /// </summary>
    public class JavaHighlighter : RegexHighlighterBase
    {
        public override string Language
        {
            get { return "java"; }
        }

        public override string[] Aliases
        {
            get { return new string[] { "jdk", "jre" }; }
        }

        protected override TokenPattern[] GetPatterns()
        {
            return new TokenPattern[]
            {
                // Single-line comments
                new TokenPattern(@"//.*$", TokenType.Comment, 1),

                // Multi-line comments
                new TokenPattern(@"/\*[\s\S]*?\*/", TokenType.Comment, 2),

                // String literals
                new TokenPattern(@"""(?:[^""\\]|\\.)*""", TokenType.String, 3),

                // Character literals
                new TokenPattern(@"'\\?.'", TokenType.String, 4),

                // Numbers (integers, floats, hex, octal, binary)
                new TokenPattern(@"\b(?:0[xX][0-9a-fA-F]+|0[bB][01]+|0[oO]?[0-7]+|\d+(?:_\d+)*\.\d+(?:[eE][+-]?\d+)?[dDfF]?)\b|\b\d+\b", TokenType.Number, 5),

                // Keywords
                new TokenPattern(@"\b(?:abstract|assert|boolean|break|byte|case|catch|char|class|const|continue|default|do|double|else|enum|extends|final|finally|float|for|if|goto|implements|import|instanceof|int|interface|long|native|new|null|package|private|protected|public|return|short|static|strictfp|super|switch|synchronized|this|throw|throws|transient|try|void|volatile|while)\b", TokenType.Keyword, 6),

                // Built-in types
                new TokenPattern(@"\b(?:boolean|byte|char|double|float|int|long|short|void)\b", TokenType.Type, 7),

                // Method and constructor calls
                new TokenPattern(@"\b[a-zA-Z_][a-zA-Z0-9_]*(?=\s*\()", TokenType.Method, 8),

                // Operators
                new TokenPattern(@"[+\-*/%=!<>&|^~?:]+|\(\+\+|--|&&|\|\||==|!=|<=|>=|->|::|\.\.\.", TokenType.Operator, 9),

                // Punctuation
                new TokenPattern(@"[{}()\[\];.,]", TokenType.Punctuation, 10)
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
            get { return "javascript"; }
        }

        public override string[] Aliases
        {
            get { return new string[] { "js", "node", "nodejs" }; }
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

        public override string[] Aliases
        {
            get { return new string[] { "jsonc", "json5" }; }
        }

        protected override TokenPattern[] GetPatterns()
        {
            return new TokenPattern[]
            {
                // Property names (strings immediately followed by optional whitespace and a colon)
                // Higher priority so keys are colored differently than string values
                new TokenPattern(@"""(?:[^""\\]|\\.)*""(?=\s*:)", TokenType.Type, 1),

                // String literals (values and non-key strings)
                new TokenPattern(@"""(?:[^""\\]|\\.)*""", TokenType.String, 2),
                
                // Numbers
                new TokenPattern(@"-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?", TokenType.Number, 3),
                
                // Boolean and null values
                new TokenPattern(@"\b(?:true|false|null)\b", TokenType.Keyword, 4),
                
                // Punctuation
                new TokenPattern(@"[{}()\[\]:,]", TokenType.Punctuation, 5)
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

        public override string[] Aliases
        {
            get { return new string[] { "py", "python3", "py3" }; }
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

    /// <summary>
    /// TypeScript syntax highlighter
    /// </summary>
    public class TypeScriptHighlighter : RegexHighlighterBase
    {
        public override string Language
        {
            get { return "typescript"; }
        }

        public override string[] Aliases
        {
            get { return new string[] { "ts" }; }
        }

        protected override TokenPattern[] GetPatterns()
        {
            return new TokenPattern[]
            {
                // Single-line comments
                new TokenPattern(@"//.*$", TokenType.Comment, 1),

                // Multi-line comments
                new TokenPattern(@"/\*[\s\S]*?\*/", TokenType.Comment, 2),

                // String literals (single, double, and template literals)
                new TokenPattern(@"`(?:[^`\\]|\\.)*`|'(?:[^'\\]|\\.)*'|""(?:[^""\\]|\\.)*""", TokenType.String, 3),

                // Regular expressions
                new TokenPattern(@"/(?:[^/\\\n]|\\.)+/[gimuy]*", TokenType.String, 4),

                // Numbers
                new TokenPattern(@"\b(?:0[xX][0-9a-fA-F]+|0[bB][01]+|0[oO][0-7]+|\d+(\.\d+)?([eE][+-]?\d+)?|NaN|Infinity)\b", TokenType.Number, 5),

                // Keywords
                new TokenPattern(@"\b(?:abstract|any|as|async|await|boolean|break|case|catch|class|const|continue|debugger|declare|default|delete|do|else|enum|export|extends|false|finally|for|from|function|if|implements|import|in|infer|instanceof|interface|is|keyof|let|module|namespace|never|null|number|object|package|private|protected|public|readonly|require|return|string|super|switch|symbol|this|throw|true|try|typeof|undefined|unique|var|void|while|with|yield)\b", TokenType.Keyword, 6),

                // Type annotations
                new TokenPattern(@"\b(?:Array|Date|Promise|PromiseLike|RegExp|Set|Map|WeakSet|WeakMap|ReadonlyArray|Partial|Required|Readonly|Record|Pick|Omit|InstanceType|Parameters|ReturnType)\b", TokenType.Type, 7),

                // TypeScript specific structures
                new TokenPattern(@"\b(?:type|interface|implements|extends|constructor|declare|namespace)\b", TokenType.Keyword, 8),

                // Method and function calls
                new TokenPattern(@"\b[a-zA-Z_$][a-zA-Z0-9_$]*(?=\s*\()", TokenType.Method, 9),

                // Operators
                new TokenPattern(@"[+\-*/%=!<>&|^~?:]+|<<|>>|\+\+|--|&&|\|\||==|!=|<=|>=|===|!==|\?\?", TokenType.Operator, 10),

                // Punctuation
                new TokenPattern(@"[{}()\[\];,.:]", TokenType.Punctuation, 11)
            };
        }
    }

    /// <summary>
    /// Ruby syntax highlighter
    /// </summary>
    public class RubyHighlighter : RegexHighlighterBase
    {
        public override string Language
        {
            get { return "ruby"; }
        }

        public override string[] Aliases
        {
            get { return new string[] { "rb" }; }
        }

        protected override TokenPattern[] GetPatterns()
        {
            return new TokenPattern[]
            {
                // Single-line comments
                new TokenPattern(@"#.*$", TokenType.Comment, 1),

                // Here-documents
                new TokenPattern(@"<<[-~]?['""]?(\w+)['""]?[\s\S]+?\1", TokenType.String, 2),

                // String literals (single, double)
                new TokenPattern(@"'([^'\\]|\\.)*'|""([^""\\]|\\.)*""", TokenType.String, 3),

                // Symbol literals
                new TokenPattern(@":\w+", TokenType.String, 4),

                // Numbers (integers, floats, hex, octal, binary)
                new TokenPattern(@"\b(?:0[xX][0-9a-fA-F]+|0[bB][01]+|0[oO]?[0-7]+|\d+(\.\d+)?([eE][+-]?\d+)?)\b", TokenType.Number, 5),

                // Keywords
                new TokenPattern(@"\b(?:BEGIN|END|alias|and|begin|break|case|class|def|defined\?|do|else|elsif|end|ensure|false|for|if|in|module|next|nil|not|or|redo|rescue|retry|return|self|super|then|true|undef|unless|until|when|while|yield)\b", TokenType.Keyword, 6),

                // Built-in types and constants
                new TokenPattern(@"\b(?:Array|Bignum|Class|Dir|File|Fixnum|Float|Hash|Integer|Module|NilClass|Object|Range|Regexp|String|Symbol|Thread|Time)\b", TokenType.Type, 7),

                // Method definitions
                new TokenPattern(@"\bdef\s+[a-zA-Z_][a-zA-Z0-9_]*", TokenType.Method, 8),

                // Function calls (identifier followed by opening parenthesis)
                new TokenPattern(@"\b[a-zA-Z_][a-zA-Z0-9_]*(?=\s*\()", TokenType.Method, 9),

                // Operators
                new TokenPattern(@"[+\-*/%=!<>&|^~?:]+|<<|>>|\*\*|\+=|-=|\*=|/=|&&|\|\||==|!=|<=|>=|\[\]=", TokenType.Operator, 10),

                // Punctuation
                new TokenPattern(@"[{}()\[\];,.]", TokenType.Punctuation, 11)
            };
        }
    }

    /// <summary>
    /// Rust syntax highlighter
    /// </summary>
    public class RustHighlighter : RegexHighlighterBase
    {
        public override string Language
        {
            get { return "rust"; }
        }

        public override string[] Aliases
        {
            get { return new string[] { "rs", "rustlang" }; }
        }

        protected override TokenPattern[] GetPatterns()
        {
            return new TokenPattern[]
            {
                // Single-line comments
                new TokenPattern(@"//.*$", TokenType.Comment, 1),
                
                // Multi-line comments (block comments)
                new TokenPattern(@"/\*[\s\S]*?\*/", TokenType.Comment, 2),
                
                // String literals (including raw strings)
                new TokenPattern(@"r#?""(?:[^""\\]|\\.)*""#?|""(?:[^""\\]|\\.)*""", TokenType.String, 3),
                
                // Character literals
                new TokenPattern(@"'\\?.'", TokenType.String, 4),
                
                // Numbers (integers, floats, hex, octal, binary)
                new TokenPattern(@"\b(?:0x[0-9a-fA-F_]+|0b[01_]+|0o[0-7_]+|\d+(?:_\d+)*(\.\d+(?:_\d+)*)?(?:[eE][+-]?\d+)?)[iufIlL]*\b", TokenType.Number, 5),
                
                // Keywords
                new TokenPattern(@"\b(?:as|break|const|continue|crate|else|enum|extern|false|fn|for|if|impl|in|let|loop|match|mod|move|mut|pub|ref|return|self|Self|static|struct|super|trait|true|type|unsafe|use|where|while|async|await|dyn|abstract|become|box|do|final|macro|override|priv|try|yield|union)\b", TokenType.Keyword, 6),
                
                // Types
                new TokenPattern(@"\b(?:bool|char|str|u8|u16|u32|u64|u128|usize|i8|i16|i32|i64|i128|isize|f32|f64|Option|Result|String|Vec|Box|Result)\b", TokenType.Type, 7),
                
                // Macro definitions and uses
                new TokenPattern(@"\b[a-zA-Z_][a-zA-Z0-9_]*!(?=\s*[\[{(])", TokenType.Method, 8),
                
                // Function definitions and calls
                new TokenPattern(@"\b[a-zA-Z_][a-zA-Z0-9_]*(?=\s*\()", TokenType.Method, 9),
                
                // Operators
                new TokenPattern(@"[+\-*/%=!<>&|^~?.@:$]+|<<|>>|\*\*|&&|\|\||==|!=|<=|>=|->|=>|::|->|\.\.\.", TokenType.Operator, 10),
                
                // Punctuation
                new TokenPattern(@"[{}()\[\];,.]", TokenType.Punctuation, 11)
            };
        }
    }

    /// <summary>
    /// Zig syntax highlighter
    /// </summary>
    public class ZigHighlighter : RegexHighlighterBase
    {
        public override string Language
        {
            get { return "zig"; }
        }

        public override string[] Aliases
        {
            get { return new string[] { "ziglang" }; }
        }

        protected override TokenPattern[] GetPatterns()
        {
            return new TokenPattern[]
            {
                // Single-line comments
                new TokenPattern(@"//.*$", TokenType.Comment, 1),

                // Multi-line comments
                new TokenPattern(@"/\*[\s\S]*?\*/", TokenType.Comment, 2),

                // String literals (including raw strings)
                new TokenPattern(@"\\?""(?:[^""\\]|\\.)*""", TokenType.String, 3),

                // Character literals
                new TokenPattern(@"'\\?.'", TokenType.String, 4),

                // Numbers (integers, floats, hex, octal, binary)
                new TokenPattern(@"\b(?:0x[0-9a-fA-F_]+|0b[01_]+|0o[0-7_]+|\d+(?:_\d+)*(\.\d+(?:_\d+)*)?(?:[eE][+-]?\d+)?)[a-zA-Z]*\b", TokenType.Number, 5),

                // Keywords
                new TokenPattern(@"\b(?:async|await|break|catch|comptime|const|continue|defer|else|enum|errdefer|export|extern|false|fn|for|if|inline|noalias|null|opaque|or|packed|pub|return|struct|switch|threadlocal|true|try|union|unreachable|usingnamespace|var|volatile|while)\b", TokenType.Keyword, 6),

                // Types and built-in types
                new TokenPattern(@"\b(?:bool|f16|f32|f64|f128|i8|i16|i32|i64|i128|u8|u16|u32|u64|u128|isize|usize|c_void|c_int|noreturn|c_float|void)\b", TokenType.Type, 7),

                // Function and function-like calls (identifier followed by opening parenthesis)
                new TokenPattern(@"\b[a-zA-Z_][a-zA-Z0-9_]*(?=\s*\()", TokenType.Method, 8),

                // Operators
                new TokenPattern(@"[+\-*/%=!<>&|^~?]+|==|!=|<=|>=|&&|\|\||<-|->|\.\.\.", TokenType.Operator, 9),

                // Punctuation
                new TokenPattern(@"[{}()\[\];,.]", TokenType.Punctuation, 10)
            };
        }
    }
}
