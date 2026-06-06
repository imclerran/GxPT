using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace GxPT
{
    // A ToolStripProfessionalRenderer that guards against a long-standing GDI+ failure mode
    // when painting menu/toolbar items.
    //
    // When a ToolStripItem that has an Image is disabled, the default renderer calls
    // ToolStripRenderer.CreateDisabledImage(image) on every paint to produce the grayed-out
    // version. That helper allocates a fresh GDI+ bitmap and runs Graphics.DrawImage with a
    // grayscale color matrix. On resource-constrained machines (e.g. a small VM) GDI+ can
    // fail that allocation and surface it as a misleading System.OutOfMemoryException -- GDI+
    // maps several internal resource failures, including handle/resource exhaustion, onto the
    // "Out of memory" status, which is why it fires even when the machine has plenty of free
    // RAM. Because the failure happens inside the paint loop it escapes as an unhandled
    // exception and tears down (or visibly corrupts, e.g. a blank box with a red X) the menu.
    //
    // This renderer makes disabled-image rendering resilient in two ways:
    //   1. It caches the disabled image per source image, so the grayed bitmap is created at
    //      most once instead of on every paint.
    //   2. If creation throws, it degrades gracefully -- caching the failure and drawing no
    //      icon -- rather than letting the exception escape the paint loop.
    internal sealed class SafeToolStripRenderer : ToolStripProfessionalRenderer
    {
        // Maps a source image to its cached disabled version. A null value means disabled-image
        // creation failed for that source and should not be retried on subsequent paints.
        private readonly Dictionary<Image, Image> _disabledImageCache = new Dictionary<Image, Image>();

        protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
        {
            try
            {
                if (e != null && e.Item != null && !e.Item.Enabled && e.Image != null)
                {
                    Image disabled = GetCachedDisabledImage(e.Image);
                    // Draw the grayed icon when available; if it could not be made, draw no
                    // icon at all rather than an out-of-place full-color image on a disabled item.
                    if (disabled != null)
                        e.Graphics.DrawImage(disabled, e.ImageRectangle);
                    return;
                }
            }
            catch
            {
                // Never let an image-render failure tear down the surrounding menu paint.
                return;
            }

            base.OnRenderItemImage(e);
        }

        private Image GetCachedDisabledImage(Image normalImage)
        {
            Image disabled;
            if (_disabledImageCache.TryGetValue(normalImage, out disabled))
                return disabled;

            try
            {
                disabled = CreateDisabledImage(normalImage);
            }
            catch
            {
                disabled = null;
            }

            // Cache even a null result so a failing creation is not retried on every paint.
            _disabledImageCache[normalImage] = disabled;
            return disabled;
        }
    }
}
