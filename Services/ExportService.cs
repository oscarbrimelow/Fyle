using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Fyle.Core;

namespace Fyle.Services
{
    /// <summary>
    /// Export scan results to various formats
    /// </summary>
    public class ExportService
    {
        public class ExportMetadata
        {
            public string AppVersion { get; set; } = "";
            public DateTime ExportedAtUtc { get; set; } = DateTime.UtcNow;
            public string ScanTargetPath { get; set; } = "";
            public bool UsedMft { get; set; }
            public string FilterText { get; set; } = "";
            public int FileTypeFilterIndex { get; set; }
            public bool IncludeHidden { get; set; } = true;
            public bool IncludeSystem { get; set; } = true;
            public bool IncludeFiles { get; set; } = true;
            public long MinFileSizeBytes { get; set; }
            public List<string> ExcludedPaths { get; set; } = new();
        }

        public static void ExportToCsv(DirectoryNode root, string filePath, int maxDepth = 10)
        {
            ExportToCsv(root, filePath, new ExportMetadata(), CancellationToken.None, maxDepth);
        }

        public static void ExportToCsv(DirectoryNode root, string filePath, ExportMetadata metadata, CancellationToken cancellationToken, int maxDepth = 10)
        {
            using var writer = new StreamWriter(filePath, false, Encoding.UTF8);

            writer.WriteLine("Key,Value");
            foreach (var kvp in MetadataToPairs(metadata, root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.WriteLine($"\"{kvp.Key.Replace("\"", "\"\"")}\",\"{kvp.Value.Replace("\"", "\"\"")}\"");
            }
            writer.WriteLine();

            writer.WriteLine("Path,Name,Size (Bytes),Size (Formatted),Type,Depth,Parent");
            ExportNodeCsv(writer, root, "", 0, maxDepth, cancellationToken);
        }

        private static void ExportNodeCsv(StreamWriter writer, DirectoryNode node, string parent, int depth, int maxDepth, CancellationToken cancellationToken)
        {
            if (depth > maxDepth) return;
            cancellationToken.ThrowIfCancellationRequested();
            
            var type = node.IsDirectory ? "Folder" : "File";
            var escapedPath = $"\"{node.Path?.Replace("\"", "\"\"") ?? ""}\"";
            var escapedName = $"\"{node.Name?.Replace("\"", "\"\"") ?? ""}\"";
            var escapedParent = $"\"{parent.Replace("\"", "\"\"")}\"";
            
            writer.WriteLine($"{escapedPath},{escapedName},{node.Size},{node.FormattedSize},{type},{depth},{escapedParent}");
            
            foreach (var child in node.Children.OrderByDescending(c => c.Size))
            {
                ExportNodeCsv(writer, child, node.Name ?? "", depth + 1, maxDepth, cancellationToken);
            }
        }

        public static void ExportToHtml(DirectoryNode root, string filePath, string title = "Fyle Disk Report")
        {
            ExportToHtml(root, filePath, title, new ExportMetadata(), CancellationToken.None);
        }

        public static void ExportToHtml(DirectoryNode root, string filePath, string title, ExportMetadata metadata, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            sb.AppendLine($"    <p>Scanned: <strong>{root.Path}</strong></p>");
            sb.AppendLine($"    <p style=\"color:#666;\">Exported: {metadata.ExportedAtUtc:yyyy-MM-dd HH:mm} UTC • App: {EscapeHtml(metadata.AppVersion)} • MFT: {(metadata.UsedMft ? "Yes" : "No")}</p>");
            if (!string.IsNullOrWhiteSpace(metadata.FilterText) || metadata.FileTypeFilterIndex != 0 || metadata.MinFileSizeBytes > 0 || (metadata.ExcludedPaths?.Count ?? 0) > 0)
            {
                sb.AppendLine("    <div style=\"background:#f8f9fa;border-radius:6px;padding:12px 14px;margin:10px 0;\">");
                sb.AppendLine("      <div style=\"font-weight:600;margin-bottom:6px;\">Filters</div>");
                sb.AppendLine($"      <div style=\"color:#666;\">Search: <strong>{EscapeHtml(metadata.FilterText)}</strong></div>");
                sb.AppendLine($"      <div style=\"color:#666;\">Type filter index: <strong>{metadata.FileTypeFilterIndex}</strong></div>");
                sb.AppendLine($"      <div style=\"color:#666;\">Min file size: <strong>{FormatBytes(metadata.MinFileSizeBytes)}</strong></div>");
                sb.AppendLine($"      <div style=\"color:#666;\">Include hidden: <strong>{metadata.IncludeHidden}</strong> • Include system: <strong>{metadata.IncludeSystem}</strong> • Include files: <strong>{metadata.IncludeFiles}</strong></div>");
                if ((metadata.ExcludedPaths?.Count ?? 0) > 0)
                {
                    sb.AppendLine($"      <div style=\"color:#666;\">Excluded: <strong>{metadata.ExcludedPaths?.Count ?? 0}</strong></div>");
                }
                sb.AppendLine("    </div>");
            }
            
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
                cancellationToken.ThrowIfCancellationRequested();
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
                cancellationToken.ThrowIfCancellationRequested();
                var dir = Path.GetDirectoryName(file.Path) ?? "";
                sb.AppendLine($"      <tr>");
                sb.AppendLine($"        <td class=\"file\">{file.Name}</td>");
                sb.AppendLine($"        <td style=\"color: #999; font-size: 12px;\">{dir}</td>");
                sb.AppendLine($"        <td class=\"size\">{file.FormattedSize}</td>");
                sb.AppendLine($"      </tr>");
            }
            
            sb.AppendLine("    </table>");
            
            sb.AppendLine($"    <div class=\"footer\">Generated by Fyle - {DateTime.Now:yyyy-MM-dd HH:mm:ss}</div>");
            sb.AppendLine("  </div>");
            sb.AppendLine("</body>");
            sb.AppendLine("</html>");
            
            File.WriteAllText(filePath, sb.ToString());
        }

        public static void ExportToJson(DirectoryNode root, string filePath, int maxDepth = 10)
        {
            ExportToJson(root, filePath, new ExportMetadata(), CancellationToken.None, maxDepth);
        }

        public static void ExportToJson(DirectoryNode root, string filePath, ExportMetadata metadata, CancellationToken cancellationToken, int maxDepth = 10)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = new Dictionary<string, object>
            {
                ["metadata"] = MetadataToPairs(metadata, root).ToDictionary(k => k.Key, v => v.Value),
                ["root"] = ConvertToDict(root, 0, maxDepth, cancellationToken)
            };
            var json = System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            cancellationToken.ThrowIfCancellationRequested();
            File.WriteAllText(filePath, json);
        }

        private static Dictionary<string, object> ConvertToDict(DirectoryNode node, int depth, int maxDepth, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                    .Select(c => ConvertToDict(c, depth + 1, maxDepth, cancellationToken))
                    .ToList();
            }

            return dict;
        }

        public static void ExportToXml(DirectoryNode root, string filePath, int maxDepth = 10)
        {
            ExportToXml(root, filePath, new ExportMetadata(), CancellationToken.None, maxDepth);
        }

        public static void ExportToXml(DirectoryNode root, string filePath, ExportMetadata metadata, CancellationToken cancellationToken, int maxDepth = 10)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.AppendLine($"<FyleScan exportedAtUtc=\"{metadata.ExportedAtUtc:O}\" appVersion=\"{EscapeXml(metadata.AppVersion)}\">");
            sb.AppendLine("  <Metadata>");
            foreach (var kvp in MetadataToPairs(metadata, root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                sb.AppendLine($"    <Item key=\"{EscapeXml(kvp.Key)}\" value=\"{EscapeXml(kvp.Value)}\" />");
            }
            sb.AppendLine("  </Metadata>");
            ExportNodeXml(sb, root, 1, 0, maxDepth, cancellationToken);
            sb.AppendLine("</FyleScan>");
            cancellationToken.ThrowIfCancellationRequested();
            File.WriteAllText(filePath, sb.ToString());
        }

        private static void ExportNodeXml(StringBuilder sb, DirectoryNode node, int indent, int depth, int maxDepth, CancellationToken cancellationToken)
        {
            if (depth > maxDepth) return;
            cancellationToken.ThrowIfCancellationRequested();
            
            var padding = new string(' ', indent * 2);
            var type = node.IsDirectory ? "Folder" : "File";
            var escapedName = System.Security.SecurityElement.Escape(node.Name ?? "");
            var escapedPath = System.Security.SecurityElement.Escape(node.Path ?? "");
            
            if (node.IsDirectory && node.Children.Count > 0 && depth < maxDepth)
            {
                sb.AppendLine($"{padding}<{type} name=\"{escapedName}\" size=\"{node.Size}\" sizeFormatted=\"{node.FormattedSize}\">");
                foreach (var child in node.Children.OrderByDescending(c => c.Size).Take(100))
                {
                    ExportNodeXml(sb, child, indent + 1, depth + 1, maxDepth, cancellationToken);
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

        private static List<KeyValuePair<string, string>> MetadataToPairs(ExportMetadata metadata, DirectoryNode root)
        {
            var pairs = new List<KeyValuePair<string, string>>
            {
                new("appVersion", metadata.AppVersion ?? ""),
                new("exportedAtUtc", metadata.ExportedAtUtc.ToString("O")),
                new("scanTargetPath", metadata.ScanTargetPath ?? root.Path ?? ""),
                new("usedMft", metadata.UsedMft ? "true" : "false"),
                new("filterText", metadata.FilterText ?? ""),
                new("fileTypeFilterIndex", metadata.FileTypeFilterIndex.ToString()),
                new("includeHidden", metadata.IncludeHidden ? "true" : "false"),
                new("includeSystem", metadata.IncludeSystem ? "true" : "false"),
                new("includeFiles", metadata.IncludeFiles ? "true" : "false"),
                new("minFileSizeBytes", metadata.MinFileSizeBytes.ToString()),
                new("excludedPathsCount", (metadata.ExcludedPaths?.Count ?? 0).ToString()),
                new("totalSizeBytes", root.Size.ToString()),
                new("totalSizeFormatted", root.FormattedSize ?? ""),
                new("fileCount", root.FileCount.ToString()),
                new("folderCount", root.DirectoryCount.ToString())
            };

            if (metadata.ExcludedPaths != null && metadata.ExcludedPaths.Count > 0)
            {
                pairs.Add(new("excludedPaths", string.Join(" | ", metadata.ExcludedPaths)));
            }

            return pairs;
        }

        private static string EscapeHtml(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        private static string EscapeXml(string? s)
        {
            return System.Security.SecurityElement.Escape(s ?? "") ?? "";
        }

        private static string FormatBytes(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }
    }
}

