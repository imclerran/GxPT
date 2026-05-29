using System;

namespace GxPT
{
    // Resolves the transcript layout widths from settings in one place:
    //   - MaxContentWidth: the centered content area width in pixels (clamped 300..1900).
    //   - BubbleWidthPercent: the message bubble width as a percent of the content area (50..100).
    // Also performs a one-time migration of the legacy pixel-based "message_max_width" value
    // to a percentage, persisting the normalized value back to settings.
    internal struct TranscriptWidthSettings
    {
        public int MaxContentWidth;
        public int BubbleWidthPercent;

        public static TranscriptWidthSettings Resolve()
        {
            int tw = (int)AppSettings.GetDouble("transcript_max_width", 1000);
            if (tw <= 0) tw = 1000;
            if (tw < 300) tw = 300;
            if (tw > 1900) tw = 1900;

            int rawMp = (int)AppSettings.GetDouble("message_max_width", 90);
            int mp = rawMp;
            if (mp < 50 || mp > 100)
            {
                // Legacy value stored as pixels: convert to a percent of the content width.
                int computed = (rawMp <= 0) ? 90 : (int)Math.Round(100.0 * rawMp / Math.Max(1, tw));
                if (computed < 50) computed = 50;
                if (computed > 100) computed = 100;
                mp = computed;
                try { AppSettings.SetInt("message_max_width", mp); }
                catch { }
            }
            if (mp < 50) mp = 50;
            if (mp > 100) mp = 100;

            TranscriptWidthSettings r;
            r.MaxContentWidth = tw;
            r.BubbleWidthPercent = mp;
            return r;
        }

        // Apply these widths to a transcript control (each setter guarded individually).
        public void ApplyTo(ChatTranscriptControl transcript)
        {
            if (transcript == null) return;
            try { transcript.MaxContentWidth = MaxContentWidth; }
            catch { }
            try { transcript.BubbleWidthPercent = BubbleWidthPercent; }
            catch { }
        }
    }
}
