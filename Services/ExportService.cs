using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Fyle.Core;

namespace Fyle.Services
{
    /// <summary>
    /// Export scan results to various formats
    /// </summary>
    public class ExportService
    {
        public static void ExportToCsv(DirectoryNode root, string filePath, int maxDepth = 10)
        {
            using var writer = new StreamWriter(filePath, false, Encoding.UTF8);
            writer.WriteLine("Path,Name,Size (Bytes),Size (Formatted),Type,Depth,Parent");
            ExportNodeCsv(writer, root, "", 0, maxDepth);
        }

        private static void ExportNodeCsv(StreamWriter writer, DirectoryNode node, string parent, int depth, int maxDepth)
        {
            if (depth > maxDepth) return;
            
            var type = node.IsDirectory ? "Folder" : "File";
            var escapedPath = $"\"{node.Path?.Replace("\"", "\"\"") ?? ""}\"";
            var escapedName = $"\"{node.Name?.Replace("\"", "\"\"") ?? ""}\"";
            var escapedParent = $"\"{parent.Replace("\"", "\"\"")}\"";
            
            writer.WriteLine($"{escapedPath},{escapedName},{node.Size},{node.FormattedSize},{type},{depth},{escapedParent}");
            
            foreach (var child in node.Children.OrderByDescending(c => c.Size))
            {
                ExportNodeCsv(writer, child, node.Name ?? "", depth + 1, maxDepth);
            }
        }

        public static void ExportToHtml(DirectoryNode root, string filePath, string title = "Fyle Disk Report")
        {
            var sb = new StringBuilder();
            
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html lang=\"en\">");
            sb.AppendLine("<head>");
            sb.AppendLine("  <meta charset=\"UTF-8\">");
            sb.AppendLine($"  <title>{title}</title>");
            sb.AppendLine("  <style>");
            sb.AppendLine("    body { font-family: 'Segoe UI', Arial, sans-serif; margin: 20px; background: #f5f5f5; }");
            sb.AppendLine("    .container { max-width: 1200px; margin: 0 auto; background: white; padding: 30px; border-radius: 8px; box-shadow: 0 2px 10px rgba(0,0,0,0.1); }");
            sb.AppendLine("    h1 { color: #333; border-bottom: 2px solid #3182ce; padding-bottom: 10px; }");
            sb.AppendLine("    h2 { color: #555; margin-top: 30px; }");
            sb.AppendLine("    .summary { display: grid; grid-template-columns: repeat(3, 1fr); gap: 20px; margin: 20px 0; }");
            sb.AppendLine("    .stat { background: #f8f9fa; padding: 20px; border-radius: 6px; text-align: center; }");
            sb.AppendLine("    .stat-value { font-size: 28px; font-weight: bold; color: #3182ce; }");
            sb.AppendLine("    .stat-label { color: #666; margin-top: 5px; }");
            sb.AppendLine("    table { width: 100%; border-collapse: collapse; margin: 20px 0; }");
            sb.AppendLine("    th, td { padding: 12px; text-align: left; border-bottom: 1px solid #eee; }");
            sb.AppendLine("    th { background: #f8f9fa; font-weight: 600; }");
            sb.AppendLine("    tr:hover { background: #f5f5f5; }");
            sb.AppendLine("    .size { text-align: right; font-family: monospace; }");
            sb.AppendLine("    .bar { height: 8px; background: #e0e0e0; border-radius: 4px; overflow: hidden; }");
            sb.AppendLine("    .bar-fill { height: 100%; background: linear-gradient(90deg, #3182ce, #63b3ed); }");
            sb.AppendLine("    .folder { color: #d69e2e; }");
            sb.AppendLine("    .file { color: #718096; }");
            sb.AppendLine("    .footer { margin-top: 30px; padding-top: 20px; border-top: 1px solid #eee; color: #999; font-size: 12px; }");
            sb.AppendLine("  </style>");
            sb.AppendLine("</head>");
            sb.AppendLine("<body>");
            sb.AppendLine("  <div class=\"container\">");
            sb.AppendLine($"    <h1>📊 {title}</h1>");
            sb.AppendLine($"    <p>Scanned: <strong>{root.Path}</strong> on {DateTime.Now:yyyy-MM-dd HH:mm}</p>");
            
            // Summary stats
            sb.AppendLine("    <div class=\"summary\">");
            sb.AppendLine($"      <div class=\"stat\"><div class=\"stat-value\">{root.FormattedSize}</div><div class=\"stat-label\">Total Size</div></div>");
            sb.AppendLine($"      <div class=\"stat\"><div class=\"stat-value\">{root.FileCount:N0}</div><div class=\"stat-label\">Files</div></div>");
            sb.AppendLine($"      <div class=\"stat\"><div class=\"stat-value\">{root.DirectoryCount:N0}</div><div class=\"stat-label\">Folders</div></div>");
            sb.AppendLine("    </div>");
            
            // Top folders
            sb.AppendLine("    <h2>📁 Largest Folders</h2>");
            sb.AppendLine("    <table>");
            sb.AppendLine("      <tr><th>Folder</th><th>Size</th><th>% of Total</th><th></th></tr>");
            
            var topFolders = root.Children
                .Where(c => c.IsDirectory)
                .OrderByDescending(c => c.Size)
                .Take(20);
                
            foreach (var folder in topFolders)
            {
                var percent = root.Size > 0 ? (folder.Size / (double)root.Size) * 100 : 0;
                sb.AppendLine($"      <tr>");
                sb.AppendLine($"        <td class=\"folder\">📁 {folder.Name}</td>");
                sb.AppendLine($"        <td class=\"size\">{folder.FormattedSize}</td>");
                sb.AppendLine($"        <td class=\"size\">{percent:F1}%</td>");
                sb.AppendLine($"        <td><div class=\"bar\"><div class=\"bar-fill\" style=\"width: {percent}%\"></div></div></td>");
                sb.AppendLine($"      </tr>");
            }
            
            sb.AppendLine("    </table>");
            
            // Top files
            sb.AppendLine("    <h2>📄 Largest Files</h2>");
            sb.AppendLine("    <table>");
            sb.AppendLine("      <tr><th>File</th><th>Path</th><th>Size</th></tr>");
            
            var allFiles = new List<DirectoryNode>();
            CollectFiles(root, allFiles);
            var topFiles = allFiles.OrderByDescending(f => f.Size).Take(20);
            
            foreach (var file in topFiles)
            {
                var dir = Path.GetDirectoryName(file.Path) ?? "";
                sb.AppendLine($"      <tr>");
                sb.AppendLine($"        <td class=\"file\">{file.Name}</td>");
                sb.AppendLine($"        <td style=\"color: #999; font-size: 12px;\">{dir}</td>");
                sb.AppendLine($"        <td class=\"size\">{file.FormattedSize}</td>");
                sb.AppendLine($"      </tr>");
            }
            
            sb.AppendLine("    </table>");
            
            sb.AppendLine($"    <div class=\"footer\">Generated by Fyle v1.0 - {DateTime.Now:yyyy-MM-dd HH:mm:ss}</div>");
            sb.AppendLine("  </div>");
            sb.AppendLine("</body>");
            sb.AppendLine("</html>");
            
            File.WriteAllText(filePath, sb.ToString());
        }

        public static void ExportToJson(DirectoryNode root, string filePath, int maxDepth = 10)
        {
            var data = ConvertToDict(root, 0, maxDepth);
            var json = System.Text.Json.JsonSerializer.Serialize(data, new System.Text.Json.JsonSerializerOptions 
            { 
                WriteIndented = true 
            });
            File.WriteAllText(filePath, json);
        }

        private static Dictionary<string, object> ConvertToDict(DirectoryNode node, int depth, int maxDepth)
        {
            var dict = new Dictionary<string, object>
            {
                ["name"] = node.Name ?? "",
                ["path"] = node.Path ?? "",
                ["size"] = node.Size,
                ["sizeFormatted"] = node.FormattedSize ?? "",
                ["isDirectory"] = node.IsDirectory
            };

            if (node.IsDirectory && depth < maxDepth && node.Children.Count > 0)
            {
                dict["children"] = node.Children
                    .OrderByDescending(c => c.Size)
                    .Take(100) // Limit children per folder
                    .Select(c => ConvertToDict(c, depth + 1, maxDepth))
                    .ToList();
            }

            return dict;
        }

        public static void ExportToXml(DirectoryNode root, string filePath, int maxDepth = 10)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.AppendLine($"<FyleScan date=\"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\">");
            ExportNodeXml(sb, root, 1, 0, maxDepth);
            sb.AppendLine("</FyleScan>");
            File.WriteAllText(filePath, sb.ToString());
        }

        private static void ExportNodeXml(StringBuilder sb, DirectoryNode node, int indent, int depth, int maxDepth)
        {
            if (depth > maxDepth) return;
            
            var padding = new string(' ', indent * 2);
            var type = node.IsDirectory ? "Folder" : "File";
            var escapedName = System.Security.SecurityElement.Escape(node.Name ?? "");
            var escapedPath = System.Security.SecurityElement.Escape(node.Path ?? "");
            
            if (node.IsDirectory && node.Children.Count > 0 && depth < maxDepth)
            {
                sb.AppendLine($"{padding}<{type} name=\"{escapedName}\" size=\"{node.Size}\" sizeFormatted=\"{node.FormattedSize}\">");
                foreach (var child in node.Children.OrderByDescending(c => c.Size).Take(100))
                {
                    ExportNodeXml(sb, child, indent + 1, depth + 1, maxDepth);
                }
                sb.AppendLine($"{padding}</{type}>");
            }
            else
            {
                sb.AppendLine($"{padding}<{type} name=\"{escapedName}\" path=\"{escapedPath}\" size=\"{node.Size}\" sizeFormatted=\"{node.FormattedSize}\" />");
            }
        }

        private static void CollectFiles(DirectoryNode node, List<DirectoryNode> files)
        {
            foreach (var child in node.Children)
            {
                if (!child.IsDirectory)
                {
                    files.Add(child);
                }
                else
                {
                    CollectFiles(child, files);
                }
            }
        }
    }
}

