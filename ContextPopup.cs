using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GeminiQuota
{
    internal class OverlayForm : Form
    {
        protected float scale = 1;

        protected OverlayForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
        }

        protected int Px(float n) { return (int)Math.Round(n * scale); }
        protected Font FontAt(int px, FontStyle style)
        {
            return new Font("Microsoft YaHei UI", Px(px), style, GraphicsUnit.Pixel);
        }

        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get
            {
                var p = base.CreateParams;
                // No activation, no taskbar entry, and hit testing passes to Antigravity.
                p.ExStyle |= 0x08000000 | 0x00000080 | 0x00000020 | 0x00080000;
                return p;
            }
        }
        [DllImport("user32.dll")]
        private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            SetLayeredWindowAttributes(Handle, 0, 255, 2);
        }
    }

    internal sealed class ContextPopup : OverlayForm
    {
        private const string Heading = "对话上下文 · 上次模型请求";
        private const TextFormatFlags BodyFlags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix |
            TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis;
        private string body;
        private int titleHeight, maxWidth;
        private Size measured;
        private ContextReading lastReading;
        private bool dark;

        internal void Present(QuotaBar bar, Rectangle target, Rectangle area, ContextReading reading, float dpi, bool night)
        {
            // Reformat only for a fresh sample. Exact counts can change while the rounded
            // percentage and the geometry of the bar both stay the same.
            string nextBody = body == null || lastReading != reading ? ContextReading.Describe(reading) : body;
            lastReading = reading;
            if (body != nextBody || scale != dpi || maxWidth != area.Width)
            {
                body = nextBody;
                scale = dpi;
                maxWidth = area.Width;
                Text = AccessibleName = Heading + "\n" + body;
                int pad = Px(12), limit = Math.Max(1, Math.Min(Px(400), maxWidth) - 2 * pad);
                using (var g = CreateGraphics())
                using (var title = FontAt(13, FontStyle.Bold))
                using (var detail = FontAt(13, FontStyle.Regular))
                {
                    Size heading = TextRenderer.MeasureText(g, Heading, title, new Size(limit, Int32.MaxValue), BodyFlags);
                    Size content = TextRenderer.MeasureText(g, body, detail, new Size(limit, Int32.MaxValue), BodyFlags);
                    titleHeight = heading.Height;
                    measured = new Size(2 * pad + Math.Min(limit, Math.Max(heading.Width, content.Width)),
                        2 * pad + heading.Height + Px(6) + content.Height);
                }
                Invalidate();
            }
            if (dark != night) { dark = night; Invalidate(); }
            int x = Math.Max(area.Left, Math.Min(target.Right - measured.Width, area.Right - measured.Width));
            int y = bar.Top - Px(5) - measured.Height;
            if (y < area.Top) y = bar.Bottom + Px(5);
            if (y + measured.Height > area.Bottom) { Hide(); return; }
            var bounds = new Rectangle(new Point(x, y), measured);
            if (Bounds != bounds) Bounds = bounds;
            if (!Visible) Show(bar);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(dark ? Color.FromArgb(39, 43, 43) : Color.FromArgb(250, 252, 251));
            int pad = Px(12), width = ClientSize.Width - 2 * pad, y = pad + titleHeight + Px(6);
            Color ink = dark ? Color.FromArgb(233, 238, 235) : Color.FromArgb(45, 54, 51);
            using (var edge = new Pen(dark ? Color.FromArgb(75, 85, 79) : Color.FromArgb(196, 208, 200)))
            using (var title = FontAt(13, FontStyle.Bold))
            using (var detail = FontAt(13, FontStyle.Regular))
            {
                g.DrawRectangle(edge, 0, 0, Width - 1, Height - 1);
                TextRenderer.DrawText(g, Heading, title, new Rectangle(pad, pad, width, titleHeight), ink, BodyFlags);
                TextRenderer.DrawText(g, body, detail, new Rectangle(pad, y, width, Height - y - pad), ink, BodyFlags);
            }
        }
    }
}
