using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Fyle.Core
{
    public class Scanner
    {
        private CancellationTokenSource? _cancellationTokenSource;
        private readonly object _lockObject = new();
        private readonly ManualResetEventSlim _pauseEvent = new(true);
        private volatile bool _isPaused;

        public event Action<string>? CurrentPathChanged;
        public event Action<double>? ProgressChanged;
        public event Action<DirectoryNode>? ScanCompleted;

        public bool IsScanning { get; private set; }
        public bool IsPaused => _isPaused;

        public void Cancel()
        {
            _cancellationTokenSource?.Cancel();
        }

        public void Pause()
        {
            if (!IsScanning) return;
            _isPaused = true;
            _pauseEvent.Reset();
        }

        public void Resume()
        {
            _isPaused = false;
            _pauseEvent.Set();
        }

        public async Task<DirectoryNode?> ScanDriveAsync(string drivePath)
        {
            return await ScanDriveAsync(drivePath, ScanOptions.Default, CancellationToken.None);
        }

        public async Task<DirectoryNode?> ScanDriveAsync(string drivePath, ScanOptions options, CancellationToken cancellationToken)
        {
            if (IsScanning)
                throw new InvalidOperationException("Scan already in progress");

            IsScanning = true;
            _isPaused = false;
            _pauseEvent.Set();
            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            try
            {
                return await Task.Run(
                    () => ScanDirectory(drivePath, options ?? ScanOptions.Default, _cancellationTokenSource.Token),
                    _cancellationTokenSource.Token);
            }
            finally
            {
                IsScanning = false;
                _isPaused = false;
                _pauseEvent.Set();
            }
        }

        private DirectoryNode ScanDirectory(string path, ScanOptions options, CancellationToken cancellationToken)
        {
            var root = new DirectoryNode
            {
                Path = path,
                Name = Path.GetFileName(path) ?? path,
                IsDirectory = true
            };

            var excluded = NormalizeExcludedPaths(options.ExcludedPaths);
            var totalDirs = CountDirectories(path, options, excluded, cancellationToken);
            var scannedDirs = new ThreadSafeCounter();

            ScanDirectoryRecursive(root, options, excluded, cancellationToken, scannedDirs, totalDirs);

            ScanCompleted?.Invoke(root);
            return root;
        }

        private class ThreadSafeCounter
        {
            private int _value;
            public int Increment() => Interlocked.Increment(ref _value);
            public int Value => _value;
        }

        private int CountDirectories(string path, ScanOptions options, HashSet<string> excluded, CancellationToken cancellationToken)
        {
            try
            {
                if (IsExcludedPath(path, excluded)) return 0;
                if (!options.IncludeHidden || !options.IncludeSystem)
                {
                    try
                    {
                        var attr = new DirectoryInfo(path).Attributes;
                        if (!options.IncludeHidden && (attr & FileAttributes.Hidden) == FileAttributes.Hidden) return 0;
                        if (!options.IncludeSystem && (attr & FileAttributes.System) == FileAttributes.System) return 0;
                    }
                    catch { }
                }

                int count = 1;
                var dirs = Directory.GetDirectories(path);
                foreach (var dir in dirs)
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    _pauseEvent.Wait(cancellationToken);
                    try
                    {
                        count += CountDirectories(dir, options, excluded, cancellationToken);
                    }
                    catch { }
                }
                return count;
            }
            catch
            {
                return 1;
            }
        }

        private void ScanDirectoryRecursive(DirectoryNode node, ScanOptions options, HashSet<string> excluded, CancellationToken cancellationToken, ThreadSafeCounter scannedDirs, int totalDirs)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            try
            {
                if (IsExcludedPath(node.Path, excluded)) return;
                _pauseEvent.Wait(cancellationToken);

                CurrentPathChanged?.Invoke(node.Path);
                
                string[] files = Array.Empty<string>();
                if (options.IncludeFiles)
                {
                    try { files = Directory.GetFiles(node.Path); } catch { files = Array.Empty<string>(); }
                }
                long fileSize = 0;
                int fileCount = 0;
                var fileNodes = new List<DirectoryNode>();

                foreach (var file in files)
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    _pauseEvent.Wait(cancellationToken);
                    try
                    {
                        if (IsExcludedPath(file, excluded)) continue;
                        var info = new FileInfo(file);
                        if (info.Exists)
                        {
                            if (!options.IncludeHidden && (info.Attributes & FileAttributes.Hidden) == FileAttributes.Hidden) continue;
                            if (!options.IncludeSystem && (info.Attributes & FileAttributes.System) == FileAttributes.System) continue;

                            var logicalSize = info.Length;
                            if (logicalSize < options.MinFileSizeBytes) continue;

                            var compressedSize = DirectoryNode.GetCompressedFileSize(file);
                            if (compressedSize < 0) compressedSize = logicalSize;
                            
                            fileSize += logicalSize;
                            fileCount++;
                            
                            // Add file as a child node
                            var fileNode = new DirectoryNode
                            {
                                Path = file,
                                Name = Path.GetFileName(file),
                                IsDirectory = false,
                                Size = logicalSize,
                                CompressedSize = compressedSize,
                                Parent = node
                            };
                            fileNodes.Add(fileNode);
                        }
                    }
                    catch { }
                }

                // Add file nodes to children
                foreach (var fileNode in fileNodes)
                {
                    node.Children.Add(fileNode);
                }

                node.Size += fileSize;
                node.FileCount = fileCount;

                string[] subDirs;
                try { subDirs = Directory.GetDirectories(node.Path); } catch { subDirs = Array.Empty<string>(); }
                var childNodes = new List<DirectoryNode>();

                // Process subdirectories in parallel but collect results
                Parallel.ForEach(subDirs, new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = Environment.ProcessorCount
                }, subDir =>
                {
                    if (cancellationToken.IsCancellationRequested) return;

                    try
                    {
                        _pauseEvent.Wait(cancellationToken);
                        if (IsExcludedPath(subDir, excluded)) return;

                        if (!options.IncludeHidden || !options.IncludeSystem)
                        {
                            try
                            {
                                var attr = new DirectoryInfo(subDir).Attributes;
                                if (!options.IncludeHidden && (attr & FileAttributes.Hidden) == FileAttributes.Hidden) return;
                                if (!options.IncludeSystem && (attr & FileAttributes.System) == FileAttributes.System) return;
                            }
                            catch { }
                        }

                        var childNode = new DirectoryNode
                        {
                            Path = subDir,
                            Name = Path.GetFileName(subDir) ?? subDir,
                            IsDirectory = true,
                            Parent = node
                        };

                        ScanDirectoryRecursive(childNode, options, excluded, cancellationToken, scannedDirs, totalDirs);

                        lock (_lockObject)
                        {
                            childNodes.Add(childNode);
                        }
                    }
                    catch { }
                });

                // Add all children and update sizes after parallel processing completes
                lock (_lockObject)
                {
                    foreach (var childNode in childNodes)
                    {
                        node.Children.Add(childNode);
                        node.Size += childNode.Size;
                        node.DirectoryCount += childNode.DirectoryCount + 1;
                        node.FileCount += childNode.FileCount;
                    }
                }
                
                var currentScanned = scannedDirs.Increment();
                if (totalDirs > 0)
                {
                    var progress = (currentScanned / (double)totalDirs) * 100.0;
                    ProgressChanged?.Invoke(progress);
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Skip directories we can't access
            }
            catch (DirectoryNotFoundException)
            {
                // Skip directories that don't exist
            }
            catch
            {
                // Ignore other errors
            }
        }

        private static HashSet<string> NormalizeExcludedPaths(IEnumerable<string>? excludedPaths)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (excludedPaths == null) return set;

            foreach (var p in excludedPaths)
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                var normalized = p.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (normalized.Length == 0) continue;
                set.Add(normalized);
            }
            return set;
        }

        private static bool IsExcludedPath(string? path, HashSet<string> excluded)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (excluded.Count == 0) return false;

            var normalized = path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            foreach (var ex in excluded)
            {
                if (normalized.Equals(ex, StringComparison.OrdinalIgnoreCase)) return true;
                if (normalized.StartsWith(ex + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
                if (normalized.StartsWith(ex + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public class ScanOptions
        {
            public static ScanOptions Default { get; } = new ScanOptions();

            public bool IncludeFiles { get; set; } = true;
            public bool IncludeHidden { get; set; } = true;
            public bool IncludeSystem { get; set; } = true;
            public long MinFileSizeBytes { get; set; } = 0;
            public List<string> ExcludedPaths { get; set; } = new();
        }
    }
}

