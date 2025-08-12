using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using GxPT;

namespace GxPT
{
    public partial class Form1 : Form
    {
        private ChatTranscriptControl chatControl;

        public Form1()
        {
            InitializeComponent();
            InitializeChatControl();
            AddDemoMessages();
        }

        private void InitializeChatControl()
        {
            chatControl = new ChatTranscriptControl();
            chatControl.Dock = DockStyle.Fill;
            this.Controls.Add(chatControl);
        }

        private void AddDemoMessages()
        {
            // Add a user message
            chatControl.AddMessage(MessageRole.User, "Hello! Can you show me what markdown features you support, including syntax highlighting?");

            // Add an assistant message demonstrating various markdown features
            string assistantMessage = "# Markdown Demo with Syntax Highlighting\n\n" +
                "Hello! I support various **markdown features** including syntax highlighting:\n\n" +
                "## Text Formatting\n" +
                "- **Bold text** using **double asterisks**\n" +
                "- *Italic text* using *single asterisks*\n" +
                "- `Inline code` using backticks\n" +
                "- ***Bold and italic*** combined\n\n" +
                "## Code Blocks with Syntax Highlighting\n\n" +
                "### C# Example:\n" +
                "```cs\n" +
                "using System;\n\n" +
                "public class HelloWorld\n" +
                "{\n" +
                "    public static void Main(string[] args)\n" +
                "    {\n" +
                "        Console.WriteLine(\"Hello, World!\");\n" +
                "        int number = 42;\n" +
                "        bool isTrue = true;\n" +
                "        // This is a comment\n" +
                "        if (isTrue)\n" +
                "        {\n" +
                "            Console.WriteLine($\"The number is {number}\");\n" +
                "        }\n" +
                "    }\n" +
                "}\n" +
                "```\n\n" +
                "### JavaScript Example:\n" +
                "```js\n" +
                "// JavaScript with syntax highlighting\n" +
                "function greetUser(name) {\n" +
                "    const message = `Hello, ${name}!`;\n" +
                "    console.log(message);\n" +
                "    \n" +
                "    let numbers = [1, 2, 3, 4, 5];\n" +
                "    return numbers.map(n => n * 2);\n" +
                "}\n\n" +
                "const result = greetUser(\"World\");\n" +
                "```\n\n" +
                "### JSON Example:\n" +
                "```json\n" +
                "{\n" +
                "    \"name\": \"John Doe\",\n" +
                "    \"age\": 30,\n" +
                "    \"isActive\": true,\n" +
                "    \"hobbies\": [\"reading\", \"coding\", \"gaming\"]\n" +
                "}\n" +
                "```\n\n" +
                "### Python Example:\n" +
                "```python\n" +
                "# Python code with syntax highlighting\n" +
                "def fibonacci(n):\n" +
                "    if n <= 0:\n" +
                "        return []\n" +
                "    elif n == 1:\n" +
                "        return [0]\n" +
                "    \n" +
                "    sequence = [0, 1]\n" +
                "    while len(sequence) < n:\n" +
                "        next_val = sequence[-1] + sequence[-2]\n" +
                "        sequence.append(next_val)\n" +
                "    \n" +
                "    return sequence\n\n" +
                "result = fibonacci(10)\n" +
                "print('First 10 fibonacci numbers:', result)\n" +
                "```\n\n" +
                "That covers the main features including **syntax highlighting** for C#, JavaScript, JSON, and Python!";

            chatControl.AddMessage(MessageRole.Assistant, assistantMessage);

            // Add another message showing unsupported language (should fallback to normal text)
            chatControl.AddMessage(MessageRole.User, "What about other languages?");

            string fallbackMessage = "For unsupported languages, the code is displayed as plain text:\n\n" +
                "```rust\n" +
                "// Rust code (not highlighted - shows as plain text)\n" +
                "fn main() {\n" +
                "    println!(\"Hello, world!\");\n" +
                "    let x = 5;\n" +
                "    let y = 10;\n" +
                "    println!(\"x + y = {}\", x + y);\n" +
                "}\n" +
                "```\n\n" +
                "But the core languages (C#, JavaScript, JSON, Python) get full syntax highlighting!";

            chatControl.AddMessage(MessageRole.Assistant, fallbackMessage);
        }
    }
}
