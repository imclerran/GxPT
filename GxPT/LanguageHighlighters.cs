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
    /// Bash/Shell syntax highlighter
    /// </summary>
    public class BashHighlighter : RegexHighlighterBase
    {
        public override string Language
        {
            get { return "bash"; }
        }

        public override string[] Aliases
        {
            get { return new string[] { "sh", "shell", "zsh", "ksh" }; }
        }

        protected override TokenPattern[] GetPatterns()
        {
            return new TokenPattern[]
            {
                // Comments
                new TokenPattern(@"#.*$", TokenType.Comment, 1),

                // Here-Documents (simple heuristic)
                    new TokenPattern(@"<<-?['""]?(\w+)['""]?[\s\S]+?\n\1\b", TokenType.String, 2),

                    new TokenPattern(@"\$?""(?:[^""\\]|\\.)*""|\$?'(?:[^'\\]|\\.)*'|`(?:[^`\\]|\\.)*`", TokenType.String, 3),
                new TokenPattern(@"\$?""(?:[^""\\]|\\.)*""|\$?'(?:[^'\\]|\\.)*'|`(?:[^`\\]|\\.)*`", TokenType.String, 3),

                // Variables ($VAR, ${VAR}, $1, $@, $#, $$, etc.)
                new TokenPattern(@"\$\{[^}]+\}|\$[a-zA-Z_][a-zA-Z0-9_]*|\$[0-9]|\$[@#?$!*_-]", TokenType.Type, 4),

                // Numbers
                new TokenPattern(@"\b\d+(?:\.\d+)?\b", TokenType.Number, 5),

                // Keywords (reserved words)
                new TokenPattern(@"\b(?:if|then|else|elif|fi|for|in|do|done|case|esac|while|until|select|function|time|coproc|return|break|continue|shift|getopts|exit|trap|local|readonly|declare|typeset|export|unset|alias|unalias|source)\b", TokenType.Keyword, 6),

                // Common built-in commands (treated as types for color variety)
                new TokenPattern(@"\b(?:echo|printf|read|cd|pwd|test|true|false|type|hash|help|umask|ulimit|jobs|fg|bg|kill|wait)\b", TokenType.Type, 7),

                // Function names: highlight 'function name' and 'name() {' forms
                new TokenPattern(@"\bfunction\s+[a-zA-Z_][a-zA-Z0-9_-]*", TokenType.Method, 8),
                new TokenPattern(@"\b[a-zA-Z_][a-zA-Z0-9_-]*(?=\s*\(\s*\)\s*\{)", TokenType.Method, 9),

                // Operators (pipes, redirects, boolean, test ops)
                new TokenPattern(@"\|\||&&|\|&|\|>|\||>>|>|<|<<|<<<|=|==|=~|!|\+|-|\*|/|%|\^|~", TokenType.Operator, 10),

                // Punctuation
                new TokenPattern(@"[{}()\[\];,:]", TokenType.Punctuation, 11)
            };
        }
    }

    /// <summary>
    /// Classic BASIC (GW-BASIC/QBasic/QuickBASIC) syntax highlighter
    /// </summary>
    public class BasicHighlighter : RegexHighlighterBase
    {
        public override string Language
        {
            get { return "basic"; }
        }

        public override string[] Aliases
        {
            get { return new string[] { "basic", "qbasic", "quickbasic", "gw-basic", "gwbasic", "bas", "basic-80" }; }
        }

        protected override TokenPattern[] GetPatterns()
        {
            return new TokenPattern[]
            {
                // Optional line numbers at start of line
                new TokenPattern(@"(?im)^\s*\d+\b", TokenType.Number, 1),

                // String literals: "..." with doubled quotes for escapes
                new TokenPattern(@"""(?:[^""]|"""")*""", TokenType.String, 2),

                // Comments: ' ... and REM ...
                new TokenPattern(@"(?i)'.*$", TokenType.Comment, 3),
                new TokenPattern(@"(?i)\bREM\b.*$", TokenType.Comment, 4),

                // Keywords (common across many BASIC dialects)
                new TokenPattern(@"(?i)\b(?:PRINT|INPUT|LET|IF|THEN|ELSE|ELSEIF|END|STOP|RUN|GOTO|GOSUB|RETURN|FOR|TO|STEP|NEXT|WHILE|WEND|DO|LOOP|UNTIL|SELECT|CASE|ON|ERROR|RESUME|DIM|REDIM|SHARED|STATIC|AS|SUB|FUNCTION|CALL|OPEN|CLOSE|PUT|GET|READ|DATA|RESTORE|DECLARE|TYPE|CONST|RANDOMIZE|OPTION|BASE|PSET|LINE|CIRCLE|PAINT|DRAW|SCREEN|COLOR|CLS|LOCATE)\b", TokenType.Keyword, 5),

                // Built-in functions/constants (highlight as types for color variety)
                new TokenPattern(@"(?i)\b(?:ABS|ASC|ATN|CHR\$|COS|EXP|FIX|INT|LEFT\$|LEN|LOG|MID\$|RIGHT\$|RND|SGN|SIN|SPACE\$|SQR|STR\$|STRING\$|TAN|TIMER|UCASE\$|LCASE\$|VAL|INSTR|INKEY\$|PEEK|POKE|EOF)\b", TokenType.Type, 6),

                // Variables with type suffix characters ($ % & ! # @)
                new TokenPattern(@"(?i)\b[A-Za-z][A-Za-z0-9]*[\$\%\&\!\#@]\b", TokenType.Type, 7),

                // Numbers: decimal with optional exponent and type suffix; hex/oct/bin (&H/&O/&B)
                new TokenPattern(@"(?i)\b(?:&H[0-9A-F]+|&O[0-7]+|&B[01]+|(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?)(?:[!#%&@])?\b", TokenType.Number, 8),

                // User-defined/built-in function calls (identifier followed by '(')
                new TokenPattern(@"(?i)\b[A-Za-z][A-Za-z0-9]*\b(?=\s*\()", TokenType.Method, 9),

                // Word operators
                new TokenPattern(@"(?i)\b(?:AND|OR|NOT|XOR|EQV|IMP|MOD)\b", TokenType.Operator, 10),

                // Symbol operators
                new TokenPattern(@"<=|>=|<>|<<|>>|[+\-*/^\\=<>]", TokenType.Operator, 11),

                // Punctuation (statement separators, lists, grouping)
                new TokenPattern(@"[(),;:]", TokenType.Punctuation, 12),
            };
        }
    }

    /// <summary>
    /// Windows Batch (CMD) syntax highlighter
    /// </summary>
    public class BatchHighlighter : RegexHighlighterBase
    {
        public override string Language
        {
            get { return "batch"; }
        }

        public override string[] Aliases
        {
            get { return new string[] { "bat", "cmd", "dos", "batchfile" }; }
        }

        protected override TokenPattern[] GetPatterns()
        {
            return new TokenPattern[]
            {
                // Comments: REM ... or lines starting with ::
                new TokenPattern(@"^\s*(?:REM|rem)\b.*$", TokenType.Comment, 1),
                new TokenPattern(@"^\s*::.*$", TokenType.Comment, 2),

                // Strings (double quotes)
                new TokenPattern("\"(?:[^\\\"]|\\.)*\"", TokenType.String, 3),

                // Variables: %VAR%, !VAR!, %1, %~dp0, etc.
                new TokenPattern(@"%~?[0-9A-Za-z][^%\s]*%|![^!\s]+!", TokenType.Type, 4),

                // Labels: :label at line start
                new TokenPattern(@"^\s*:[A-Za-z_][A-Za-z0-9_\-.]*", TokenType.Type, 5),

                // Numbers
                new TokenPattern(@"\b\d+\b", TokenType.Number, 6),

                // Keywords and common commands
                new TokenPattern(@"\b(?:if|else|not|exist|defined|errorlevel|equ|neq|lss|leq|gtr|geq|for|in|do|call|goto|shift|set|setlocal|endlocal|echo|type|copy|move|ren|del|erase|mkdir|rmdir|rd|dir|pause|choice|start|exit|color|title|cls|pushd|popd|path|prompt|assoc|ftype)\b", TokenType.Keyword, 7),

                // Operators and redirection
                new TokenPattern(@"==|\|\||&&|\||>>|>|<|2>&1", TokenType.Operator, 8),

                // Punctuation
                new TokenPattern(@"[()\[\]{};,]", TokenType.Punctuation, 9)
            };
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
    /// CSS syntax highlighter
    /// </summary>
    public class CssHighlighter : RegexHighlighterBase
    {
        public override string Language
        {
            get { return "css"; }
        }

        public override string[] Aliases
        {
            get { return new string[] { "css3", "stylesheet" }; }
        }

        protected override TokenPattern[] GetPatterns()
        {
            return new TokenPattern[]
            {
                // Comments
                new TokenPattern(@"/\*[\s\S]*?\*/", TokenType.Comment, 1),

                // Strings (single and double quotes)
                new TokenPattern(@"'(?:[^'\\]|\\.)*'|""(?:[^""\\]|\\.)*""", TokenType.String, 2),

                // URLs in url() functions
                new TokenPattern(@"url\s*\(\s*(?:'(?:[^'\\]|\\.)*'|""(?:[^""\\]|\\.)*""|[^)]*)\s*\)", TokenType.String, 3),

                // Hex colors
                new TokenPattern(@"#[0-9a-fA-F]{3,8}\b", TokenType.Number, 4),

                // Numbers with units
                new TokenPattern(@"-?\d+(?:\.\d+)?(?:px|em|rem|%|vh|vw|vmin|vmax|cm|mm|in|pt|pc|ex|ch|fr|deg|rad|grad|turn|s|ms|Hz|kHz|dpi|dpcm|dppx)\b", TokenType.Number, 5),

                // Plain numbers
                new TokenPattern(@"-?\d+(?:\.\d+)?\b", TokenType.Number, 6),

                // At-rules (@media, @import, @keyframes, etc.)
                new TokenPattern(@"@(?:media|import|charset|namespace|supports|document|page|font-face|keyframes|-webkit-keyframes|-moz-keyframes)", TokenType.Keyword, 7),

                // Pseudo-classes and pseudo-elements
                new TokenPattern(@"::?(?:hover|active|focus|visited|link|first-child|last-child|nth-child|nth-of-type|before|after|first-line|first-letter|selection|not|lang|root|empty|target|enabled|disabled|checked|invalid|valid|required|optional)\b", TokenType.Type, 8),

                // CSS properties (common ones)
                new TokenPattern(@"\b(?:color|background|background-color|background-image|background-repeat|background-position|background-size|background-attachment|border|border-color|border-style|border-width|border-radius|margin|padding|width|height|min-width|max-width|min-height|max-height|display|position|top|right|bottom|left|float|clear|overflow|visibility|opacity|z-index|font|font-family|font-size|font-weight|font-style|font-variant|line-height|text-align|text-decoration|text-transform|text-indent|letter-spacing|word-spacing|white-space|vertical-align|list-style|list-style-type|list-style-position|list-style-image|table-layout|border-collapse|border-spacing|empty-cells|caption-side|content|quotes|counter-reset|counter-increment|outline|outline-color|outline-style|outline-width|cursor|box-shadow|text-shadow|transform|transition|animation|flex|grid|align-items|justify-content)\b", TokenType.Type, 9),

                // CSS values and keywords
                new TokenPattern(@"\b(?:inherit|initial|unset|revert|auto|none|normal|bold|italic|underline|overline|line-through|uppercase|lowercase|capitalize|left|right|center|justify|top|middle|bottom|block|inline|inline-block|flex|grid|absolute|relative|fixed|static|sticky|hidden|visible|scroll|solid|dashed|dotted|double|groove|ridge|inset|outset|transparent|currentColor|serif|sans-serif|monospace|cursive|fantasy)\b", TokenType.Keyword, 10),

                // Selectors: class names (.class)
                new TokenPattern(@"\.[a-zA-Z_-][a-zA-Z0-9_-]*", TokenType.Type, 11),

                // Selectors: IDs (#id)
                new TokenPattern(@"#[a-zA-Z_-][a-zA-Z0-9_-]*", TokenType.Method, 12),

                // HTML tag selectors
                new TokenPattern(@"\b(?:html|head|title|meta|link|script|style|body|header|nav|main|section|article|aside|footer|h1|h2|h3|h4|h5|h6|p|div|span|a|img|ul|ol|li|table|thead|tbody|tfoot|tr|th|td|form|input|textarea|button|select|option|label|fieldset|legend)\b", TokenType.Method, 13),

                // Important declaration
                new TokenPattern(@"!important\b", TokenType.Keyword, 14),

                // Punctuation
                new TokenPattern(@"[{}()\[\]:;,>+~*]", TokenType.Punctuation, 15)
            };
        }
    }

    /// <summary>
    /// CSV syntax highlighter – highlights columns 1, 2, 3 as different token types and repeats.
    /// Column mapping (1-based): 1 -> Type, 2 -> Method, 3 -> Keyword, then repeats 1,2,3,...
    /// Supports RFC 4180-style quoted fields with doubled quote escapes and multi-line fields.
    /// </summary>
    public class CsvHighlighter : RegexHighlighterBase
    {
        public override string Language
        {
            get { return "csv"; }
        }

        public override string[] Aliases
        {
            get { return new string[] { "csv" }; }
        }

        // Not used; CSV uses a custom tokenizer. Return empty to satisfy base ctor.
        protected override TokenPattern[] GetPatterns()
        {
            return new TokenPattern[0];
        }

        public override List<CodeToken> Tokenize(string sourceCode)
        {
            var tokens = new List<CodeToken>();
            if (string.IsNullOrEmpty(sourceCode))
                return tokens;

            int i = 0;
            int fieldStart = 0;
            int columnIndex = 0; // zero-based
            bool inQuotes = false;

            while (i < sourceCode.Length)
            {
                char c = sourceCode[i];

                if (inQuotes)
                {
                    if (c == '"')
                    {
                        // Handle doubled quotes inside quoted field
                        if (i + 1 < sourceCode.Length && sourceCode[i + 1] == '"')
                        {
                            i += 2; // consume escaped quote
                            continue;
                        }
                        inQuotes = false; // closing quote
                        i++; // include the closing quote in the field token
                        continue;
                    }
                    // Within quoted field: consume character (including newlines)
                    i++;
                    continue;
                }
                else
                {
                    if (c == '"')
                    {
                        inQuotes = true;
                        i++;
                        continue;
                    }

                    if (c == ',')
                    {
                        // Emit field token (may be empty)
                        EmitFieldToken(tokens, sourceCode, fieldStart, i - fieldStart, columnIndex);

                        // Comma punctuation
                        tokens.Add(new CodeToken(",", TokenType.Punctuation, i));

                        // Move to next field/column
                        i++;
                        fieldStart = i;
                        columnIndex++;
                        continue;
                    }

                    if (c == '\r' || c == '\n')
                    {
                        // Emit the field up to the newline
                        EmitFieldToken(tokens, sourceCode, fieldStart, i - fieldStart, columnIndex);

                        // Newline punctuation (treat CRLF as a single token)
                        int nlStart = i;
                        if (c == '\r' && i + 1 < sourceCode.Length && sourceCode[i + 1] == '\n')
                        {
                            i += 2;
                            tokens.Add(new CodeToken("\r\n", TokenType.Punctuation, nlStart));
                        }
                        else
                        {
                            i++;
                            tokens.Add(new CodeToken(sourceCode.Substring(nlStart, 1), TokenType.Punctuation, nlStart));
                        }

                        // Reset for the next record
                        fieldStart = i;
                        columnIndex = 0;
                        continue;
                    }

                    // Regular character outside quotes
                    i++;
                }
            }

            // Emit trailing field if any
            EmitFieldToken(tokens, sourceCode, fieldStart, i - fieldStart, columnIndex);

            return tokens;
        }

        private static void EmitFieldToken(List<CodeToken> tokens, string source, int start, int length, int columnIndex)
        {
            // Emit even for empty fields as a zero-length token? Skip zero-length to avoid clutter.
            if (length <= 0)
                return;

            TokenType type;
            switch (columnIndex % 3)
            {
                case 0: // 1st, 4th, 7th, ... columns
                    type = TokenType.Type;
                    break;
                case 1: // 2nd, 5th, 8th, ... columns
                    type = TokenType.String;
                    break;
                default: // 3rd, 6th, 9th, ... columns
                    type = TokenType.Keyword;
                    break;
            }

            string text = source.Substring(start, length);
            tokens.Add(new CodeToken(text, type, start));
        }
    }

    /// <summary>
    /// EBNF (Extended Backus-Naur Form) syntax highlighter
    /// </summary>
    public class EbnfHighlighter : RegexHighlighterBase
    {
        public override string Language
        {
            get { return "ebnf"; }
        }

        public override string[] Aliases
        {
            get { return new string[] { "bnf", "abnf", "grammar", "yacc", "bison" }; }
        }

        protected override TokenPattern[] GetPatterns()
        {
            return new TokenPattern[]
            {
                // Comments (various styles: (* ... *), // ..., # ...)
                new TokenPattern(@"\(\*[\s\S]*?\*\)", TokenType.Comment, 1),
                new TokenPattern(@"//.*$", TokenType.Comment, 2),
                new TokenPattern(@"#.*$", TokenType.Comment, 3),
                new TokenPattern(@"/\*[\s\S]*?\*/", TokenType.Comment, 4),

                // Terminal strings (EBNF style: doubled quotes to escape quotes; backslash is literal)
                new TokenPattern(@"'(?:[^']|'')*'", TokenType.String, 5),
                new TokenPattern("\"(?:[^\"]|\"\")*\"", TokenType.String, 6),

                // Character ranges and sets [a-z], [0-9], etc.
                new TokenPattern(@"\[[^\]]*\]", TokenType.String, 7),

                // Production operators - BEFORE non-terminal patterns
                new TokenPattern(@"::=|:=|->|=>|=", TokenType.Operator, 8),

                // Non-terminal identifiers (rule names) - left side of production
                new TokenPattern(@"^\s*[A-Za-z_][A-Za-z0-9_-]*(?=\s*(?:::=|:=|=|->))", TokenType.Method, 9),

                // EBNF operators and meta-symbols - BEFORE non-terminal references
                new TokenPattern(@"\.\.\.|\.\.|\||&|\+|\*|\?|!", TokenType.Operator, 10),

                // Grouping and optional constructs
                new TokenPattern(@"[(){}\[\]]", TokenType.Punctuation, 11),

                // Repetition indicators {n}, {n,m}, {n,}
                new TokenPattern(@"\{\s*\d+(?:\s*,\s*\d*)?\s*\}", TokenType.Number, 12),

                // Special symbols and keywords
                new TokenPattern(@"\b(?:empty|epsilon|lambda|nil|null|EOF|EOL|START)\b", TokenType.Keyword, 13),

                // Semantic actions in { ... } (for parser generators) - but not repetition
                new TokenPattern(@"\{(?![0-9\s,}])[^}]*\}", TokenType.Comment, 14),

                // Non-terminal references (identifiers) - LOWER PRIORITY
                new TokenPattern(@"\b[A-Za-z_][A-Za-z0-9_-]*\b", TokenType.Type, 15),

                // Semicolon and comma separators
                new TokenPattern(@"[;,.]", TokenType.Punctuation, 16)
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
    /// HTML syntax highlighter
    /// </summary>
    public class HtmlHighlighter : RegexHighlighterBase
    {
        public override string Language
        {
            get { return "html"; }
        }

        public override string[] Aliases
        {
            get { return new string[] { "htm", "html5", "xhtml", "hta", "asp", "aspx", "jsp", "php", "erb", "ejs" }; }
        }

        protected override TokenPattern[] GetPatterns()
        {
            return new TokenPattern[]
            {
                // HTML comments
                new TokenPattern(@"<!--[\s\S]*?-->", TokenType.Comment, 1),

                // DOCTYPE declarations
                new TokenPattern(@"<!DOCTYPE[^>]*>", TokenType.Comment, 2),

                // CDATA sections
                new TokenPattern(@"<!\[CDATA\[[\s\S]*?\]\]>", TokenType.String, 3),

                // Script and style content (simplified - treat as strings to avoid conflicts)
                new TokenPattern(@"(?i)<script[^>]*>[\s\S]*?</script>", TokenType.String, 4),
                new TokenPattern(@"(?i)<style[^>]*>[\s\S]*?</style>", TokenType.String, 5),

                // String literals within attributes (single and double quotes)
                new TokenPattern(@"(?<=\s(?:src|href|class|id|name|value|alt|title|data-\w+|style|onclick|onload|onchange|onsubmit|action|method|type|placeholder|content|charset|rel|lang|role|aria-\w+)\s*=\s*)(?:'[^']*'|""[^""]*"")", TokenType.String, 6),

                // Tag names (opening and closing)
                new TokenPattern(@"(?i)</?(?:html|head|title|meta|link|script|style|body|header|nav|main|section|article|aside|footer|div|span|p|h[1-6]|a|img|ul|ol|li|table|thead|tbody|tfoot|tr|th|td|form|input|textarea|button|select|option|label|fieldset|legend|iframe|embed|object|param|video|audio|source|canvas|svg|figure|figcaption|details|summary|dialog|template|slot|br|hr|area|base|col|embed|input|link|meta|param|source|track|wbr)\b", TokenType.Keyword, 7),

                // Custom elements and unknown tags
                new TokenPattern(@"(?i)</?[a-zA-Z][a-zA-Z0-9-]*(?:\s|>|/>)", TokenType.Method, 8),

                // Attribute names
                new TokenPattern(@"(?i)\b(?:id|class|src|href|alt|title|width|height|style|type|name|value|placeholder|content|charset|rel|lang|role|data-[a-zA-Z0-9-]+|aria-[a-zA-Z0-9-]+|onclick|onload|onchange|onsubmit|onfocus|onblur|onmouseover|onmouseout|action|method|target|enctype|accept|autocomplete|autofocus|checked|disabled|hidden|readonly|required|selected|multiple|size|maxlength|min|max|step|pattern)\b(?=\s*=)", TokenType.Type, 9),

                // HTML entities
                new TokenPattern(@"&(?:[a-zA-Z][a-zA-Z0-9]*|#(?:\d+|[xX][0-9a-fA-F]+));", TokenType.Number, 10),

                // Tag delimiters and self-closing tags
                new TokenPattern(@"</?|/>|>", TokenType.Punctuation, 11),

                // Attribute assignment
                new TokenPattern(@"=", TokenType.Operator, 12)
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
    /// Perl syntax highlighter
    /// </summary>
    public class PerlHighlighter : RegexHighlighterBase
    {
        public override string Language
        {
            get { return "perl"; }
        }

        public override string[] Aliases
        {
            get { return new string[] { "pl", "pm", "perl5", "perl6", "raku" }; }
        }

        protected override TokenPattern[] GetPatterns()
        {
            return new TokenPattern[]
            {
                // Single-line comments
                new TokenPattern(@"#.*$", TokenType.Comment, 1),

                // POD documentation blocks
                new TokenPattern(@"^=\w+[\s\S]*?^=cut", TokenType.Comment, 2),

                // Here-documents (simplified pattern)
                new TokenPattern(@"<<['""]?(\w+)['""]?[\s\S]*?\n\1$", TokenType.String, 3),

                // String literals: single, double quotes, and q operators
                new TokenPattern(@"q[qwrx]?\s*([^\w\s])[^\\1]*?\1|'(?:[^'\\]|\\.)*'|""(?:[^""\\]|\\.)*""", TokenType.String, 4),

                // Regular expressions: m//, //, s///, tr///
                new TokenPattern(@"(?:m|qr|s|tr|y)\s*([^\w\s\(\[\{])[^\\1]*?\1(?:[^\\1]*?\1)?[gimosxep]*|/(?:[^/\\\n]|\\.)+/[gimosxep]*", TokenType.String, 5),

                // Numbers (integers, floats, hex, octal, binary)
                new TokenPattern(@"\b(?:0x[0-9a-fA-F]+|0b[01]+|0[0-7]+|\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)\b", TokenType.Number, 6),

                // Special variables ($_, $@, $!, $1, etc.)
                new TokenPattern(@"\$(?:[0-9]+|[_@!?$^&*()+=\[\]{};'"":<>.,|\\/-]|\w+)", TokenType.Type, 7),

                // Array and hash variables (@array, %hash)
                new TokenPattern(@"[@%][a-zA-Z_][a-zA-Z0-9_]*", TokenType.Type, 8),

                // Keywords
                new TokenPattern(@"\b(?:BEGIN|END|AUTOLOAD|DESTROY|abs|accept|alarm|and|atan2|bind|binmode|bless|break|caller|chdir|chmod|chomp|chop|chown|chr|chroot|close|closedir|cmp|connect|continue|cos|crypt|dbmclose|dbmopen|defined|delete|die|do|dump|each|else|elsif|endgrent|endhostent|endnetent|endprotoent|endpwent|endservent|eof|eq|eval|exec|exists|exit|exp|fcntl|fileno|flock|for|foreach|fork|format|formline|ge|getc|getgrent|getgrgid|getgrnam|gethostbyaddr|gethostbyname|gethostent|getlogin|getnetbyaddr|getnetbyname|getnetent|getpeername|getpgrp|getppid|getpriority|getprotobyname|getprotobynumber|getprotoent|getpwent|getpwnam|getpwuid|getservbyname|getservbyport|getservent|getsockname|getsockopt|given|glob|gmtime|goto|grep|gt|hex|if|import|index|int|ioctl|join|keys|kill|last|lc|lcfirst|le|length|link|listen|local|localtime|lock|log|lstat|lt|map|mkdir|msgctl|msgget|msgrcv|msgsnd|my|ne|next|no|not|oct|open|opendir|or|ord|our|pack|package|pipe|pop|pos|print|printf|prototype|push|quotemeta|rand|read|readdir|readline|readlink|readpipe|recv|redo|ref|rename|require|reset|return|reverse|rewinddir|rindex|rmdir|scalar|seek|seekdir|select|semctl|semget|semop|send|setgrent|sethostent|setnetent|setpgrp|setpriority|setprotoent|setpwent|setservent|setsockopt|shift|shmctl|shmget|shmread|shmwrite|shutdown|sin|sleep|socket|socketpair|sort|splice|split|sprintf|sqrt|srand|stat|study|sub|substr|symlink|syscall|sysopen|sysread|sysseek|system|syswrite|tell|telldir|tie|tied|time|times|tr|truncate|uc|ucfirst|umask|undef|unless|unlink|unpack|unshift|untie|until|use|utime|values|vec|wait|waitpid|wantarray|warn|when|while|write|xor|y)\b", TokenType.Keyword, 9),

                // Built-in functions and pragmas
                new TokenPattern(@"\b(?:strict|warnings|utf8|constant|lib|base|parent|Exporter|Carp|Data::Dumper|File::Spec|IO::Handle|Scalar::Util|List::Util)\b", TokenType.Type, 10),

                // Subroutine calls and definitions
                new TokenPattern(@"&?[a-zA-Z_][a-zA-Z0-9_]*(?=\s*\()|sub\s+[a-zA-Z_][a-zA-Z0-9_]*", TokenType.Method, 11),

                // Operators (including Perl-specific ones)
                new TokenPattern(@"[+\-*/%=!<>&|^~?:]+|<<|>>|<=>|=>|=~|!~|\+\+|--|&&|\|\||eq|ne|lt|le|gt|ge|cmp|and|or|not|xor|\.\.|\.\.\.", TokenType.Operator, 12),

                // Punctuation
                new TokenPattern(@"[{}()\[\];,.]", TokenType.Punctuation, 13)
            };
        }
    }

    /// <summary>
    /// PowerShell syntax highlighter
    /// </summary>
    public class PowerShellHighlighter : RegexHighlighterBase
    {
        public override string Language
        {
            get { return "powershell"; }
        }

        public override string[] Aliases
        {
            get { return new string[] { "ps1", "psm1", "psd1", "ps", "pwsh" }; }
        }

        protected override TokenPattern[] GetPatterns()
        {
            return new TokenPattern[]
            {
                // Comments: # ... and <# ... #>
                new TokenPattern(@"<#[\s\S]*?#>", TokenType.Comment, 1),
                new TokenPattern(@"#.*$", TokenType.Comment, 2),

                // Here-strings: @"..."@, @'...'@
                new TokenPattern(@"@['""][\s\S]*?['""]@", TokenType.String, 3),

                // String literals: "...", '...', expandable strings
                new TokenPattern(@"""(?:[^""\\`]|`.|\\[\\""'`bnrt0afv])*""", TokenType.String, 4),
                new TokenPattern(@"'(?:[^'\\]|\\.)*'", TokenType.String, 5),

                // Variables: $var, ${var}, $global:var, $script:var, etc.
                new TokenPattern(@"\$(?:(?:global|local|script|private|using):)?(?:\{[^}]+\}|[a-zA-Z_][a-zA-Z0-9_]*|\?|\$|\^|_)", TokenType.Type, 6),

                // Automatic variables
                new TokenPattern(@"\$(?:\$|0|1|2|3|4|5|6|7|8|9|\?|\^|_|args|input|lastexitcode|matches|myinvocation|pid|profile|pshome|pwd|shellid|host|home|error|executioncontext|false|true|null|foreach|psitem|this|pscmdlet|psversiontable)\b", TokenType.Type, 7),

                // Numbers: integers, floats, hex, scientific notation with optional suffixes
                new TokenPattern(@"\b(?:0[xX][0-9a-fA-F]+[lL]?|(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?[dDfFlL]?)\b", TokenType.Number, 8),

                // Keywords
                new TokenPattern(@"(?i)\b(?:begin|break|catch|class|continue|data|define|do|dynamicparam|else|elseif|end|enum|exit|filter|finally|for|foreach|from|function|hidden|if|in|param|process|return|static|switch|throw|trap|try|until|using|var|while|workflow|parallel|sequence|inlinescript)\b", TokenType.Keyword, 9),

                // Operators (PowerShell-specific)
                new TokenPattern(@"(?i)-(?:eq|ne|lt|le|gt|ge|like|notlike|match|notmatch|contains|notcontains|in|notin|replace|split|join|is|isnot|as|band|bor|bxor|bnot|shl|shr|and|or|xor|not|f)\b", TokenType.Operator, 10),

                // Type accelerators and .NET types
                new TokenPattern(@"\[(?:[a-zA-Z_][a-zA-Z0-9_.]*(?:\[\])?)\]", TokenType.Type, 11),

                // Cmdlets (Verb-Noun pattern)
                new TokenPattern(@"\b(?:Add|Clear|Close|Copy|Enter|Exit|Find|Format|Get|Hide|Join|Lock|Move|New|Open|Optimize|Pop|Push|Redo|Remove|Rename|Reset|Resize|Search|Select|Set|Show|Skip|Split|Step|Switch|Undo|Unlock|Watch|Backup|Checkpoint|Compare|Compress|Convert|ConvertFrom|ConvertTo|Dismount|Edit|Expand|Export|Group|Import|Initialize|Limit|Merge|Mount|Out|Publish|Restore|Save|Sync|Unpublish|Update|Debug|Measure|Ping|Repair|Resolve|Test|Trace|Connect|Disconnect|Read|Receive|Send|Write|Block|Grant|Protect|Revoke|Unblock|Unprotect|Disable|Enable|Install|Register|Request|Restart|Resume|Start|Stop|Submit|Suspend|Uninstall|Unregister|Wait|Invoke|Approve|Assert|Complete|Confirm|Deny|Suspend|Use|New|Set|Get|Remove|Add|Clear|Copy|Move|Rename|Test|Start|Stop|Restart|Suspend|Resume|Wait|Invoke|Import|Export|Convert|Format|Out|Write|Read|Measure|Compare|Sort|Group|Select|Where|ForEach)-[a-zA-Z][a-zA-Z0-9]*\b", TokenType.Method, 12),

                // Function calls and method invocations
                new TokenPattern(@"\b[a-zA-Z_][a-zA-Z0-9_-]*(?=\s*\()", TokenType.Method, 13),

                // Parameters: -ParameterName
                new TokenPattern(@"-[a-zA-Z_][a-zA-Z0-9_]*\b", TokenType.Type, 14),

                // Splatting: @variableName
                new TokenPattern(@"@[a-zA-Z_][a-zA-Z0-9_]*\b", TokenType.Type, 15),

                // Subexpression operators: $(...), @(...)
                new TokenPattern(@"[\$@](?=\()", TokenType.Operator, 16),

                // Member access and dot sourcing
                new TokenPattern(@"\.|::", TokenType.Operator, 17),

                // Mathematical and assignment operators
                new TokenPattern(@"[+\-*/%=!<>&|^~]+|\+\+|--|&&|\|\||==|!=|<=|>=|\+=|-=|\*=|/=|%=", TokenType.Operator, 18),

                // Pipeline operators
                new TokenPattern(@"\|(?!\||&)", TokenType.Operator, 19),

                // Ampersand (call operator)
                new TokenPattern(@"&", TokenType.Operator, 20),

                // Semicolon (statement separator)
                new TokenPattern(@";", TokenType.Punctuation, 21),

                // Parentheses, brackets, braces
                new TokenPattern(@"[{}\[\]()]", TokenType.Punctuation, 22),

                // Comma
                new TokenPattern(@",", TokenType.Punctuation, 23),

                // Backtick (escape character and line continuation)
                new TokenPattern(@"`.", TokenType.String, 24)
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
    /// Regular Expression (Regex) syntax highlighter
    /// </summary>
    public class RegexHighlighter : RegexHighlighterBase
    {
        public override string Language
        {
            get { return "regex"; }
        }

        public override string[] Aliases
        {
            get { return new string[] { "regexp", "re", "pcre", "posix" }; }
        }

        protected override TokenPattern[] GetPatterns()
        {
            return new TokenPattern[]
            {
                // Comments (in extended mode: (?x) or (?#comment))
                new TokenPattern(@"\(\?\#[^)]*\)", TokenType.Comment, 1),

                // Inline modifiers (?imsx-imsx), lookaheads, lookbehinds
                new TokenPattern(@"\(\?(?:[imsx+-]*:|\#[^)]*|<?[!=])", TokenType.Keyword, 2),

                // Named groups (?<name>...) and (?P<name>...)
                new TokenPattern(@"\(\?<?P?<[^>]+>", TokenType.Method, 3),

                // Character classes and ranges [a-z], [^abc], [:alpha:]
                new TokenPattern(@"\[(?:\^?[^\]\\]|\\[^\]])*\]", TokenType.Type, 4),

                // Predefined character classes \d, \w, \s, \D, \W, \S
                new TokenPattern(@"\\[dwsWDS]", TokenType.Type, 5),

                // Unicode categories and properties \p{L}, \P{Nd}
                new TokenPattern(@"\\[pP]\{[^}]+\}", TokenType.Type, 6),

                // Escape sequences \n, \t, \r, \x20, \u0020, \U00000020
                new TokenPattern(@"\\(?:[nrtfvabe]|x[0-9a-fA-F]{2}|u[0-9a-fA-F]{4}|U[0-9a-fA-F]{8}|c[A-Z]|0[0-7]{2})", TokenType.String, 7),

                // Backreferences \1, \2, \k<name>, \g<name>
                new TokenPattern(@"\\(?:\d+|k<[^>]+>|g<[^>]+>)", TokenType.Number, 8),

                // Anchors ^, $, \A, \Z, \z, \b, \B
                new TokenPattern(@"[\^$]|\\[AZzBb]", TokenType.Operator, 9),

                // Quantifiers *, +, ?, {n}, {n,m}, {n,} (with optional ? for non-greedy)
                new TokenPattern(@"[*+?]\??|\{(?:\d+(?:,\d*)?|,\d+)\}\??", TokenType.Operator, 10),

                // Groups () and non-capturing groups (?:...)
                new TokenPattern(@"\(\?\:", TokenType.Keyword, 11),
                new TokenPattern(@"[()]", TokenType.Punctuation, 12),

                // Alternation |
                new TokenPattern(@"\|", TokenType.Operator, 13),

                // Dot (any character except newline)
                new TokenPattern(@"\.", TokenType.Operator, 14),

                // Escaped special characters \*, \+, \?, etc.
                new TokenPattern(@"\\[.*+?^${}()|[\]\\]", TokenType.String, 15),

                // Regular characters and literals
                new TokenPattern(@"[^\\.*+?^${}()|[\]]+", TokenType.Normal, 16)
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
    /// Visual Basic (VB6/VBA/VB.NET) syntax highlighter
    /// </summary>
    public class VisualBasicHighlighter : RegexHighlighterBase
    {
        public override string Language
        {
            get { return "visualbasic"; }
        }

        public override string[] Aliases
        {
            get { return new string[] { "vb", "vba", "vb6", "vbnet", "vb.net", "visualbasic", "visual-basic" }; }
        }

        protected override TokenPattern[] GetPatterns()
        {
            return new TokenPattern[]
            {
                // String literals first to avoid "'" inside strings being treated as comments
                new TokenPattern(@"""(?:[^""]|"""")*""", TokenType.String, 1),

                // Date literals (#...#)
                new TokenPattern(@"#(?:[^#\r\n])*#", TokenType.String, 2),

                // Comments: ' ... and REM ...
                new TokenPattern(@"(?i)'.*$", TokenType.Comment, 3),
                new TokenPattern(@"(?i)\bREM\b.*$", TokenType.Comment, 4),

                // Preprocessor directives (#Region, #If, etc.)
                new TokenPattern(@"(?im)^\s*#\s*(?:Region|End\s+Region|If|ElseIf|Else|End\s+If|Const|ExternalSource|End\s+ExternalSource)\b.*$", TokenType.Comment, 5),

                // Attributes: <Attr(...)>
                new TokenPattern(@"(?i)<\s*[A-Za-z_]\w*(?:\s*\([^<>]*\))?\s*>", TokenType.Type, 6),

                // Built-in types (colored as Type)
                new TokenPattern(@"(?i)\b(?:Boolean|Byte|SByte|Short|UShort|Integer|UInteger|Long|ULong|Decimal|Single|Double|Date|Char|String|Object)\b", TokenType.Type, 7),

                // Numbers: hex/oct/bin and decimals with optional exponent and VB/VB.NET suffixes
                new TokenPattern(@"(?i)\b(?:&H[0-9A-F_]+|&O[0-7_]+|&B[01_]+|(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?)(?:[RDFSIL]|UI|UL|US)?\b", TokenType.Number, 8),

                // Variables with legacy type suffixes ($ % & ! # @)
                new TokenPattern(@"(?i)\b[A-Za-z_]\w*[\$\%\&\!\#@]\b", TokenType.Type, 9),

                // Keywords
                new TokenPattern(@"(?i)\b(?:AddHandler|AddressOf|Alias|And|AndAlso|As|ByRef|ByVal|Call|Case|Catch|CBool|CByte|CChar|CDate|CDbl|CDec|CInt|Class|CLng|CObj|Const|Continue|CSByte|CShort|CSng|CStr|CType|CUInt|CULng|CUShort|Declare|Default|Delegate|Dim|DirectCast|Do|Each|Else|ElseIf|End|Enum|Erase|Error|Event|Exit|False|Finally|For|Friend|Function|Get|GetType|Global|GoTo|Handles|If|Implements|Imports|In|Inherits|Interface|Is|IsNot|Let|Lib|Like|Loop|Me|Mod|Module|MustInherit|MustOverride|MyBase|MyClass|Namespace|Narrowing|New|Next|Not|Nothing|NotInheritable|NotOverridable|Of|On|Operator|Option|Optional|Or|OrElse|Overloads|Overridable|Overrides|ParamArray|Partial|Private|Property|Protected|Public|RaiseEvent|ReadOnly|ReDim|RemoveHandler|Return|Select|Set|Shadows|Shared|Short|Single|Static|Step|Stop|String|Structure|Sub|SyncLock|Then|Throw|To|True|Try|TryCast|TypeOf|Using|Variant|Wend|When|While|Widening|With|WithEvents|WriteOnly|Xor)\b", TokenType.Keyword, 10),

                // Method/function calls (identifier followed by '(')
                new TokenPattern(@"(?i)\b[A-Za-z_]\w*(?=\s*\()", TokenType.Method, 11),

                // Word operators (logic/relational)
                new TokenPattern(@"(?i)\b(?:And|Or|Not|Xor|Mod|Like|Is|IsNot|AndAlso|OrElse)\b", TokenType.Operator, 12),

                // Symbol operators (math, concat, compare, shifts, integer division)
                new TokenPattern(@"<=|>=|<>|<<|>>|[+\-*/^\\=&]", TokenType.Operator, 13),

                // Punctuation
                new TokenPattern(@"[{}\[\]().,:;]", TokenType.Punctuation, 14),
            };
        }
    }

    /// <summary>
    /// XML syntax highlighter
    /// </summary>
    public class XmlHighlighter : RegexHighlighterBase
    {
        public override string Language
        {
            get { return "xml"; }
        }

        public override string[] Aliases
        {
            get
            {
                return new string[] { 
                "xaml", "xsl", "xslt", "xsd", "xhtml", "svg", "rss", "atom", 
                "plist", "config", "web.config", "app.config", "machine.config",
                "csproj", "vbproj", "fsproj", "vcxproj", "proj", "targets", "props",
                "resx", "settings", "manifest", "nuspec", "packages.config",
                "wsdl", "disco", "asmx", "sitemap", "master", "ascx",
                "kml", "gpx", "tei", "docbook", "fo", "ant", "maven", "pom"
            };
            }
        }

        protected override TokenPattern[] GetPatterns()
        {
            return new TokenPattern[]
            {
                // XML comments
                new TokenPattern(@"<!--[\s\S]*?-->", TokenType.Comment, 1),

                // XML declarations and processing instructions
                new TokenPattern(@"<\?[\s\S]*?\?>", TokenType.Comment, 2),

                // DOCTYPE declarations
                new TokenPattern(@"<!DOCTYPE[\s\S]*?>", TokenType.Comment, 3),

                // CDATA sections
                new TokenPattern(@"<!\[CDATA\[[\s\S]*?\]\]>", TokenType.String, 4),

                // Attribute values (single and double quotes)
                new TokenPattern(@"(?<=\s[a-zA-Z:_][a-zA-Z0-9:._-]*\s*=\s*)(?:'[^']*'|""[^""]*"")", TokenType.String, 5),

                // Namespace prefixes and element names
                new TokenPattern(@"(?i)</?(?:[a-zA-Z_][a-zA-Z0-9._-]*:)?[a-zA-Z_][a-zA-Z0-9._-]*", TokenType.Keyword, 6),

                // Attribute names (including namespaced attributes)
                new TokenPattern(@"(?i)\b(?:[a-zA-Z_][a-zA-Z0-9._-]*:)?[a-zA-Z_][a-zA-Z0-9._-]*(?=\s*=)", TokenType.Type, 7),

                // Microsoft-specific elements and attributes (highlight differently)
                new TokenPattern(@"(?i)\b(?:assembly|configuration|system\.web|appSettings|connectionStrings|compilation|authentication|authorization|customErrors|httpModules|httpHandlers|sessionState|globalization|trace|debug|machineKey|trust|webServices|bindings|endpoint|service|contract|behavior|extensions|standardEndpoints|protocolMapping|serviceHostingEnvironment)\b", TokenType.Method, 8),

                // Common XML Schema and namespace URIs
                new TokenPattern(@"(?i)(?:xmlns|xsi:|xsd:|xml:)", TokenType.Method, 9),

                // XML entities
                new TokenPattern(@"&(?:[a-zA-Z][a-zA-Z0-9]*|#(?:\d+|[xX][0-9a-fA-F]+));", TokenType.Number, 10),

                // Tag delimiters
                new TokenPattern(@"</?|/>|>", TokenType.Punctuation, 11),

                // Attribute assignment
                new TokenPattern(@"=", TokenType.Operator, 12)
            };
        }
    }

    /// <summary>
    /// YAML syntax highlighter
    /// </summary>
    public class YamlHighlighter : RegexHighlighterBase
    {
        public override string Language
        {
            get { return "yaml"; }
        }

        public override string[] Aliases
        {
            get { return new string[] { "yml", "yaml", "docker-compose" }; }
        }

        protected override TokenPattern[] GetPatterns()
        {
            return new TokenPattern[]
            {
                // Comments (# comments)
                new TokenPattern(@"#.*$", TokenType.Comment, 1),

                // Document separators (--- and ...)
                new TokenPattern(@"^(?:---|\.\.\.)\s*$", TokenType.Operator, 2),

                // Multi-line strings (literal | and folded >)
                new TokenPattern(@"(?:^|\s)[\|>][-+]?\s*$", TokenType.Operator, 3),

                // Double-quoted strings with escape sequences
                new TokenPattern(@"""(?:[^""\\]|\\.)*""", TokenType.String, 4),

                // Single-quoted strings
                new TokenPattern(@"'(?:[^'\\]|\\.)*'", TokenType.String, 5),

                // Unquoted strings that look like values (after : or - )
                new TokenPattern(@"(?<=:\s)(?![>\|])[^#\r\n]+(?=\s*(?:#|$))|(?<=^-\s)(?![>\|])[^#\r\n]+(?=\s*(?:#|$))", TokenType.String, 6),

                // Numbers (integers, floats, scientific notation, hex, octal, binary)
                new TokenPattern(@"\b(?:[-+]?(?:0x[0-9a-fA-F]+|0o[0-7]+|0b[01]+|\d+(?:\.\d+)?(?:[eE][-+]?\d+)?)|\.inf|\.Inf|\.INF|\.nan|\.NaN|\.NAN)\b", TokenType.Number, 7),

                // Boolean values
                new TokenPattern(@"\b(?:true|True|TRUE|false|False|FALSE|yes|Yes|YES|no|No|NO|on|On|ON|off|Off|OFF)\b", TokenType.Keyword, 8),

                // Null values
                new TokenPattern(@"\b(?:null|Null|NULL|~)\b", TokenType.Keyword, 9),

                // YAML tags (!Tag)
                new TokenPattern(@"![a-zA-Z_][a-zA-Z0-9_-]*(?:/[a-zA-Z_][a-zA-Z0-9_-]*)*", TokenType.Type, 10),

                // Anchors and aliases (&anchor, *alias)
                new TokenPattern(@"[&*][a-zA-Z_][a-zA-Z0-9_-]*", TokenType.Type, 11),

                // Keys (words followed by colon, including quoted keys)
                new TokenPattern(@"(?:^|\s)(?:""(?:[^""\\]|\\.)*""|'(?:[^'\\]|\\.)*'|[^:\s#][^:#]*?)(?=\s*:(?:\s|$))", TokenType.Method, 12),

                // Flow indicators and operators
                new TokenPattern(@"[{}\[\],]|[-:?](?=\s)", TokenType.Punctuation, 13),

                // YAML merge key (<<)
                new TokenPattern(@"<<", TokenType.Operator, 14)
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
