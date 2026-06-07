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
            // Handle shell-open: if launched with a .gxpt/.gxcv file, import it on startup
            string fileArg = null;
            try
            {
                var args = Environment.GetCommandLineArgs();
                if (args != null && args.Length > 1)
                {
                    // Prefer the first existing file with a supported extension
                    for (int i = 1; i < args.Length; i++)
                    {
                        var a = args[i];
                        if (a == null) continue;
                        if (a.Length == 0 || a.Trim().Length == 0) continue;
                        try
                        {
                            string p = a.Trim().Trim('"');
                            if (System.IO.File.Exists(p))
                            {
                                string ext = System.IO.Path.GetExtension(p);
                                if (ext != null) ext = ext.ToLowerInvariant();
                                if (ext == ".gxpt" || ext == ".gxcv" || ext == ".zip") { fileArg = p; break; }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }

            var mainForm = new MainForm();
            if (!string.IsNullOrEmpty(fileArg))
            {
                // Defer to after the form is shown so dialogs are parented correctly
                mainForm.Shown += (s, e) =>
                {
                    try { mainForm.ImportArchiveFromShell(fileArg); }
                    catch { }
                };
            }
            // TEMP shutdown instrumentation: capture the tail past Dispose into CLR/process exit.
            AppDomain.CurrentDomain.ProcessExit += delegate
            {
                if (!ShutdownDiag.Enabled) return;
                ShutdownDiag.Mark("ProcessExit (finalizers/CLR shutdown begin)");
                ShutdownDiag.Flush();
            };

            Application.Run(mainForm);

            // Everything below is gated on the diagnostic being active, so normal (logging-off)
            // shutdowns are completely unaffected (no forced GC, no extra work).
            if (ShutdownDiag.Enabled)
            {
                // The message loop has exited and the form is disposed by here.
                ShutdownDiag.Mark("Application.Run returned");

                // Deterministic probe: drop the form root and force a full GC + finalizer pass so we
                // can MEASURE finalizer/native-handle cleanup cost here (reliably, while we can still
                // log) instead of inferring it from the unpredictable CLR-shutdown finalization tail.
                long __g = ShutdownDiag.Now;
                mainForm = null;
                System.GC.Collect();
                System.GC.WaitForPendingFinalizers();
                System.GC.Collect();
                ShutdownDiag.Mark("forced GC + finalizers done (+" + (ShutdownDiag.Now - __g) + "ms)");
                ShutdownDiag.Flush();
            }
        }
    }
}
