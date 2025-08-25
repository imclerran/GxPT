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
        // Shared, process-wide cache of compiled Regex objects to avoid re-compilation per instance
        // .NET 3.5 safe: guarded by a simple lock; key combines options and pattern
        private static readonly object s_regexCacheLock = new object();
        private static readonly Dictionary<string, Regex> s_regexCache = new Dictionary<string, Regex>(StringComparer.Ordinal);

        // Shared, process-wide cache of TokenPattern arrays per highlighter Type.
        // Many instances of the same highlighter class share the same (sorted) patterns.
        private static readonly object s_patternCacheLock = new object();
        private static readonly Dictionary<Type, TokenPattern[]> s_patternCache = new Dictionary<Type, TokenPattern[]>();

        // Default regex options used across patterns (most patterns rely on Multiline for ^/$ anchors)
        private const RegexOptions DefaultRegexOptions = RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.CultureInvariant;

        // Returns a shared compiled Regex for a given pattern/options.
        protected static Regex GetSharedRegex(string pattern, RegexOptions options)
        {
            string key = ((int)options).ToString() + "\n" + pattern;
            Regex rx;
            lock (s_regexCacheLock)
            {
                if (!s_regexCache.TryGetValue(key, out rx))
                {
                    rx = new Regex(pattern, options);
                    s_regexCache[key] = rx;
                }
            }
            return rx;
        }

        protected struct TokenPattern
        {
            public Regex Regex;
            public TokenType Type;
            public int Priority; // Lower number = higher priority

            public TokenPattern(string pattern, TokenType type, int priority)
            {
                // Obtain a shared compiled regex to avoid per-instance compilation costs
                Regex = GetSharedRegex(pattern, DefaultRegexOptions);
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
            // Reuse a single, sorted TokenPattern[] per concrete highlighter type
            var t = this.GetType();
            TokenPattern[] cached;
            lock (s_patternCacheLock)
            {
                if (!s_patternCache.TryGetValue(t, out cached))
                {
                    cached = GetPatterns();
                    // Sort patterns by priority (lower number = higher priority)
                    Array.Sort(cached, delegate(TokenPattern a, TokenPattern b) { return a.Priority.CompareTo(b.Priority); });
                    s_patternCache[t] = cached;
                }
            }
            _patterns = cached;
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
    /// Assembly language syntax highlighter - supports x86/x64, ARM/AArch64, and RISC-V
    /// </summary>
    public class AssemblyHighlighter : RegexHighlighterBase
    {
        public static readonly string[] FileTypes = new string[] { "*.asm", "*.s", "*.S", "*.nasm" };
        public override string Language
        {
            get { return "assembly"; }
        }

        public override string[] Aliases
        {
            get
            {
                return new string[]
            {
                "asm", "s", "x86", "x64", "arm", "aarch64", "arm64",
                "nasm", "masm", "gas", "att", "intel",
                "riscv", "risc-v", "rv32", "rv64"
            };
            }
        }

        protected override TokenPattern[] GetPatterns()
        {
            return new TokenPattern[]
            {
                // Comments (semicolon, hash, double slash, C-style)
                new TokenPattern(@";.*$", TokenType.Comment, 1),
                new TokenPattern(@"#.*$", TokenType.Comment, 2),
                new TokenPattern(@"//.*$", TokenType.Comment, 3),
                new TokenPattern(@"/\*[\s\S]*?\*/", TokenType.Comment, 4),

                // String literals (single and double quotes)
                new TokenPattern(@"'(?:[^'\\]|\\.)*'|""(?:[^""\\]|\\.)*""", TokenType.String, 5),

                // Character literals and escape sequences
                new TokenPattern(@"'\\[nrtbfav\\']'|'\\[0-7]{1,3}'|'\\x[0-9a-fA-F]{1,2}'", TokenType.String, 6),

                // Numbers (hex, binary, octal, decimal with various prefixes/suffixes)
                new TokenPattern(@"\b(?:0[xX][0-9a-fA-F]+[hH]?|[0-9a-fA-F]+[hH]|0[bB][01]+[bB]?|[01]+[bB]|0[oO]?[0-7]+[oO]?|\d+[dD]?)\b", TokenType.Number, 7),

                // Assembler directives (NASM/MASM/GAS + some RISC-V-specific)
                new TokenPattern(@"(?i)\.?(?:section|segment|data|text|bss|rodata|code|const|global|extern|public|extrn|include|incbin|equ|define|macro|endm|proc|endp|struct|ends|align|org|resb|resw|resd|resq|db|dw|dd|dq|dt|times|byte|word|dword|qword|tbyte|ascii|asciz|string|zero|space|comm|lcomm|size|type|weak|hidden|protected|default|rel|abs|entry|end|option|attribute|insn)\b", TokenType.Keyword, 8),

                // x86/x64 registers
                new TokenPattern(@"(?i)\b(?:eax|ebx|ecx|edx|esi|edi|esp|ebp|ax|bx|cx|dx|si|di|sp|bp|al|ah|bl|bh|cl|ch|dl|dh|rax|rbx|rcx|rdx|rsi|rdi|rsp|rbp|r8|r9|r10|r11|r12|r13|r14|r15|r8d|r9d|r10d|r11d|r12d|r13d|r14d|r15d|r8w|r9w|r10w|r11w|r12w|r13w|r14w|r15w|r8b|r9b|r10b|r11b|r12b|r13b|r14b|r15b|sil|dil|bpl|spl|cs|ds|es|fs|gs|ss|cr0|cr1|cr2|cr3|cr4|cr8|dr0|dr1|dr2|dr3|dr6|dr7|eflags|rflags|eip|rip|st0|st1|st2|st3|st4|st5|st6|st7|mm0|mm1|mm2|mm3|mm4|mm5|mm6|mm7|xmm0|xmm1|xmm2|xmm3|xmm4|xmm5|xmm6|xmm7|xmm8|xmm9|xmm10|xmm11|xmm12|xmm13|xmm14|xmm15|ymm0|ymm1|ymm2|ymm3|ymm4|ymm5|ymm6|ymm7|ymm8|ymm9|ymm10|ymm11|ymm12|ymm13|ymm14|ymm15|zmm0|zmm1|zmm2|zmm3|zmm4|zmm5|zmm6|zmm7|zmm8|zmm9|zmm10|zmm11|zmm12|zmm13|zmm14|zmm15)\b", TokenType.Type, 9),

                // ARM/AArch64 registers
                new TokenPattern(@"(?i)\b(?:r0|r1|r2|r3|r4|r5|r6|r7|r8|r9|r10|r11|r12|r13|r14|r15|sp|lr|pc|cpsr|spsr|w0|w1|w2|w3|w4|w5|w6|w7|w8|w9|w10|w11|w12|w13|w14|w15|w16|w17|w18|w19|w20|w21|w22|w23|w24|w25|w26|w27|w28|w29|w30|wzr|wsp|x0|x1|x2|x3|x4|x5|x6|x7|x8|x9|x10|x11|x12|x13|x14|x15|x16|x17|x18|x19|x20|x21|x22|x23|x24|x25|x26|x27|x28|x29|x30|xzr|s0|s1|s2|s3|s4|s5|s6|s7|s8|s9|s10|s11|s12|s13|s14|s15|d0|d1|d2|d3|d4|d5|d6|d7|d8|d9|d10|d11|d12|d13|d14|d15|q0|q1|q2|q3|q4|q5|q6|q7|v0|v1|v2|v3|v4|v5|v6|v7|v8|v9|v10|v11|v12|v13|v14|v15|v16|v17|v18|v19|v20|v21|v22|v23|v24|v25|v26|v27|v28|v29|v30|v31)\b", TokenType.Type, 10),

                // RISC-V integer/FPU/vector registers (x0–x31 and ABI names; f0–f31; v0–v31)
                new TokenPattern(@"(?i)\b(?:
                    x(?:[12]?\d|3[01]|[0-9])|
                    zero|ra|sp|gp|tp|
                    t(?:0|1|2|3|4|5|6)|
                    s(?:0|1|2|3|4|5|6|7|8|9|10|11)|
                    a(?:0|1|2|3|4|5|6|7)|
                    f(?:[12]?\d|3[01])|
                    ft(?:0|1|2|3|4|5|6|7|8|9|10|11)|fs(?:0|1)|fa(?:0|1|2|3|4|5|6|7)|
                    v(?:[12]?\d|3[01])|vl|vtype|vlenb
                )\b", TokenType.Type, 21),

                // Common RISC-V CSRs
                new TokenPattern(@"(?i)\b(?:mstatus|misa|mie|mip|mtvec|mscratch|mepc|mcause|mtval|mhartid|mvendorid|marchid|mimpid|satp|sstatus|sie|sip|stvec|sscratch|sepc|scause|stval|cycleh?|timeh?|instreth?|fflags|frm|fcsr|ustatus|uie|uip|utvec|uscratch|uepc|ucause|utval)\b", TokenType.Type, 22),

                // x86/x64 instruction mnemonics
                new TokenPattern(@"(?i)\b(?:mov|movb|movw|movl|movq|movsx|movzx|movsxd|lea|push|pop|xchg|cmpxchg|add|adc|sub|sbb|mul|imul|div|idiv|inc|dec|neg|cmp|test|and|or|xor|not|shl|shr|sal|sar|rol|ror|rcl|rcr|bt|bts|btr|btc|bsf|bsr|jmp|je|jz|jne|jnz|jl|jnge|jle|jng|jg|jnle|jge|jnl|ja|jnbe|jae|jnb|jb|jnae|jbe|jna|js|jns|jp|jpe|jnp|jpo|jc|jnc|jo|jno|call|ret|retn|retf|enter|leave|int|into|iret|iretd|iretq|hlt|nop|wait|lock|rep|repe|repz|repne|repnz|cld|std|cli|sti|clc|stc|cmc|lahf|sahf|pushf|popf|pushfd|popfd|pushfq|popfq|cpuid|rdtsc|rdtscp|prefetch|clflush|mfence|lfence|sfence|pause|ud2|syscall|sysenter|sysexit|sysret)\b", TokenType.Method, 11),

                // ARM/AArch64 instruction mnemonics
                new TokenPattern(@"(?i)\b(?:add|adds|adc|adcs|sub|subs|sbc|sbcs|rsb|rsbs|rsc|rscs|mul|mla|umull|umlal|smull|smlal|mov|movs|mvn|mvns|cmp|cmn|tst|teq|and|ands|eor|eors|orr|orrs|bic|bics|lsl|lsr|asr|ror|rrx|ldr|ldrb|ldrh|ldrsb|ldrsh|str|strb|strh|ldm|ldmia|ldmib|ldmda|ldmdb|stm|stmia|stmib|stmda|stmdb|push|pop|b|bl|bx|blx|swi|svc|mrs|msr|nop|wfi|wfe|sev|yield|dmb|dsb|isb|ldrex|strex|clrex|adr|adrl|ldp|stp|cbz|cbnz|tbz|tbnz|adrp|br|blr|ret|eret|smc|hvc|hint)\b", TokenType.Method, 12),

                // RISC-V instruction mnemonics (RV32I/RV64I, Zicsr, A, F/D, Zifencei, basic V)
                new TokenPattern(@"(?i)\b(?:
                    lui|auipc|jal|jalr|
                    beq|bne|blt|bge|bltu|bgeu|
                    lb|lh|lw|lbu|lhu|sb|sh|sw|
                    lwu|ld|sd|addi|slti|sltiu|xori|ori|andi|
                    sll|srl|sra|slli|srli|srai|add|sub|slt|sltu|xor|or|and|
                    fence|fence\.i|ecall|ebreak|
                    csrrw|csrrs|csrrc|csrrwi|csrrsi|csrrci|
                    lr\.w|sc\.w|amoswap\.w|amoadd\.w|amoxor\.w|amoand\.w|amoor\.w|amomin\.w|amomax\.w|amominu\.w|amomaxu\.w|
                    lr\.d|sc\.d|amoswap\.d|amoadd\.d|amoxor\.d|amoand\.d|amoor\.d|amomin\.d|amomax\.d|amominu\.d|amomaxu\.d|
                    flw|fsw|fld|fsd|fence\.tso|
                    fadd\.s|fsub\.s|fmul\.s|fdiv\.s|fsqrt\.s|fmin\.s|fmax\.s|fmadd\.s|fmsub\.s|fnmsub\.s|fnmadd\.s|
                    fadd\.d|fsub\.d|fmul\.d|fdiv\.d|fsqrt\.d|fmin\.d|fmax\.d|fmadd\.d|fmsub\.d|fnmsub\.d|fnmadd\.d|
                    fcvt\.[sd]\.[sd]|fcvt\.w[ud]\.[sd]|fcvt\.[sd]\.w[ud]|fsgnj\.[sd]|fsgnjn\.[sd]|fsgnjx\.[sd]|
                    feq\.[sd]|flt\.[sd]|fle\.[sd]|fclass\.[sd]|fmv\.x\.w|fmv\.w\.x|fmv\.x\.d|fmv\.d\.x|
                    vsetvli|vsetvl|vadd\.vv|vadd\.vx|vmul\.vv|vmul\.vx|vle\d+\.[vb]|vse\d+\.[vb]
                )\b", TokenType.Method, 23),

                // Condition codes (ARM and x86)
                new TokenPattern(@"(?i)\b(?:eq|ne|cs|hs|cc|lo|mi|pl|vs|vc|hi|ls|ge|lt|gt|le|al|nv)\b", TokenType.Keyword, 13),

                // Size/type keywords
                new TokenPattern(@"(?i)\b(?:byte|word|dword|qword|tbyte|ptr|offset|near|far|short|high|low|seg|length|size|type|this)\b", TokenType.Keyword, 14),

                // Labels (identifier followed by ':')
                new TokenPattern(@"^\s*[a-zA-Z_][a-zA-Z0-9_]*:", TokenType.Method, 15),

                // Memory addressing - x86 style [base+index*scale+disp]
                new TokenPattern(@"\[(?:[^[\]]+)\]", TokenType.String, 16),

                // Memory addressing - RISC-V style offset(base), e.g., 0(sp) or 0x10(a0)
                new TokenPattern(@"\b[-+]?(?:\d+|0[xX][0-9a-fA-F]+)\s*\([^)]+\)", TokenType.String, 24),

                // Immediate values (# for ARM, $ for AT&T)
                new TokenPattern(@"[#$][-+]?(?:0[xX][0-9a-fA-F]+|\d+)", TokenType.Number, 17),

                // Register lists (ARM style {r0-r3,...})
                new TokenPattern(@"\{[^}]+\}", TokenType.Type, 18),

                // Operators and arithmetic
                new TokenPattern(@"[+\-*/%&|^~<>!=]+|<<|>>", TokenType.Operator, 19),

                // Punctuation and delimiters
                new TokenPattern(@"[,():\[\]]", TokenType.Punctuation, 20),
            };
        }
    }

    /// <summary>
    /// Bash/Shell syntax highlighter
    /// </summary>
    public class BashHighlighter : RegexHighlighterBase
    {
        public static readonly string[] FileTypes = new string[] { "*.sh", "*.bash", "*.zsh", "*.ksh", "*.fish", "*.csh", "*.tcsh" };
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

                // Strings: double, single, and backtick; optional $-prefixed
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
        public static readonly string[] FileTypes = new string[] { "*.bas", "*.basic" };
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
        public static readonly string[] FileTypes = new string[] { "*.bat", "*.cmd" };
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
        public static readonly string[] FileTypes = new string[] { "*.c", "*.h" };
        public override string Language
        {
            get { return "c"; }
        }

        public override string[] Aliases
        {
            get { return new string[] { "ansi-c", "c99", "c11", "c17", "h" }; }
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
    /// Clojure/EDN syntax highlighter
    /// </summary>
    public class ClojureHighlighter : RegexHighlighterBase
    {
        public static readonly string[] FileTypes = new string[] { "*.clj", "*.cljs", "*.cljc", "*.edn" };
        public override string Language
        {
            get { return "clojure"; }
        }

        public override string[] Aliases
        {
            get { return new string[] { "clj", "cljs", "cljc", "edn", "clojure" }; }
        }

        protected override TokenPattern[] GetPatterns()
        {
            return new TokenPattern[]
            {
                // Line comments
                new TokenPattern(@";.*$", TokenType.Comment, 1),

                // (comment ...) forms (heuristic, not balanced)
                new TokenPattern(@"\(\s*comment\b[\s\S]*?\)", TokenType.Comment, 2),

                // Regex literals: #"..."
                new TokenPattern(@"#""(?:[^""\\]|\\.)*""", TokenType.String, 3),

                // Strings
                new TokenPattern(@"""(?:[^""\\]|\\.)*""", TokenType.String, 4),

                // Character literals: \c, \newline, \uXXXX, \oNNN
                new TokenPattern(@"\\(?:u[0-9A-Fa-f]{4}|o[0-7]{1,3}|newline|space|tab|backspace|formfeed|return|quote|.)", TokenType.String, 5),

                // Numbers: hex, ratios, decimals, base-N (e.g., 2r1010), with optional exponent and N/M suffixes
                new TokenPattern(@"\b[+-]?(?:\d+r[0-9A-Za-z]+|0[xX][0-9A-Fa-f]+|\d+/\d+|(?:\d+(?:_\d+)*\.(?:\d+(?:_\d+)*)?|\d+(?:_\d+)*|\.(?:\d+(?:_\d+)*))(?:[eE][+-]?\d+)?[MN]?)\b", TokenType.Number, 6),

                // Booleans and nil
                new TokenPattern(@"\b(?:true|false|nil)\b", TokenType.Keyword, 7),

                // Clojure keywords (:kw, ::kw, :ns/kw)
                new TokenPattern(@"::{0,1}[A-Za-z0-9_*!+<>=?/\.\-]+(?:/[A-Za-z0-9_*!+<>=?/\.\-]+)?", TokenType.Type, 8),

                // Tagged literals (EDN): #inst, #uuid, #myapp/type
                new TokenPattern(@"#[A-Za-z_][A-Za-z0-9_.-]*/?[A-Za-z0-9_.-]*", TokenType.Type, 9),

                // Special forms and common macros/functions
                new TokenPattern(@"\b(?:def|defn|defn-|defmacro|defmulti|defmethod|defonce|defprotocol|extend|extend-type|extend-protocol|reify|proxy|gen-class|ns|in-ns|import|require|use|refer|alias|refer-clojure|let|letfn|loop|recur|if|if-let|if-some|when|when-not|when-let|when-some|cond|condp|case|do|fn|fn\*|try|catch|finally|throw|doseq|dotimes|for|while|->|->>|as->|doto|some->|some->>|binding|with-open|with-redefs)\b", TokenType.Keyword, 10),

                // Function/macro symbol immediately after '(' (typical call position)
                new TokenPattern(@"(?<=\()[A-Za-z_*!+<>=?/.\-][A-Za-z0-9_*!+<>=?/.\-:]*", TokenType.Method, 11),

                // Reader macro/operators and metadata markers
                new TokenPattern(@"~@|[`'~^@#]", TokenType.Operator, 12),

                // Punctuation and delimiters: lists, vectors, maps, sets
                new TokenPattern(@"[()\[\]{}]", TokenType.Punctuation, 13)
            };
        }
    }

    /// <summary>
    /// C++ syntax highlighter
    /// </summary>
    public class CppHighlighter : RegexHighlighterBase
    {
        public static readonly string[] FileTypes = new string[] { "*.cpp", "*.cxx", "*.cc", "*.hpp", "*.hxx", "*.hh" };
        public override string Language
        {
            get { return "cpp"; }
        }

        public override string[] Aliases
        {
            get { return new string[] { "c++", "cplusplus", "cpp", "cxx", "cc", "hpp", "hxx", "hh" }; }
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
        public static readonly string[] FileTypes = new string[] { "*.cs", "*.csx" };
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
        public static readonly string[] FileTypes = new string[] { "*.css", "*.scss", "*.sass", "*.less" };
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
        public static readonly string[] FileTypes = new string[] { "*.csv" };
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
    /// Dart syntax highlighter (modern Dart with null safety)
    /// </summary>
    public class DartHighlighter : RegexHighlighterBase
    {
        public static readonly string[] FileTypes = new string[] { "*.dart" };
        public override string Language
        {
            get { return "dart"; }
        }

        public override string[] Aliases
        {
            get { return new string[] { "dart", "flutter" }; }
        }

        protected override TokenPattern[] GetPatterns()
        {
            return new TokenPattern[]
            {
                // Line comments and documentation comments
                new TokenPattern(@"///.*$", TokenType.Comment, 1),
                new TokenPattern(@"//.*$", TokenType.Comment, 2),

                // Block comments (including doc blocks)
                new TokenPattern(@"/\*[\s\S]*?\*/", TokenType.Comment, 3),

                // String literals: raw and normal; single/double; triple quotes allow multiline
                new TokenPattern("r?'''[\\s\\S]*?'''|r?\\\"\\\"\\\"[\\s\\S]*?\\\"\\\"\\\"|r?'(?:[^'\\\\]|\\\\.)*'|r?\\\"(?:[^\\\"\\\\]|\\\\.)*\\\"", TokenType.String, 4),

                // Numbers: decimal and hex, with underscores and exponents
                new TokenPattern(@"\b(?:0[xX][0-9a-fA-F_]+|(?:\d+(?:_\d+)*\.(?:\d+(?:_\d+)*)?|\d+(?:_\d+)*|\.(?:\d+(?:_\d+)*))(?:[eE][+-]?\d+)?)\b", TokenType.Number, 5),

                // Keywords (Dart 3+, including null-safety related)
                new TokenPattern(@"\b(?:abstract|as|assert|async|await|break|case|catch|class|const|continue|covariant|default|deferred|do|else|enum|export|extends|extension|external|factory|false|final|finally|for|Function|get|hide|if|implements|import|in|interface|is|late|library|mixin|native|new|null|of|on|operator|part|required|rethrow|return|sealed|set|show|static|super|switch|sync|this|throw|true|try|typedef|var|void|while|with|yield|base)\b", TokenType.Keyword, 6),

                // Built-in and common types (with optional nullable suffix ?)
                new TokenPattern(@"\b(?:int|double|num|bool|String|List|Map|Set|Iterable|Future|Stream|Object|dynamic|Never|Null|Record|Symbol|Duration|DateTime|BigInt|Pattern|RegExp|Uri|Match|StackTrace|Type)(\?)?\b", TokenType.Type, 7),

                // Metadata annotations: @identifier or @prefix.Identifier
                new TokenPattern(@"@[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)?", TokenType.Type, 8),

                // Function/method invocations
                new TokenPattern(@"\b[a-zA-Z_][a-zA-Z0-9_]*(?=\s*\()", TokenType.Method, 9),

                // Operators, including null-safety and cascade/spread operators
                new TokenPattern(@"\?\?=|\?\?|\?\.|\?\.\.|\.\.\.|\?\.\.|\.\.|=>|==|!=|<=|>=|&&|\|\||>>>?=?|<<=?|\+=|-=|\*=|/=|%=|&=|\|=|\^=|~/=|[+\-*/%&|^~!=<>?:.]|!", TokenType.Operator, 10),

                // Punctuation and delimiters
                new TokenPattern(@"[{}()\[\];,<>]", TokenType.Punctuation, 11)
            };
        }
    }

    /// <summary>
    /// EBNF (Extended Backus-Naur Form) syntax highlighter
    /// </summary>
    public class EbnfHighlighter : RegexHighlighterBase
    {
        public static readonly string[] FileTypes = new string[] { "*.ebnf", "*.bnf", "*.abnf", "*.grammar", "*.yacc", "*.bison", "*.y", "*.yy" };
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
    /// Fortran syntax highlighter - supports FORTRAN 77 through modern Fortran (2018+)
    /// </summary>
    public class FortranHighlighter : RegexHighlighterBase
    {
        public static readonly string[] FileTypes = new string[] { "*.f", "*.for", "*.ftn", "*.f77", "*.f90", "*.f95", "*.f03", "*.f08" };
        public override string Language
        {
            get { return "fortran"; }
        }

        public override string[] Aliases
        {
            get { return new string[] { "f77", "f90", "f95", "f2003", "f2008", "f2018", "for", "ftn", "f", "fortran77", "fortran90", "fortran95" }; }
        }

        protected override TokenPattern[] GetPatterns()
        {
            return new TokenPattern[]
            {
                // Comments: C or * in column 1 (F77 style), or ! anywhere (F90+ style)
                new TokenPattern(@"^[Cc*].*$", TokenType.Comment, 1),
                new TokenPattern(@"!.*$", TokenType.Comment, 2),

                // Line continuation: & at end of line (F90+) or non-blank in column 6 (F77)
                new TokenPattern(@"&\s*$", TokenType.Operator, 3),

                // String literals (single and double quotes)
                new TokenPattern(@"'(?:[^'\\]|\\.)*'|""(?:[^""\\]|\\.)*""", TokenType.String, 4),

                // Hollerith constants (legacy F77): nHtext
                new TokenPattern(@"\b\d+H[^\s]*", TokenType.String, 5),

                // Numbers (integers, reals, double precision, complex)
                new TokenPattern(@"\b(?:\d+\.?\d*|\.\d+)(?:[eEdD][+-]?\d+)?(?:_(?:real|double|quad|int|kind=\d+))?\b", TokenType.Number, 6),

                // Hexadecimal, octal, binary literals (modern Fortran)
                new TokenPattern(@"\b(?:z|o|b)'[0-9a-fA-F]+'\b|\b(?:z|o|b)""[0-9a-fA-F]+""\b", TokenType.Number, 7),

                // Program units and structure keywords
                new TokenPattern(@"(?i)\b(?:program|end\s+program|module|end\s+module|submodule|end\s+submodule|interface|end\s+interface|abstract\s+interface|procedure|end\s+procedure|function|end\s+function|subroutine|end\s+subroutine|block\s+data|end\s+block\s+data|type|end\s+type|class|end\s+class|enum|end\s+enum|associate|end\s+associate|block|end\s+block|critical|end\s+critical|forall|end\s+forall|where|end\s+where|select\s+case|end\s+select|select\s+type|end\s+select|do\s+concurrent|end\s+do)\b", TokenType.Keyword, 8),

                // Control flow keywords
                new TokenPattern(@"(?i)\b(?:if|then|else|elseif|endif|do|enddo|continue|stop|pause|return|call|goto|go\s+to|case|default|exit|cycle|select|where|forall|associate|block|critical)\b", TokenType.Keyword, 9),

                // Declaration keywords
                new TokenPattern(@"(?i)\b(?:implicit|none|dimension|parameter|data|save|common|equivalence|external|intrinsic|public|private|protected|allocatable|pointer|target|optional|intent|in|out|inout|pure|elemental|recursive|result|bind|abstract|deferred|final|generic|import|non_overridable|nopass|pass|sequence|extends|value|volatile|asynchronous|contiguous|codimension)\b", TokenType.Keyword, 10),

                // Data types (intrinsic and legacy)
                new TokenPattern(@"(?i)\b(?:integer|real|double\s+precision|complex|double\s+complex|logical|character|byte|type|class|procedure)\b", TokenType.Type, 11),

                // Kind parameters and modern type declarations
                new TokenPattern(@"(?i)\b(?:kind|len|selected_int_kind|selected_real_kind|selected_char_kind|int8|int16|int32|int64|real32|real64|real128|c_int|c_long|c_float|c_double|c_char|c_bool|iso_c_binding|iso_fortran_env)\b", TokenType.Type, 12),

                // Memory management and pointers
                new TokenPattern(@"(?i)\b(?:allocate|deallocate|nullify|associated|allocated|null|c_null_ptr|c_null_funptr)\b", TokenType.Keyword, 13),

                // I/O keywords
                new TokenPattern(@"(?i)\b(?:read|write|print|open|close|rewind|backspace|endfile|inquire|format|namelist|advance|access|action|blank|delim|direct|err|exist|file|fmt|form|formatted|iostat|name|named|nextrec|nml|number|opened|pad|position|readwrite|rec|recl|sequential|size|stat|status|unformatted|unit)\b", TokenType.Keyword, 14),

                // Intrinsic procedures (built-in functions)
                new TokenPattern(@"(?i)\b(?:abs|achar|acos|adjustl|adjustr|aimag|aint|all|allocated|anint|any|asin|associated|atan|atan2|bit_size|btest|ceiling|char|cmplx|conjg|cos|cosh|count|cshift|date_and_time|dble|digits|dim|dot_product|dprod|eoshift|epsilon|exp|exponent|floor|fraction|huge|iachar|iand|ibclr|ibits|ibset|ichar|ieor|index|int|ior|ishft|ishftc|kind|lbound|len|len_trim|log|log10|logical|matmul|max|maxexponent|maxloc|maxval|merge|min|minexponent|minloc|minval|mod|modulo|nearest|nint|not|pack|precision|present|product|radix|random_number|random_seed|range|real|repeat|reshape|rrspacing|scale|scan|selected_int_kind|selected_real_kind|set_exponent|shape|sign|sin|sinh|size|spacing|spread|sqrt|sum|system_clock|tan|tanh|tiny|transfer|transpose|trim|ubound|unpack|verify)\b(?=\s*\()", TokenType.Method, 15),

                // User-defined functions and subroutines
                new TokenPattern(@"(?i)\b[a-zA-Z_][a-zA-Z0-9_]*(?=\s*\()", TokenType.Method, 16),

                // Statement labels (numbers at beginning of line in F77)
                new TokenPattern(@"^\s*\d{1,5}(?=\s)", TokenType.Number, 17),

                // Operators
                new TokenPattern(@"(?i)\.(?:eq|ne|lt|le|gt|ge|and|or|not|eqv|neqv)\.|\*\*|//|[+\-*/=<>]|=>", TokenType.Operator, 18),

                // Array syntax and slicing
                new TokenPattern(@"[():]", TokenType.Punctuation, 19),

                // Other punctuation
                new TokenPattern(@"[{}\[\];,.]", TokenType.Punctuation, 20)
            };
        }
    }

    /// <summary>
    /// F# syntax highlighter - supports F# functional programming syntax
    /// </summary>
    public class FSharpHighlighter : RegexHighlighterBase
    {
        public static readonly string[] FileTypes = new string[] { "*.fs", "*.fsi", "*.fsx" };
        public override string Language
        {
            get { return "fsharp"; }
        }

        public override string[] Aliases
        {
            get { return new string[] { "fs", "f#", "fsx", "fsi", "ml", "fsharp" }; }
        }

        protected override TokenPattern[] GetPatterns()
        {
            return new TokenPattern[]
            {
                // Single-line comments (// comment)
                new TokenPattern(@"//.*$", TokenType.Comment, 1),

                // Multi-line comments (* comment *)
                new TokenPattern(@"\(\*[\s\S]*?\*\)", TokenType.Comment, 2),

                // XML documentation comments (/// comment)
                new TokenPattern(@"///.*$", TokenType.Comment, 3),

                // String literals (regular, verbatim, and triple-quoted)
                new TokenPattern(@"@""(?:[^""]|"""")*""|""""""[\s\S]*?""""""|""(?:[^""\\]|\\.)*""", TokenType.String, 4),

                // Character literals
                new TokenPattern(@"'(?:[^'\\]|\\.)'", TokenType.String, 5),

                // Byte strings and byte arrays
                new TokenPattern(@"B?""(?:[^""\\]|\\.)*""B?", TokenType.String, 6),

                // Numbers (integers, floats, hex, octal, binary with suffixes)
                new TokenPattern(@"\b(?:0[xX][0-9a-fA-F]+[uUlLyYsSnN]?|0[oO][0-7]+[uUlLyYsSnN]?|0[bB][01]+[uUlLyYsSnN]?|(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?[fFmMlLyYsSnN]?)\b", TokenType.Number, 7),

                // F# Keywords
                new TokenPattern(@"\b(?:abstract|and|as|assert|base|begin|class|default|delegate|do|done|downcast|downto|elif|else|end|exception|extern|false|finally|for|fun|function|global|if|in|inherit|inline|interface|internal|lazy|let|match|member|module|mutable|namespace|new|not|null|of|open|or|override|private|public|rec|return|sig|static|struct|then|to|true|try|type|upcast|use|val|void|when|while|with|yield|atomic|break|checked|component|const|constraint|constructor|continue|eager|event|external|fixed|functor|include|method|mixin|object|parallel|process|protected|pure|sealed|tailcall|trait|virtual|volatile)\b", TokenType.Keyword, 8),

                // F# operators and symbols (before other patterns)
                new TokenPattern(@"<@|@>|<@@|@@>|\|>|<\||>\||<-|->|:>|:?>|:=|;;|::\?|\?\?|<>|\|\||&&|::|\.\.", TokenType.Operator, 9),

                // Computation expressions and workflows
                new TokenPattern(@"\b(?:async|seq|query|maybe|option|list|array|computation|workflow)\b(?=\s*\{)", TokenType.Keyword, 10),

                // Attributes
                new TokenPattern(@"\[<[\s\S]*?>\]", TokenType.Type, 11),

                // .NET types and F# types
                new TokenPattern(@"\b(?:bool|byte|sbyte|int16|uint16|int|int32|uint32|int64|uint64|nativeint|unativeint|float|float32|double|decimal|char|string|unit|obj|exn|bigint|list|option|array|seq|Set|Map|Result|Choice|Async|Lazy|ref|byref|outref|inref)\b", TokenType.Type, 12),

                // .NET Framework types
                new TokenPattern(@"\b(?:System|Microsoft|FSharp)(?:\.[A-Z][a-zA-Z0-9_]*)+\b", TokenType.Type, 13),

                // Active patterns (|Pattern|_| or |Pattern|)
                new TokenPattern(@"\|[A-Z][a-zA-Z0-9_]*(?:\|_\||\|)", TokenType.Method, 14),

                // Function and value definitions
                new TokenPattern(@"(?:let|and|member|override|abstract|default)\s+(?:mutable\s+|inline\s+|rec\s+)?([a-zA-Z_][a-zA-Z0-9_']*)", TokenType.Method, 15),

                // Type definitions
                new TokenPattern(@"(?:type|and)\s+([A-Z][a-zA-Z0-9_']*)", TokenType.Type, 16),

                // Module and namespace names
                new TokenPattern(@"(?:module|namespace)\s+([A-Z][a-zA-Z0-9_'.]*)", TokenType.Type, 17),

                // Function calls and method invocations
                new TokenPattern(@"\b[a-zA-Z_][a-zA-Z0-9_']*(?=\s*\()", TokenType.Method, 18),

                // Generic type parameters
                new TokenPattern(@"'[a-zA-Z_][a-zA-Z0-9_]*\b", TokenType.Type, 19),

                // Discriminated union cases and record fields
                new TokenPattern(@"\b[A-Z][a-zA-Z0-9_]*\b", TokenType.Type, 20),

                // Operators (mathematical, comparison, logical)
                new TokenPattern(@"[+\-*/%=!<>&|^~?:]+|<=|>=|<>", TokenType.Operator, 21),

                // Punctuation and delimiters
                new TokenPattern(@"[{}()\[\];,.]", TokenType.Punctuation, 22)
            };
        }
    }

    /// <summary>
    /// Go (Golang) syntax highlighter
    /// </summary>
    public class GoHighlighter : RegexHighlighterBase
    {
        public static readonly string[] FileTypes = new string[] { "*.go" };
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
    /// Haskell syntax highlighter
    /// </summary>
    public class HaskellHighlighter : RegexHighlighterBase
    {
        public static readonly string[] FileTypes = new string[] { "*.hs", "*.lhs" };
        public override string Language
        {
            get { return "haskell"; }
        }

        public override string[] Aliases
        {
            get { return new string[] { "hs", "lhs", "haskell" }; }
        }

        protected override TokenPattern[] GetPatterns()
        {
            return new TokenPattern[]
            {
                // Pragmas and block comments (note: nested comments not fully supported by regex)
                new TokenPattern(@"\{-#[\s\S]*?#-\}", TokenType.Comment, 1),
                new TokenPattern(@"\{-[\s\S]*?-\}", TokenType.Comment, 2),

                // Line comments
                new TokenPattern(@"--.*$", TokenType.Comment, 3),

                // Quasiquotes: [qq| ... |] or [name|...|]
                new TokenPattern(@"\[[A-Za-z_][A-Za-z0-9_]*\|[\s\S]*?\|\]", TokenType.String, 4),

                // String literals (double-quoted with escapes)
                new TokenPattern(@"""(?:[^""\\]|\\.)*""", TokenType.String, 5),

                // Character literals
                new TokenPattern(@"'(?:[^'\\]|\\.)'", TokenType.String, 6),

                // Numbers: hex (0x), octal (0o), binary (0b), decimal and floats with exponents
                new TokenPattern(@"\b(?:0[xX][0-9a-fA-F_]+|0[oO][0-7_]+|0[bB][01_]+|(?:\d+(?:_\d+)*\.(?:\d+(?:_\d+)*)?|\d+(?:_\d+)*|\.(?:\d+(?:_\d+)*))(?:[eE][+-]?\d+)?)\b", TokenType.Number, 7),

                // Keywords (Haskell 2010 + common extensions)
                new TokenPattern(@"\b(?:as|case|class|data|default|deriving|do|else|family|forall|foreign|hiding|if|import|in|infix|infixl|infixr|instance|let|mdo|module|newtype|of|qualified|then|type|where|pattern|proc|rec|static|stock|via|unsafe)\b", TokenType.Keyword, 8),

                // Boolean and unit literals
                new TokenPattern(@"\b(?:True|False)\b|\(\)", TokenType.Keyword, 9),

                // Common/built-in types (treat as Type)
                new TokenPattern(@"\b(?:Int|Integer|Float|Double|Rational|Bool|Char|String|Maybe|Either|Ordering|IO|Read|Show|Eq|Ord|Enum|Bounded|Monad|Functor|Applicative|Foldable|Traversable|Num|Real|Integral|Fractional|Floating|RealFrac|RealFloat|Semigroup|Monoid|Void|Array|List|Map|Set|Vector|ByteString|Text)\b", TokenType.Type, 10),

                // Uppercase-starting identifiers (type/data constructors, modules)
                new TokenPattern(@"\b[A-Z][A-Za-z0-9_']*(?:\.[A-Z][A-Za-z0-9_']*)*\b", TokenType.Type, 11),

                // Backticked identifiers used as infix: `function`
                new TokenPattern(@"`[A-Za-z_][A-Za-z0-9_']*`", TokenType.Method, 12),

                // Function/variable definitions at line start (name :: ... or name = ...)
                new TokenPattern(@"(?m)^[\t ]*[a-z_][A-Za-z0-9_']*(?=\s*(::|=))", TokenType.Method, 13),

                // Function calls: simple heuristic - lowercase identifier followed by space and not definition
                new TokenPattern(@"\b[a-z_][A-Za-z0-9_']*(?=\s*\()", TokenType.Method, 14),

                // Operators (including common Haskell operators and symbols)
                new TokenPattern(@"::|->|<-|=>|\\|\.=|\.\.|\.|:|\$|\|\||&&|==|/=|<=|>=|\+\+|!!|\+|-|\*|/|\^|==|/=|<|>|=|@|~|%", TokenType.Operator, 15),

                // Punctuation and delimiters
                new TokenPattern(@"[{}()\[\];,]", TokenType.Punctuation, 16)
            };
        }
    }

    /// <summary>
    /// HTML syntax highlighter
    /// </summary>
    public class HtmlHighlighter : RegexHighlighterBase
    {
        public static readonly string[] FileTypes = new string[] { "*.html", "*.htm", "*.xhtml", "*.asp", "*.aspx", "*.jsp", "*.php", "*.erb", "*.ejs", "*.hta" };
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
    /// Java syntax highlighter
    /// </summary>
    public class JavaHighlighter : RegexHighlighterBase
    {
        public static readonly string[] FileTypes = new string[] { "*.java", "*.jav" };
        public override string Language
        {
            get { return "java"; }
        }

        public override string[] Aliases
        {
            get { return new string[] { "jdk", "jre", "jav" }; }
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
    /// Kotlin syntax highlighter
    /// </summary>
    public class KotlinHighlighter : RegexHighlighterBase
    {
        public static readonly string[] FileTypes = new string[] { "*.kt", "*.kts" };
        public override string Language
        {
            get { return "kotlin"; }
        }

        public override string[] Aliases
        {
            get { return new string[] { "kt", "kts", "kotlin" }; }
        }

        protected override TokenPattern[] GetPatterns()
        {
            return new TokenPattern[]
            {
                // Single-line comments
                new TokenPattern(@"//.*$", TokenType.Comment, 1),

                // Multi-line comments (/* ... */)
                new TokenPattern(@"/\*[\s\S]*?\*/", TokenType.Comment, 2),

                // String literals: triple quotes and normal strings
                new TokenPattern("\"\"\"[\\s\\S]*?\"\"\"|\"(?:[^\"\\\\]|\\\\.)*\"", TokenType.String, 3),

                // Character literals
                new TokenPattern(@"'(?:[^'\\]|\\.)'", TokenType.String, 4),

                // Numbers: hex, binary, decimal, floats with underscores and suffixes (L, f/F)
                new TokenPattern(@"\b(?:0[xX][0-9a-fA-F_]+|0[bB][01_]+|(?:\d+(?:_\d+)*\.(?:\d+(?:_\d+)*)?|\d+(?:_\d+)*|\.(?:\d+(?:_\d+)*))(?:[eE][+-]?\d+)?[fFdDlL]?)\b", TokenType.Number, 5),

                // Keywords (includes soft keywords commonly used in declarations)
                new TokenPattern(@"\b(?:package|import|class|interface|enum|object|companion|fun|operator|infix|inline|noinline|crossinline|tailrec|external|const|vararg|suspend|data|sealed|value|annotation|open|final|abstract|actual|expect|override|private|protected|public|internal|lateinit|by|where|constructor|init|get|set|field|property|receiver|param|setparam|delegate|typealias|as|is|in|out|reified|this|super|return|break|continue|when|if|else|for|while|do|try|catch|finally|throw|true|false|null)
                \b", TokenType.Keyword, 6),

                // Built-in/common types
                new TokenPattern(@"\b(?:Any|Nothing|Unit|Boolean|Byte|Short|Int|Long|UByte|UShort|UInt|ULong|Float|Double|Char|String|Array|List|MutableList|Set|MutableSet|Map|MutableMap|Sequence|Pair|Triple|Result)
                \b", TokenType.Type, 7),

                // Annotations (including use-site targets like @file:, @get:, etc.)
                new TokenPattern(@"@[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)?(?::[A-Za-z_][A-Za-z0-9_]*)?", TokenType.Type, 8),

                // Function and constructor calls (identifier before '(')
                new TokenPattern(@"\b[a-zA-Z_][a-zA-Z0-9_]*(?=\s*\()", TokenType.Method, 9),

                // Operators: safe calls, elvis, not-null, ranges, assignment and logical/arithmetics
                new TokenPattern(@"\?\?:|\?\.|::|!!|\.\.<|\.\.|===|!==|==|!=|<=|>=|&&|\|\||->|\+=|-=|\*=|/=|%=|<<|>>|\+\+|--|=|[+\-*/%&|^~!?<>:]", TokenType.Operator, 10),

                // Punctuation
                new TokenPattern(@"[{}()\[\];,.:]", TokenType.Punctuation, 11)
            };
        }
    }

    /// <summary>
    /// JavaScript syntax highlighter
    /// </summary>
    public class JavaScriptHighlighter : RegexHighlighterBase
    {
        public static readonly string[] FileTypes = new string[] { "*.js", "*.jsx", "*.mjs", "*.cjs" };
        public override string Language
        {
            get { return "javascript"; }
        }

        public override string[] Aliases
        {
            get { return new string[] { "js", "node", "nodejs", "mjs", "cjs" }; }
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
        public static readonly string[] FileTypes = new string[] { "*.json", "*.jsonc", "*.json5" };
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
    /// Lua syntax highlighter - supports Lua 5.1-5.4 syntax
    /// </summary>
    public class LuaHighlighter : RegexHighlighterBase
    {
        public static readonly string[] FileTypes = new string[] { "*.lua", "*.luau" };
        public override string Language
        {
            get { return "lua"; }
        }

        public override string[] Aliases
        {
            get { return new string[] { "lua5.1", "lua5.2", "lua5.3", "lua5.4", "luajit", "moonscript" }; }
        }

        protected override TokenPattern[] GetPatterns()
        {
            return new TokenPattern[]
            {
                // Single-line comments (-- comment)
                new TokenPattern(@"--(?!\[\[).*$", TokenType.Comment, 1),

                // Multi-line comments (--[[ comment ]] or --[=[comment]=])
                new TokenPattern(@"--\[=*\[[\s\S]*?\]=*\]", TokenType.Comment, 2),

                // Long string literals ([[string]] or [=[string]=])
                new TokenPattern(@"\[=*\[[\s\S]*?\]=*\]", TokenType.String, 3),

                // String literals (single and double quotes)
                new TokenPattern(@"'(?:[^'\\]|\\.)*'|""(?:[^""\\]|\\.)*""", TokenType.String, 4),

                // Numbers (integers, decimals, hex, scientific notation)
                new TokenPattern(@"\b(?:0[xX][0-9a-fA-F]+(?:\.[0-9a-fA-F]*)?(?:[pP][+-]?\d+)?|\d+(?:\.\d*)?(?:[eE][+-]?\d+)?)\b", TokenType.Number, 5),

                // Keywords
                new TokenPattern(@"\b(?:and|break|do|else|elseif|end|false|for|function|goto|if|in|local|nil|not|or|repeat|return|then|true|until|while)\b", TokenType.Keyword, 6),

                // Built-in global variables and constants
                new TokenPattern(@"\b(?:_G|_VERSION|nil|true|false)\b", TokenType.Type, 7),

                // Standard library functions and modules
                new TokenPattern(@"\b(?:assert|collectgarbage|dofile|error|getfenv|getmetatable|ipairs|load|loadfile|loadstring|module|next|pairs|pcall|print|rawequal|rawget|rawlen|rawset|require|select|setfenv|setmetatable|tonumber|tostring|type|unpack|xpcall|bit32|coroutine|debug|io|math|os|package|string|table|utf8)\b", TokenType.Type, 8),

                // Common standard library methods (when followed by dot or colon)
                new TokenPattern(@"\b(?:abs|acos|asin|atan|atan2|ceil|cos|cosh|deg|exp|floor|fmod|frexp|huge|ldexp|log|log10|max|min|modf|pi|pow|rad|random|randomseed|sin|sinh|sqrt|tan|tanh|byte|char|dump|find|format|gmatch|gsub|len|lower|match|rep|reverse|sub|upper|clock|date|difftime|execute|exit|getenv|remove|rename|setlocale|time|tmpname|close|flush|input|lines|open|output|popen|read|stderr|stdin|stdout|tmpfile|type|write|concat|insert|maxn|pack|remove|sort|unpack|create|resume|running|status|wrap|yield)\b(?=\s*[.:])", TokenType.Method, 9),

                // Function calls (identifier followed by opening parenthesis)
                new TokenPattern(@"\b[a-zA-Z_][a-zA-Z0-9_]*(?=\s*\()", TokenType.Method, 10),

                // Method calls (identifier after dot or colon)
                new TokenPattern(@"(?<=[.:])[a-zA-Z_][a-zA-Z0-9_]*", TokenType.Method, 11),

                // Metamethods
                new TokenPattern(@"\b__(?:add|sub|mul|div|mod|pow|unm|idiv|band|bor|bxor|bnot|shl|shr|concat|len|eq|lt|le|index|newindex|call|tostring|gc|mode|name|metatable|pairs|ipairs|close)\b", TokenType.Type, 12),

                // Operators (including Lua-specific ones)
                new TokenPattern(@"\.\.\.|\.\.|[+\-*/%^=!<>&|~#]+|<=|>=|==|~=|and|or|not", TokenType.Operator, 13),

                // Self reference
                new TokenPattern(@"\bself\b", TokenType.Keyword, 14),

                // Labels (for goto statements in Lua 5.2+)
                new TokenPattern(@"::[a-zA-Z_][a-zA-Z0-9_]*::", TokenType.Type, 15),

                // Punctuation
                new TokenPattern(@"[{}()\[\];,.:?]", TokenType.Punctuation, 16)
            };
        }
    }

    /// <summary>
    /// Pascal/Delphi/Object Pascal syntax highlighter
    /// </summary>
    public class PascalHighlighter : RegexHighlighterBase
    {
        public static readonly string[] FileTypes = new string[] { "*.pas", "*.pp", "*.dpr", "*.dpk", "*.inc" };
        public override string Language
        {
            get { return "pascal"; }
        }

        public override string[] Aliases
        {
            get { return new string[] { "pas", "pp", "delphi", "objectpascal", "freepascal", "lazarus", "dpr", "dpk", "inc" }; }
        }

        protected override TokenPattern[] GetPatterns()
        {
            return new TokenPattern[]
            {
                // Single-line comments (// style - Delphi/modern Pascal)
                new TokenPattern(@"//.*$", TokenType.Comment, 1),

                // Multi-line comments (* ... *) and { ... }
                new TokenPattern(@"\(\*[\s\S]*?\*\)", TokenType.Comment, 2),
                new TokenPattern(@"\{[\s\S]*?\}", TokenType.Comment, 3),

                // String literals (single quotes, with doubled quotes for escapes)
                new TokenPattern(@"'(?:[^']|'')*'", TokenType.String, 4),

                // Character literals (#65, #$41, ^A)
                new TokenPattern(@"#(?:\$[0-9a-fA-F]+|\d+)|\^[A-Za-z]", TokenType.String, 5),

                // Numbers (integers, reals, hex, octal, binary)
                new TokenPattern(@"(?i)\b(?:\$[0-9a-fA-F]+|&[0-7]+|%[01]+|(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?)\b", TokenType.Number, 6),

                // Pascal/Delphi keywords
                new TokenPattern(@"(?i)\b(?:and|array|as|asm|begin|case|class|const|constructor|destructor|div|do|downto|else|end|except|exports|file|finalization|finally|for|function|goto|if|implementation|in|inherited|initialization|inline|interface|is|label|library|mod|nil|not|object|of|or|packed|procedure|program|property|raise|record|repeat|set|shl|shr|string|then|threadvar|to|try|type|unit|until|uses|var|while|with|xor)\b", TokenType.Keyword, 7),

                // Delphi-specific keywords (more modern Object Pascal)
                new TokenPattern(@"(?i)\b(?:absolute|abstract|automated|cdecl|default|dispid|dynamic|export|external|far|forward|index|message|name|near|nodefault|override|pascal|private|protected|public|published|read|readonly|register|reintroduce|resident|safecall|stdcall|stored|virtual|write|writeonly)\b", TokenType.Keyword, 8),

                // Built-in types
                new TokenPattern(@"(?i)\b(?:boolean|byte|bytebool|cardinal|char|comp|currency|double|extended|int64|integer|longbool|longint|longword|pansichar|pchar|pointer|real|real48|shortint|shortstring|single|smallint|text|variant|widechar|widestring|word|wordbool|ansistring|ansichar|olevariant|pwidechar|utf8string)\b", TokenType.Type, 9),

                // Standard constants
                new TokenPattern(@"(?i)\b(?:false|true|maxint|pi)\b", TokenType.Keyword, 10),

                // Standard procedures and functions
                new TokenPattern(@"(?i)\b(?:abs|arctan|chr|cos|dispose|eof|eoln|exp|get|halt|inc|dec|length|ln|new|odd|ord|pack|page|pred|put|read|readln|reset|rewrite|round|sin|sqr|sqrt|succ|trunc|unpack|write|writeln|append|assign|blockread|blockwrite|close|erase|flush|rename|seek|seekeof|seekeoln|truncate|copy|delete|insert|pos|str|val|hi|lo|swap|random|randomize|paramcount|paramstr|getdir|mkdir|rmdir|chdir|upcase|filepos|filesize|sizeof|addr|cseg|dseg|sseg|seg|ofs|ptr|freemem|getmem|maxavail|memavail|move|fillchar)\b(?=\s*\()", TokenType.Method, 11),

                // Procedure/function declarations and calls
                new TokenPattern(@"(?i)\b[a-zA-Z_][a-zA-Z0-9_]*(?=\s*\()", TokenType.Method, 12),

                // Compiler directives and pragmas {$...}
                new TokenPattern(@"(?i)\{\$[^}]*\}", TokenType.Comment, 13),

                // Operators
                new TokenPattern(@":=|<=|>=|<>|\.\.|[+\-*/:=<>]", TokenType.Operator, 14),

                // Assignment operator (separate for clarity)
                new TokenPattern(@":=", TokenType.Operator, 15),

                // Punctuation (including range operator ..)
                new TokenPattern(@"[();,\[\]{}@\^.]", TokenType.Punctuation, 16)
            };
        }
    }

    /// <summary>
    /// Perl syntax highlighter
    /// </summary>
    public class PerlHighlighter : RegexHighlighterBase
    {
        public static readonly string[] FileTypes = new string[] { "*.pl", "*.pm", "*.pod", "*.perl" };
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
    /// PHP syntax highlighter (modern PHP 7/8 with attributes and null coalescing)
    /// </summary>
    public class PhpHighlighter : RegexHighlighterBase
    {
        public static readonly string[] FileTypes = new string[] { "*.php", "*.phtml", "*.php3", "*.php4", "*.php5", "*.phps" };
        public override string Language
        {
            get { return "php"; }
        }

        public override string[] Aliases
        {
            get { return new string[] { "php", "phtml" }; }
        }

        protected override TokenPattern[] GetPatterns()
        {
            return new TokenPattern[]
            {
                // Comments
                new TokenPattern(@"//.*$", TokenType.Comment, 1),
                new TokenPattern(@"#.*$", TokenType.Comment, 2),
                new TokenPattern(@"/\*[\s\S]*?\*/", TokenType.Comment, 3),

                // Heredoc / Nowdoc (heuristic; supports optional indent with <<<-)
                new TokenPattern(@"<<<-?\s*'([A-Za-z_][A-Za-z0-9_]*)'[\s\S]*?^\1;?\s*$", TokenType.String, 4),
                new TokenPattern(@"<<<-?\s*([A-Za-z_][A-Za-z0-9_]*)[\s\S]*?^\1;?\s*$", TokenType.String, 5),

                // Strings: single and double quoted
                new TokenPattern(@"'(?:[^'\\]|\\.)*'|""(?:[^""\\]|\\.)*""", TokenType.String, 6),

                // PHP open/close tags
                new TokenPattern(@"<\?(?:php|=)?|\?>", TokenType.Punctuation, 7),

                // Superglobals (higher priority than regular variables)
                new TokenPattern(@"\$(?:GLOBALS|_SERVER|_GET|_POST|_FILES|_COOKIE|_SESSION|_REQUEST|_ENV)\b", TokenType.Type, 8),

                // Variables ($var, $this)
                new TokenPattern(@"\$[A-Za-z_][A-Za-z0-9_]*", TokenType.Type, 9),

                // Magic constants and special
                new TokenPattern(@"__\w+__\b|\b__halt_compiler\b", TokenType.Keyword, 10),

                // Numbers: hex, bin, oct, decimal (with underscores and exponents)
                new TokenPattern(@"\b(?:0[xX][0-9a-fA-F_]+|0[bB][01_]+|0[0-7_]+|(?:\d+(?:_\d+)*\.(?:\d+(?:_\d+)*)?|\d+(?:_\d+)*|\.(?:\d+(?:_\d+)*))(?:[eE][+-]?\d+)?)\b", TokenType.Number, 11),

                // Keywords (case-insensitive)
                new TokenPattern(@"(?i)\b(?:abstract|and|array|as|break|callable|case|catch|class|clone|const|continue|declare|default|die|do|echo|else|elseif|empty|enddeclare|endfor|endforeach|endif|endswitch|endwhile|eval|exit|extends|final|finally|fn|for|foreach|function|global|goto|if|implements|include|include_once|instanceof|insteadof|interface|isset|list|match|namespace|new|or|print|private|protected|public|require|require_once|return|static|switch|throw|trait|try|unset|use|var|while|xor|yield|from|enum|readonly)\b", TokenType.Keyword, 12),

                // Boolean/null literals (case-insensitive)
                new TokenPattern(@"(?i)\b(?:true|false|null)\b", TokenType.Keyword, 13),

                // Built-in types (with optional nullable suffix ?)
                new TokenPattern(@"(?i)\b(?:int|float|double|string|bool|boolean|array|object|mixed|void|never|resource|iterable|self|parent|static)(\?)?\b", TokenType.Type, 14),

                // Attribute syntax #[...] (PHP 8+)
                new TokenPattern(@"#\[[^\]]*\]", TokenType.Type, 15),

                // Namespace and use declarations (qualified names)
                new TokenPattern(@"(?<=\b(?:namespace|use)\s+)[A-Za-z_\\][A-Za-z0-9_\\]*", TokenType.Type, 16),

                // Class/interface/trait/enum names after declaration
                new TokenPattern(@"(?<=\b(?:class|interface|trait|enum)\s+)[A-Za-z_][A-Za-z0-9_]*", TokenType.Type, 17),

                // Function definition names
                new TokenPattern(@"(?<=\bfunction\s+)&?[A-Za-z_][A-Za-z0-9_]*", TokenType.Method, 18),

                // Function/method calls
                new TokenPattern(@"\\?[A-Za-z_][A-Za-z0-9_\\]*(?=\s*\()", TokenType.Method, 19),

                // Operators (including PHP-specific)
                new TokenPattern(@"\?\?=|\?\?|<=>|\?->|->|::|===|!==|==|!=|<=|>=|&&|\|\||<<=|>>=|\.=|\+=|-=|\*=|/=|%=|&=|\|=|\^=|<<|>>|\.|~|\?|:|[+\-*/%&|^!=<>]", TokenType.Operator, 20),

                // Punctuation
                new TokenPattern(@"[{}()\[\];,:]", TokenType.Punctuation, 21)
            };
        }
    }

    /// <summary>
    /// PowerShell syntax highlighter
    /// </summary>
    public class PowerShellHighlighter : RegexHighlighterBase
    {
        public static readonly string[] FileTypes = new string[] { "*.ps1", "*.psm1", "*.psd1", "*.ps1xml" };
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
    /// Properties/configuration file syntax highlighter (key=value format)
    /// </summary>
    public class PropertiesHighlighter : RegexHighlighterBase
    {
        public static readonly string[] FileTypes = new string[] { "*.ini", "*.cfg", "*.conf", "*.config", "*.properties", "*.desktop", "*.reg", "*.inf", "*.gitconfig" };
        public override string Language
        {
            get { return "properties"; }
        }

        public override string[] Aliases
        {
            get { return new string[] { "ini", "conf", "cfg", "desktop", "reg", "inf", "gitconfig" }; }
        }

        protected override TokenPattern[] GetPatterns()
        {
            return new TokenPattern[]
            {
                // Comments: ; comment and # comment (at start of line or after whitespace)
                new TokenPattern(@"^\s*[;#].*$", TokenType.Comment, 1),
                new TokenPattern(@"(?<=\s)[;#].*$", TokenType.Comment, 2),

                // Section headers: [SectionName]
                new TokenPattern(@"^\s*\[[^\]]*\]", TokenType.Keyword, 3),

                // Key-value pairs: key=value (key part)
                new TokenPattern(@"^\s*[^=\[\];#\r\n]+(?==)", TokenType.Type, 4),

                // Quoted values: "value", 'value'
                new TokenPattern(@"(?<==\s*)(?:'[^']*'|""[^""]*"")", TokenType.String, 5),

                // Unquoted values after = (excluding comments)
                new TokenPattern(@"(?<==\s*)[^;#\r\n]+(?=\s*(?:[;#]|$))", TokenType.String, 6),

                // Environment variable references: %VAR%, ${VAR}, $VAR
                new TokenPattern(@"%[^%\s]+%|\$\{[^}]+\}|\$[a-zA-Z_][a-zA-Z0-9_]*", TokenType.Method, 7),

                // Numbers (integers and floats)
                new TokenPattern(@"(?<==\s*)\b\d+(?:\.\d+)?\b(?=\s*(?:[;#]|$))", TokenType.Number, 8),

                // Boolean-like values
                new TokenPattern(@"(?i)(?<==\s*)\b(?:true|false|yes|no|on|off|enabled|disabled|1|0)\b(?=\s*(?:[;#]|$))", TokenType.Keyword, 9),

                // File paths and URLs (simple heuristic)
                new TokenPattern(@"(?<==\s*)(?:[a-zA-Z]:[\\\/]|\\\\|\/|https?:\/\/)[^\s;#]*", TokenType.String, 10),

                // Registry-style GUID/UUID values
                new TokenPattern(@"(?<==\s*)\{[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\}", TokenType.Number, 11),

                // Hex values (common in Windows INI files)
                new TokenPattern(@"(?<==\s*)0[xX][0-9a-fA-F]+", TokenType.Number, 12),

                // Assignment operator
                new TokenPattern(@"=", TokenType.Operator, 13),

                // Continuation lines (backslash at end of line)
                new TokenPattern(@"\\$", TokenType.Operator, 14),

                // Brackets for array-like syntax [0], [1]
                new TokenPattern(@"\[\d+\]", TokenType.Punctuation, 15)
            };
        }
    }

    /// <summary>
    /// Python syntax highlighter
    /// </summary>
    public class PythonHighlighter : RegexHighlighterBase
    {
        public static readonly string[] FileTypes = new string[] { "*.py", "*.pyw", "*.pyi", "*.pyx" };
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
    /// Ruby syntax highlighter
    /// </summary>
    public class RubyHighlighter : RegexHighlighterBase
    {
        public static readonly string[] FileTypes = new string[] { "*.rb", "*.rbw", "*.rake", "*.gemspec" };
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
        public static readonly string[] FileTypes = new string[] { "*.regex", "*.regexp", "*.re" };
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
        public static readonly string[] FileTypes = new string[] { "*.rs" };
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
    /// Scala syntax highlighter
    /// </summary>
    public class ScalaHighlighter : RegexHighlighterBase
    {
        public static readonly string[] FileTypes = new string[] { "*.scala", "*.sc" };
        public override string Language
        {
            get { return "scala"; }
        }

        public override string[] Aliases
        {
            get { return new string[] { "scala", "sc" }; }
        }

        protected override TokenPattern[] GetPatterns()
        {
            return new TokenPattern[]
            {
                // ScalaDoc and block comments
                new TokenPattern(@"/\*\*[\s\S]*?\*/", TokenType.Comment, 1),
                new TokenPattern(@"/\*[\s\S]*?\*/", TokenType.Comment, 2),

                // Line comments
                new TokenPattern(@"//.*$", TokenType.Comment, 3),

                // Strings: triple-quoted and normal (optionally with interpolator prefix like s"""...""" or f"...")
                new TokenPattern("(?:[A-Za-z_]\\w*)?\"\"\"[\\s\\S]*?\"\"\"", TokenType.String, 4),
                new TokenPattern("(?:[A-Za-z_]\\w*)?\"(?:[^\\\"\\\\]|\\\\.)*\"", TokenType.String, 5),

                // Character literals
                new TokenPattern(@"'(?:[^'\\]|\\.)'", TokenType.String, 6),

                // Numbers: hex, binary (Scala 3), decimal, floats with optional underscores and suffixes
                new TokenPattern(@"\b(?:0[xX][0-9a-fA-F_]+|0[bB][01_]+|(?:\d+(?:_\d+)*\.(?:\d+(?:_\d+)*)?|\d+(?:_\d+)*|\.(?:\d+(?:_\d+)*))(?:[eE][+-]?\d+)?[fFdDlL]?)\b", TokenType.Number, 7),

                // Keywords (Scala 2 and 3)
                new TokenPattern(@"\b(?:abstract|case|catch|class|def|do|else|enum|export|extends|extension|false|final|finally|for|given|if|implicit|import|inline|lazy|match|new|null|object|opaque|open|override|package|private|protected|return|sealed|super|then|this|throw|trait|true|try|type|using|val|var|while|with|yield|end)\b", TokenType.Keyword, 8),

                // Common/built-in types
                new TokenPattern(@"\b(?:Any|AnyRef|AnyVal|Nothing|Null|Unit|Boolean|Byte|Short|Int|Long|Float|Double|Char|String|Option|Some|None|Either|Left|Right|Try|Success|Failure|List|Vector|Seq|IndexedSeq|Map|Set|Iterator|Future|BigInt|BigDecimal)\b", TokenType.Type, 9),

                // Annotations
                new TokenPattern(@"@[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*", TokenType.Type, 10),

                // Method/function calls (identifier followed by '(')
                new TokenPattern(@"\b[a-zA-Z_][a-zA-Z0-9_]*(?=\s*\()", TokenType.Method, 11),

                // Operators: arrows, comparison, boolean, assignment, ranges, cons, etc.
                new TokenPattern(@"=>|<-|:=|::|:::|\|\||&&|===|!==|==|!=|<=|>=|<<|>>|\+\+|--|\+=|-=|\*=|/=|%=|::=|\.|:|~|\?|[+\-*/%&|^!<>]=?", TokenType.Operator, 12),

                // Punctuation
                new TokenPattern(@"[{}()\[\];,]", TokenType.Punctuation, 13)
            };
        }
    }

    /// <summary>
    /// SQL syntax highlighter - supports standard SQL with common database extensions
    /// </summary>
    public class SqlHighlighter : RegexHighlighterBase
    {
        public static readonly string[] FileTypes = new string[] { "*.sql" };
        public override string Language
        {
            get { return "sql"; }
        }

        public override string[] Aliases
        {
            get { return new string[] { "mysql", "postgresql", "postgres", "sqlite", "tsql", "mssql", "oracle", "plsql", "mariadb", "sqlserver" }; }
        }

        protected override TokenPattern[] GetPatterns()
        {
            return new TokenPattern[]
            {
                // Single-line comments (-- comment)
                new TokenPattern(@"--.*$", TokenType.Comment, 1),

                // Multi-line comments (/* comment */)
                new TokenPattern(@"/\*[\s\S]*?\*/", TokenType.Comment, 2),

                // MySQL-style comments (# comment)
                new TokenPattern(@"#.*$", TokenType.Comment, 3),

                // String literals (single quotes, standard SQL)
                new TokenPattern(@"'(?:[^'\\]|\\.)*'", TokenType.String, 4),

                // Quoted identifiers (double quotes, backticks for MySQL)
                new TokenPattern(@"""(?:[^""\\]|\\.)*""|`(?:[^`\\]|\\.)*`", TokenType.String, 5),

                // Square bracket identifiers (SQL Server style)
                new TokenPattern(@"\[(?:[^\]\\]|\\.)*\]", TokenType.String, 6),

                // Numbers (integers, decimals, scientific notation)
                new TokenPattern(@"\b(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?\b", TokenType.Number, 7),

                // SQL Keywords (DML, DDL, DCL, TCL)
                new TokenPattern(@"(?i)\b(?:SELECT|FROM|WHERE|JOIN|INNER|LEFT|RIGHT|FULL|OUTER|CROSS|ON|USING|GROUP|BY|HAVING|ORDER|ASC|DESC|LIMIT|OFFSET|DISTINCT|ALL|UNION|INTERSECT|EXCEPT|MINUS|INSERT|INTO|VALUES|UPDATE|SET|DELETE|TRUNCATE|CREATE|ALTER|DROP|TABLE|VIEW|INDEX|DATABASE|SCHEMA|PROCEDURE|FUNCTION|TRIGGER|SEQUENCE|CONSTRAINT|PRIMARY|FOREIGN|KEY|UNIQUE|CHECK|DEFAULT|NULL|NOT|AUTO_INCREMENT|IDENTITY|SERIAL|GRANT|REVOKE|COMMIT|ROLLBACK|SAVEPOINT|TRANSACTION|BEGIN|END|IF|ELSE|WHILE|LOOP|FOR|CASE|WHEN|THEN|WITH|RECURSIVE|WINDOW|PARTITION|OVER|ROW_NUMBER|RANK|DENSE_RANK|LEAD|LAG|FIRST_VALUE|LAST_VALUE|EXISTS|IN|ANY|SOME|ALL|BETWEEN|LIKE|ILIKE|REGEXP|RLIKE|IS|ESCAPE|AS|CAST|CONVERT|EXTRACT|INTERVAL|CURRENT_DATE|CURRENT_TIME|CURRENT_TIMESTAMP|SYSDATE|GETDATE|NOW)\b", TokenType.Keyword, 8),

                // Data types
                new TokenPattern(@"(?i)\b(?:CHAR|VARCHAR|VARCHAR2|NCHAR|NVARCHAR|TEXT|NTEXT|CLOB|NCLOB|BLOB|BINARY|VARBINARY|IMAGE|BIT|BOOLEAN|BOOL|TINYINT|SMALLINT|MEDIUMINT|INT|INTEGER|BIGINT|DECIMAL|NUMERIC|NUMBER|FLOAT|REAL|DOUBLE|MONEY|SMALLMONEY|DATE|TIME|DATETIME|DATETIME2|TIMESTAMP|TIMESTAMPTZ|YEAR|UUID|UNIQUEIDENTIFIER|XML|JSON|JSONB|ARRAY|ENUM|GEOMETRY|GEOGRAPHY)\b", TokenType.Type, 9),

                // Built-in functions (aggregate, string, date, math)
                new TokenPattern(@"(?i)\b(?:COUNT|SUM|AVG|MIN|MAX|STDDEV|VARIANCE|GROUPING|COALESCE|NULLIF|ISNULL|IFNULL|NVL|NVL2|DECODE|GREATEST|LEAST|ABS|CEIL|CEILING|FLOOR|ROUND|TRUNC|TRUNCATE|POWER|SQRT|EXP|LOG|LOG10|SIN|COS|TAN|ASIN|ACOS|ATAN|ATAN2|DEGREES|RADIANS|PI|RAND|RANDOM|UPPER|LOWER|INITCAP|LENGTH|LEN|CHAR_LENGTH|CHARACTER_LENGTH|SUBSTRING|SUBSTR|LEFT|RIGHT|LTRIM|RTRIM|TRIM|REPLACE|TRANSLATE|CONCAT|CONCAT_WS|LPAD|RPAD|REVERSE|ASCII|CHR|CHAR|POSITION|INSTR|LOCATE|CHARINDEX|PATINDEX|SOUNDEX|DIFFERENCE|STUFF|SPACE|REPLICATE|REPEAT|FORMAT|TO_CHAR|TO_NUMBER|TO_DATE|DATEADD|DATEDIFF|DATEPART|DATENAME|YEAR|MONTH|DAY|HOUR|MINUTE|SECOND|WEEKDAY|DAYOFWEEK|DAYOFYEAR|WEEK|QUARTER|LAST_DAY|NEXT_DAY|ADD_MONTHS|MONTHS_BETWEEN|EXTRACT|DATE_FORMAT|STR_TO_DATE|UNIX_TIMESTAMP|FROM_UNIXTIME)\b(?=\s*\()", TokenType.Method, 10),

                // Operators
                new TokenPattern(@"[+\-*/%=!<>&|^~]+|<<|>>|<>|<=|>=|:=|\|\||&&", TokenType.Operator, 11),

                // Variables and parameters (@ for SQL Server, : for Oracle, $ for PostgreSQL)
                new TokenPattern(@"[@:$][a-zA-Z_][a-zA-Z0-9_]*", TokenType.Type, 12),

                // Table/column identifiers (unquoted identifiers)
                new TokenPattern(@"\b[a-zA-Z_][a-zA-Z0-9_]*\b", TokenType.Normal, 13),

                // Punctuation
                new TokenPattern(@"[(){}\[\];,.]", TokenType.Punctuation, 14)
            };
        }
    }

    /// <summary>
    /// Swift syntax highlighter (modern Swift with optionals and concurrency keywords)
    /// </summary>
    public class SwiftHighlighter : RegexHighlighterBase
    {
        public static readonly string[] FileTypes = new string[] { "*.swift" };
        public override string Language
        {
            get { return "swift"; }
        }

        public override string[] Aliases
        {
            get { return new string[] { "swift", "swiftlang" }; }
        }

        protected override TokenPattern[] GetPatterns()
        {
            return new TokenPattern[]
            {
                // Comments (doc and single-line)
                new TokenPattern(@"///.*$", TokenType.Comment, 1),
                new TokenPattern(@"//.*$", TokenType.Comment, 2),

                // Block comments (including nested in practice, but regex handles basic)
                new TokenPattern(@"/\*[\s\S]*?\*/", TokenType.Comment, 3),

                // String literals: triple quotes (multiline), raw (#"..."# / ##"..."##), and normal strings
                new TokenPattern("\"\"\"[\\s\\S]*?\"\"\"|#\\\"(?:[^\\\"\\\\]|\\\\.)*\\\"#|##\\\"(?:[^\\\"\\\\]|\\\\.)*\\\"##|\\\"(?:[^\\\"\\\\]|\\\\.)*\\\"", TokenType.String, 4),

                // Numbers: hex (with optional p-exponent), binary, octal, decimal with underscores and exponents
                new TokenPattern(@"\b(?:0x[0-9A-Fa-f_]+(?:\.[0-9A-Fa-f_]+)?(?:[pP][+-]?\d+)?|0b[01_]+|0o[0-7_]+|(?:\d+(?:_\d+)*(?:\.\d+(?:_\d+)*)?|\.\d+(?:_\d+)*)(?:[eE][+-]?\d+)?)\b", TokenType.Number, 5),

                // Keywords (including access control, modifiers, error handling, concurrency)
                new TokenPattern(@"\b(?:actor|as|associatedtype|async|await|break|case|catch|class|continue|convenience|defer|deinit|default|deinit|do|dynamic|else|enum|extension|fallthrough|false|fileprivate|final|for|func|get|guard|if|import|in|indirect|infix|init|inout|internal|is|lazy|let|mutating|nonmutating|nil|none|nonisolated|open|operator|override|postfix|prefix|private|protocol|public|rethrows|return|required|self|Self|set|some|static|struct|subscript|super|switch|throw|throws|true|try|typealias|unowned|weak|where|while|willSet|didSet|yield|yielded|any)\b", TokenType.Keyword, 6),

                // Built-in/common types with optional nullable suffix ?
                new TokenPattern(@"\b(?:Int|Int8|Int16|Int32|Int64|UInt|UInt8|UInt16|UInt32|UInt64|Float|Double|Bool|String|Character|Any|AnyObject|Never|Optional|Array|Dictionary|Set|Result|Data|URL|UUID|Decimal|Date|Error|NSObject|CGFloat|CGPoint|CGSize|CGRect)(\?)?\b", TokenType.Type, 7),

                // Attributes (annotations) like @available, @objc, @MainActor, etc.
                new TokenPattern(@"@[A-Za-z_][A-Za-z0-9_]*", TokenType.Type, 8),

                // Compile-time and magic directives: #if, #available, #selector, #file, #line, etc.
                new TokenPattern(@"#[A-Za-z_][A-Za-z0-9_]*", TokenType.Keyword, 9),

                // Function and method calls (identifier followed by '(')
                new TokenPattern(@"\b[a-zA-Z_][a-zA-Z0-9_]*(?=\s*\()", TokenType.Method, 10),

                // Operators: ranges, nil-coalescing, optional chaining, assignment and comparisons
                new TokenPattern(@"\?\?|\?\.|\.\.\.|\.\.<|->|===|!==|==|!=|<=|>=|&&|\|\||<<|>>|\+=|-=|\*=|/=|%=|&=|\|=|\^=|~|\?|:|\.|!|[+\-*/%&|^<>=]", TokenType.Operator, 11),

                // Punctuation
                new TokenPattern(@"[{}()\[\];,]", TokenType.Punctuation, 12)
            };
        }
    }

    /// <summary>
    /// TypeScript syntax highlighter
    /// </summary>
    public class TypeScriptHighlighter : RegexHighlighterBase
    {
        public static readonly string[] FileTypes = new string[] { "*.ts", "*.tsx" };
        public override string Language
        {
            get { return "typescript"; }
        }

        public override string[] Aliases
        {
            get { return new string[] { "ts", "tsx" }; }
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
    /// Visual Basic (VB6/VBA/VB.NET) syntax highlighter
    /// </summary>
    public class VisualBasicHighlighter : RegexHighlighterBase
    {
        public static readonly string[] FileTypes = new string[] { "*.vb", "*.vbs", "*.vba" };
        public override string Language
        {
            get { return "visualbasic"; }
        }

        public override string[] Aliases
        {
            get { return new string[] { "vb", "vba", "vb6", "vbnet", "vb.net", "visualbasic", "visual-basic", "vbs" }; }
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
        public static readonly string[] FileTypes = new string[] { "*.xml", "*.xaml", "*.xsl", "*.xslt", "*.xsd", "*.svg", "*.rss", "*.atom", "*.plist", "*.resx", "*.settings", "*.manifest", "*.nuspec", "*.wsdl", "*.disco", "*.asmx", "*.sitemap", "*.master", "*.ascx", "*.kml", "*.gpx", "*.tei", "*.docbook", "*.fo", "*.ant", "*.maven", "*.pom", "*.csproj", "*.vbproj", "*.fsproj", "*.vcxproj", "*.proj", "*.targets", "*.props", "*.packages.config", "*.web.config", "*.app.config", "*.machine.config", "*.ps1xml" };
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
                "resx", "settings", "manifest", "nuspec", "packages.config", "ps1xml",
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
        public static readonly string[] FileTypes = new string[] { "*.yml", "*.yaml" };
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
        public static readonly string[] FileTypes = new string[] { "*.zig" };
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
