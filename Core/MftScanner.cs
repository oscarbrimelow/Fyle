using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Fyle.Services;

namespace Fyle.Core
{
    /// <summary>
    /// Ultra-fast NTFS scanner using Master File Table (MFT)
    /// 10-100x faster than standard file system scanning
    /// </summary>
    public class MftScanner
    {
        public event Action<string>? StatusChanged;
        public event Action<int>? ProgressChanged;
        public event Action<DirectoryNode>? ScanCompleted;

        public bool IsScanning { get; private set; }
        private CancellationTokenSource? _cts;
        private readonly ManualResetEventSlim _pauseEvent = new(true);
        private volatile bool _isPaused;
        public bool IsPaused => _isPaused;
        private Scanner.ScanOptions _options = Scanner.ScanOptions.Default;
        private HashSet<string> _excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        
        private void Log(string message)
        {
            Logger.Log($"[MFT] {message}");
            System.Diagnostics.Debug.WriteLine($"[MFT] {message}");
        }

        #region Native API

        private const uint GENERIC_READ = 0x80000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;
        private const int FSCTL_ENUM_USN_DATA = 0x000900b3;
        private const int FSCTL_QUERY_USN_JOURNAL = 0x000900f4;
        private const int FILE_ATTRIBUTE_DIRECTORY = 0x10;
        private const int FILE_ATTRIBUTE_HIDDEN = 0x2;
        private const int FILE_ATTRIBUTE_SYSTEM = 0x4;

        [StructLayout(LayoutKind.Sequential)]
        private struct USN_JOURNAL_DATA_V0
        {
            public ulong UsnJournalID;
            public long FirstUsn;
            public long NextUsn;
            public long LowestValidUsn;
            public long MaxUsn;
            public ulong MaximumSize;
            public ulong AllocationDelta;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MFT_ENUM_DATA_V0
        {
            public ulong StartFileReferenceNumber;
            public long LowUsn;
            public long HighUsn;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct USN_RECORD_V2
        {
            public uint RecordLength;
            public ushort MajorVersion;
            public ushort MinorVersion;
            public ulong FileReferenceNumber;
            public ulong ParentFileReferenceNumber;
            public long Usn;
            public long TimeStamp;
            public uint Reason;
            public uint SourceInfo;
            public uint SecurityId;
            public uint FileAttributes;
            public ushort FileNameLength;
            public ushort FileNameOffset;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern SafeFileHandle CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            ref MFT_ENUM_DATA_V0 lpInBuffer,
            int nInBufferSize,
            IntPtr lpOutBuffer,
            int nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            IntPtr lpInBuffer,
            int nInBufferSize,
            out USN_JOURNAL_DATA_V0 lpOutBuffer,
            int nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll")]
        private static extern uint GetCompressedFileSizeW(
            [MarshalAs(UnmanagedType.LPWStr)] string lpFileName,
            out uint lpFileSizeHigh);

        #endregion

        private class MftEntry
        {
            public ulong FileReferenceNumber;
            public ulong ParentFileReferenceNumber;
            public string FileName = "";
            public bool IsDirectory;
            public uint FileAttributes;
            
            // NTFS file reference numbers have sequence number in upper 16 bits
            // We need just the file number (lower 48 bits) for tree building
            public ulong FileNumber => FileReferenceNumber & 0x0000FFFFFFFFFFFF;
            public ulong ParentFileNumber => ParentFileReferenceNumber & 0x0000FFFFFFFFFFFF;
        }
        
        // Root directory is FRN 5 in NTFS (after masking)
        private const ulong ROOT_FILE_NUMBER = 5;

        public void Cancel()
        {
            _cts?.Cancel();
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

        /// <summary>
        /// Check if MFT scanning is available for this drive
        /// </summary>
        public static bool IsMftAvailable(string drivePath)
        {
            try
            {
                var driveLetter = drivePath.TrimEnd('\\', ':');
                var driveInfo = new DriveInfo(driveLetter + ":");
                return driveInfo.IsReady && driveInfo.DriveFormat == "NTFS";
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Scan drive using MFT - extremely fast for NTFS drives
        /// </summary>
        public async Task<DirectoryNode?> ScanDriveAsync(string drivePath, CancellationToken cancellationToken = default)
        {
            return await ScanDriveAsync(drivePath, Scanner.ScanOptions.Default, cancellationToken);
        }

        public async Task<DirectoryNode?> ScanDriveAsync(string drivePath, Scanner.ScanOptions options, CancellationToken cancellationToken = default)
        {
            if (IsScanning) return null;

            IsScanning = true;
            _isPaused = false;
            _pauseEvent.Set();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _options = options ?? Scanner.ScanOptions.Default;
            _excluded = NormalizeExcludedPaths(_options.ExcludedPaths);

            try
            {
                return await Task.Run(() => ScanDriveInternal(drivePath), _cts.Token);
            }
            finally
            {
                IsScanning = false;
                _isPaused = false;
                _pauseEvent.Set();
            }
        }

        private DirectoryNode? ScanDriveInternal(string drivePath)
        {
            var driveLetter = drivePath.TrimEnd('\\', ':');
            Log($"Starting MFT scan for drive: {drivePath}");
            
            // Verify NTFS
            var driveInfo = new DriveInfo(driveLetter + ":");
            if (!driveInfo.IsReady)
            {
                Log("ERROR: Drive not ready");
                StatusChanged?.Invoke("Drive not ready");
                return null;
            }
            
            Log($"Drive format: {driveInfo.DriveFormat}");
            if (driveInfo.DriveFormat != "NTFS")
            {
                Log($"ERROR: Drive is {driveInfo.DriveFormat}, not NTFS");
                StatusChanged?.Invoke($"Drive is {driveInfo.DriveFormat}, not NTFS - MFT not available");
                return null;
            }

            StatusChanged?.Invoke("Opening volume for MFT access...");
            Log("Opening volume handle...");

            var volumePath = @"\\.\" + driveLetter + ":";
            
            using var handle = CreateFile(
                volumePath,
                GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                0,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                Log($"ERROR: Cannot open volume - Win32 Error {error}");
                StatusChanged?.Invoke($"Cannot open volume (Error {error}). Run as Administrator.");
                return null;
            }
            Log("Volume handle opened successfully");

            // Query USN Journal
            StatusChanged?.Invoke("Querying USN Journal...");
            Log("Querying USN Journal...");
            
            if (!DeviceIoControl(handle, FSCTL_QUERY_USN_JOURNAL, IntPtr.Zero, 0,
                out USN_JOURNAL_DATA_V0 journalData, Marshal.SizeOf<USN_JOURNAL_DATA_V0>(),
                out _, IntPtr.Zero))
            {
                var error = Marshal.GetLastWin32Error();
                Log($"ERROR: Cannot query USN journal - Win32 Error {error}");
                StatusChanged?.Invoke($"Cannot query USN journal (Error {error})");
                return null;
            }
            Log($"USN Journal queried - NextUsn: {journalData.NextUsn}");

            // Read all MFT entries
            StatusChanged?.Invoke("Reading Master File Table...");
            
            var entries = new Dictionary<ulong, MftEntry>();
            var mftData = new MFT_ENUM_DATA_V0
            {
                StartFileReferenceNumber = 0,
                LowUsn = 0,
                HighUsn = journalData.NextUsn
            };

            const int bufferSize = 128 * 1024; // 128KB buffer
            var buffer = Marshal.AllocHGlobal(bufferSize);

            try
            {
                int totalRecords = 0;
                var sw = System.Diagnostics.Stopwatch.StartNew();

                while (true)
                {
                    if (_cts?.Token.IsCancellationRequested == true) break;
                    _pauseEvent.Wait(_cts!.Token);

                    if (!DeviceIoControl(handle, FSCTL_ENUM_USN_DATA, ref mftData,
                        Marshal.SizeOf<MFT_ENUM_DATA_V0>(), buffer, bufferSize,
                        out uint bytesReturned, IntPtr.Zero))
                    {
                        break; // End of data
                    }

                    if (bytesReturned <= 8) break;

                    // First 8 bytes is the next USN
                    var nextUsn = Marshal.ReadInt64(buffer);
                    int offset = 8;

                    while (offset < bytesReturned)
                    {
                        if (offset + Marshal.SizeOf<USN_RECORD_V2>() > bytesReturned) break;
                        if (_cts?.Token.IsCancellationRequested == true) break;
                        _pauseEvent.Wait(_cts!.Token);

                        var recordPtr = IntPtr.Add(buffer, offset);
                        var recordLength = Marshal.ReadInt32(recordPtr);
                        
                        if (recordLength == 0 || offset + recordLength > bytesReturned) break;

                        var record = Marshal.PtrToStructure<USN_RECORD_V2>(recordPtr);
                        
                        // Read filename
                        var fileNamePtr = IntPtr.Add(recordPtr, record.FileNameOffset);
                        var fileName = Marshal.PtrToStringUni(fileNamePtr, record.FileNameLength / 2) ?? "";

                        if (!string.IsNullOrEmpty(fileName) && fileName != "." && fileName != "..")
                        {
                            entries[record.FileReferenceNumber] = new MftEntry
                            {
                                FileReferenceNumber = record.FileReferenceNumber,
                                ParentFileReferenceNumber = record.ParentFileReferenceNumber,
                                FileName = fileName,
                                IsDirectory = (record.FileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0,
                                FileAttributes = record.FileAttributes
                            };
                        }

                        offset += recordLength;
                        totalRecords++;

                        if (totalRecords % 100000 == 0)
                        {
                            StatusChanged?.Invoke($"Read {totalRecords:N0} MFT entries... ({sw.Elapsed.TotalSeconds:F1}s)");
                        }
                    }

                    mftData.StartFileReferenceNumber = (ulong)nextUsn;
                }

                Log($"MFT enumeration complete: {totalRecords:N0} total records, {entries.Count:N0} valid entries");
                StatusChanged?.Invoke($"Read {totalRecords:N0} entries in {sw.Elapsed.TotalSeconds:F1}s");
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            if (_cts?.Token.IsCancellationRequested == true)
            {
                Log("Scan cancelled by user");
                return null;
            }

            Log($"Building directory tree from {entries.Count:N0} entries...");
            // Build directory tree
            StatusChanged?.Invoke("Building directory tree...");
            var root = BuildDirectoryTree(entries, driveLetter + ":\\");
            
            Log($"Tree built - Root has {root.Children.Count} direct children");

            if (_cts?.Token.IsCancellationRequested == true)
            {
                Log("Scan cancelled by user during tree build");
                return null;
            }

            // Calculate sizes in parallel
            Log("Calculating file sizes...");
            StatusChanged?.Invoke("Calculating file sizes...");
            CalculateSizesParallel(root);

            Log($"Scan complete: {root.Children.Count} items, {FormatSize(root.Size)}");
            StatusChanged?.Invoke($"Scan complete: {root.Children.Count} items, {FormatSize(root.Size)}");
            ScanCompleted?.Invoke(root);
            
            return root;
        }

        private DirectoryNode BuildDirectoryTree(Dictionary<ulong, MftEntry> entries, string rootPath)
        {
            Log($"BuildDirectoryTree starting with {entries.Count} entries, rootPath={rootPath}");
            
            var root = new DirectoryNode
            {
                Path = rootPath,
                Name = rootPath,
                IsDirectory = true
            };

            // Create nodes dictionary for quick lookup - keyed by FILE NUMBER (not full reference)
            var nodes = new Dictionary<ulong, DirectoryNode>();
            nodes[ROOT_FILE_NUMBER] = root;

            // First pass: create all directory nodes (using masked file numbers)
            int dirCount = 0;
            foreach (var entry in entries.Values.Where(e => e.IsDirectory))
            {
                var fileNum = entry.FileNumber;
                if (fileNum == ROOT_FILE_NUMBER) continue;
                if (nodes.ContainsKey(fileNum)) continue; // Skip duplicates
                
                nodes[fileNum] = new DirectoryNode
                {
                    Name = entry.FileName,
                    IsDirectory = true
                };
                dirCount++;
            }
            Log($"First pass: Created {dirCount} directory nodes (plus root)");

            // Second pass: link directories and add files
            int processed = 0;
            int total = entries.Count;
            int linkedDirs = 0;
            int linkedFiles = 0;
            int orphanedEntries = 0;
            int rootDirectChildren = 0;

            foreach (var entry in entries.Values)
            {
                if (_cts?.Token.IsCancellationRequested == true) break;
                _pauseEvent.Wait(_cts!.Token);

                processed++;
                if (processed % 50000 == 0)
                {
                    ProgressChanged?.Invoke((int)((processed / (double)total) * 50));
                    StatusChanged?.Invoke($"Building tree: {processed:N0}/{total:N0}");
                }

                var fileNum = entry.FileNumber;
                var parentFileNum = entry.ParentFileNumber;
                
                // Skip root directory entry itself
                if (fileNum == ROOT_FILE_NUMBER) continue;
                
                // Skip system metadata files (FRN 0-15 are reserved)
                if (fileNum < 16 && !entry.IsDirectory) continue;

                // Find parent using masked file number
                DirectoryNode? parent = null;
                if (nodes.TryGetValue(parentFileNum, out var parentNode))
                {
                    parent = parentNode;
                    if (parentFileNum == ROOT_FILE_NUMBER)
                    {
                        rootDirectChildren++;
                    }
                }

                if (parent == null)
                {
                    orphanedEntries++;
                    continue;
                }

                if (entry.IsDirectory)
                {
                    if (nodes.TryGetValue(fileNum, out var dirNode))
                    {
                        dirNode.Parent = parent;
                        dirNode.Path = Path.Combine(parent.Path ?? rootPath, entry.FileName);
                        if (IsExcludedPath(dirNode.Path, _excluded))
                        {
                            nodes.Remove(fileNum);
                            continue;
                        }

                        if (!_options.IncludeHidden && (entry.FileAttributes & FILE_ATTRIBUTE_HIDDEN) != 0)
                        {
                            nodes.Remove(fileNum);
                            continue;
                        }

                        if (!_options.IncludeSystem && (entry.FileAttributes & FILE_ATTRIBUTE_SYSTEM) != 0)
                        {
                            nodes.Remove(fileNum);
                            continue;
                        }

                        parent.Children.Add(dirNode);
                        linkedDirs++;
                    }
                }
                else
                {
                    if (!_options.IncludeFiles) continue;
                    if (parent.Path != null)
                    {
                        var candidate = Path.Combine(parent.Path, entry.FileName);
                        if (IsExcludedPath(candidate, _excluded)) continue;
                    }
                    if (!_options.IncludeHidden && (entry.FileAttributes & FILE_ATTRIBUTE_HIDDEN) != 0) continue;
                    if (!_options.IncludeSystem && (entry.FileAttributes & FILE_ATTRIBUTE_SYSTEM) != 0) continue;

                    // File node
                    var fileNode = new DirectoryNode
                    {
                        Name = entry.FileName,
                        Path = Path.Combine(parent.Path ?? rootPath, entry.FileName),
                        IsDirectory = false,
                        Parent = parent
                    };
                    parent.Children.Add(fileNode);
                    linkedFiles++;
                }
            }

            Log($"Second pass complete: {linkedDirs} dirs linked, {linkedFiles} files linked, {orphanedEntries} orphaned");
            Log($"Direct root children found: {rootDirectChildren}");
            Log($"Root.Children.Count: {root.Children.Count}");
            
            // Debug: List first 10 root children
            if (root.Children.Count > 0)
            {
                var names = root.Children.Take(10).Select(c => c.Name);
                Log($"First root children: {string.Join(", ", names)}");
            }
            else
            {
                // Debug: Check if any entries have root as parent
                var rootParentEntries = entries.Values.Where(e => e.ParentFileNumber == ROOT_FILE_NUMBER).Take(10).ToList();
                Log($"Entries with root parent (ParentFileNumber=5): {rootParentEntries.Count}");
                foreach (var e in rootParentEntries)
                {
                    Log($"  - {e.FileName} (FRN={e.FileReferenceNumber}, Parent={e.ParentFileReferenceNumber}, FileNum={e.FileNumber}, ParentNum={e.ParentFileNumber})");
                }
            }
            
            return root;
        }

        private void CalculateSizesParallel(DirectoryNode root)
        {
            Log("CalculateSizesParallel starting...");
            
            // Collect all file nodes
            var allFiles = new ConcurrentBag<DirectoryNode>();
            CollectFiles(root, allFiles);

            int total = allFiles.Count;
            int processed = 0;
            long totalSize = 0;
            int errorCount = 0;

            Log($"Collected {total} files for size calculation");

            if (total == 0)
            {
                Log("WARNING: No files found to calculate sizes!");
                return;
            }

            // Get file sizes in parallel
            Parallel.ForEach(allFiles, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount * 2 }, file =>
            {
                if (_cts?.Token.IsCancellationRequested == true) return;
                _pauseEvent.Wait(_cts!.Token);

                try
                {
                    file.Size = GetFileSize(file.Path ?? "");
                    Interlocked.Add(ref totalSize, file.Size);
                }
                catch
                {
                    file.Size = 0;
                    Interlocked.Increment(ref errorCount);
                }

                var count = Interlocked.Increment(ref processed);
                if (count % 10000 == 0)
                {
                    ProgressChanged?.Invoke(50 + (int)((count / (double)total) * 50));
                    StatusChanged?.Invoke($"Calculating sizes: {count:N0}/{total:N0}");
                }
            });

            Log($"Size calculation complete: {total} files, {FormatSize(totalSize)} total, {errorCount} errors");

            // Calculate directory sizes (bottom-up)
            Log("Calculating directory sizes...");
            if (_options.MinFileSizeBytes > 0)
            {
                PruneSmallFiles(root, _options.MinFileSizeBytes);
            }
            CalculateDirectorySizes(root);
            Log($"Final root size: {FormatSize(root.Size)}");
        }

        private static void PruneSmallFiles(DirectoryNode node, long minBytes)
        {
            if (node.Children.Count == 0) return;

            for (int i = node.Children.Count - 1; i >= 0; i--)
            {
                var child = node.Children[i];
                if (child.IsDirectory)
                {
                    PruneSmallFiles(child, minBytes);
                }
                else
                {
                    if (child.Size < minBytes)
                    {
                        node.Children.RemoveAt(i);
                    }
                }
            }
        }

        private void CollectFiles(DirectoryNode node, ConcurrentBag<DirectoryNode> files)
        {
            foreach (var child in node.Children)
            {
                if (child.IsDirectory)
                {
                    CollectFiles(child, files);
                }
                else
                {
                    files.Add(child);
                }
            }
        }

        private void CalculateDirectorySizes(DirectoryNode node)
        {
            long size = 0;
            int fileCount = 0;

            foreach (var child in node.Children)
            {
                if (child.IsDirectory)
                {
                    CalculateDirectorySizes(child);
                }
                size += child.Size;
                fileCount += child.IsDirectory ? child.FileCount : 1;
            }

            node.Size = size;
            node.FileCount = fileCount;
        }

        private static long GetFileSize(string path)
        {
            if (string.IsNullOrEmpty(path)) return 0;

            try
            {
                // Use FileInfo - more reliable than GetCompressedFileSize for MFT scanning
                var info = new FileInfo(path);
                if (info.Exists)
                {
                    return info.Length;
                }
                return 0;
            }
            catch
            {
                return 0;
            }
        }

        private static string FormatSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len /= 1024;
            }
            return $"{len:0.##} {sizes[order]}";
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
    }
}
