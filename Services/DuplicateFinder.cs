using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Fyle.Core;

namespace Fyle.Services
{
    /// <summary>
    /// Advanced duplicate file finder with MD5/SHA256 verification
    /// </summary>
    public class DuplicateFinder
    {
        public event Action<string>? StatusChanged;
        public event Action<double>? ProgressChanged;

        public class DuplicateGroup
        {
            public string Hash { get; set; } = "";
            public long FileSize { get; set; }
            public List<string> FilePaths { get; set; } = new();
            public long WastedSpace => FileSize * (FilePaths.Count - 1);
        }

        public enum HashAlgorithm
        {
            None,       // Just compare size and name
            MD5,
            SHA256
        }

        public async Task<List<DuplicateGroup>> FindDuplicatesAsync(
            DirectoryNode root, 
            HashAlgorithm algorithm = HashAlgorithm.MD5,
            long minFileSize = 1024 * 1024, // 1MB minimum
            CancellationToken cancellationToken = default)
        {
            var results = new List<DuplicateGroup>();
            
            StatusChanged?.Invoke("Collecting files...");
            
            // Get all files
            var allFiles = new List<(string Path, long Size)>();
            CollectFiles(root, allFiles);

            // Filter by minimum size
            var eligibleFiles = allFiles.Where(f => f.Size >= minFileSize).ToList();
            
            StatusChanged?.Invoke($"Found {eligibleFiles.Count} files >= {FormatBytes(minFileSize)}");

            // Group by size first (quick filter)
            var sizeGroups = eligibleFiles
                .GroupBy(f => f.Size)
                .Where(g => g.Count() > 1)
                .ToList();

            StatusChanged?.Invoke($"Found {sizeGroups.Count} size groups with potential duplicates");

            int processed = 0;
            int total = sizeGroups.Sum(g => g.Count());

            foreach (var sizeGroup in sizeGroups)
            {
                if (cancellationToken.IsCancellationRequested) break;

                if (algorithm == HashAlgorithm.None)
                {
                    // Just group by size
                    results.Add(new DuplicateGroup
                    {
                        Hash = $"size:{sizeGroup.Key}",
                        FileSize = sizeGroup.Key,
                        FilePaths = sizeGroup.Select(f => f.Path).ToList()
                    });
                }
                else
                {
                    // Calculate hashes
                    var hashGroups = new Dictionary<string, List<string>>();
                    
                    foreach (var file in sizeGroup)
                    {
                        if (cancellationToken.IsCancellationRequested) break;
                        
                        processed++;
                        ProgressChanged?.Invoke((processed / (double)total) * 100);
                        
                        try
                        {
                            var hash = await CalculateHashAsync(file.Path, algorithm, cancellationToken);
                            if (!hashGroups.ContainsKey(hash))
                                hashGroups[hash] = new List<string>();
                            hashGroups[hash].Add(file.Path);
                        }
                        catch
                        {
                            // Skip files we can't read
                        }
                    }

                    // Add groups with duplicates
                    foreach (var hashGroup in hashGroups.Where(g => g.Value.Count > 1))
                    {
                        results.Add(new DuplicateGroup
                        {
                            Hash = hashGroup.Key,
                            FileSize = sizeGroup.Key,
                            FilePaths = hashGroup.Value
                        });
                    }
                }
            }

            // Sort by wasted space
            results.Sort((a, b) => b.WastedSpace.CompareTo(a.WastedSpace));

            var totalWasted = results.Sum(g => g.WastedSpace);
            StatusChanged?.Invoke($"Found {results.Count} duplicate groups, {FormatBytes(totalWasted)} wasted");

            return results;
        }

        private void CollectFiles(DirectoryNode node, List<(string Path, long Size)> files)
        {
            foreach (var child in node.Children)
            {
                if (!child.IsDirectory && !string.IsNullOrEmpty(child.Path))
                {
                    files.Add((child.Path, child.Size));
                }
                else if (child.IsDirectory)
                {
                    CollectFiles(child, files);
                }
            }
        }

        private async Task<string> CalculateHashAsync(string filePath, HashAlgorithm algorithm, CancellationToken cancellationToken)
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            
            byte[] hashBytes;
            
            if (algorithm == HashAlgorithm.SHA256)
            {
                using var sha256 = System.Security.Cryptography.SHA256.Create();
                hashBytes = await sha256.ComputeHashAsync(stream, cancellationToken);
            }
            else // MD5
            {
                using var md5 = System.Security.Cryptography.MD5.Create();
                hashBytes = await md5.ComputeHashAsync(stream, cancellationToken);
            }
            
            return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
        }

        private string FormatBytes(long bytes)
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

