namespace GxPT
{
    partial class ChatTranscriptControl
    {
        /// <summary> 
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary> 
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            // TEMP shutdown instrumentation: only accounts for disposals during the close window.
            bool __diag = disposing && ShutdownDiag.Running;
            long __t = __diag ? ShutdownDiag.Now : 0;
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            try { SyntaxHighlightingRenderer.SegmentsReady -= OnSegmentsReady; }
            catch { }
            base.Dispose(disposing);
            if (__diag)
            {
                ShutdownDiag.TranscriptCount++;
                ShutdownDiag.TranscriptTotalMs += (ShutdownDiag.Now - __t);
            }
        }

        #region Component Designer generated code

        /// <summary> 
        /// Required method for Designer support - do not modify 
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            components = new System.ComponentModel.Container();
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
        }

        #endregion
    }
}
