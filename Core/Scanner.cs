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

        public event Action<string>? CurrentPathChanged;
        public event Action<double>? ProgressChanged;
        public event Action<DirectoryNode>? ScanCompleted;

        public bool IsScanning { get; private set; }

        public void Cancel()
        {
            _cancellationTokenSource?.Cancel();
        }

        public async Task<DirectoryNode?> ScanDriveAsync(string drivePath)
        {
            if (IsScanning)
                throw new InvalidOperationException("Scan already in progress");

            IsScanning = true;
            _cancellationTokenSource = new CancellationTokenSource();

            try
            {
                return await Task.Run(() => ScanDirectory(drivePath, _cancellationTokenSource.Token), _cancellationTokenSource.Token);
            }
            finally
            {
                IsScanning = false;
            }
        }

        private DirectoryNode ScanDirectory(string path, CancellationToken cancellationToken)
        {
            var root = new DirectoryNode
            {
                Path = path,
                Name = Path.GetFileName(path) ?? path,
                IsDirectory = true
            };

            var totalDirs = CountDirectories(path, cancellationToken);
            var scannedDirs = new ThreadSafeCounter();

            ScanDirectoryRecursive(root, cancellationToken, scannedDirs, totalDirs);

            ScanCompleted?.Invoke(root);
            return root;
        }

        private class ThreadSafeCounter
        {
            private int _value;
            public int Increment() => Interlocked.Increment(ref _value);
            public int Value => _value;
        }

        private int CountDirectories(string path, CancellationToken cancellationToken)
        {
            try
            {
                int count = 1;
                var dirs = Directory.GetDirectories(path);
                foreach (var dir in dirs)
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    try
                    {
                        count += CountDirectories(dir, cancellationToken);
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

        private void ScanDirectoryRecursive(DirectoryNode node, CancellationToken cancellationToken, ThreadSafeCounter scannedDirs, int totalDirs)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            try
            {
                CurrentPathChanged?.Invoke(node.Path);
                
                var files = Directory.GetFiles(node.Path);
                long fileSize = 0;
                int fileCount = 0;
                var fileNodes = new List<DirectoryNode>();

                foreach (var file in files)
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    try
                    {
                        var info = new FileInfo(file);
                        if (info.Exists)
                        {
                            var logicalSize = info.Length;
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

                var subDirs = Directory.GetDirectories(node.Path);
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
                        var childNode = new DirectoryNode
                        {
                            Path = subDir,
                            Name = Path.GetFileName(subDir) ?? subDir,
                            IsDirectory = true,
                            Parent = node
                        };

                        ScanDirectoryRecursive(childNode, cancellationToken, scannedDirs, totalDirs);

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
                
                System.Diagnostics.Debug.WriteLine($"ScanDirectoryRecursive: {node.Path} - Added {childNodes.Count} children, Total size: {node.Size}");

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
    }
}

