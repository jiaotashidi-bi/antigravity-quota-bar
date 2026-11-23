using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace GeminiQuota
{
    public static class Composer
    {
        // The Antigravity 2.34 desktop client assigns this ID to the outer
        // composer surface, including its toolbar, rounded border and prompt.
        private const string SurfaceId = "antigravity.agentSidePanelInputBox";
        // Model selection, View Usage and the other native menus are portal
        // siblings of the composer. Search from the app window for both.
        private static readonly Condition Layout = new OrCondition(
            new PropertyCondition(AutomationElement.AutomationIdProperty, SurfaceId),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Menu));
        private static readonly TreeWalker Documents = new TreeWalker(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));

        public static Rectangle Find(AutomationElement root)
        {
            bool menuOpen;
            return Find(root, out menuOpen);
        }

        public static Rectangle Find(AutomationElement root, out bool menuOpen)
        {
            string conversationId;
            return Find(root, out menuOpen, out conversationId);
        }

        public static Rectangle Find(AutomationElement root, out bool menuOpen, out string conversationId)
        {
            menuOpen = false;
            conversationId = null;
            if (root == null)
                return Rectangle.Empty;

            try
            {
                // Chromium exposes layout groups only in the raw accessibility tree.
                var request = new CacheRequest { TreeFilter = Automation.RawViewCondition };
                AutomationElementCollection elements;
                using (request.Activate())
                    elements = root.FindAll(TreeScope.Descendants, Layout);
                var host = root.Current.BoundingRectangle;
                var surfaces = new List<AutomationElement>();
                foreach (AutomationElement element in elements)
                {
                    Rectangle bounds = VisibleBounds(element, host);
                    if (bounds.IsEmpty)
                        continue;

                    if (element.Current.ControlType == ControlType.Menu)
                    {
                        menuOpen = true;
                        return Rectangle.Empty;
                    }

                    surfaces.Add(element);
                }

                var chosen = surfaces.Count == 1 ? surfaces[0] : null;
                if (surfaces.Count > 1)
                {
                    // Several panes in one window can each contain a prompt.
                    var focused = AutomationElement.FocusedElement;
                    while (focused != null && !Automation.Compare(focused, root))
                    {
                        if (focused.Current.AutomationId == SurfaceId)
                        {
                            chosen = surfaces.Find(s => Automation.Compare(focused, s));
                            break;
                        }
                        focused = TreeWalker.RawViewWalker.GetParent(focused);
                    }
                }
                if (chosen == null) return Rectangle.Empty;
                Rectangle anchor = VisibleBounds(chosen, host);
                if (!anchor.IsEmpty) conversationId = ReadConversation(chosen, surfaces);
                return anchor;
            }
            catch (ElementNotAvailableException) { }
            catch (InvalidOperationException) { }
            catch (COMException) { }
            catch (Win32Exception) { }
            return Rectangle.Empty;
        }

        private static string ReadConversation(AutomationElement chosen, List<AutomationElement> surfaces)
        {
            try
            {
                var document = Documents.GetParent(chosen);
                if (document == null) return null;
                foreach (var other in surfaces)
                {
                    if (other == chosen) continue;
                    var otherDocument = Documents.GetParent(other);
                    // A shared document exposes one URL for both panes.
                    if (otherDocument == null || Automation.Compare(document, otherDocument)) return null;
                }

                object pattern;
                if (!document.TryGetCurrentPattern(ValuePattern.Pattern, out pattern)) return null;
                return IdFromDocumentUrl(((ValuePattern)pattern).Current.Value);
            }
            catch (ElementNotAvailableException) { }
            catch (InvalidOperationException) { }
            catch (COMException) { }
            catch (Win32Exception) { }
            return null;
        }

        private static string IdFromDocumentUrl(string address)
        {
            if (String.IsNullOrEmpty(address)) return null;
            int suffix = address.IndexOfAny(new[] { '?', '#' });
            Uri uri;
            if (!Uri.TryCreate(suffix < 0 ? address : address.Substring(0, suffix), UriKind.Absolute, out uri) ||
                !uri.IsLoopback || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)) return null;
            string path = uri.AbsolutePath;
            Guid id;
            if (path.Length != 39 || !path.StartsWith("/c/", StringComparison.Ordinal) ||
                !Guid.TryParseExact(path.Substring(3), "D", out id) || id == Guid.Empty) return null;
            return id.ToString("D");
        }

        private static Rectangle VisibleBounds(AutomationElement element, System.Windows.Rect host)
        {
            var bounds = element.Current.BoundingRectangle;
            if (element.Current.IsOffscreen || host.IsEmpty || bounds.IsEmpty ||
                bounds.Width <= 0 || bounds.Height <= 0 || !host.IntersectsWith(bounds))
                return Rectangle.Empty;

            return Rectangle.FromLTRB(
                (int)Math.Floor(bounds.Left), (int)Math.Floor(bounds.Top),
                (int)Math.Ceiling(bounds.Right), (int)Math.Ceiling(bounds.Bottom));
        }
    }
}
