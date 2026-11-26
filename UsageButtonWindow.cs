using System;
using System.Drawing;
using System.Windows.Forms;

namespace GeminiQuota
{
    // The strip remains mouse-transparent across process boundaries. Only this
    // button-sized, owned window receives clicks above the Antigravity composer.
    internal sealed class UsageButtonWindow : OverlayForm
    {
        private readonly Button button = new EntryButton();
        private Font buttonFont;
        private bool dark;

        private sealed class EntryButton : Button
        {
            protected override void WndProc(ref Message message)
            {
                if (message.Msg != 0x0201) { base.WndProc(ref message); return; }
                // A WinForms Button normally focuses itself on left-down, even
                // inside a no-activate owner. Keep focus on the composer until click.
                SetStyle(ControlStyles.Selectable, false);
                try { base.WndProc(ref message); }
                finally { SetStyle(ControlStyles.Selectable, true); }
            }
        }

        internal UsageButtonWindow()
        {
            Text = "用量看板";
            button.Text = "用量";
            button.AccessibleName = "打开用量看板";
            button.Dock = DockStyle.Fill;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.UseVisualStyleBackColor = false;
            button.Cursor = Cursors.Hand;
            button.TabStop = false;
            Controls.Add(button);
        }

        internal event EventHandler UsageRequested
        {
            add { button.Click += value; }
            remove { button.Click -= value; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var p = base.CreateParams;
                p.ExStyle &= ~0x00000020; // Clear WS_EX_TRANSPARENT for this window only.
                return p;
            }
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0021) // WM_MOUSEACTIVATE
            {
                message.Result = new IntPtr(3); // MA_NOACTIVATE: deliver the click.
                return;
            }
            base.WndProc(ref message);
        }

        internal void Present(QuotaBar bar, Rectangle target, float dpi, bool night)
        {
            if (buttonFont == null || scale != dpi || dark != night)
            {
                scale = dpi;
                dark = night;
                var old = buttonFont;
                button.Font = buttonFont = FontAt(12, FontStyle.Regular);
                if (old != null) old.Dispose();
                button.ForeColor = night ? Color.FromArgb(183, 223, 205) : Color.FromArgb(35, 94, 69);
                button.BackColor = night ? Color.FromArgb(52, 66, 59) : Color.FromArgb(231, 242, 235);
                button.FlatAppearance.MouseOverBackColor = night ? Color.FromArgb(65, 84, 74) : Color.FromArgb(213, 234, 221);
                button.FlatAppearance.MouseDownBackColor = night ? Color.FromArgb(75, 98, 86) : Color.FromArgb(193, 222, 206);
            }
            if (Bounds != target) Bounds = target;
            if (!Visible) Show(bar);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && buttonFont != null) buttonFont.Dispose();
        }
    }
}
