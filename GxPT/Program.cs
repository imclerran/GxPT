using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace GxPT
{
    static class Program
    {
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // Install global hover-to-scroll router (keeps focus where it is)
            try { HoverWheelRouter.Install(); }
            catch { }
            Application.Run(new MainForm());
        }
    }
}
