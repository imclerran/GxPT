using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;
using Ionic.Zip;

namespace GxPT
{
    internal sealed class DocxAttachmentExtractor : IAttachmentExtractor
    {
        public bool CanHandle(string filePath)
        {
            try
            {
                string ext = Path.GetExtension(filePath);
                ext = string.IsNullOrEmpty(ext) ? string.Empty : ext.ToLowerInvariant();
                return ext == ".docx";
            }
            catch { return false; }
        }

        public AttachedFile Extract(string filePath)
        {
            string text = ExtractMarkdownish(filePath) ?? string.Empty;
            return new AttachedFile(Path.GetFileName(filePath), text);
        }

        public IList<string> GetFileDialogPatterns()
        {
            return new List<string> { "*.docx" };
        }

        public string GetCategoryLabel()
        {
            return "Word Documents";
        }

        private static string ExtractMarkdownish(string filePath)
        {
            // Open .docx (ZIP), read word/document.xml and parse with System.Xml
            try
            {
                using (var zip = ZipFile.Read(filePath))
                {
                    var entry = zip["word/document.xml"]; // throws if missing
                    if (entry == null) return string.Empty;
                    using (var ms = new MemoryStream())
                    {
                        entry.Extract(ms);
                        ms.Position = 0;
                        return ExtractFromDocumentXml(ms);
                    }
                }
            }
            catch (Exception ex)
            {
                try { Logger.Log("DOCX", "Failed to read DOCX zip: " + ex.Message); }
                catch { }
                return string.Empty;
            }
        }

        private static string ExtractFromDocumentXml(Stream xmlStream)
        {
            var sb = new StringBuilder(2048);
            try
            {
                var doc = new XmlDocument();
                doc.PreserveWhitespace = false;
                doc.Load(xmlStream);
                var nsmgr = new XmlNamespaceManager(doc.NameTable);
                nsmgr.AddNamespace("w", "http://schemas.openxmlformats.org/wordprocessingml/2006/main");

                var body = doc.SelectSingleNode("/w:document/w:body", nsmgr);
                if (body == null) return string.Empty;
                foreach (XmlNode node in body.ChildNodes)
                {
                    if (node == null || node.NodeType != XmlNodeType.Element) continue;
                    if (node.LocalName == "p")
                    {
                        AppendParagraphMarkdown(sb, node, nsmgr);
                    }
                    else if (node.LocalName == "tbl")
                    {
                        AppendTableAsText(sb, node, nsmgr);
                    }
                }
            }
            catch { }
            return sb.ToString();
        }

        private static void AppendParagraphMarkdown(StringBuilder sb, XmlNode pNode, XmlNamespaceManager ns)
        {
            // Determine if heading based on style name (e.g., Heading1..Heading6)
            int headingLevel = 0;
            try
            {
                var pStyle = pNode.SelectSingleNode("w:pPr/w:pStyle", ns);
                string styleVal = (pStyle != null && pStyle.Attributes != null)
                    ? GetAttr(pStyle, ns, "val")
                    : null;
                if (!string.IsNullOrEmpty(styleVal) && styleVal.StartsWith("Heading", StringComparison.OrdinalIgnoreCase))
                {
                    string tail = styleVal.Substring("Heading".Length);
                    int n; headingLevel = (int.TryParse(tail, out n)) ? Math.Max(1, Math.Min(6, n)) : 1;
                }
            }
            catch { headingLevel = 0; }

            bool isListItem = false;
            try
            {
                var numPr = pNode.SelectSingleNode("w:pPr/w:numPr", ns);
                isListItem = (numPr != null);
            }
            catch { isListItem = false; }

            string text = ExtractParagraphText(pNode, ns);
            if (text == null) text = string.Empty;
            text = text.Replace("\r\n", "\n").Replace("\r", "\n");

            if (text.Length == 0)
            {
                sb.AppendLine();
                return;
            }

            if (headingLevel > 0)
            {
                sb.Append(new string('#', headingLevel)).Append(' ').AppendLine(text);
                sb.AppendLine();
            }
            else if (isListItem)
            {
                sb.Append("- ").AppendLine(text);
            }
            else
            {
                sb.AppendLine(text);
                sb.AppendLine();
            }
        }

        private static string ExtractParagraphText(XmlNode pNode, XmlNamespaceManager ns)
        {
            var sb = new StringBuilder();
            try
            {
                // Collect text and breaks in document order
                var nodes = pNode.SelectNodes(".//w:t|.//w:br", ns);
                if (nodes != null)
                {
                    foreach (XmlNode n in nodes)
                    {
                        if (n.LocalName == "t")
                        {
                            if (!string.IsNullOrEmpty(n.InnerText)) sb.Append(n.InnerText);
                        }
                        else if (n.LocalName == "br")
                        {
                            sb.AppendLine();
                        }
                    }
                }
            }
            catch { }
            return sb.ToString();
        }

        private static void AppendTableAsText(StringBuilder sb, XmlNode tblNode, XmlNamespaceManager ns)
        {
            try
            {
                var rows = tblNode.SelectNodes("w:tr", ns);
                if (rows == null) return;
                foreach (XmlNode row in rows)
                {
                    bool first = true;
                    var cells = row.SelectNodes("w:tc", ns);
                    if (cells == null) continue;
                    foreach (XmlNode cell in cells)
                    {
                        if (!first) sb.Append(" | ");
                        first = false;
                        var cellText = new StringBuilder();
                        var paras = cell.SelectNodes("w:p", ns);
                        if (paras != null)
                        {
                            foreach (XmlNode cp in paras)
                            {
                                string s = ExtractParagraphText(cp, ns);
                                if (!string.IsNullOrEmpty(s))
                                {
                                    if (cellText.Length > 0) cellText.Append(" ");
                                    cellText.Append(s.Trim());
                                }
                            }
                        }
                        sb.Append(cellText.ToString());
                    }
                    sb.AppendLine();
                }
                sb.AppendLine();
            }
            catch { }
        }

        private static string ExtractPlainText(string filePath)
        {
            try
            {
                using (var zip = ZipFile.Read(filePath))
                {
                    var entry = zip["word/document.xml"]; if (entry == null) return string.Empty;
                    using (var ms = new MemoryStream())
                    {
                        entry.Extract(ms); ms.Position = 0;
                        var doc = new XmlDocument();
                        doc.PreserveWhitespace = false; doc.Load(ms);
                        var nsmgr = new XmlNamespaceManager(doc.NameTable);
                        nsmgr.AddNamespace("w", "http://schemas.openxmlformats.org/wordprocessingml/2006/main");
                        var paragraphs = doc.SelectNodes("//w:body/w:p", nsmgr);
                        var sb = new StringBuilder();
                        if (paragraphs != null)
                        {
                            foreach (XmlNode p in paragraphs)
                            {
                                sb.AppendLine(ExtractParagraphText(p, nsmgr));
                            }
                        }
                        return sb.ToString();
                    }
                }
            }
            catch { return string.Empty; }
        }

        private static string GetAttr(XmlNode node, XmlNamespaceManager ns, string localName)
        {
            if (node == null || node.Attributes == null) return null;
            for (int i = 0; i < node.Attributes.Count; i++)
            {
                var a = node.Attributes[i];
                if (a == null) continue;
                if (string.Equals(a.LocalName, localName, StringComparison.Ordinal))
                    return a.Value;
            }
            return null;
        }
    }
}
