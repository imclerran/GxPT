using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using XpChat;

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
            chatControl.AddMessage(MessageRole.User, "Hello! Can you show me what markdown features you support?");

            // Add an assistant message demonstrating various markdown features
            string assistantMessage = @"# Markdown Demo

Hello! I support various **markdown features**:

## Text Formatting
- **Bold text** using **double asterisks**
- *Italic text* using *single asterisks*
- `Inline code` using backticks
- ***Bold and italic*** combined

## Lists

### Bullet Lists:
- First bullet item
  - Nested bullet (hollow circle)
    - Deeply nested (square)
- Second bullet item
- Third bullet item

### Numbered Lists:
1. First numbered item
2. Second numbered item
  1. Nested numbered item
  2. Another nested number
3. Third numbered item

## Code Blocks
Here's a code example:

```cs
public void HelloWorld()
{
    Console.WriteLine(""Hello, World!"");
    return;
}
```

## Headings
# Heading 1
## Heading 2  
### Heading 3
#### Heading 4
##### Heading 5
###### Heading 6

That covers the main features I support!";

            chatControl.AddMessage(MessageRole.Assistant, assistantMessage);
        }
    }
}
