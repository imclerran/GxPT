using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace GxPT
{
    // Folder-selection dialog. On Windows Vista and later it shows the modern
    // Explorer-style picker (grid view, address bar, double-click navigation) by
    // reusing the .NET Framework's OWN Vista file-dialog COM interop (the internal
    // System.Windows.Forms.FileDialogNative types that WinForms uses for its Vista
    // file dialogs) and flipping on the "pick folders" option via reflection. This
    // avoids hand-rolled COM interface declarations entirely. On Windows XP (which
    // has no native folder picker) it coerces a standard Explorer-style
    // OpenFileDialog into returning a folder, preserving the grid/double-click feel.
    // If either modern path fails, it falls back to the classic FolderBrowserDialog
    // tree. XP / .NET 3.5 compatible.
    internal static class FolderPicker
    {
        // FILEOPENDIALOGOPTIONS we OR into the dialog's existing options.
        private const uint FOS_PICKFOLDERS = 0x00000020;
        private const uint FOS_FORCEFILESYSTEM = 0x00000040;
        // SIGDN_FILESYSPATH: ask the shell item for its full filesystem path.
        private const uint SIGDN_FILESYSPATH = 0x80058000;

        private const BindingFlags InstanceNonPublic = BindingFlags.Instance | BindingFlags.NonPublic;

        // Picks a folder. Returns true and sets selectedPath on OK; false on cancel.
        public static bool TrySelectFolder(IWin32Window owner, string initialDir, string title, out string selectedPath)
        {
            // The Vista dialog only exists on NT 6.0 (Vista) or higher.
            if (Environment.OSVersion.Platform == PlatformID.Win32NT
                && Environment.OSVersion.Version.Major >= 6)
            {
                try
                {
                    return TryVistaDialog(owner, initialDir, title, out selectedPath);
                }
                catch (Exception ex)
                {
                    // Surface the real cause, then degrade to the classic tree dialog.
                    Logger.Log("FolderPicker", "Vista folder dialog failed, using FolderBrowserDialog: " + ex);
                }
                return TryClassicDialog(owner, initialDir, title, out selectedPath);
            }

            // Pre-Vista (Windows XP): there is no native folder picker, but we can coerce a
            // standard Explorer-style OpenFileDialog into returning a folder, which gives the
            // grid/double-click experience. Cancel returns false (no tree fallback); only an
            // unexpected error degrades to the classic tree.
            try
            {
                return TryOpenFileDialogTrick(owner, initialDir, title, out selectedPath);
            }
            catch (Exception ex)
            {
                Logger.Log("FolderPicker", "OpenFileDialog folder trick failed, using FolderBrowserDialog: " + ex);
            }
            return TryClassicDialog(owner, initialDir, title, out selectedPath);
        }

        // Windows XP grid-style folder picker: a standard OpenFileDialog with the file list
        // hidden, coerced into returning a folder. The user navigates INTO the target folder
        // and clicks Open; we derive the folder from the (non-existent) placeholder file name.
        private static bool TryOpenFileDialogTrick(IWin32Window owner, string initialDir, string title, out string selectedPath)
        {
            selectedPath = null;
            using (OpenFileDialog ofd = new OpenFileDialog())
            {
                ofd.Title = string.IsNullOrEmpty(title) ? "Select Folder" : title;
                ofd.CheckFileExists = false; // the placeholder "file" never exists
                ofd.CheckPathExists = true;  // but the containing folder must
                ofd.ValidateNames = false;   // allow the placeholder name through
                ofd.Multiselect = false;
                ofd.AddExtension = false;
                // A filter that matches no real file hides the file list, leaving only folders.
                ofd.Filter = "Folders|*.__gxpt_select_folder__";
                ofd.FileName = "Select this folder";
                if (!string.IsNullOrEmpty(initialDir) && Directory.Exists(initialDir))
                    ofd.InitialDirectory = initialDir;

                if (ofd.ShowDialog(owner) != DialogResult.OK) return false;

                string dir = Path.GetDirectoryName(ofd.FileName);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
                selectedPath = dir;
                return true;
            }
        }

        // Modern Explorer-style folder picker, driven through WinForms' own COM interop.
        private static bool TryVistaDialog(IWin32Window owner, string initialDir, string title, out string selectedPath)
        {
            selectedPath = null;

            Assembly winforms = typeof(OpenFileDialog).Assembly;
            Type tIFileDialog = winforms.GetType("System.Windows.Forms.FileDialogNative+IFileDialog");
            Type tIShellItem = winforms.GetType("System.Windows.Forms.FileDialogNative+IShellItem");
            if (tIFileDialog == null || tIShellItem == null)
                throw new NotSupportedException("WinForms Vista file-dialog interop is not available on this runtime.");

            using (OpenFileDialog ofd = new OpenFileDialog())
            {
                ofd.AddExtension = false;
                ofd.CheckFileExists = false;
                ofd.DereferenceLinks = true;
                ofd.Multiselect = false;
                if (!string.IsNullOrEmpty(title)) ofd.Title = title;
                if (!string.IsNullOrEmpty(initialDir) && Directory.Exists(initialDir))
                    ofd.InitialDirectory = initialDir;

                // Build the native dialog the same way OpenFileDialog.RunDialog would.
                object dialog = InvokeFileDialogMethod(ofd, "CreateVistaDialog", null);
                InvokeFileDialogMethod(ofd, "OnBeforeVistaDialog", new object[] { dialog });

                // GetOptions returns a FileDialogNative.FOS enum; OR in folder-picker mode.
                object fosOptions = InvokeFileDialogMethod(ofd, "GetOptions", null);
                Type tFos = fosOptions.GetType();
                uint options = Convert.ToUInt32(fosOptions) | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM;
                tIFileDialog.GetMethod("SetOptions").Invoke(dialog, new object[] { Enum.ToObject(tFos, options) });

                IntPtr hwnd = (owner != null) ? owner.Handle : IntPtr.Zero;
                int hr = (int)tIFileDialog.GetMethod("Show").Invoke(dialog, new object[] { hwnd });
                if (hr != 0) return false; // non-zero HRESULT includes ERROR_CANCELLED (user cancelled)

                // GetResult(out IShellItem) -> GetDisplayName(SIGDN_FILESYSPATH, out string).
                object[] resultArgs = new object[] { null };
                tIFileDialog.GetMethod("GetResult").Invoke(dialog, resultArgs);
                object shellItem = resultArgs[0];
                if (shellItem == null) return false;

                object sigdn = Enum.ToObject(GetSigdnType(tIShellItem), SIGDN_FILESYSPATH);
                object[] nameArgs = new object[] { sigdn, null };
                tIShellItem.GetMethod("GetDisplayName").Invoke(shellItem, nameArgs);
                selectedPath = nameArgs[1] as string;
                return !string.IsNullOrEmpty(selectedPath);
            }
        }

        // CreateVistaDialog/OnBeforeVistaDialog live on OpenFileDialog; GetOptions on FileDialog.
        // GetMethod won't return a base type's non-public instance method, so probe both.
        private static object InvokeFileDialogMethod(OpenFileDialog ofd, string name, object[] args)
        {
            MethodInfo mi = typeof(OpenFileDialog).GetMethod(name, InstanceNonPublic)
                            ?? typeof(FileDialog).GetMethod(name, InstanceNonPublic);
            if (mi == null)
                throw new MissingMethodException("FileDialog." + name + " not found on this runtime.");
            return mi.Invoke(ofd, args);
        }

        // The first parameter of IShellItem.GetDisplayName is a FileDialogNative.SIGDN enum.
        private static Type GetSigdnType(Type tIShellItem)
        {
            ParameterInfo[] ps = tIShellItem.GetMethod("GetDisplayName").GetParameters();
            return ps[0].ParameterType;
        }

        // Classic tree-style folder browser. Used on XP and as the Vista-failure fallback.
        private static bool TryClassicDialog(IWin32Window owner, string initialDir, string title, out string selectedPath)
        {
            selectedPath = null;
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                if (!string.IsNullOrEmpty(title)) dlg.Description = title;
                if (!string.IsNullOrEmpty(initialDir)) dlg.SelectedPath = initialDir;
                if (dlg.ShowDialog(owner) != DialogResult.OK) return false;
                selectedPath = dlg.SelectedPath;
                return !string.IsNullOrEmpty(selectedPath);
            }
        }
    }
}
