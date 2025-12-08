using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Fyle.Core;

namespace Fyle.Services
{
    /// <summary>
    /// Manages saving and loading scan data for comparison
    /// </summary>
    public class ScanDataManager
    {
        public class ScanSnapshot
        {
            public string DrivePath { get; set; } = "";
            public DateTime ScanDate { get; set; }
            public long TotalSize { get; set; }
            public int TotalFiles { get; set; }
            public int TotalFolders { get; set; }
            public List<FolderSnapshot> Folders { get; set; } = new();
        }

        public class FolderSnapshot
        {
            public string Path { get; set; } = "";
            public string Name { get; set; } = "";
            public long Size { get; set; }
            public int FileCount { get; set; }
        }

        public class ComparisonResult
        {
            public string Path { get; set; } = "";
            public string Name { get; set; } = "";
            public long OldSize { get; set; }
            public long NewSize { get; set; }
            public long Difference => NewSize - OldSize;
            public double PercentChange => OldSize > 0 ? ((NewSize - OldSize) / (double)OldSize) * 100 : 100;
        }

        public static void SaveScan(DirectoryNode root, string filePath)
        {
            var snapshot = new ScanSnapshot
            {
                DrivePath = root.Path,
                ScanDate = DateTime.Now,
                TotalSize = root.Size,
                TotalFiles = root.FileCount,
                TotalFolders = root.DirectoryCount
            };

            // Save top-level folders
            CollectFolders(root, snapshot.Folders, 3); // 3 levels deep

            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(filePath, json);
        }

        private static void CollectFolders(DirectoryNode node, List<FolderSnapshot> folders, int depth)
        {
            if (depth <= 0) return;

            foreach (var child in node.Children)
            {
                if (child.IsDirectory)
                {
                    folders.Add(new FolderSnapshot
                    {
                        Path = child.Path,
                        Name = child.Name,
                        Size = child.Size,
                        FileCount = child.FileCount
                    });

                    CollectFolders(child, folders, depth - 1);
                }
            }
        }

        public static ScanSnapshot? LoadScan(string filePath)
        {
            try
            {
                var json = File.ReadAllText(filePath);
                return JsonSerializer.Deserialize<ScanSnapshot>(json);
            }
            catch
            {
                return null;
            }
        }

        public static List<ComparisonResult> CompareScan(ScanSnapshot oldScan, DirectoryNode currentRoot)
        {
            var results = new List<ComparisonResult>();
            var currentFolders = new Dictionary<string, DirectoryNode>(StringComparer.OrdinalIgnoreCase);
            
            // Build lookup of current folders
            BuildFolderLookup(currentRoot, currentFolders);

            // Compare each folder from old scan
            foreach (var oldFolder in oldScan.Folders)
            {
                long newSize = 0;
                if (currentFolders.TryGetValue(oldFolder.Path, out var currentFolder))
                {
                    newSize = currentFolder.Size;
                }

                // Only include if there's a significant change
                long diff = newSize - oldFolder.Size;
                if (Math.Abs(diff) > 1024 * 1024) // > 1MB change
                {
                    results.Add(new ComparisonResult
                    {
                        Path = oldFolder.Path,
                        Name = oldFolder.Name,
                        OldSize = oldFolder.Size,
                        NewSize = newSize
                    });
                }
            }

            // Sort by absolute difference
            results.Sort((a, b) => Math.Abs(b.Difference).CompareTo(Math.Abs(a.Difference)));

            return results;
        }

        private static void BuildFolderLookup(DirectoryNode node, Dictionary<string, DirectoryNode> lookup)
        {
            if (node.IsDirectory && !string.IsNullOrEmpty(node.Path))
            {
                lookup[node.Path] = node;
            }

            foreach (var child in node.Children)
            {
                if (child.IsDirectory)
                {
                    BuildFolderLookup(child, lookup);
                }
            }
        }
    }
}

