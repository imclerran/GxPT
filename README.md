# GxPT

A native chatbot client for Windows XP, written in C# and .NET 3.5. GxPT aims to provide a modern and user-friendly chat interface on legacy Windows systems, with robust Markdown and code syntax highlighting support — plus tool calling via the Model Context Protocol (MCP).

## Screenshot

### Light Mode:
![GxPT Light Mode Screenshot](GxPT-light.png)
### Dark Mode:
![GxPT Dark Mode Screenshot](GxPT-dark.png)
### Windows 7:
![GxPT Windows 7 Screenshot](GxPT-win7.png)

## Features

- **Modern Chat UI**: Clean, responsive chat transcript display.
- **Markdown Rendering**: Supports headings, bold/italic, links, bullet and numbered lists (including deeply nested lists), tables, code blocks, and inline code.
- **Code Syntax Highlighting**: Out-of-the-box support for a wide range of languages, including:
   - Ada, ASM, Bash, Basic, Batch, C, Clojure, C++, C#, CSS, CSV, Dart, EBNF, Elixir, Erlang, F#, Fortran, Go, Haskell, HTML, Java, JavaScript, JSON, Kotlin, Lisp (Common, Scheme/Racket, Clojure, Emacs), Lua, OCaml, Pascal, Perl, PHP, PowerShell, Properties, Python, Ruby, Regex, Rust, Scala, SQL, Swift, TypeScript, Visual Basic, XML, YAML, Zig
- **Conversation Management**: Tabbed conversations and conversation history.
- **MCP Tool Calling**: Connect AI models to tools via the [Model Context Protocol](https://modelcontextprotocol.io/). Bundled first-party servers provide **web search** (Tavily), **GitHub** (over HTTP), and **file**, **git**, and **shell command** access scoped to a per-conversation working folder. Custom MCP servers can be added via `mcp.json`.
- **Tool Approval & Sandboxing**: Every tool call is gated by an in-app approval prompt showing the exact tool and arguments before anything runs; destructive operations (delete, shell commands, `git push`) always confirm. Approvals can be remembered per-tool, per-command, or per-directory for the session. File/git/command tools are confined to the working folder you choose.
- **File Attachments**: Add text file attachments to your messages to avoid cluttering up the conversation with long pasted text.
- **Conversation Editing**: Don't like the response a model gave you? Go back and edit your message and get a new response.
- **Data Stored Locally**: Conversations are stored locally, but may be exported and imported to migrate data across machines.
- **Settings and Customization**: Customize settings with Visual settings UI or built-in JSON editor. 
- **Frontier Model Support**: Support for a huge range of AI models, including frontier models, from the OpenRouter.ai API. 
- **Legacy Compatibility**: Runs on Windows XP and .NET 3.5.

## Getting Started

1. **Requirements**
   - Windows XP or later (XP optimized)
   - .NET Framework 3.5

2. **Building & Running**
   - Open the solution in Visual Studio 2008 or later.
   - Build the solution; required libraries are included. This also builds the bundled MCP servers (file/git/command/web), which are deployed alongside `GxPT.exe`.
   - Build the setup project.
   - Run `GxPT.exe`, or install via `GxPT.Setup.msi`. 

3. **Configuration**
   - Launch the app and open the settings window to configure your API key and preferences.
   - See the in-app help page for instructions on obtaining and entering an OpenRouter API key.
   - To enable tools, open the **MCP** tab in settings: toggle the built-in servers, paste a Tavily key (web search) or GitHub PAT, and edit `mcp.json` for custom servers. File, git, and command tools activate once you set a working folder for a conversation.