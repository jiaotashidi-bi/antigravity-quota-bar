using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace GeminiQuota
{
    internal sealed class UsageDashboard : Form
    {
        private static readonly int[] Days = { 1, 7, 30, 0 };
        private readonly string directory;
        private readonly List<Font> fonts = new List<Font>();
        private readonly ToolTip tips = new ToolTip();
        private readonly TableLayoutPanel page = new TableLayoutPanel();
        private readonly RadioButton[] ranges = new RadioButton[Days.Length];
        private readonly Label[] figures = new Label[3], captions = new Label[3];
        private readonly Label period = new Label(), status = new Label(), empty = new Label(), context = new Label();
        private readonly DataGridView grid = new DataGridView();
        private readonly DailyTrend trend = new DailyTrend();
        private readonly Button refresh = new Button();
        private UsageReport report;
        private DateTime lastRead;
        private bool busy, readFailed, dark;
        private int selected = 1;

        internal UsageDashboard() : this(ContextReader.ConversationsDirectory) { }

        internal UsageDashboard(string directory)
        {
            this.directory = directory;
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = "Antigravity 用量看板";
            StartPosition = FormStartPosition.CenterScreen;
            Font = Face("Microsoft YaHei UI", 10, FontStyle.Regular);
            MinimumSize = new Size(580, 390);
            ClientSize = new Size(890, 540);
            SizeGripStyle = SizeGripStyle.Show;
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

            page.Dock = DockStyle.Fill;
            page.Padding = new Padding(22, 15, 22, 12);
            page.ColumnCount = 1;
            page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            page.RowCount = 7;
            foreach (int height in new[] { 46, 48, 85 })
                page.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            page.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
            page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(page);

            var header = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, ColumnCount = 2, TabStop = false };
            header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
            var heading = Caption("用量概览", Face("Microsoft YaHei UI", 18, FontStyle.Bold));
            header.Controls.Add(heading, 0, 0);
            refresh.Text = "刷新";
            refresh.AccessibleName = "刷新本地用量";
            refresh.Dock = DockStyle.Fill;
            refresh.Margin = new Padding(0, 6, 0, 8);
            refresh.FlatStyle = FlatStyle.Flat;
            refresh.Click += async delegate { await ReadReport(); };
            header.Controls.Add(refresh, 1, 0);
            page.Controls.Add(header, 0, 0);

            var filters = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, ColumnCount = 2 };
            filters.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            filters.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Fill, Margin = Padding.Empty };
            string[] names = { "今天", "近 7 天", "近 30 天", "全部" };
            for (int i = 0; i < names.Length; i++)
            {
                var button = new RadioButton { Appearance = Appearance.Button, AutoSize = false,
                    FlatStyle = FlatStyle.Flat, Size = new Size(i == 2 ? 77 : 65, 30), Text = names[i],
                    AccessibleName = "统计范围：" + names[i], TextAlign = ContentAlignment.MiddleCenter,
                    Margin = new Padding(0, 4, 4, 0), Tag = i, Checked = (i == selected) };
                button.FlatAppearance.BorderSize = 0;
                button.CheckedChanged += delegate(object sender, EventArgs e)
                {
                    var choice = (RadioButton)sender;
                    if (!choice.Checked) return;
                    selected = (int)choice.Tag;
                    StyleRangeButtons();
                    RenderReport();
                };
                ranges[i] = button;
                buttons.Controls.Add(button);
            }
            period.Dock = DockStyle.Fill;
            period.TextAlign = ContentAlignment.MiddleRight;
            period.AutoEllipsis = true;
            filters.Controls.Add(buttons, 0, 0);
            filters.Controls.Add(period, 1, 0);
            page.Controls.Add(filters, 0, 1);

            var metrics = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, Margin = Padding.Empty };
            metrics.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            string[] labels = { "模型请求", "总 Token", "缓存占输入" };
            var numberFont = Face("Segoe UI", 19, FontStyle.Bold);
            for (int i = 0; i < labels.Length; i++)
            {
                metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 21));
                var card = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, Padding = new Padding(12, 6, 7, 6),
                    Margin = new Padding(i == 0 ? 0 : 3, 0, 3, 0) };
                card.RowStyles.Add(new RowStyle(SizeType.Percent, 38));
                card.RowStyles.Add(new RowStyle(SizeType.Percent, 62));
                captions[i] = Caption(labels[i], Font);
                figures[i] = Caption("—", numberFont);
                card.Controls.Add(captions[i], 0, 0);
                card.Controls.Add(figures[i], 0, 1);
                metrics.Controls.Add(card, i, 0);
            }
            metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 37));
            trend.Dock = DockStyle.Fill;
            trend.Margin = new Padding(3, 0, 0, 0);
            metrics.Controls.Add(trend, 3, 0);
            page.Controls.Add(metrics, 0, 2);
            context.AutoSize = true;
            context.Dock = DockStyle.Fill;
            context.Margin = new Padding(0, 12, 0, 0);
            context.Visible = false;
            page.Controls.Add(context, 0, 3);
            page.Controls.Add(Caption("按模型统计", Face("Microsoft YaHei UI", 11, FontStyle.Bold)), 0, 4);

            var table = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            ConfigureGrid();
            table.Controls.Add(grid);
            empty.Dock = DockStyle.Fill;
            empty.Padding = new Padding(12);
            empty.TextAlign = ContentAlignment.MiddleCenter;
            table.Controls.Add(empty);
            page.Controls.Add(table, 0, 5);
            status.AutoSize = true;
            status.Dock = DockStyle.Fill;
            status.Margin = new Padding(0, 8, 0, 0);
            page.Controls.Add(status, 0, 6);

            ApplyColors();
            RenderReport();
            ResumeLayout(true);
        }

        private Font Face(string name, float size, FontStyle style)
        {
            var face = new Font(name, size, style, GraphicsUnit.Point);
            fonts.Add(face);
            return face;
        }

        internal void SetContext(ContextReading reading)
        {
            string details = "当前对话尚无窗口读数";
            if (reading != null && reading.Limit > 0)
            {
                long left = Math.Max(0L, reading.Limit - reading.Used);
                string model = String.IsNullOrWhiteSpace(reading.Model) ? "" : reading.Model + "  ·  ";
                details = model + String.Format("预计剩余 {0:N0} / {1:N0} tokens（{2:P1}）", left, reading.Limit, (double)left / reading.Limit);
            }
            context.Text = "打开看板时的对话上下文  ·  " + details;
            context.Visible = true;
        }

        private static Label Caption(string text, Font font)
        {
            return new Label { Text = text, Font = font, AutoEllipsis = true, Dock = DockStyle.Fill,
                Margin = Padding.Empty, TextAlign = ContentAlignment.MiddleLeft };
        }

        private void ConfigureGrid()
        {
            grid.Dock = DockStyle.Fill;
            grid.ReadOnly = true;
            grid.AccessibleName = "各模型的请求和 Token 用量";
            grid.AllowUserToAddRows = grid.AllowUserToDeleteRows = grid.AllowUserToResizeRows = false;
            grid.RowHeadersVisible = false;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCellsExceptHeaders;
            grid.BorderStyle = BorderStyle.None;
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            grid.EnableHeadersVisualStyles = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.MultiSelect = false;
            grid.DefaultCellStyle.Padding = new Padding(5, 4, 5, 4);
            grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(5, 4, 5, 4);
            string[] titles = { "模型", "请求", "未缓存输入", "缓存输入", "输出", "缓存占比" };
            int[] widths = { 195, 58, 94, 94, 82, 80 };
            for (int i = 0; i < titles.Length; i++)
            {
                var c = new DataGridViewTextBoxColumn { HeaderText = titles[i], FillWeight = widths[i],
                    MinimumWidth = i == 0 ? 120 : 58, SortMode = DataGridViewColumnSortMode.Automatic,
                    ValueType = i == 0 ? typeof(string) : i == 5 ? typeof(double) : typeof(long) };
                if (i > 0)
                {
                    c.DefaultCellStyle.Alignment = c.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleRight;
                    c.DefaultCellStyle.Format = i == 5 ? "P1" : "N0";
                    c.DefaultCellStyle.NullValue = "—";
                }
                grid.Columns.Add(c);
            }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            Rectangle work = Screen.FromHandle(Handle).WorkingArea;
            if (MinimumSize.Width > work.Width || MinimumSize.Height > work.Height)
                MinimumSize = new Size(Math.Min(MinimumSize.Width, work.Width), Math.Min(MinimumSize.Height, work.Height));
            Size = new Size(Math.Min(Width, work.Width), Math.Min(Height, work.Height));
            var screenBounds = new Rectangle(Location, Size);
            if (!work.Contains(screenBounds)) CenterToScreen();
        }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            await ReadReport();
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            ApplyColors();
        }

        private Color ColorFor(int light, int night) { return Color.FromArgb((dark ? night : light) | unchecked((int)0xff000000)); }
        private void ApplyColors()
        {
            dark = Object.Equals(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1), 0);
            BackColor = ColorFor(0xf7f8f7, 0x202523);
            ForeColor = ColorFor(0x20352e, 0xe4eee8);
            var surface = ColorFor(0xffffff, 0x2c332f);
            var subtle = ColorFor(0x55675e, 0xb0c0b5);
            status.ForeColor = period.ForeColor = empty.ForeColor = subtle;
            empty.BackColor = grid.BackgroundColor = surface;
            grid.DefaultCellStyle.BackColor = surface;
            grid.DefaultCellStyle.ForeColor = ForeColor;
            grid.DefaultCellStyle.SelectionBackColor = ColorFor(0xd8e9df, 0x3f5c4c);
            grid.DefaultCellStyle.SelectionForeColor = ForeColor;
            grid.ColumnHeadersDefaultCellStyle.BackColor = ColorFor(0xe9efeb, 0x35413a);
            grid.ColumnHeadersDefaultCellStyle.ForeColor = ForeColor;
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = grid.ColumnHeadersDefaultCellStyle.BackColor;
            grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = ForeColor;
            grid.GridColor = ColorFor(0xe1e7e2, 0x455048);
            trend.BackColor = ColorFor(0xe9f1eb, 0x2e3e35);
            trend.ForeColor = subtle;
            trend.BarColor = ColorFor(0x4b8664, 0x95caa4);
            refresh.BackColor = surface;
            refresh.ForeColor = ForeColor;
            refresh.FlatAppearance.BorderColor = grid.GridColor;
            for (int i = 0; i < figures.Length; i++)
            {
                captions[i].Parent.BackColor = ColorFor(0xe9f1eb, 0x2e3e35);
                captions[i].ForeColor = subtle;
                figures[i].ForeColor = ColorFor(0x22563d, 0xb7dfbf);
            }
            StyleRangeButtons();
        }

        private void StyleRangeButtons()
        {
            foreach (var b in ranges)
            {
                b.FlatAppearance.CheckedBackColor = ColorFor(0x286247, 0xa8d3b6);
                b.BackColor = b.Checked ? b.FlatAppearance.CheckedBackColor : ColorFor(0xe9efeb, 0x303a34);
                b.ForeColor = b.Checked ? ColorFor(0xffffff, 0x193526) : ForeColor;
            }
        }

        private async Task ReadReport()
        {
            if (busy) return;
            busy = true;
            readFailed = false;
            refresh.Enabled = false;
            RenderReport();
            try
            {
                report = await Task.Run(() => UsageReader.Read(directory));
                lastRead = DateTime.Now;
            }
            catch (Exception) { readFailed = true; }
            finally
            {
                busy = false;
                if (!IsDisposed && !Disposing) { refresh.Enabled = true; RenderReport(); }
            }
        }

        private void RenderReport()
        {
            int days = Days[selected];
            DateTime now = DateTime.Now;
            period.Text = days == 0 ? "全部本地记录" : days == 1 ? now.ToString("yyyy-MM-dd") :
                now.AddDays(1 - days).ToString("yyyy-MM-dd") + " 至 " + now.ToString("MM-dd");
            grid.Rows.Clear();
            var summary = report == null ? null : report.Summarize(days, now);
            bool missing = report != null && !report.DirectoryExists;
            bool completeFailure = report != null && (missing || (report.ReadDatabases == 0 && report.SkippedDatabases > 0));
            if (summary != null && !completeFailure)
            {
                decimal total = (decimal)summary.Input + summary.Cache + summary.Output;
                double? share = CacheShare(summary.Input, summary.Cache);
                SetFigure(0, Compact(summary.Requests), summary.Requests.ToString("N0") + " 次有 Token 记录的模型请求");
                SetFigure(1, Compact(total), total.ToString("N0") + " tokens（未缓存输入、缓存输入及输出之和）");
                SetFigure(2, share.HasValue ? share.Value.ToString("P1") : "—", "缓存输入 ÷（未缓存输入 + 缓存输入）");
                foreach (var model in summary.Models)
                    grid.Rows.Add(model.Model, model.Requests, model.Input, model.Cache, model.Output, CacheShare(model.Input, model.Cache));
            }
            else
                for (int i = 0; i < figures.Length; i++) SetFigure(i, "—", "");
            trend.SetValues(summary, days, now);
            if (grid.SortedColumn != null && grid.SortOrder != SortOrder.None)
                grid.Sort(grid.SortedColumn, grid.SortOrder == SortOrder.Ascending ?
                    System.ComponentModel.ListSortDirection.Ascending : System.ComponentModel.ListSortDirection.Descending);
            grid.ClearSelection();
            grid.Visible = grid.Rows.Count > 0;
            empty.Visible = grid.Rows.Count == 0;
            empty.Text = busy ? "正在读取本地用量…" : readFailed ? "读取失败，可点击“刷新”重试。" :
                missing ? "未找到本机 Antigravity 会话目录。" : completeFailure ? "暂时无法读取本机会话。请稍后刷新。" :
                report != null && (report.SkippedDatabases > 0 || report.SkippedRecords > 0)
                    ? "所选时间内没有可统计的用量，部分记录未能读取。" : "此时间范围内还没有含 Token 数的模型请求。";

            var notes = new List<string>();
            if (busy) notes.Add("正在读取本地会话…");
            else if (readFailed) notes.Add(report == null ? "读取失败" : "刷新失败，显示上一次读数");
            if (report != null)
            {
                notes.Add("读取于 " + lastRead.ToString("HH:mm:ss"));
                if (report.SkippedDatabases > 0) notes.Add(report.SkippedDatabases + " 个会话未能读取");
                if (report.SkippedRecords > 0) notes.Add(report.SkippedRecords + " 条记录未能解析");
                if (days != 0 && report.UndatedRequests > 0) notes.Add(report.UndatedRequests + " 条无日期请求可在“全部”查看");
            }
            status.Text = String.Join("  ·  ", notes.ToArray());
        }

        private static double? CacheShare(long input, long cache)
        {
            double all = (double)input + cache;
            return all > 0 ? (double?)((double)cache / all) : null;
        }
        private static string Compact(decimal amount)
        {
            if (amount >= 100000000m) return (amount / 100000000m).ToString("0.##", CultureInfo.CurrentCulture) + " 亿";
            if (amount >= 10000m) return (amount / 10000m).ToString("0.##", CultureInfo.CurrentCulture) + " 万";
            return amount.ToString("N0", CultureInfo.CurrentCulture);
        }
        private void SetFigure(int index, string value, string description)
        {
            figures[index].Text = value;
            tips.SetToolTip(figures[index], description);
            tips.SetToolTip(captions[index], description);
        }

        private sealed class DailyTrend : Control
        {
            private readonly ToolTip tip = new ToolTip();
            private DateTime first, last;
            private int interval, hovered = -1;
            private decimal[] totals = new decimal[0];
            internal Color BarColor;

            internal DailyTrend()
            {
                DoubleBuffered = true;
                SetStyle(ControlStyles.ResizeRedraw, true);
                AccessibleName = "所选日期范围的 Token 趋势";
            }

            internal void SetValues(UsageSummary summary, int days, DateTime now)
            {
                totals = new decimal[0];
                hovered = -1;
                tip.SetToolTip(this, "");
                if (summary != null && summary.Days.Count > 0)
                {
                    first = days == 0 ? summary.Days[0].Day : now.Date.AddDays(1 - days);
                    last = days == 0 ? summary.Days[summary.Days.Count - 1].Day : now.Date;
                    int span = (last - first).Days + 1;
                    interval = Math.Max(1, (span + 29) / 30);
                    totals = new decimal[(span + interval - 1) / interval];
                    foreach (var day in summary.Days)
                    {
                        int slot = (day.Day - first).Days / interval;
                        if (slot >= 0 && slot < totals.Length) totals[slot] += (decimal)day.Input + day.Cache + day.Output;
                    }
                }
                AccessibleDescription = totals.Length > 0 ? RangeText(first, last) : "暂无有日期的记录";
                Invalidate();
            }

            private static string RangeText(DateTime from, DateTime to)
            {
                return from.Date == to.Date ? from.ToString("yyyy-MM-dd") :
                    from.ToString("yyyy-MM-dd") + " 至 " + to.ToString(from.Year == to.Year ? "MM-dd" : "yyyy-MM-dd");
            }

            private int Pad { get { return Math.Max(6, Font.Height / 2); } }
            private Rectangle Plot { get { int p = Pad; return Rectangle.FromLTRB(p, p + Font.Height + p / 2, Width - p, Height - p); } }
            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                Rectangle plot = Plot;
                if (plot.Width < 1 || plot.Height < 1) return;
                var label = new Rectangle(Pad, Pad, plot.Width, Font.Height);
                string heading = interval > 1 && totals.Length > 0 ? "每 " + interval + " 天 Token" : "每日 Token";
                TextRenderer.DrawText(e.Graphics, heading, Font, label, ForeColor, TextFormatFlags.Left | TextFormatFlags.NoPadding);
                if (totals.Length == 0)
                {
                    TextRenderer.DrawText(e.Graphics, "暂无有日期的记录", Font, plot, ForeColor,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                    return;
                }
                decimal peak = 0;
                foreach (decimal total in totals) peak = Math.Max(peak, total);
                using (var baseline = new Pen(Color.FromArgb(75, ForeColor)))
                using (var brush = new SolidBrush(BarColor))
                {
                    e.Graphics.DrawLine(baseline, plot.Left, plot.Bottom - 1, plot.Right, plot.Bottom - 1);
                    for (int i = 0; i < totals.Length && peak > 0; i++)
                    {
                        if (totals[i] == 0) continue;
                        int l = plot.Left + i * plot.Width / totals.Length;
                        int r = plot.Left + (i + 1) * plot.Width / totals.Length;
                        int width = Math.Max(1, Math.Min(Pad * 2, (r - l) * 3 / 4));
                        int height = Math.Max(1, (int)((plot.Height - 1) * (totals[i] / peak)));
                        e.Graphics.FillRectangle(brush, l + (r - l - width) / 2, plot.Bottom - height, width, height);
                    }
                }
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                var plot = Plot;
                int slot = totals.Length > 0 && plot.Contains(e.Location) ? (e.X - plot.X) * totals.Length / plot.Width : -1;
                if (slot == hovered) return;
                hovered = slot;
                string text = "";
                if (slot >= 0)
                {
                    DateTime from = first.AddDays(slot * interval);
                    DateTime to = from.AddDays(Math.Min(interval - 1, (last - from).Days));
                    text = RangeText(from, to) + "  ·  " + totals[slot].ToString("N0") + " tokens";
                }
                tip.SetToolTip(this, text);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) tip.Dispose();
                base.Dispose(disposing);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) tips.Dispose();
            base.Dispose(disposing);
            if (disposing)
            {
                foreach (var font in fonts) font.Dispose();
                fonts.Clear();
            }
        }
    }
}
