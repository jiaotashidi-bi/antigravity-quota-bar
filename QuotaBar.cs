using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Microsoft.Win32;

namespace GeminiQuota
{
    internal sealed class QuotaBar : Form
    {
        private IntPtr owner;
        private float scale = 1;
        private readonly List<Row> rows = new List<Row>();
        private string note = "", layoutKey;
        private readonly List<Band> bands = new List<Band>();
        private int measuredHeight, maxWidth;
        private long renderedSecond;
        private QuotaSnapshot lastData;
        private ContextReading lastContext;
        private string contextValue = "—";
        private bool dark;
        private DateTime nextThemeCheck;

        public QuotaBar()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.None;
            Text = "Gemini 额度";
            DoubleBuffered = true;
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get
            {
                var p = base.CreateParams;
                // Layered + transparent gives mouse input to the application underneath.
                p.ExStyle |= 0x08000000 | 0x00000080 | 0x00000020 | 0x00080000;
                return p;
            }
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            SetLayeredWindowAttributes(Handle, 0, 255, 2);
        }
        private sealed class Cell
        {
            public bool Provided;
            public string Percent, Reset;

            public Cell(QuotaBucket bucket, DateTime now)
            {
                Provided = bucket != null;
                Percent = QuotaSnapshot.Display(bucket, now);
                Reset = bucket == null ? "" : QuotaSnapshot.ResetIn(bucket, now);
            }
        }
        private sealed class Row
        {
            public string Name, Label;
            public Cell Weekly, FiveHour;
        }

        private static string ShortName(string name)
        {
            if (String.Equals(name, "Gemini Models", StringComparison.OrdinalIgnoreCase)) return "Gemini";
            if (String.Equals(name, "Claude and GPT models", StringComparison.OrdinalIgnoreCase)) return "Claude / GPT";
            return name;
        }

        private static GraphicsPath Rounded(float x, float y, float w, float h, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Min(Math.Min(w, h), 2 * radius);
            p.AddArc(x, y, d, d, 180, 90);
            p.AddArc(x + w - d, y, d, d, 270, 90);
            p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            p.AddArc(x, y + h - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (Width < 2 || Height < 2) return;
            using (var p = Rounded(0, 0, Width, Height, Px(9)))
            {
                var old = Region;
                Region = new Region(p);
                if (old != null) old.Dispose();
            }
        }

        private static readonly TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix |
            TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
        private int Px(float n) { return (int)Math.Round(n * scale); }
        private Font FontAt(int px, FontStyle style)
        {
            return new Font("Microsoft YaHei UI", Px(px), style, GraphicsUnit.Pixel);
        }
        private static string CellDescription(Cell c)
        {
            if (!c.Provided) return "—";
            string reset = c.Reset == "—" ? "时间未提供" : c.Reset;
            return c.Percent + "（重置：" + reset + "）";
        }

        // Derive the countdown from the absolute deadline locally. Only remeasure/repaint
        // when the text changes, the composer resizes or the DPI changes.
        private bool UpdateLayout(QuotaSnapshot data, ContextReading context, DateTime now, string status, int available, float dpi)
        {
            long second = now.Ticks / TimeSpan.TicksPerSecond;
            bool geometryChanged = dpi != scale || maxWidth != available;
            if (!geometryChanged && lastData == data && lastContext == context && renderedSecond == second && status == note) return false;
            maxWidth = available;
            scale = dpi;
            note = status;
            renderedSecond = second;
            lastData = data;
            lastContext = context;
            contextValue = "—";
            if (context != null && context.Limit > 0)
            {
                long remaining = Math.Max(0L, context.Limit - context.Used);
                double percent = remaining * 100.0 / context.Limit;
                contextValue = percent > 0 && percent < 1 ? "<1%" : "≈" + Math.Round(percent, MidpointRounding.AwayFromZero) + "%";
            }
            rows.Clear();
            var title = new StringBuilder("模型额度");
            if (data != null)
            {
                foreach (var group in data.Groups)
                {
                    var row = new Row { Name = group.Name, Label = ShortName(group.Name),
                        Weekly = new Cell(group.Weekly, now), FiveHour = new Cell(group.FiveHour, now) };
                    rows.Add(row);
                    title.Append(" · ").Append(row.Name).Append(" 本周剩余 ").Append(CellDescription(row.Weekly))
                        .Append("，5 小时剩余 ").Append(CellDescription(row.FiveHour));
                }
                string description = "更新于 " + data.SeenUtc.ToLocalTime().ToString("HH:mm:ss");
                if (AccessibleDescription != description) AccessibleDescription = description;
            }
            if (note.Length > 0) title.Append(" · ").Append(note);
            title.Append(" · 上下文剩余（最近一次模型请求）").Append(contextValue);
            string key = title.ToString();
            if (!geometryChanged && layoutKey == key) return false;
            layoutKey = key;
            Text = AccessibleName = key;
            using (var small = FontAt(11, FontStyle.Regular))
            using (var label = FontAt(12, FontStyle.Regular))
            using (var value = FontAt(12, FontStyle.Bold))
            using (var canvas = CreateGraphics())
            {
                int inner = available - 2 * Px(10);
                var blocks = BuildBlocks(canvas, label, value, small, false);
                if (TotalWidth(blocks) > inner)
                    blocks = BuildBlocks(canvas, label, value, small, true);
                bands.Clear();
                foreach (var block in blocks)
                    Wrap(block, inner);
                measuredHeight = 2 * Px(4) + Math.Max(1, bands.Count) * Px(20);
            }
            return true;
        }

        private enum Role { Name, Period, Percent, Reset, Note, Context }
        private sealed class Run
        {
            public string Text;
            public Role Role;
            public int Width, Gap;
            public bool StartsWindow, Clock;
        }
        private sealed class Block
        {
            public readonly List<Run> Runs = new List<Run>();
            public int Width;
            public void Add(Run run)
            {
                Width += (Runs.Count == 0 ? 0 : run.Gap) + run.Width;
                Runs.Add(run);
            }
        }
        private sealed class Band
        {
            public readonly List<Block> Blocks = new List<Block>();
            public int Width;
        }
        private Run MakeRun(Graphics canvas, string text, Role role, Font font, int gap, bool clock = false)
        {
            // Measure on a drawing context so GDI honors the same NoPadding flag as DrawText.
            int width = TextRenderer.MeasureText(canvas, text, font, new Size(8192, 256), Flags).Width;
            return new Run { Text = text, Role = role, Gap = Px(gap), Clock = clock,
                Width = width + (clock ? Px(12) : 0) };
        }
        private List<Block> BuildBlocks(Graphics canvas, Font name, Font number, Font small, bool compact)
        {
            var blocks = new List<Block>();
            foreach (var row in rows)
            {
                var block = new Block();
                string title = row.Label;
                if (compact && title == "Claude / GPT") title = "Claude/GPT";
                if (compact && title == "Gemini 4 Argon") title = "Argon";
                block.Add(MakeRun(canvas, title, Role.Name, name, 0));
                AddWindow(canvas, block, "周", row.Weekly, small, number, compact);
                AddWindow(canvas, block, "5h", row.FiveHour, small, number, compact);
                blocks.Add(block);
            }
            if (note.Length != 0 || blocks.Count == 0)
            {
                var status = new Block();
                status.Add(MakeRun(canvas, blocks.Count == 0 ? "模型额度  " + note : note, Role.Note, small, 0));
                blocks.Add(status);
            }
            var context = new Block();
            context.Add(MakeRun(canvas, compact ? "上下文余" : "上下文剩余", Role.Name, name, 0));
            context.Add(MakeRun(canvas, contextValue, Role.Context, number, compact ? 3 : 5));
            blocks.Add(context);
            return blocks;
        }
        private void AddWindow(Graphics canvas, Block block, string period, Cell cell, Font small, Font number, bool compact)
        {
            if (!cell.Provided) return;
            var start = MakeRun(canvas, period, Role.Period, small, compact ? 6 : 9);
            start.StartsWindow = true;
            block.Add(start);
            block.Add(MakeRun(canvas, cell.Percent, Role.Percent, number, 3));
            string reset = cell.Reset.Replace("不足1分", "<1m").Replace("天", "d").Replace("小时", "h").Replace("分", "m");
            block.Add(MakeRun(canvas, reset, Role.Reset, small, compact ? 3 : 4, reset != "—"));
        }
        private int TotalWidth(List<Block> blocks)
        {
            int total = Math.Max(0, blocks.Count - 1) * Px(14);
            foreach (var block in blocks) total += block.Width;
            return total;
        }

        // Keep a model and its windows together if that group fits at all. At very
        // narrow widths, wrap on window boundaries before splitting individual runs.
        private void Wrap(Block block, int inner)
        {
            if (block.Width <= inner) { PlaceBlock(block, inner); return; }
            var part = new Block();
            for (int i = 0; i < block.Runs.Count; i++)
            {
                Run run = block.Runs[i];
                if (run.StartsWindow && part.Runs.Count > 0)
                {
                    int windowWidth = run.Width;
                    for (int j = i + 1; j < block.Runs.Count && !block.Runs[j].StartsWindow; j++)
                        windowWidth += block.Runs[j].Gap + block.Runs[j].Width;
                    if (part.Width + run.Gap + windowWidth > inner)
                    {
                        PlaceBlock(part, inner);
                        part = new Block();
                    }
                }
                if (part.Runs.Count > 0 && part.Width + run.Gap + run.Width > inner)
                {
                    PlaceBlock(part, inner);
                    part = new Block();
                }
                run.Width = Math.Min(run.Width, inner);
                part.Add(run);
            }
            if (part.Runs.Count > 0) PlaceBlock(part, inner);
        }
        private void PlaceBlock(Block block, int inner)
        {
            Band band = bands.Count == 0 ? null : bands[bands.Count - 1];
            if (band == null || band.Width + Px(14) + block.Width > inner)
            {
                band = new Band();
                bands.Add(band);
            }
            band.Width += (band.Blocks.Count == 0 ? 0 : Px(14)) + block.Width;
            band.Blocks.Add(block);
        }

        public void Present(IntPtr host, Rectangle composer, QuotaSnapshot data, bool failed, bool busy, ContextReading context = null)
        {
            var now = DateTime.UtcNow;
            bool old = data != null && (failed || (now - data.SeenUtc).TotalMinutes >= 3);
            string status = old ? "上次更新 " + data.SeenUtc.ToLocalTime().ToString("HH:mm") :
                data == null ? (busy ? "读取中" : "暂未获取") : "";
            float dpi = Native.Scale(host);
            var area = Rectangle.Intersect(Native.Bounds(host), Screen.FromRectangle(composer).WorkingArea);
            var span = Rectangle.Intersect(composer, area);
            int available = span.Width;
            if (span.IsEmpty || available < (int)(110 * dpi)) { Hide(); return; }
            bool dirty = UpdateLayout(data, context, now, status, available, dpi);
            if (now >= nextThemeCheck)
            {
                object v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1);
                bool nextDark = v is int && (int)v == 0;
                dirty |= nextDark != dark;
                dark = nextDark;
                nextThemeCheck = now.AddSeconds(10);
            }
            int top = composer.Top - Px(5) - measuredHeight;
            if (top < area.Top) { Hide(); return; }
            var target = new Rectangle(span.Left, top, available, measuredHeight);
            if (owner != host) { Native.SetOwner(Handle, host); owner = host; }
            if (Bounds != target || !Visible) Native.Place(Handle, target);
            if (!Visible) Show();
            if (dirty) Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color bg = dark ? Color.FromArgb(39, 43, 43) : Color.FromArgb(250, 252, 251);
            Color ink = dark ? Color.FromArgb(233, 238, 235) : Color.FromArgb(45, 54, 51);
            Color muted = dark ? Color.FromArgb(179, 191, 183) : Color.FromArgb(92, 103, 97);
            Color edge = dark ? Color.FromArgb(75, 85, 79) : Color.FromArgb(211, 222, 215);
            g.Clear(bg);
            using (var border = new Pen(edge, Math.Max(1f, scale)))
            using (var shape = Rounded(scale, scale, Width - 2 * scale, Height - 2 * scale, Px(9)))
            using (var small = FontAt(11, FontStyle.Regular))
            using (var name = FontAt(12, FontStyle.Regular))
            using (var number = FontAt(12, FontStyle.Bold))
            {
                g.DrawPath(border, shape);
                int padding = Px(10), inner = Width - 2 * padding;
                int y = (Height - bands.Count * Px(20)) / 2;
                foreach (var band in bands)
                {
                    int x = padding;
                    // Distribute free space between the groups while keeping the first
                    // and last aligned with the composer's inner margins.
                    int gaps = band.Blocks.Count - 1;
                    int extra = Math.Max(0, inner - band.Width);
                    for (int i = 0; i < band.Blocks.Count; i++)
                    {
                        if (i > 0)
                        {
                            int space = Px(14) + (extra * i / gaps - extra * (i - 1) / gaps);
                            int divider = x + space / 2;
                            g.DrawLine(border, divider, y + Px(4), divider, y + Px(16));
                            x += space;
                        }
                        var block = band.Blocks[i];
                        for (int j = 0; j < block.Runs.Count; j++)
                        {
                            var run = block.Runs[j];
                            if (j > 0) x += run.Gap;
                            Font face = run.Role == Role.Percent || run.Role == Role.Context ? number : run.Role == Role.Name ? name : small;
                            Color color = run.Role == Role.Percent ? AmountColor(run.Text, muted) :
                                run.Role == Role.Context ? (dark ? Color.FromArgb(159, 203, 232) : Color.FromArgb(54, 101, 135)) :
                                run.Role == Role.Name ? ink : muted;
                            int textX = x;
                            if (run.Clock)
                            {
                                DrawClock(g, x, y + Px(10), muted);
                                textX += Px(12);
                            }
                            if (textX < x + run.Width)
                                TextRenderer.DrawText(g, run.Text, face, new Rectangle(textX, y, x + run.Width - textX, Px(20)), color, Flags);
                            x += run.Width;
                        }
                    }
                    y += Px(20);
                }
            }
        }

        private Color AmountColor(string text, Color muted)
        {
            int percent;
            if (!int.TryParse(text.TrimEnd('%'), out percent)) return muted;
            if (percent <= 10) return dark ? Color.LightSalmon : Color.FromArgb(163, 62, 43);
            if (percent <= 25) return dark ? Color.Khaki : Color.FromArgb(142, 96, 24);
            return dark ? Color.FromArgb(152, 217, 174) : Color.FromArgb(42, 112, 76);
        }
        private void DrawClock(Graphics g, int x, int cy, Color color)
        {
            float radius = 3.6f * scale, cx = x + 4.5f * scale;
            using (var stroke = new Pen(color, Math.Max(1f, scale * 0.85f)))
            {
                g.DrawEllipse(stroke, cx - radius, cy - radius, 2 * radius, 2 * radius);
                g.DrawLine(stroke, cx, cy - radius * 0.62f, cx, cy);
                g.DrawLine(stroke, cx, cy, cx + radius * 0.55f, cy + radius * 0.25f);
            }
        }
    }
}
