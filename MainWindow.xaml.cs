using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Fyle.Core;
using Fyle.Services;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Win32;
using static Fyle.Services.Logger;

namespace Fyle
{
    public partial class MainWindow : Window
    {
        private readonly Scanner _scanner = new();
        private readonly ThemeService _themeService = new();
        private readonly List<string> _recentScans = new();
        private DirectoryNode? _currentRoot;
        private string? _currentDrive;
        private bool _isDarkMode = false;
        private Stopwatch _scanTimer = new();
        private int _colorMode = 0; // 0=Size, 1=Type, 2=Age
        private bool _rightPanelVisible = true;
        private FileSystemWatcher? _fileWatcher;
        private System.Timers.Timer? _debounceTimer;
        private HashSet<string> _changedPaths = new();
        private bool _useMftScanning = false;
        private int _topItemsCount = 10;
        private bool _autoRefreshEnabled = false; // Disabled by default for performance
        private MftScanner? _mftScanner;
        private CancellationTokenSource? _scanCts;
        private CancellationTokenSource? _exportCts;
        private bool _scanUsedMft;
        private bool _scanIncludeHidden = true;
        private bool _scanIncludeSystem = true;
        private bool _scanIncludeFiles = true;
        private long _scanMinFileSizeBytes;
        private readonly List<string> _excludedPaths = new();
        private int _maxItemsToRender = 5000;
        private long _lastPathUpdateStamp;
        private long _lastProgressUpdateStamp;
        private bool _isLoadingSettings;
        private string _ownerFilter = "";
        private string _pendingOwnerFilter = "";
        private readonly Dictionary<string, string> _ownerCache = new(StringComparer.OrdinalIgnoreCase);

        private sealed class OwnerOption
        {
            public required string Display { get; init; }
            public required string Value { get; init; }
        }

        public MainWindow()
        {
            Log("MainWindow constructor starting");
            try
            {
                InitializeComponent();
                Log("InitializeComponent done");
                LoadDrives();
                Log("LoadDrives done");
                SetupScanner();
                Log("SetupScanner done");
                SetupKeyboardShortcuts();
                Log("SetupKeyboardShortcuts done");
                
                _themeService.SetTheme(ThemeService.Theme.Light);
                LoadSettings();
                UpdateStatisticsVisibility();
                UpdateLegend();

                TreemapViewControl.ExcludePathRequested += path => Dispatcher.Invoke(() => ExcludePath(path));
                
                // Handle command line auto-scan
                if (!string.IsNullOrEmpty(App.AutoScanPath))
                {
                    Loaded += async (s, e) => await ScanDrive(App.AutoScanPath);
                }
                
                // Apply MFT setting from command line
                if (App.UseMftScanning)
                {
                    _useMftScanning = true;
                }
                
                Log("MainWindow constructor complete");
            }
            catch (Exception ex)
            {
                LogError("MainWindow constructor", ex);
                throw;
            }
        }

        private void SetupFileWatcher(string path)
        {
            try
            {
                // Dispose old watcher
                _fileWatcher?.Dispose();
                _debounceTimer?.Dispose();
                
                // Only set up if auto-refresh is enabled
                if (!_autoRefreshEnabled) return;
                
                _fileWatcher = new FileSystemWatcher(path)
                {
                    NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName,
                    IncludeSubdirectories = true,
                    EnableRaisingEvents = true,
                    InternalBufferSize = 4096 // Smaller buffer = less memory
                };

                // Only watch for actual file/folder additions and deletions
                _fileWatcher.Created += OnFileSystemChanged;
                _fileWatcher.Deleted += OnFileSystemChanged;
                _fileWatcher.Renamed += OnFileSystemRenamed;
                
                // Debounce timer - only fires once after changes stop
                _debounceTimer = new System.Timers.Timer(5000); // 5 second delay (more efficient)
                _debounceTimer.AutoReset = false; // Only fire once
                _debounceTimer.Elapsed += async (s, e) =>
                {
                    // Use BeginInvoke to avoid blocking
                    await Dispatcher.BeginInvoke(new Action(async () =>
                    {
                        if (!_scanner.IsScanning && _currentRoot != null)
                        {
                            // Get changed paths and clear
                            List<string> pathsToUpdate;
                            lock (_changedPaths)
                            {
                                pathsToUpdate = _changedPaths.ToList();
                                _changedPaths.Clear();
                            }
                            
                            if (pathsToUpdate.Count > 0)
                            {
                                StatusText.Text = $"🔄 Updating {pathsToUpdate.Count} folder(s)...";
                                Log($"Incremental refresh for {pathsToUpdate.Count} paths");
                                
                                // Do incremental update on background thread
                                await Task.Run(() => IncrementalUpdate(pathsToUpdate));
                                
                                // Update UI after
                                StatusText.Text = "✓ Updated";
                            }
                        }
                    }));
                };
                
                Log($"FileWatcher started for: {path}");
            }
            catch (Exception ex)
            {
                LogError("SetupFileWatcher", ex);
            }
        }

        private void OnFileSystemChanged(object sender, FileSystemEventArgs e)
        {
            // Only trigger on create/delete (not every write)
            if (e.ChangeType == WatcherChangeTypes.Created || e.ChangeType == WatcherChangeTypes.Deleted)
            {
                Log($"File system change: {e.ChangeType} - {e.Name}");
                
                // Track the parent folder of the changed item
                var changedPath = Path.Combine(_currentDrive ?? "", e.Name ?? "");
                var parentPath = Path.GetDirectoryName(changedPath);
                if (!string.IsNullOrEmpty(parentPath))
                {
                    lock (_changedPaths)
                    {
                        _changedPaths.Add(parentPath);
                    }
                }
                
                TriggerDelayedRefresh();
            }
        }

        private void OnFileSystemRenamed(object sender, RenamedEventArgs e)
        {
            Log($"File system rename: {e.OldName} -> {e.Name}");
            
            var changedPath = Path.Combine(_currentDrive ?? "", e.Name ?? "");
            var parentPath = Path.GetDirectoryName(changedPath);
            if (!string.IsNullOrEmpty(parentPath))
            {
                lock (_changedPaths)
                {
                    _changedPaths.Add(parentPath);
                }
            }
            
            TriggerDelayedRefresh();
        }

        private void TriggerDelayedRefresh()
        {
            // Reset the timer - this debounces multiple rapid changes
            if (_debounceTimer != null)
            {
                _debounceTimer.Stop();
                _debounceTimer.Start();
                
                Dispatcher.Invoke(() =>
                {
                    StatusText.Text = "📁 Change detected, will refresh...";
                });
            }
        }

        private void SetupKeyboardShortcuts()
        {
            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape || e.Key == Key.Back)
                {
                    if (TreemapViewControl.CanGoBack())
                    {
                        TreemapViewControl.GoBack();
                        UpdateBreadcrumb();
                        BackButton.IsEnabled = TreemapViewControl.CanGoBack();
                    }
                    e.Handled = true;
                }
                else if (e.Key == Key.F5)
                {
                    if (!string.IsNullOrEmpty(_currentDrive) && !_scanner.IsScanning)
                    {
                        RescanButton_Click(this, new RoutedEventArgs());
                    }
                    e.Handled = true;
                }
                else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    SearchBox.Focus();
                    e.Handled = true;
                }
            };
        }

        private void LoadDrives()
        {
            var drives = DriveInfo.GetDrives()
                .Where(d => d.IsReady)
                .Select(d => new
                {
                    Name = $"{d.Name.TrimEnd('\\')} ({d.VolumeLabel})",
                    Path = d.Name,
                    FreeSpace = d.AvailableFreeSpace,
                    TotalSize = d.TotalSize,
                    FreeSpaceFormatted = $"{FormatBytes(d.AvailableFreeSpace)} free of {FormatBytes(d.TotalSize)}"
                })
                .ToList();

            DriveListBox.ItemsSource = drives;
        }

        private void SetupScanner()
        {
            _scanner.CurrentPathChanged += path =>
            {
                var now = Stopwatch.GetTimestamp();
                var last = Interlocked.Read(ref _lastPathUpdateStamp);
                if (now - last < Stopwatch.Frequency / 8) return;
                Interlocked.Exchange(ref _lastPathUpdateStamp, now);

                Dispatcher.BeginInvoke(() =>
                {
                    CurrentPathText.Text = TruncatePath(path, 40);
                    StatusText.Text = $"Scanning: {Path.GetFileName(path)}";
                });
            };

            _scanner.ProgressChanged += progress =>
            {
                var now = Stopwatch.GetTimestamp();
                var last = Interlocked.Read(ref _lastProgressUpdateStamp);
                if (now - last < Stopwatch.Frequency / 10 && progress < 99.9) return;
                Interlocked.Exchange(ref _lastProgressUpdateStamp, now);

                Dispatcher.BeginInvoke(() =>
                {
                    ScanProgressBar.Value = progress;
                    ProgressText.Text = $"{progress:F0}% • {_scanTimer.Elapsed:mm\\:ss}";
                });
            };
        }

        private void UpdateStats(DirectoryNode root)
        {
            var totalSize = FormatBytes(root.Size);
            var drive = DriveInfo.GetDrives().FirstOrDefault(d => d.Name == _currentDrive);
            
            if (drive != null)
            {
                var freeSpace = drive.AvailableFreeSpace;
                var totalSpace = drive.TotalSize;
                var usedSpace = totalSpace - freeSpace;
                var usedPercent = (usedSpace / (double)totalSpace) * 100;
                
                SizeInfoText.Text = $"Used: {FormatBytes(usedSpace)} ({usedPercent:F0}%)";
            }
            else
            {
                SizeInfoText.Text = $"Total: {totalSize}";
            }
            
            var currentNode = TreemapViewControl.CurrentNode;
            var visibleCount = TreemapViewControl.VisibleItemCount;
            ItemCountText.Text = $"{currentNode?.Children.Count ?? 0} items • {visibleCount} visible";
        }

        private void UpdateFileTypeStats(DirectoryNode root)
        {
            if (root == null) return;
            
            var allFiles = GetAllFiles(root, 10000); // Limit for performance
            var typeGroups = new Dictionary<string, (string Icon, string Name, long Size, SolidColorBrush Color)>
            {
                { "video", ("🎬", "Videos", 0, new SolidColorBrush(Color.FromRgb(229, 62, 62))) },
                { "image", ("🖼️", "Images", 0, new SolidColorBrush(Color.FromRgb(56, 161, 105))) },
                { "audio", ("🎵", "Audio", 0, new SolidColorBrush(Color.FromRgb(49, 130, 206))) },
                { "document", ("📄", "Documents", 0, new SolidColorBrush(Color.FromRgb(214, 158, 46))) },
                { "archive", ("📦", "Archives", 0, new SolidColorBrush(Color.FromRgb(128, 90, 213))) },
                { "application", ("💻", "Applications", 0, new SolidColorBrush(Color.FromRgb(237, 137, 54))) },
                { "other", ("📁", "Other", 0, new SolidColorBrush(Color.FromRgb(113, 128, 150))) }
            };

            foreach (var file in allFiles)
            {
                if (file?.Name == null) continue;
                var type = GetFileCategory(file.Name);
                if (typeGroups.ContainsKey(type))
                {
                    var current = typeGroups[type];
                    typeGroups[type] = (current.Icon, current.Name, current.Size + file.Size, current.Color);
                }
            }

            // Add folder sizes to "other"
            var filesSize = allFiles.Sum(f => f?.Size ?? 0);
            var folderSize = root.Size - filesSize;
            if (folderSize > 0)
            {
                var current = typeGroups["other"];
                typeGroups["other"] = (current.Icon, current.Name, current.Size + folderSize, current.Color);
            }

            var totalSize = root.Size > 0 ? root.Size : 1;
            var stats = typeGroups.Values
                .Where(t => t.Size > 0)
                .OrderByDescending(t => t.Size)
                .Select(t => new
                {
                    t.Icon,
                    t.Name,
                    Size = FormatBytes(t.Size),
                    Percentage = (t.Size / (double)totalSize) * 100,
                    t.Color
                })
                .ToList();

            FileTypeStats.ItemsSource = stats;
        }

        private void UpdateLargestFiles(DirectoryNode root)
        {
            if (root == null) return;

            var files = GetAllFiles(root, 10000)
                .Where(f => f != null && PassesActiveFilters(f))
                .OrderByDescending(f => f.Size)
                .Take(_topItemsCount)
                .Select(f => new { Name = f.Name ?? "Unknown", Path = f.Path ?? "", Size = FormatBytes(f.Size), Node = f })
                .ToList();

            LargestFilesList.ItemsSource = files;
        }

        private void UpdateLargestFolders(DirectoryNode root)
        {
            if (root == null) return;

            var folders = GetAllFolders(root, 50000)
                .Where(f => f != null && PassesActiveFilters(f))
                .OrderByDescending(f => f.Size)
                .Take(_topItemsCount)
                .Select(f => new { Name = f.Name ?? "Unknown", Path = f.Path ?? "", Size = FormatBytes(f.Size), Node = f })
                .ToList();

            LargestFoldersList.ItemsSource = folders;
        }

        private void FindDuplicates(DirectoryNode root)
        {
            if (root == null) return;
            
            var allFiles = GetAllFiles(root, 10000);
            var duplicates = allFiles
                .Where(f => f?.Name != null && f.Size > 1024 * 1024) // Only files > 1MB
                .GroupBy(f => (f.Name.ToLower(), f.Size))
                .Where(g => g.Count() > 1)
                .OrderByDescending(g => g.Key.Size * g.Count())
                .Take(10)
                .Select(g => new
                {
                    Name = g.First()?.Name ?? "Unknown",
                    Count = $"{g.Count()} copies",
                    TotalSize = FormatBytes(g.Key.Size * g.Count()),
                    Files = g.ToList()
                })
                .ToList();

            DuplicateFilesList.ItemsSource = duplicates;
            NoDuplicatesText.Visibility = duplicates.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private List<DirectoryNode> GetAllFiles(DirectoryNode node, int maxFiles = 50000)
        {
            var files = new List<DirectoryNode>();
            CollectFiles(node, files, maxFiles);
            return files;
        }

        private void CollectFiles(DirectoryNode node, List<DirectoryNode> files, int maxFiles)
        {
            if (node?.Children == null || files.Count >= maxFiles) return;
            
            foreach (var child in node.Children)
            {
                if (files.Count >= maxFiles) break;
                
                if (child == null) continue;
                
                if (!child.IsDirectory)
                {
                    files.Add(child);
                }
                else
                {
                    CollectFiles(child, files, maxFiles);
                }
            }
        }

        private string GetFileCategory(string fileName)
        {
            var ext = Path.GetExtension(fileName).ToLower();
            return ext switch
            {
                ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".flv" or ".webm" or ".m4v" => "video",
                ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".svg" or ".webp" or ".ico" or ".tiff" => "image",
                ".mp3" or ".wav" or ".flac" or ".aac" or ".ogg" or ".wma" or ".m4a" => "audio",
                ".pdf" or ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx" or ".txt" or ".rtf" or ".odt" => "document",
                ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".bz2" or ".xz" => "archive",
                ".exe" or ".msi" or ".dll" or ".app" or ".dmg" => "application",
                _ => "other"
            };
        }

        private List<DirectoryNode> GetAllFolders(DirectoryNode node, int maxFolders = 50000)
        {
            var folders = new List<DirectoryNode>();
            CollectFolders(node, folders, maxFolders);
            return folders;
        }

        private void CollectFolders(DirectoryNode node, List<DirectoryNode> folders, int maxFolders)
        {
            if (node?.Children == null || folders.Count >= maxFolders) return;

            foreach (var child in node.Children)
            {
                if (folders.Count >= maxFolders) break;
                if (child == null) continue;
                if (child.IsDirectory)
                {
                    folders.Add(child);
                    CollectFolders(child, folders, maxFolders);
                }
            }
        }

        private bool PassesActiveFilters(DirectoryNode node)
        {
            if (node == null) return false;
            if (IsExcluded(node.Path)) return false;

            var filterIndex = FileTypeFilter?.SelectedIndex ?? 0;
            var q = (SearchBox?.Text ?? "").Trim();
            var ownerFilter = GetOwnerFilterValue();

            if (node.IsDirectory)
            {
                if (filterIndex == 1)
                {
                    return q.Length == 0 || NameOrPathMatches(node, q);
                }

                var fileFiltersActive = filterIndex > 1 || !string.IsNullOrWhiteSpace(ownerFilter) || _scanMinFileSizeBytes > 0;
                if (!fileFiltersActive)
                {
                    if (q.Length == 0) return true;
                    return NameOrPathMatches(node, q);
                }

                if (q.Length > 0 && NameOrPathMatches(node, q) && filterIndex <= 1 && string.IsNullOrWhiteSpace(ownerFilter) && _scanMinFileSizeBytes <= 0)
                    return true;

                return DirectoryHasMatchingFileDescendant(node, filterIndex, ownerFilter, q);
            }

            return PassesActiveFiltersForFile(node, filterIndex, ownerFilter, q);
        }

        private bool PassesActiveFiltersForFile(DirectoryNode node, int filterIndex, string ownerFilter, string q)
        {
            if (node == null) return false;
            if (node.IsDirectory) return false;
            if (IsExcluded(node.Path)) return false;

            if (!_scanIncludeFiles) return false;
            if (_scanMinFileSizeBytes > 0 && node.Size < _scanMinFileSizeBytes) return false;

            if (filterIndex == 1) return false;
            if (filterIndex > 1)
            {
                if (!CategoryMatchesFilterIndex(node.Name ?? "", filterIndex)) return false;
            }

            if (!string.IsNullOrWhiteSpace(ownerFilter))
            {
                if (!OwnerMatches(node, ownerFilter)) return false;
            }

            if (q.Length > 0)
            {
                if (!NameOrPathMatches(node, q)) return false;
            }

            return true;
        }

        private static bool NameOrPathMatches(DirectoryNode node, string q)
        {
            if (string.IsNullOrWhiteSpace(q)) return true;
            var name = node.Name ?? "";
            var path = node.Path ?? "";
            return name.Contains(q, StringComparison.OrdinalIgnoreCase) || path.Contains(q, StringComparison.OrdinalIgnoreCase);
        }

        private bool DirectoryHasMatchingFileDescendant(DirectoryNode node, int filterIndex, string ownerFilter, string q)
        {
            const int maxDepth = 4;
            const int maxVisited = 2500;
            int visited = 0;

            var stack = new Stack<(DirectoryNode Node, int Depth)>();
            stack.Push((node, 0));

            while (stack.Count > 0)
            {
                var (current, depth) = stack.Pop();
                if (current == null) continue;
                if (visited++ > maxVisited) return true;
                if (IsExcluded(current.Path)) continue;
                if (current.Children == null || current.Children.Count == 0) continue;

                foreach (var child in current.Children)
                {
                    if (child == null) continue;
                    if (IsExcluded(child.Path)) continue;

                    if (child.IsDirectory)
                    {
                        if (depth < maxDepth) stack.Push((child, depth + 1));
                        continue;
                    }

                    if (PassesActiveFiltersForFile(child, filterIndex, ownerFilter, q)) return true;
                }
            }

            return false;
        }

        private string GetOwnerFilterValue()
        {
            if (OwnerFilter?.SelectedValue is string selectedValue) return selectedValue;
            if (OwnerFilter?.SelectedItem is OwnerOption opt) return opt.Value;
            return _ownerFilter;
        }

        private bool OwnerMatches(DirectoryNode node, string ownerFilter)
        {
            if (string.IsNullOrWhiteSpace(ownerFilter)) return true;
            if (string.IsNullOrWhiteSpace(node.Path)) return false;
            var owner = GetOwnerCached(node.Path, node.IsDirectory);
            return string.Equals(owner, ownerFilter, StringComparison.OrdinalIgnoreCase);
        }

        private string GetOwnerCached(string path, bool isDirectory)
        {
            if (_ownerCache.TryGetValue(path, out var cached)) return cached;
            var owner = GetOwner(path, isDirectory);
            _ownerCache[path] = owner;
            return owner;
        }

        private static string GetOwner(string path, bool isDirectory)
        {
            try
            {
                IdentityReference? identity;
                if (isDirectory)
                {
                    var acl = new DirectoryInfo(path).GetAccessControl();
                    identity = acl.GetOwner(typeof(NTAccount));
                }
                else
                {
                    var acl = new FileInfo(path).GetAccessControl();
                    identity = acl.GetOwner(typeof(NTAccount));
                }
                return identity?.Value ?? "";
            }
            catch
            {
                return "";
            }
        }

        private bool CategoryMatchesFilterIndex(string fileName, int filterIndex)
        {
            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            return filterIndex switch
            {
                2 => ext is ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".flv" or ".webm" or ".m4v",
                3 => ext is ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".svg" or ".webp" or ".ico" or ".tiff",
                4 => ext is ".mp3" or ".wav" or ".flac" or ".aac" or ".ogg" or ".wma" or ".m4a",
                5 => ext is ".pdf" or ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx" or ".txt" or ".rtf" or ".odt",
                6 => ext is ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".bz2" or ".xz",
                7 => ext is ".exe" or ".msi" or ".dll",
                _ => true
            };
        }

        private bool IsExcluded(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (_excludedPaths.Count == 0) return false;

            var normalized = path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            foreach (var ex in _excludedPaths)
            {
                if (normalized.Equals(ex, StringComparison.OrdinalIgnoreCase)) return true;
                if (normalized.StartsWith(ex + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
                if (normalized.StartsWith(ex + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private async void DriveListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DriveListBox.SelectedItem == null) return;

            dynamic selectedDrive = DriveListBox.SelectedItem;
            string drivePath = selectedDrive.Path;

            if (AdminElevation.RequiresElevation(drivePath) && !AdminElevation.IsAdministrator())
            {
                var result = MessageBox.Show(
                    "Administrator privileges recommended for full access.\n\nContinue without elevation?",
                    "Elevation Recommended",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.No)
                {
                    DriveListBox.SelectedItem = null;
                    return;
                }
            }

            await ScanDrive(drivePath);
        }

        private async System.Threading.Tasks.Task ScanDrive(string drivePath)
        {
            _currentDrive = drivePath;
            _scanCts?.Cancel();
            _scanCts?.Dispose();
            _scanCts = new CancellationTokenSource();
            _mftScanner = null;
            _scanUsedMft = false;
            Interlocked.Exchange(ref _lastPathUpdateStamp, 0);
            Interlocked.Exchange(ref _lastProgressUpdateStamp, 0);

            _scanTimer.Restart();
            CurrentPathText.Text = "Starting scan...";
            StatusText.Text = "Initializing...";
            ScanProgressBar.Visibility = Visibility.Visible;
            ScanProgressBar.Value = 0;
            CancelButton.Visibility = Visibility.Visible;
            CancelButton.Content = "✕ Cancel";
            PauseButton.Visibility = Visibility.Visible;
            PauseButton.Content = "⏸ Pause";
            ProgressText.Text = "0%";
            RescanButton.IsEnabled = false;
            SettingsExportButton.IsEnabled = false;
            BreadcrumbText.Text = drivePath;
            ScanTimeText.Text = "";

            // Clear stats
            FileTypeStats.ItemsSource = null;
            LargestFilesList.ItemsSource = null;
            LargestFoldersList.ItemsSource = null;
            DuplicateFilesList.ItemsSource = null;
            _ownerCache.Clear();

            try
            {
                DirectoryNode? root = null;
                var options = CreateScanOptions();
                
                // Try MFT scanning if enabled
                if (_useMftScanning && MftScanner.IsMftAvailable(drivePath))
                {
                    _scanUsedMft = true;
                    StatusText.Text = "⚡ MFT Fast Scan mode...";
                    _mftScanner = new MftScanner();
                    
                    _mftScanner.StatusChanged += status =>
                    {
                        var now = Stopwatch.GetTimestamp();
                        var last = Interlocked.Read(ref _lastPathUpdateStamp);
                        if (now - last < Stopwatch.Frequency / 8) return;
                        Interlocked.Exchange(ref _lastPathUpdateStamp, now);
                        Dispatcher.BeginInvoke(() => StatusText.Text = $"⚡ {status}");
                    };

                    _mftScanner.ProgressChanged += progress =>
                    {
                        var now = Stopwatch.GetTimestamp();
                        var last = Interlocked.Read(ref _lastProgressUpdateStamp);
                        if (now - last < Stopwatch.Frequency / 10 && progress < 99) return;
                        Interlocked.Exchange(ref _lastProgressUpdateStamp, now);
                        Dispatcher.BeginInvoke(() =>
                        {
                            ScanProgressBar.Value = progress;
                            ProgressText.Text = $"{progress}% • {_scanTimer.Elapsed:mm\\:ss}";
                        });
                    };
                    
                    root = await _mftScanner.ScanDriveAsync(drivePath, options, _scanCts.Token);
                    
                    if (root == null)
                    {
                        // MFT scan failed, fall back to standard
                        _scanUsedMft = false;
                        _mftScanner = null;
                        StatusText.Text = "MFT unavailable, using standard scan...";
                        root = await _scanner.ScanDriveAsync(drivePath, options, _scanCts.Token);
                    }
                }
                else
                {
                    // Standard scanning
                    root = await _scanner.ScanDriveAsync(drivePath, options, _scanCts.Token);
                }
                
                if (root != null)
                {
                    ProcessScanResult(root);
                }
            }
            catch (OperationCanceledException)
            {
                StatusText.Text = "Scan cancelled";
                ScanProgressBar.Visibility = Visibility.Collapsed;
                CancelButton.Visibility = Visibility.Collapsed;
                PauseButton.Visibility = Visibility.Collapsed;
                RescanButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                LogError("ScanDrive", ex);
                MessageBox.Show($"Error scanning drive: {ex.Message}", "Scan Error", MessageBoxButton.OK, MessageBoxImage.Error);
                ScanProgressBar.Visibility = Visibility.Collapsed;
                CancelButton.Visibility = Visibility.Collapsed;
                PauseButton.Visibility = Visibility.Collapsed;
                RescanButton.IsEnabled = true;
                StatusText.Text = "Scan failed";
            }
            finally
            {
                _mftScanner = null;
                _scanUsedMft = false;
                _scanCts?.Dispose();
                _scanCts = null;
            }
        }

        private Scanner.ScanOptions CreateScanOptions()
        {
            return new Scanner.ScanOptions
            {
                IncludeHidden = _scanIncludeHidden,
                IncludeSystem = _scanIncludeSystem,
                IncludeFiles = _scanIncludeFiles,
                MinFileSizeBytes = _scanMinFileSizeBytes,
                ExcludedPaths = _excludedPaths.ToList()
            };
        }

        private ExportService.ExportMetadata CreateExportMetadata()
        {
            var asm = Assembly.GetExecutingAssembly().GetName();
            var version = asm.Version?.ToString() ?? "";

            return new ExportService.ExportMetadata
            {
                AppVersion = version,
                ExportedAtUtc = DateTime.UtcNow,
                ScanTargetPath = _currentDrive ?? _currentRoot?.Path ?? "",
                UsedMft = _scanUsedMft,
                FilterText = (SearchBox?.Text ?? "").Trim(),
                FileTypeFilterIndex = FileTypeFilter?.SelectedIndex ?? 0,
                IncludeHidden = _scanIncludeHidden,
                IncludeSystem = _scanIncludeSystem,
                IncludeFiles = _scanIncludeFiles,
                MinFileSizeBytes = _scanMinFileSizeBytes,
                ExcludedPaths = _excludedPaths.ToList()
            };
        }

        private void ProcessScanResult(DirectoryNode root)
        {
            _scanTimer.Stop();
            _currentRoot = root;

            TreemapViewControl.SetColorMode(_colorMode);
            ApplyFiltersToTreemap();
            TreemapViewControl.DisplayNode(root, root);

            ScanProgressBar.Visibility = Visibility.Collapsed;
            CancelButton.Visibility = Visibility.Collapsed;
            PauseButton.Visibility = Visibility.Collapsed;
            ProgressText.Text = "";
            RescanButton.IsEnabled = true;
            SettingsExportButton.IsEnabled = true;
            BackButton.IsEnabled = false;

            UpdateBreadcrumb();
            UpdateStats(root);
            UpdateOwnerFilterOptions(root);

            try { UpdateFileTypeStats(root); } catch (Exception ex) { LogError("UpdateFileTypeStats", ex); }
            try { UpdateLargestFiles(root); } catch (Exception ex) { LogError("UpdateLargestFiles", ex); }
            try { UpdateStatistics(); } catch (Exception ex) { LogError("UpdateStatistics", ex); }
            try { FindDuplicates(root); } catch (Exception ex) { LogError("FindDuplicates", ex); }

            if (!string.IsNullOrEmpty(_currentDrive) && !_recentScans.Contains(_currentDrive))
            {
                _recentScans.Insert(0, _currentDrive);
                if (_recentScans.Count > 5)
                    _recentScans.RemoveAt(5);
                RecentScansListBox.ItemsSource = _recentScans.ToList();
            }

            ScanTimeText.Text = $"⏱ {_scanTimer.Elapsed:mm\\:ss}";
            StatusText.Text = _scanUsedMft ? $"⚡ MFT Scan complete • {root.Children.Count} items found" : $"Scan complete • {root.Children.Count} items found";
            SettingsSaveScanButton.IsEnabled = true;

            SetupFileWatcher(_currentDrive ?? "");
            SaveSettings();
        }

        private void RecentScansListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (RecentScansListBox.SelectedItem == null) return;
            
            string drivePath = RecentScansListBox.SelectedItem.ToString() ?? "";
            if (Directory.Exists(drivePath) || drivePath.EndsWith(":\\"))
            {
                var matchingDrive = DriveListBox.ItemsSource?
                    .Cast<dynamic>()
                    .FirstOrDefault(d => d.Path == drivePath);
                    
                if (matchingDrive != null)
                {
                    DriveListBox.SelectedItem = matchingDrive;
                }
            }
            
            RecentScansListBox.SelectedItem = null;
        }

        private async void RescanButton_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_currentDrive))
            {
                await ScanDrive(_currentDrive);
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            if (_exportCts != null)
            {
                _exportCts.Cancel();
                StatusText.Text = "Cancelling export...";
                return;
            }

            _scanCts?.Cancel();
            _mftScanner?.Cancel();
            _scanner.Cancel();
            StatusText.Text = "Cancelling...";
        }

        private void PauseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_scanCts == null) return;

            bool isPaused = _mftScanner != null ? _mftScanner.IsPaused : _scanner.IsPaused;
            if (isPaused)
            {
                _mftScanner?.Resume();
                _scanner.Resume();
                PauseButton.Content = "⏸ Pause";
                StatusText.Text = "Resuming...";
            }
            else
            {
                _mftScanner?.Pause();
                _scanner.Pause();
                PauseButton.Content = "▶ Resume";
                StatusText.Text = "Paused";
            }
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (TreemapViewControl.CanGoBack())
            {
                TreemapViewControl.GoBack();
                UpdateBreadcrumb();
                BackButton.IsEnabled = TreemapViewControl.CanGoBack();
                
                var currentNode = TreemapViewControl.CurrentNode;
                if (currentNode != null)
                {
                    ItemCountText.Text = $"{currentNode.Children.Count} items • {TreemapViewControl.VisibleItemCount} visible";
                }
                
                // Update other views
                if (SunburstViewRadio?.IsChecked == true) UpdateSunburstView();
                if (ListViewRadio?.IsChecked == true) UpdateListView();
            }
        }

        private async void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentRoot == null) return;

            var dialog = new SaveFileDialog
            {
                Filter = "CSV File|*.csv|JSON File|*.json|HTML Report|*.html|XML File|*.xml",
                FileName = $"Fyle_Export_{DateTime.Now:yyyyMMdd_HHmmss}"
            };

            if (dialog.ShowDialog() == true)
            {
                _exportCts?.Cancel();
                _exportCts?.Dispose();
                _exportCts = new CancellationTokenSource();

                try
                {
                    StatusText.Text = "Exporting...";
                    SettingsExportButton.IsEnabled = false;
                    CancelButton.Visibility = Visibility.Visible;
                    CancelButton.Content = "✕ Cancel Export";
                    PauseButton.Visibility = Visibility.Collapsed;
                    
                    var ext = System.IO.Path.GetExtension(dialog.FileName).ToLower();
                    var metadata = CreateExportMetadata();

                    await Task.Run(() =>
                    {
                        switch (ext)
                        {
                            case ".csv":
                                ExportService.ExportToCsv(_currentRoot, dialog.FileName, metadata, _exportCts.Token);
                                break;
                            case ".json":
                                ExportService.ExportToJson(_currentRoot, dialog.FileName, metadata, _exportCts.Token);
                                break;
                            case ".html":
                                ExportService.ExportToHtml(_currentRoot, dialog.FileName, $"Fyle Report - {_currentDrive}", metadata, _exportCts.Token);
                                break;
                            case ".xml":
                                ExportService.ExportToXml(_currentRoot, dialog.FileName, metadata, _exportCts.Token);
                                break;
                        }
                    }, _exportCts.Token);
                    
                    StatusText.Text = "Export completed!";
                    
                    // Ask to open the file
                    var result = MessageBox.Show("Export completed! Open the file?", "Export", 
                        MessageBoxButton.YesNo, MessageBoxImage.Information);
                    if (result == MessageBoxResult.Yes)
                    {
                        Process.Start(new ProcessStartInfo { FileName = dialog.FileName, UseShellExecute = true });
                    }
                }
                catch (OperationCanceledException)
                {
                    StatusText.Text = "Export cancelled";
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Export failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    StatusText.Text = "Export failed";
                }
                finally
                {
                    _exportCts?.Dispose();
                    _exportCts = null;
                    CancelButton.Content = "✕ Cancel";
                    CancelButton.Visibility = Visibility.Collapsed;
                    SettingsExportButton.IsEnabled = true;
                }
            }
        }

        private void SaveScanButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentRoot == null) return;

            var dialog = new SaveFileDialog
            {
                Filter = "Fyle Scan|*.fylescan",
                FileName = $"Scan_{_currentDrive?.Replace(":\\", "")}_{DateTime.Now:yyyyMMdd_HHmmss}"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    ScanDataManager.SaveScan(_currentRoot, dialog.FileName);
                    StatusText.Text = "Scan saved for comparison!";
                    MessageBox.Show("Scan saved! You can compare this with future scans to see what changed.", 
                        "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Save failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void OpenScanButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Fyle Scan|*.fylescan",
                Title = "Open a saved scan"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    var scan = ScanDataManager.LoadScan(dialog.FileName);
                    if (scan == null)
                    {
                        MessageBox.Show("Could not load scan file.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine("📂 SAVED SCAN");
                    sb.AppendLine($"Scan date: {scan.ScanDate:yyyy-MM-dd HH:mm}");
                    sb.AppendLine($"Drive: {scan.DrivePath}");
                    sb.AppendLine();
                    sb.AppendLine($"Total size: {FormatBytes(scan.TotalSize)}");
                    sb.AppendLine($"Files: {scan.TotalFiles:N0}");
                    sb.AppendLine($"Folders: {scan.TotalFolders:N0}");
                    sb.AppendLine();

                    if (scan.Folders != null && scan.Folders.Count > 0)
                    {
                        sb.AppendLine("TOP FOLDERS (snapshot):");
                        sb.AppendLine();

                        foreach (var folder in scan.Folders.OrderByDescending(f => f.Size).Take(20))
                        {
                            sb.AppendLine($"• {folder.Name}  {FormatBytes(folder.Size)}  ({folder.FileCount:N0} files)");
                            sb.AppendLine($"  {folder.Path}");
                        }
                    }
                    else
                    {
                        sb.AppendLine("No folder snapshot data found in this scan file.");
                    }

                    MessageBox.Show(sb.ToString(), "Saved Scan", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Open failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void CompareScanButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentRoot == null) return;

            var dialog = new OpenFileDialog
            {
                Filter = "Fyle Scan|*.fylescan",
                Title = "Select a previous scan to compare"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    var oldScan = ScanDataManager.LoadScan(dialog.FileName);
                    if (oldScan == null)
                    {
                        MessageBox.Show("Could not load scan file.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    var comparison = ScanDataManager.CompareScan(oldScan, _currentRoot);
                    ShowComparisonResults(oldScan, comparison);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Comparison failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void ShowComparisonResults(ScanDataManager.ScanSnapshot oldScan, List<ScanDataManager.ComparisonResult> results)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"📊 SCAN COMPARISON");
            sb.AppendLine($"Previous scan: {oldScan.ScanDate:yyyy-MM-dd HH:mm}");
            sb.AppendLine($"Current scan: {DateTime.Now:yyyy-MM-dd HH:mm}");
            sb.AppendLine();
            sb.AppendLine($"Previous size: {FormatBytes(oldScan.TotalSize)}");
            sb.AppendLine($"Current size: {FormatBytes(_currentRoot?.Size ?? 0)}");
            
            var diff = (_currentRoot?.Size ?? 0) - oldScan.TotalSize;
            sb.AppendLine($"Difference: {(diff >= 0 ? "+" : "")}{FormatBytes(diff)}");
            sb.AppendLine();
            
            if (results.Count > 0)
            {
                sb.AppendLine("TOP CHANGES:");
                sb.AppendLine();
                
                foreach (var r in results.Take(15))
                {
                    var sign = r.Difference >= 0 ? "+" : "";
                    var arrow = r.Difference >= 0 ? "📈" : "📉";
                    sb.AppendLine($"{arrow} {r.Name}");
                    sb.AppendLine($"   {FormatBytes(r.OldSize)} → {FormatBytes(r.NewSize)} ({sign}{FormatBytes(r.Difference)})");
                }
            }
            else
            {
                sb.AppendLine("No significant changes detected.");
            }

            MessageBox.Show(sb.ToString(), "Scan Comparison", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private async void FindDuplicatesButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentRoot == null) return;

            var result = MessageBox.Show(
                "This will scan for duplicate files using MD5 checksums.\n\n" +
                "This may take a while for large drives.\n\n" +
                "Continue?",
                "Find Duplicates",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes) return;

            try
            {
                StatusText.Text = "Finding duplicates...";
                SettingsOverlay.Visibility = Visibility.Collapsed; // Close settings panel
                
                var finder = new DuplicateFinder();
                finder.StatusChanged += status => Dispatcher.Invoke(() => StatusText.Text = status);
                finder.ProgressChanged += progress => Dispatcher.Invoke(() => 
                {
                    ScanProgressBar.Visibility = Visibility.Visible;
                    ScanProgressBar.Value = progress;
                });

                var duplicates = await finder.FindDuplicatesAsync(
                    _currentRoot, 
                    DuplicateFinder.HashAlgorithm.MD5,
                    1024 * 1024, // 1MB min
                    CancellationToken.None);

                ScanProgressBar.Visibility = Visibility.Collapsed;
                
                ShowDuplicateResults(duplicates);
            }
            catch (Exception ex)
            {
                ScanProgressBar.Visibility = Visibility.Collapsed;
                MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                StatusText.Text = "Duplicate search failed";
            }
        }

        private void ShowDuplicateResults(List<DuplicateFinder.DuplicateGroup> duplicates)
        {
            if (duplicates.Count == 0)
            {
                MessageBox.Show("No duplicate files found!", "Duplicates", MessageBoxButton.OK, MessageBoxImage.Information);
                StatusText.Text = "No duplicates found";
                return;
            }

            var totalWasted = duplicates.Sum(d => d.WastedSpace);
            
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"🔍 DUPLICATE FILES FOUND");
            sb.AppendLine();
            sb.AppendLine($"Found {duplicates.Count} groups of duplicates");
            sb.AppendLine($"Total wasted space: {FormatBytes(totalWasted)}");
            sb.AppendLine();
            sb.AppendLine("TOP DUPLICATES:");
            sb.AppendLine();
            
            foreach (var group in duplicates.Take(10))
            {
                sb.AppendLine($"📄 {System.IO.Path.GetFileName(group.FilePaths.FirstOrDefault() ?? "Unknown")}");
                sb.AppendLine($"   Size: {FormatBytes(group.FileSize)} × {group.FilePaths.Count} copies");
                sb.AppendLine($"   Wasted: {FormatBytes(group.WastedSpace)}");
                foreach (var path in group.FilePaths.Take(3))
                {
                    sb.AppendLine($"   • {path}");
                }
                if (group.FilePaths.Count > 3)
                {
                    sb.AppendLine($"   ... and {group.FilePaths.Count - 3} more");
                }
                sb.AppendLine();
            }

            MessageBox.Show(sb.ToString(), "Duplicate Files", MessageBoxButton.OK, MessageBoxImage.Information);
            StatusText.Text = $"Found {duplicates.Count} duplicate groups ({FormatBytes(totalWasted)} wasted)";
        }

        private void ChartTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ChartTypeCombo == null || PieChartContainer == null || FileTypeStats == null) return;
            
            bool showPie = ChartTypeCombo.SelectedIndex == 1;
            PieChartContainer.Visibility = showPie ? Visibility.Visible : Visibility.Collapsed;
            
            if (showPie && _currentRoot != null)
            {
                DrawPieChart();
            }
        }

        private void DrawPieChart()
        {
            PieChartCanvas.Children.Clear();
            if (_currentRoot == null) return;

            var allFiles = GetAllFiles(_currentRoot, 10000);
            var typeGroups = new Dictionary<string, (long Size, Color Color)>
            {
                { "Videos", (0, Color.FromRgb(229, 62, 62)) },
                { "Images", (0, Color.FromRgb(56, 161, 105)) },
                { "Audio", (0, Color.FromRgb(49, 130, 206)) },
                { "Documents", (0, Color.FromRgb(214, 158, 46)) },
                { "Archives", (0, Color.FromRgb(128, 90, 213)) },
                { "Apps", (0, Color.FromRgb(237, 137, 54)) },
                { "Other", (0, Color.FromRgb(113, 128, 150)) }
            };

            foreach (var file in allFiles.Where(f => f != null))
            {
                var type = GetFileCategory(file.Name ?? "");
                var key = type switch
                {
                    "video" => "Videos",
                    "image" => "Images",
                    "audio" => "Audio",
                    "document" => "Documents",
                    "archive" => "Archives",
                    "application" => "Apps",
                    _ => "Other"
                };
                var current = typeGroups[key];
                typeGroups[key] = (current.Size + file.Size, current.Color);
            }

            double total = typeGroups.Values.Sum(v => v.Size);
            if (total <= 0) return;

            double centerX = 80, centerY = 80, radius = 70;
            double startAngle = 0;

            foreach (var kvp in typeGroups.Where(k => k.Value.Size > 0).OrderByDescending(k => k.Value.Size))
            {
                double sweepAngle = (kvp.Value.Size / total) * 360;
                if (sweepAngle < 1) continue;

                var path = CreatePieSlice(centerX, centerY, radius, startAngle, sweepAngle, kvp.Value.Color);
                path.ToolTip = $"{kvp.Key}: {FormatBytes(kvp.Value.Size)} ({(kvp.Value.Size / total * 100):F1}%)";
                PieChartCanvas.Children.Add(path);
                startAngle += sweepAngle;
            }
        }

        private System.Windows.Shapes.Path CreatePieSlice(double cx, double cy, double r, double startAngle, double sweepAngle, Color color)
        {
            double startRad = startAngle * Math.PI / 180;
            double endRad = (startAngle + sweepAngle) * Math.PI / 180;

            double x1 = cx + r * Math.Cos(startRad);
            double y1 = cy + r * Math.Sin(startRad);
            double x2 = cx + r * Math.Cos(endRad);
            double y2 = cy + r * Math.Sin(endRad);

            var figure = new PathFigure { StartPoint = new Point(cx, cy), IsClosed = true };
            figure.Segments.Add(new LineSegment(new Point(x1, y1), true));
            figure.Segments.Add(new ArcSegment(
                new Point(x2, y2), new Size(r, r), 0, sweepAngle > 180, SweepDirection.Clockwise, true));

            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);

            return new System.Windows.Shapes.Path
            {
                Fill = new SolidColorBrush(color),
                Data = geometry,
                Stroke = new SolidColorBrush(Colors.White),
                StrokeThickness = 1,
                Cursor = Cursors.Hand
            };
        }

        private void TogglePanelButton_Click(object sender, RoutedEventArgs e)
        {
            _rightPanelVisible = !_rightPanelVisible;
            RightPanelColumn.Width = _rightPanelVisible ? new GridLength(280) : new GridLength(0);
            RightPanel.Visibility = _rightPanelVisible ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyFiltersAndRefreshViews();
        }

        private void FileTypeFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FileTypeFilter == null) return;
            ApplyFiltersAndRefreshViews();
        }

        private void OwnerFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (OwnerFilter == null) return;
            _ownerFilter = GetOwnerFilterValue();
            ApplyFiltersAndRefreshViews();
        }

        private void ClearFilterButton_Click(object sender, RoutedEventArgs e)
        {
            SearchBox.Text = "";
            FileTypeFilter.SelectedIndex = 0;
            _ownerFilter = "";
            if (OwnerFilter != null) OwnerFilter.SelectedValue = "";
            ApplyFiltersAndRefreshViews();
        }

        private void ExportFilteredButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentRoot == null) return;

            var baseNode = TreemapViewControl?.CurrentNode ?? _currentRoot;
            if (baseNode == null) return;

            var dialog = new SaveFileDialog
            {
                Filter = "CSV File|*.csv",
                FileName = $"Fyle_Export_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
            };

            if (dialog.ShowDialog() != true) return;

            try
            {
                var filePath = dialog.FileName;
                var maxFiles = 100000;
                var maxFolders = 100000;

                var files = GetAllFiles(baseNode, maxFiles).Where(n => n != null && PassesActiveFilters(n)).ToList();
                var folders = GetAllFolders(baseNode, maxFolders).Where(n => n != null && PassesActiveFilters(n)).ToList();

                var all = folders.Concat(files).OrderByDescending(n => n.Size).ToList();

                using (var writer = new StreamWriter(filePath, false, Encoding.UTF8))
                {
                    writer.WriteLine("Path,Name,IsDirectory,SizeBytes,Size,Owner,Created,Modified,Accessed");
                    foreach (var n in all)
                    {
                        var path = n.Path ?? "";
                        var name = n.Name ?? "";
                        var owner = string.IsNullOrWhiteSpace(path) ? "" : GetOwnerCached(path, n.IsDirectory);
                        var created = GetCreationTimeSafe(path, n.IsDirectory);
                        var modified = GetLastWriteTimeSafe(path, n.IsDirectory);
                        var accessed = GetLastAccessTimeSafe(path, n.IsDirectory);

                        writer.WriteLine(
                            $"{EscapeCsv(path)}," +
                            $"{EscapeCsv(name)}," +
                            $"{(n.IsDirectory ? "true" : "false")}," +
                            $"{n.Size}," +
                            $"{EscapeCsv(FormatBytes(n.Size))}," +
                            $"{EscapeCsv(owner)}," +
                            $"{EscapeCsv(FormatDateTime(created))}," +
                            $"{EscapeCsv(FormatDateTime(modified))}," +
                            $"{EscapeCsv(FormatDateTime(accessed))}");
                    }
                }

                StatusText.Text = $"Exported {all.Count:N0} item(s)";
                Clipboard.SetText(filePath);
            }
            catch (Exception ex)
            {
                LogError("ExportFilteredButton_Click", ex);
                MessageBox.Show($"Export failed: {ex.Message}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static string EscapeCsv(string value)
        {
            value ??= "";
            if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
            {
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            }
            return value;
        }

        private static string FormatDateTime(DateTime dt)
        {
            if (dt == DateTime.MinValue || dt == DateTime.MaxValue) return "";
            return dt.ToString("yyyy-MM-dd HH:mm");
        }

        private static DateTime GetLastWriteTimeSafe(string path, bool isDirectory)
        {
            if (string.IsNullOrWhiteSpace(path)) return DateTime.MinValue;
            try { return isDirectory ? Directory.GetLastWriteTime(path) : File.GetLastWriteTime(path); } catch { return DateTime.MinValue; }
        }

        private static DateTime GetCreationTimeSafe(string path, bool isDirectory)
        {
            if (string.IsNullOrWhiteSpace(path)) return DateTime.MinValue;
            try { return isDirectory ? Directory.GetCreationTime(path) : File.GetCreationTime(path); } catch { return DateTime.MinValue; }
        }

        private static DateTime GetLastAccessTimeSafe(string path, bool isDirectory)
        {
            if (string.IsNullOrWhiteSpace(path)) return DateTime.MinValue;
            try { return isDirectory ? Directory.GetLastAccessTime(path) : File.GetLastAccessTime(path); } catch { return DateTime.MinValue; }
        }

        private void ApplyFiltersToTreemap()
        {
            TreemapViewControl.SetFilter(
                SearchBox?.Text,
                FileTypeFilter?.SelectedIndex ?? 0,
                _scanMinFileSizeBytes,
                _scanIncludeFiles,
                GetOwnerFilterValue(),
                _excludedPaths,
                _maxItemsToRender);
        }

        private void ApplyFiltersAndRefreshViews()
        {
            if (TreemapViewControl == null) return;

            ApplyFiltersToTreemap();

            var root = _currentRoot;
            var current = TreemapViewControl.CurrentNode ?? root;
            if (root != null && current != null)
            {
                TreemapViewControl.DisplayNode(current, root);
                UpdateStats(root);
                UpdateCurrentNodePanels(current);
            }

            if (SunburstViewRadio?.IsChecked == true) UpdateSunburstView();
            if (ListViewRadio?.IsChecked == true) UpdateListView();

            if (!_isLoadingSettings) SaveSettings();
        }

        private void UpdateCurrentNodePanels(DirectoryNode node)
        {
            try { UpdateLargestFiles(node); } catch (Exception ex) { LogError("UpdateLargestFiles(current)", ex); }
            try { UpdateLargestFolders(node); } catch (Exception ex) { LogError("UpdateLargestFolders(current)", ex); }
            try { UpdateStatistics(); } catch (Exception ex) { LogError("UpdateStatistics", ex); }
        }

        private void UpdateOwnerFilterOptions(DirectoryNode node)
        {
            if (OwnerFilter == null) return;

            var previous = GetOwnerFilterValue();
            var candidates = node.Children?.Where(c => c != null && !IsExcluded(c.Path) && !string.IsNullOrWhiteSpace(c.Path)).Take(1500).ToList() ?? new List<DirectoryNode>();

            var groups = candidates
                .Select(c => new { Node = c, Owner = GetOwnerCached(c.Path ?? "", c.IsDirectory) })
                .Where(x => !string.IsNullOrWhiteSpace(x.Owner))
                .GroupBy(x => x.Owner, StringComparer.OrdinalIgnoreCase)
                .Select(g => new { Owner = g.Key, Size = g.Sum(x => x.Node.Size) })
                .OrderByDescending(x => x.Size)
                .Take(40)
                .ToList();

            var options = new List<OwnerOption>
            {
                new OwnerOption { Display = "All Owners", Value = "" }
            };

            foreach (var g in groups)
            {
                options.Add(new OwnerOption { Display = $"{g.Owner} • {FormatBytes(g.Size)}", Value = g.Owner });
            }

            OwnerFilter.ItemsSource = options;

            var desired = !string.IsNullOrWhiteSpace(_pendingOwnerFilter) ? _pendingOwnerFilter : previous;
            _pendingOwnerFilter = "";
            OwnerFilter.SelectedValue = desired;
            _ownerFilter = desired;
        }

        private void ColorModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TreemapViewControl == null || ColorModeCombo == null) return;
            
            _colorMode = ColorModeCombo.SelectedIndex;
            TreemapViewControl.SetColorMode(_colorMode);
            UpdateLegend();
            
            if (_currentRoot != null && TreemapViewControl.CurrentNode != null)
            {
                TreemapViewControl.DisplayNode(TreemapViewControl.CurrentNode, _currentRoot);
            }
        }

        private void UpdateLegend()
        {
            if (LegendPanel == null) return;
            
            LegendPanel.Children.Clear();
            
            switch (_colorMode)
            {
                case 0: // Size
                    AddLegendItem("#48BB78", "Large files/folders");
                    AddLegendItem("#ECC94B", "Medium files/folders");
                    AddLegendItem("#E53E3E", "Small files/folders");
                    break;
                    
                case 1: // File Type
                    AddLegendItem("#E53E3E", "🎬 Videos");
                    AddLegendItem("#38A169", "🖼️ Images");
                    AddLegendItem("#3182CE", "🎵 Audio");
                    AddLegendItem("#D69E2E", "📄 Documents");
                    AddLegendItem("#805AD5", "📦 Archives");
                    AddLegendItem("#ED8936", "💻 Applications");
                    AddLegendItem("#718096", "📁 Folders/Other");
                    break;
                    
                case 2: // Age
                    AddLegendItem("#48BB78", "New (< 7 days)");
                    AddLegendItem("#ECC94B", "Recent (< 30 days)");
                    AddLegendItem("#ED8936", "Old (< 1 year)");
                    AddLegendItem("#E53E3E", "Very old (> 1 year)");
                    break;
            }
        }

        private void IncrementalUpdate(List<string> changedPaths)
        {
            try
            {
                // Do heavy work on background thread
                foreach (var path in changedPaths)
                {
                    // Find the node in our tree
                    var node = FindNodeByPath(_currentRoot, path);
                    if (node != null)
                    {
                        Log($"Updating node: {path}");
                        
                        // Store old size for parent update
                        long oldSize = node.Size;
                        
                        // Rescan just this folder
                        RescanSingleFolder(node);
                        
                        // Update parent sizes up the tree
                        long sizeDiff = node.Size - oldSize;
                        UpdateParentSizes(node.Parent, sizeDiff);
                    }
                }
                
                // UI updates must happen on UI thread
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        // Refresh the treemap display
                        if (TreemapViewControl.CurrentNode != null)
                        {
                            TreemapViewControl.DisplayNode(TreemapViewControl.CurrentNode, _currentRoot);
                        }
                        
                        // Update active view
                        if (SunburstViewRadio?.IsChecked == true) UpdateSunburstView();
                        if (ListViewRadio?.IsChecked == true) UpdateListView();
                        
                        // Update stats (lightweight only)
                        if (_currentRoot != null)
                        {
                            UpdateStats(_currentRoot);
                        }
                    }
                    catch { }
                }));
            }
            catch (Exception ex)
            {
                LogError("IncrementalUpdate", ex);
                StatusText.Text = "Update failed";
            }
        }

        private DirectoryNode? FindNodeByPath(DirectoryNode? root, string path)
        {
            if (root == null) return null;
            if (root.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) return root;
            
            foreach (var child in root.Children)
            {
                if (child.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
                    return child;
                
                if (child.IsDirectory && path.StartsWith(child.Path, StringComparison.OrdinalIgnoreCase))
                {
                    var found = FindNodeByPath(child, path);
                    if (found != null) return found;
                }
            }
            
            return null;
        }

        private void RescanSingleFolder(DirectoryNode node)
        {
            try
            {
                if (!Directory.Exists(node.Path)) return;
                
                // Clear existing children
                node.Children.Clear();
                node.Size = 0;
                node.FileCount = 0;
                
                // Scan files in this folder
                var files = Directory.GetFiles(node.Path);
                foreach (var file in files)
                {
                    try
                    {
                        var info = new FileInfo(file);
                        if (info.Exists)
                        {
                            var fileNode = new DirectoryNode
                            {
                                Path = file,
                                Name = Path.GetFileName(file),
                                IsDirectory = false,
                                Size = info.Length,
                                Parent = node
                            };
                            node.Children.Add(fileNode);
                            node.Size += info.Length;
                            node.FileCount++;
                        }
                    }
                    catch { }
                }
                
                // Scan subdirectories (but don't recurse deep - just get their existing sizes)
                var dirs = Directory.GetDirectories(node.Path);
                foreach (var dir in dirs)
                {
                    try
                    {
                        // Check if we already have this child
                        var existingChild = _currentRoot != null ? FindNodeByPath(_currentRoot, dir) : null;
                        
                        if (existingChild != null)
                        {
                            // Reuse existing scanned data
                            existingChild.Parent = node;
                            node.Children.Add(existingChild);
                            node.Size += existingChild.Size;
                            node.FileCount += existingChild.FileCount;
                        }
                        else
                        {
                            // New folder - do a quick size calculation
                            var childNode = new DirectoryNode
                            {
                                Path = dir,
                                Name = Path.GetFileName(dir),
                                IsDirectory = true,
                                Parent = node
                            };
                            childNode.Size = GetDirectorySize(dir);
                            node.Children.Add(childNode);
                            node.Size += childNode.Size;
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                LogError("RescanSingleFolder", ex);
            }
        }

        private long GetDirectorySize(string path)
        {
            long size = 0;
            try
            {
                foreach (var file in Directory.GetFiles(path))
                {
                    try { size += new FileInfo(file).Length; } catch { }
                }
                foreach (var dir in Directory.GetDirectories(path))
                {
                    size += GetDirectorySize(dir);
                }
            }
            catch { }
            return size;
        }

        private void UpdateParentSizes(DirectoryNode? parent, long sizeDiff)
        {
            while (parent != null)
            {
                parent.Size += sizeDiff;
                parent = parent.Parent;
            }
        }

        private void AddLegendItem(string color, string text)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            
            var colorBox = new Border
            {
                Width = 12,
                Height = 12,
                CornerRadius = new CornerRadius(2),
                Background = new BrushConverter().ConvertFromString(color) as Brush
            };
            
            var label = new TextBlock
            {
                Text = text,
                FontSize = 10,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("TextSecondaryBrush")
            };
            
            Grid.SetColumn(colorBox, 0);
            Grid.SetColumn(label, 1);
            
            grid.Children.Add(colorBox);
            grid.Children.Add(label);
            
            LegendPanel.Children.Add(grid);
        }

        private void LargestFile_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border && border.DataContext != null)
            {
                dynamic item = border.DataContext;
                string path = item.Path;
                
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{path}\"",
                        UseShellExecute = true
                    });
                }
                catch { }
            }
        }

        private void StatsItem_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            if (sender is not FrameworkElement element) return;
            if (element.DataContext == null) return;

            string path = "";
            string name = "";
            DirectoryNode? node = null;

            try
            {
                dynamic data = element.DataContext;
                try { path = data.Path as string ?? data.Path; } catch { }
                try { name = data.Name as string ?? data.Name; } catch { }
                try { node = data.Node as DirectoryNode; } catch { }
            }
            catch { }

            if (node == null && !string.IsNullOrWhiteSpace(path) && _currentRoot != null)
            {
                node = FindNodeByPath(_currentRoot, path);
            }

            if (node != null)
            {
                path = node.Path;
                name = node.Name;
            }

            if (string.IsNullOrWhiteSpace(path)) return;
            if (string.IsNullOrWhiteSpace(name)) name = System.IO.Path.GetFileName(path);

            bool isDirectory = node?.IsDirectory == true || Directory.Exists(path);
            element.ContextMenu = BuildStatsItemContextMenu(node, path, name, isDirectory);
        }

        private ContextMenu BuildStatsItemContextMenu(DirectoryNode? node, string path, string name, bool isDirectory)
        {
            var menu = new ContextMenu();

            if (isDirectory)
            {
                var openItem = new MenuItem { Header = "📂 Open in Explorer" };
                openItem.Click += (s, e) => OpenPath(path);
                menu.Items.Add(openItem);

                if (node != null && _currentRoot != null)
                {
                    var zoomItem = new MenuItem { Header = "🔍 Zoom Into Folder" };
                    zoomItem.Click += (s, e) =>
                    {
                        TreemapViewControl.DisplayNode(node, _currentRoot);
                        UpdateBreadcrumb();
                        BackButton.IsEnabled = TreemapViewControl.CanGoBack();
                    };
                    menu.Items.Add(zoomItem);
                }

                var excludeItem = new MenuItem { Header = "🚫 Exclude Folder" };
                excludeItem.Click += (s, e) => ExcludePath(path);
                menu.Items.Add(excludeItem);
            }
            else
            {
                var openFileItem = new MenuItem { Header = "📄 Open File" };
                openFileItem.Click += (s, e) => OpenPath(path);
                menu.Items.Add(openFileItem);
            }

            menu.Items.Add(new Separator());

            var showItem = new MenuItem { Header = "📍 Show in Explorer" };
            showItem.Click += (s, e) => ShowInExplorer(path);
            menu.Items.Add(showItem);

            var copyPathItem = new MenuItem { Header = "📋 Copy Path" };
            copyPathItem.Click += (s, e) => Clipboard.SetText(path);
            menu.Items.Add(copyPathItem);

            var copyNameItem = new MenuItem { Header = "📝 Copy Name" };
            copyNameItem.Click += (s, e) => Clipboard.SetText(name);
            menu.Items.Add(copyNameItem);

            menu.Items.Add(new Separator());

            var propsItem = new MenuItem { Header = "ℹ️ Properties" };
            propsItem.Click += (s, e) =>
            {
                try { Fyle.UI.NativeMethods.ShowFileProperties(path); } catch { }
            };
            menu.Items.Add(propsItem);

            menu.Items.Add(new Separator());

            var deleteItem = new MenuItem
            {
                Header = "🗑️ Delete (Recycle Bin)",
                Foreground = new SolidColorBrush(Color.FromRgb(248, 113, 113))
            };
            deleteItem.Click += (s, e) => DeletePathFromStats(node, path, name, isDirectory);
            menu.Items.Add(deleteItem);

            return menu;
        }

        private void OpenPath(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            }
            catch { }
        }

        private void ShowInExplorer(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true
                });
            }
            catch { }
        }

        private void DeletePathFromStats(DirectoryNode? node, string path, string name, bool isDirectory)
        {
            var result = MessageBox.Show(
                $"Move to Recycle Bin?\n\n{path}",
                "Confirm Delete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes) return;

            try
            {
                if (isDirectory)
                    FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                else
                    FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);

                if (_currentRoot != null)
                {
                    node ??= FindNodeByPath(_currentRoot, path);
                    if (node != null)
                        RemoveNodeFromTree(node);
                }

                RefreshAfterTreeMutation();
                StatusText.Text = $"Deleted: {name}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not delete: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RemoveNodeFromTree(DirectoryNode node)
        {
            var parent = node.Parent;
            if (parent != null)
            {
                parent.Children.Remove(node);
            }

            long sizeDelta = node.Size;
            int fileDelta = node.IsDirectory ? node.FileCount : 1;
            int dirDelta = node.IsDirectory ? node.DirectoryCount + 1 : 0;

            while (parent != null)
            {
                parent.Size = Math.Max(0, parent.Size - sizeDelta);
                parent.FileCount = Math.Max(0, parent.FileCount - fileDelta);
                parent.DirectoryCount = Math.Max(0, parent.DirectoryCount - dirDelta);
                parent = parent.Parent;
            }
        }

        private void RefreshAfterTreeMutation()
        {
            if (_currentRoot == null) return;

            ApplyFiltersToTreemap();

            var current = TreemapViewControl?.CurrentNode ?? _currentRoot;
            TreemapViewControl?.DisplayNode(current, _currentRoot);
            UpdateBreadcrumb();
            UpdateStats(_currentRoot);

            try { UpdateFileTypeStats(_currentRoot); } catch (Exception ex) { LogError("UpdateFileTypeStats(refresh)", ex); }
            try { UpdateStatistics(); } catch (Exception ex) { LogError("UpdateStatistics(refresh)", ex); }
        }

        private void TreemapViewControl_NodeSelected(DirectoryNode node)
        {
            UpdateBreadcrumb();
            BackButton.IsEnabled = TreemapViewControl.CanGoBack();
            ItemCountText.Text = $"{node.Children.Count} items • {TreemapViewControl.VisibleItemCount} visible";
            StatusText.Text = $"Viewing: {node.Name} ({node.FormattedSize})";
            UpdateOwnerFilterOptions(node);
            UpdateCurrentNodePanels(node);

            if (ChartTypeCombo?.SelectedIndex == 1)
            {
                DrawPieChart();
            }

            if (SunburstViewRadio?.IsChecked == true) UpdateSunburstView();
            if (ListViewRadio?.IsChecked == true) UpdateListView();
        }

        private void UpdateBreadcrumb()
        {
            var currentNode = TreemapViewControl.CurrentNode;
            if (currentNode != null)
            {
                BreadcrumbText.Text = currentNode.Path;
            }
        }

        // Settings Panel Methods
        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            SettingsOverlay.Visibility = Visibility.Visible;
            DarkModeToggle.IsChecked = _isDarkMode;
            MftModeToggle.IsChecked = _useMftScanning;
            AutoRefreshToggle.IsChecked = _autoRefreshEnabled;

            IncludeHiddenToggle.IsChecked = _scanIncludeHidden;
            IncludeSystemToggle.IsChecked = _scanIncludeSystem;
            IncludeFilesToggle.IsChecked = _scanIncludeFiles;
            MinFileSizeTextBox.Text = $"{(_scanMinFileSizeBytes / (1024.0 * 1024.0)):0.##}";
            ExcludedPathsTextBox.Text = string.Join(Environment.NewLine, _excludedPaths);
        }

        private void CloseSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            SettingsOverlay.Visibility = Visibility.Collapsed;
        }

        private void SettingsOverlay_Background_Click(object sender, MouseButtonEventArgs e)
        {
            // Only close if clicking on the dark background, not the settings panel itself
            if (e.OriginalSource == sender)
            {
                SettingsOverlay.Visibility = Visibility.Collapsed;
            }
        }

        private void DarkModeToggle_Click(object sender, RoutedEventArgs e)
        {
            // Guard against being called during InitializeComponent
            if (TreemapViewControl == null) return;
            
            _isDarkMode = DarkModeToggle.IsChecked == true;
            _themeService.SetTheme(_isDarkMode ? ThemeService.Theme.Dark : ThemeService.Theme.Light);
            
            if (_currentRoot != null && TreemapViewControl.CurrentNode != null)
            {
                TreemapViewControl.DisplayNode(TreemapViewControl.CurrentNode, _currentRoot);
            }
        }

        private void MftModeToggle_Click(object sender, RoutedEventArgs e)
        {
            // Guard against being called during InitializeComponent
            if (StatusText == null) return;
            
            bool newMftMode = MftModeToggle.IsChecked == true;
            
            if (newMftMode && !AdminElevation.IsAdministrator())
            {
                var result = MessageBox.Show(
                    "MFT Scanning requires Administrator privileges.\n\n" +
                    "The application will restart with elevated permissions.\n\n" +
                    "Do you want to continue?",
                    "Administrator Required",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                
                if (result == MessageBoxResult.Yes)
                {
                    // Save setting and restart with admin
                    SaveSettings();
                    AdminElevation.RestartWithElevation();
                }
                else
                {
                    MftModeToggle.IsChecked = false;
                    return;
                }
            }
            
            _useMftScanning = newMftMode;
            SaveSettings();
            
            if (_useMftScanning)
            {
                StatusText.Text = "⚡ MFT Scanning enabled - NTFS drives will scan much faster";
            }
            else
            {
                StatusText.Text = "Standard scanning mode enabled";
            }
        }

        private void AutoRefreshToggle_Click(object sender, RoutedEventArgs e)
        {
            if (StatusText == null) return;
            
            _autoRefreshEnabled = AutoRefreshToggle.IsChecked == true;
            SaveSettings();
            
            if (_autoRefreshEnabled)
            {
                StatusText.Text = "🔄 Auto-refresh enabled - view will update when files change";
                if (!string.IsNullOrEmpty(_currentDrive))
                {
                    SetupFileWatcher(_currentDrive);
                }
            }
            else
            {
                StatusText.Text = "Auto-refresh disabled - use Rescan button to update";
                _fileWatcher?.Dispose();
                _fileWatcher = null;
                _debounceTimer?.Dispose();
                _debounceTimer = null;
            }
        }

        private void IncludeHiddenToggle_Click(object sender, RoutedEventArgs e)
        {
            _scanIncludeHidden = IncludeHiddenToggle.IsChecked == true;
            ApplyFiltersAndRefreshViews();
        }

        private void IncludeSystemToggle_Click(object sender, RoutedEventArgs e)
        {
            _scanIncludeSystem = IncludeSystemToggle.IsChecked == true;
            ApplyFiltersAndRefreshViews();
        }

        private void IncludeFilesToggle_Click(object sender, RoutedEventArgs e)
        {
            _scanIncludeFiles = IncludeFilesToggle.IsChecked == true;
            ApplyFiltersAndRefreshViews();
        }

        private void MinFileSizeTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isLoadingSettings) return;
            if (MinFileSizeTextBox == null) return;

            if (double.TryParse(MinFileSizeTextBox.Text, out var mb) && mb >= 0)
            {
                _scanMinFileSizeBytes = (long)(mb * 1024 * 1024);
            }
            else
            {
                _scanMinFileSizeBytes = 0;
            }

            ApplyFiltersAndRefreshViews();
        }

        private void ExcludedPathsTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isLoadingSettings) return;
            if (ExcludedPathsTextBox == null) return;

            _excludedPaths.Clear();
            foreach (var line in (ExcludedPathsTextBox.Text ?? "").Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;
                _excludedPaths.Add(trimmed.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }

            ApplyFiltersAndRefreshViews();
        }

        private void ExcludePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            var normalized = path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (_excludedPaths.Any(p => string.Equals(p, normalized, StringComparison.OrdinalIgnoreCase))) return;

            _excludedPaths.Add(normalized);

            if (ExcludedPathsTextBox != null)
            {
                _isLoadingSettings = true;
                ExcludedPathsTextBox.Text = string.Join(Environment.NewLine, _excludedPaths);
                _isLoadingSettings = false;
            }

            ApplyFiltersAndRefreshViews();
        }

        private void StatisticsCheckbox_Changed(object sender, RoutedEventArgs e)
        {
            // Guard against being called during InitializeComponent
            if (FileTypeStats == null) return;
            
            UpdateStatisticsVisibility();
            SaveSettings();
        }

        private void TopItemsCountCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            // Guard against being called during InitializeComponent before controls exist
            if (LargestFilesHeader == null || LargestFoldersHeader == null) return;
            
            if (TopItemsCountCombo.SelectedItem is ComboBoxItem item && int.TryParse(item.Content?.ToString(), out int count))
            {
                _topItemsCount = count;
                LargestFilesHeader.Text = $"TOP {_topItemsCount} LARGEST FILES";
                LargestFoldersHeader.Text = $"TOP {_topItemsCount} LARGEST FOLDERS";
                
                if (_currentRoot != null)
                {
                    UpdateStatistics();
                }
                
                SaveSettings();
            }
        }

        private void UpdateStatisticsVisibility()
        {
            // File Type Stats
            if (FileTypeStats != null && ShowFileTypeStatsCheckbox != null)
            {
                FileTypeStats.Visibility = ShowFileTypeStatsCheckbox.IsChecked == true 
                    ? Visibility.Visible : Visibility.Collapsed;
            }
            
            // Largest Files
            if (LargestFilesSection != null && ShowLargestFilesCheckbox != null)
            {
                LargestFilesSection.Visibility = ShowLargestFilesCheckbox.IsChecked == true 
                    ? Visibility.Visible : Visibility.Collapsed;
            }
            
            // Largest Folders
            if (LargestFoldersSection != null && ShowLargestFoldersCheckbox != null)
            {
                LargestFoldersSection.Visibility = ShowLargestFoldersCheckbox.IsChecked == true 
                    ? Visibility.Visible : Visibility.Collapsed;
            }
            
            // Duplicates
            if (DuplicatesSection != null && ShowDuplicatesCheckbox != null)
            {
                DuplicatesSection.Visibility = ShowDuplicatesCheckbox.IsChecked == true 
                    ? Visibility.Visible : Visibility.Collapsed;
            }
            
            // Old Files
            if (OldFilesSection != null && ShowOldFilesCheckbox != null)
            {
                OldFilesSection.Visibility = ShowOldFilesCheckbox.IsChecked == true 
                    ? Visibility.Visible : Visibility.Collapsed;
            }
            
            // Empty Folders
            if (EmptyFoldersSection != null && ShowEmptyFoldersCheckbox != null)
            {
                EmptyFoldersSection.Visibility = ShowEmptyFoldersCheckbox.IsChecked == true 
                    ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void UpdateStatistics()
        {
            if (_currentRoot == null) return;
            
            try
            {
                var baseNode = TreemapViewControl?.CurrentNode ?? _currentRoot;

                var allFiles = new List<DirectoryNode>();
                var allFolders = new List<DirectoryNode>();
                CollectAllItems(baseNode, allFiles, allFolders);

                var filteredFiles = allFiles.Where(f => f != null && PassesActiveFilters(f)).ToList();
                var filteredFolders = allFolders.Where(f => f != null && PassesActiveFilters(f)).ToList();

                if (ShowLargestFilesCheckbox?.IsChecked == true)
                    UpdateLargestFiles(baseNode);

                if (ShowLargestFoldersCheckbox?.IsChecked == true)
                    UpdateLargestFolders(baseNode);
                
                // Update old files
                if (ShowOldFilesCheckbox?.IsChecked == true)
                {
                    var oneYearAgo = DateTime.Now.AddYears(-1);
                    var oldFiles = filteredFiles
                        .Where(f => GetFileLastModified(f.Path) < oneYearAgo)
                        .OrderBy(f => GetFileLastModified(f.Path))
                        .Take(_topItemsCount)
                        .Select(f => new { 
                            Name = f.Name, 
                            Path = f.Path, 
                            Size = FormatBytes(f.Size),
                            Age = GetFileAge(f.Path),
                            Node = f
                        })
                        .ToList();
                    OldFilesList.ItemsSource = oldFiles;
                }
                
                // Update empty folders
                if (ShowEmptyFoldersCheckbox?.IsChecked == true)
                {
                    var emptyFolders = filteredFolders
                        .Where(f => f.IsDirectory && f.Size == 0 && (f.Children?.Count ?? 0) == 0)
                        .Take(_topItemsCount)
                        .Select(f => new { Name = f.Name, Path = f.Path, Node = f })
                        .ToList();
                    EmptyFoldersList.ItemsSource = emptyFolders;
                    NoEmptyFoldersText.Visibility = emptyFolders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                }
                
                // Update age distribution chart
                UpdateAgeDistribution(filteredFiles);
                
                // Update compression stats
                UpdateCompressionStats(filteredFiles);

                UpdateOwnerDistribution(filteredFiles);
            }
            catch (Exception ex)
            {
                LogError("UpdateStatistics", ex);
            }
        }

        private void UpdateOwnerDistribution(List<DirectoryNode> allFiles)
        {
            try
            {
                if (OwnerDistributionList == null || OwnerDistributionSection == null) return;

                var files = allFiles.Where(f => f != null && !string.IsNullOrWhiteSpace(f.Path)).Take(5000).ToList();
                if (files.Count == 0)
                {
                    OwnerDistributionList.ItemsSource = null;
                    OwnerDistributionSection.Visibility = Visibility.Collapsed;
                    return;
                }

                var totals = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                long totalSize = 0;

                foreach (var f in files)
                {
                    var owner = GetOwnerCached(f.Path ?? "", false);
                    if (string.IsNullOrWhiteSpace(owner)) continue;
                    if (!totals.TryGetValue(owner, out var current)) current = 0;
                    totals[owner] = current + f.Size;
                    totalSize += f.Size;
                }

                if (totals.Count == 0 || totalSize <= 0)
                {
                    OwnerDistributionList.ItemsSource = null;
                    OwnerDistributionSection.Visibility = Visibility.Collapsed;
                    return;
                }

                var top = totals
                    .OrderByDescending(kvp => kvp.Value)
                    .Take(8)
                    .Select(kvp => new
                    {
                        Name = kvp.Key,
                        Size = FormatBytes(kvp.Value),
                        Percentage = (kvp.Value / (double)totalSize) * 100
                    })
                    .ToList();

                OwnerDistributionList.ItemsSource = top;
                OwnerDistributionSection.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                LogError("UpdateOwnerDistribution", ex);
            }
        }
        
        private void UpdateCompressionStats(List<DirectoryNode> allFiles)
        {
            try
            {
                var compressedFiles = allFiles.Where(f => f.IsCompressed).ToList();
                var totalLogical = compressedFiles.Sum(f => f.Size);
                var totalCompressed = compressedFiles.Sum(f => f.CompressedSize);
                var spaceSaved = totalLogical - totalCompressed;
                
                CompressedFilesCount.Text = compressedFiles.Count.ToString("N0");
                LogicalSizeText.Text = FormatBytes(totalLogical);
                OnDiskSizeText.Text = FormatBytes(totalCompressed);
                SpaceSavedText.Text = FormatBytes(spaceSaved);
                
                // Show/hide section based on whether there are compressed files
                CompressionStatsSection.Visibility = compressedFiles.Count > 0 
                    ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                LogError("UpdateCompressionStats", ex);
            }
        }

        private void UpdateAgeDistribution(List<DirectoryNode> allFiles)
        {
            try
            {
                var now = DateTime.Now;
                var categories = new[]
                {
                    (Label: "< 1 month", MinDays: 0, MaxDays: 30, Color: "#4CAF50"),
                    (Label: "1-6 months", MinDays: 30, MaxDays: 180, Color: "#8BC34A"),
                    (Label: "6-12 months", MinDays: 180, MaxDays: 365, Color: "#FFC107"),
                    (Label: "1-2 years", MinDays: 365, MaxDays: 730, Color: "#FF9800"),
                    (Label: "2-5 years", MinDays: 730, MaxDays: 1825, Color: "#FF5722"),
                    (Label: "> 5 years", MinDays: 1825, MaxDays: int.MaxValue, Color: "#F44336")
                };

                var distribution = new Dictionary<string, long>();
                foreach (var cat in categories)
                {
                    distribution[cat.Label] = 0;
                }

                // Use file size to estimate age distribution without hitting disk for every file
                // Group by folder modification time for better performance
                var folderAges = new Dictionary<string, DateTime>();
                
                foreach (var file in allFiles)
                {
                    try
                    {
                        if (string.IsNullOrEmpty(file.Path)) continue;
                        
                        // Get folder path and cache its modification time
                        var folderPath = System.IO.Path.GetDirectoryName(file.Path) ?? "";
                        DateTime folderTime;
                        
                        if (!folderAges.TryGetValue(folderPath, out folderTime))
                        {
                            try
                            {
                                folderTime = Directory.GetLastWriteTime(folderPath);
                            }
                            catch
                            {
                                folderTime = now; // Default to now if we can't read
                            }
                            folderAges[folderPath] = folderTime;
                        }
                        
                        var ageDays = (now - folderTime).TotalDays;

                        foreach (var cat in categories)
                        {
                            if (ageDays >= cat.MinDays && ageDays < cat.MaxDays)
                            {
                                distribution[cat.Label] += file.Size;
                                break;
                            }
                        }
                    }
                    catch { }
                }

                var maxSize = distribution.Values.Max();
                var maxBarWidth = 100.0;

                var items = categories.Select(cat => new
                {
                    Label = cat.Label,
                    Size = FormatBytes(distribution[cat.Label]),
                    Color = new System.Windows.Media.SolidColorBrush(
                        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(cat.Color)),
                    BarWidth = maxSize > 0 ? (distribution[cat.Label] / (double)maxSize) * maxBarWidth : 0
                }).ToList();

                AgeDistributionList.ItemsSource = items;
            }
            catch (Exception ex)
            {
                LogError("UpdateAgeDistribution", ex);
            }
        }

        private void CollectAllItems(DirectoryNode node, List<DirectoryNode> files, List<DirectoryNode> folders)
        {
            if (node.IsDirectory)
            {
                folders.Add(node);
                foreach (var child in node.Children)
                {
                    CollectAllItems(child, files, folders);
                }
            }
            else
            {
                files.Add(node);
            }
        }

        private DateTime GetFileLastModified(string path)
        {
            try
            {
                return File.GetLastWriteTime(path);
            }
            catch
            {
                return DateTime.Now;
            }
        }

        private string GetFileAge(string path)
        {
            try
            {
                var lastModified = File.GetLastWriteTime(path);
                var age = DateTime.Now - lastModified;
                
                if (age.TotalDays > 365)
                    return $"{(int)(age.TotalDays / 365)} years ago";
                if (age.TotalDays > 30)
                    return $"{(int)(age.TotalDays / 30)} months ago";
                return $"{(int)age.TotalDays} days ago";
            }
            catch
            {
                return "Unknown";
            }
        }

        private void LargestFolder_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext != null)
            {
                dynamic data = element.DataContext;
                string? path = data.Path as string;
                
                if (!string.IsNullOrEmpty(path) && _currentRoot != null)
                {
                    var node = FindNodeByPath(_currentRoot, path);
                    if (node != null && node.IsDirectory)
                    {
                        TreemapViewControl.DisplayNode(node, _currentRoot);
                        UpdateBreadcrumb();
                        BackButton.IsEnabled = TreemapViewControl.CanGoBack();
                    }
                }
            }
        }

        private void EmptyFolder_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext != null)
            {
                dynamic data = element.DataContext;
                string? path = data.Path as string;
                
                if (!string.IsNullOrEmpty(path))
                {
                    Process.Start(new ProcessStartInfo 
                    { 
                        FileName = "explorer.exe", 
                        Arguments = $"/select,\"{path}\"",
                        UseShellExecute = true 
                    });
                }
            }
        }

        private void DeleteEmptyFolder_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string path)
            {
                var result = MessageBox.Show(
                    $"Delete empty folder?\n\n{path}",
                    "Confirm Delete",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                
                if (result == MessageBoxResult.Yes)
                {
                    try
                    {
                        FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                        StatusText.Text = $"Deleted: {System.IO.Path.GetFileName(path)}";
                        
                        // Refresh empty folders list
                        if (_currentRoot != null)
                        {
                            UpdateStatistics();
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Failed to delete folder: {ex.Message}", "Error", 
                            MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
        }

        private void CrashReportButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new SaveFileDialog
                {
                    Filter = "Zip File|*.zip",
                    FileName = $"Fyle_CrashReport_{DateTime.Now:yyyyMMdd_HHmmss}.zip"
                };

                if (dialog.ShowDialog() != true) return;

                var zipPath = dialog.FileName;
                if (File.Exists(zipPath)) File.Delete(zipPath);

                using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);

                var envEntry = zip.CreateEntry("environment.txt", CompressionLevel.Optimal);
                using (var stream = envEntry.Open())
                using (var writer = new StreamWriter(stream, Encoding.UTF8))
                {
                    var asm = Assembly.GetExecutingAssembly().GetName();
                    writer.WriteLine($"appName={asm.Name}");
                    writer.WriteLine($"appVersion={asm.Version}");
                    writer.WriteLine($"osVersion={Environment.OSVersion}");
                    writer.WriteLine($"is64BitProcess={Environment.Is64BitProcess}");
                    writer.WriteLine($"machineName={Environment.MachineName}");
                    writer.WriteLine($"userName={Environment.UserName}");
                    writer.WriteLine($"utcNow={DateTime.UtcNow:O}");
                    writer.WriteLine($"currentDrive={_currentDrive}");
                    writer.WriteLine($"settingsPath={GetSettingsPath()}");
                    writer.WriteLine($"logPath={Logger.GetLogPath()}");
                }

                TryAddFileToZip(zip, Logger.GetLogPath(), "logs/fyle_log.txt");
                TryAddFileToZip(zip, GetSettingsPath(), "settings/settings.txt");

                StatusText.Text = "Crash report created";

                var result = MessageBox.Show("Crash report created. Open folder?", "Fyle", MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (result == MessageBoxResult.Yes)
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{zipPath}\"",
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                LogError("CrashReportButton_Click", ex);
                MessageBox.Show($"Failed to create crash report: {ex.Message}", "Fyle", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static void TryAddFileToZip(ZipArchive zip, string filePath, string entryName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(filePath)) return;
                if (!File.Exists(filePath)) return;
                zip.CreateEntryFromFile(filePath, entryName, CompressionLevel.Optimal);
            }
            catch { }
        }

        private void SaveSettings()
        {
            try
            {
                var settingsPath = GetSettingsPath();
                Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);

                var lines = new List<string>
                {
                    $"DarkMode={_isDarkMode}",
                    $"MftScanning={_useMftScanning}",
                    $"TopItemsCount={_topItemsCount}",
                    $"AutoRefresh={_autoRefreshEnabled}",
                    $"ShowFileTypeStats={ShowFileTypeStatsCheckbox?.IsChecked ?? true}",
                    $"ShowLargestFiles={ShowLargestFilesCheckbox?.IsChecked ?? true}",
                    $"ShowLargestFolders={ShowLargestFoldersCheckbox?.IsChecked ?? true}",
                    $"ShowDuplicates={ShowDuplicatesCheckbox?.IsChecked ?? true}",
                    $"ShowOldFiles={ShowOldFilesCheckbox?.IsChecked ?? false}",
                    $"ShowEmptyFolders={ShowEmptyFoldersCheckbox?.IsChecked ?? false}",
                    $"LastDrive={_currentDrive ?? ""}",
                    $"SearchText={SearchBox?.Text ?? ""}",
                    $"FileTypeFilterIndex={FileTypeFilter?.SelectedIndex ?? 0}",
                    $"OwnerFilter={GetOwnerFilterValue()}",
                    $"IncludeHidden={_scanIncludeHidden}",
                    $"IncludeSystem={_scanIncludeSystem}",
                    $"IncludeFiles={_scanIncludeFiles}",
                    $"MinFileSizeBytes={_scanMinFileSizeBytes}",
                    $"MaxItemsToRender={_maxItemsToRender}"
                };

                foreach (var p in _excludedPaths)
                {
                    if (string.IsNullOrWhiteSpace(p)) continue;
                    lines.Add($"ExcludedPath={p}");
                }

                File.WriteAllLines(settingsPath, lines);
            }
            catch (Exception ex)
            {
                LogError("SaveSettings", ex);
            }
        }

        private void LoadSettings()
        {
            try
            {
                _isLoadingSettings = true;

                var settingsPath = GetSettingsPath();
                var legacyPath = Path.Combine(AppContext.BaseDirectory, "fyle_settings.txt");

                if (!File.Exists(settingsPath) && File.Exists(legacyPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
                    File.Copy(legacyPath, settingsPath, true);
                }

                if (!File.Exists(settingsPath)) return;

                var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var excluded = new List<string>();

                foreach (var line in File.ReadAllLines(settingsPath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var idx = line.IndexOf('=');
                    if (idx <= 0) continue;
                    var key = line.Substring(0, idx).Trim();
                    var value = line.Substring(idx + 1);
                    if (key.Equals("ExcludedPath", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrWhiteSpace(value))
                            excluded.Add(value.Trim());
                        continue;
                    }
                    settings[key] = value;
                }
                
                if (settings.TryGetValue("DarkMode", out var darkMode) && bool.TryParse(darkMode, out var isDark))
                {
                    _isDarkMode = isDark;
                    _themeService.SetTheme(_isDarkMode ? ThemeService.Theme.Dark : ThemeService.Theme.Light);
                }
                
                if (settings.TryGetValue("MftScanning", out var mftMode) && bool.TryParse(mftMode, out var useMft))
                {
                    _useMftScanning = useMft;
                }
                
                if (settings.TryGetValue("AutoRefresh", out var autoRefresh) && bool.TryParse(autoRefresh, out var useAutoRefresh))
                {
                    _autoRefreshEnabled = useAutoRefresh;
                    if (AutoRefreshToggle != null) AutoRefreshToggle.IsChecked = _autoRefreshEnabled;
                }
                
                if (settings.TryGetValue("TopItemsCount", out var countStr) && int.TryParse(countStr, out var count))
                {
                    _topItemsCount = count;
                    // Find the matching combo item
                    for (int i = 0; i < TopItemsCountCombo.Items.Count; i++)
                    {
                        if (TopItemsCountCombo.Items[i] is ComboBoxItem item && item.Content?.ToString() == count.ToString())
                        {
                            TopItemsCountCombo.SelectedIndex = i;
                            break;
                        }
                    }
                }

                if (settings.TryGetValue("LastDrive", out var lastDrive) && !string.IsNullOrWhiteSpace(lastDrive))
                    _currentDrive = lastDrive;

                if (settings.TryGetValue("SearchText", out var searchText) && SearchBox != null)
                    SearchBox.Text = searchText;

                if (settings.TryGetValue("FileTypeFilterIndex", out var filterIdx) && int.TryParse(filterIdx, out var idxValue) && FileTypeFilter != null)
                    FileTypeFilter.SelectedIndex = Math.Max(0, Math.Min(FileTypeFilter.Items.Count - 1, idxValue));

                if (settings.TryGetValue("OwnerFilter", out var ownerFilterValue))
                {
                    _ownerFilter = ownerFilterValue ?? "";
                    _pendingOwnerFilter = _ownerFilter;
                    if (OwnerFilter != null) OwnerFilter.SelectedValue = _ownerFilter;
                }

                if (settings.TryGetValue("IncludeHidden", out var includeHidden) && bool.TryParse(includeHidden, out var ih))
                    _scanIncludeHidden = ih;

                if (settings.TryGetValue("IncludeSystem", out var includeSystem) && bool.TryParse(includeSystem, out var isys))
                    _scanIncludeSystem = isys;

                if (settings.TryGetValue("IncludeFiles", out var includeFiles) && bool.TryParse(includeFiles, out var ifiles))
                    _scanIncludeFiles = ifiles;

                if (settings.TryGetValue("MinFileSizeBytes", out var minBytes) && long.TryParse(minBytes, out var mb))
                    _scanMinFileSizeBytes = Math.Max(0, mb);

                if (settings.TryGetValue("MaxItemsToRender", out var maxRender) && int.TryParse(maxRender, out var mir))
                    _maxItemsToRender = Math.Max(500, mir);

                _excludedPaths.Clear();
                foreach (var p in excluded)
                {
                    var normalized = p.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (normalized.Length == 0) continue;
                    _excludedPaths.Add(normalized);
                }

                if (IncludeHiddenToggle != null) IncludeHiddenToggle.IsChecked = _scanIncludeHidden;
                if (IncludeSystemToggle != null) IncludeSystemToggle.IsChecked = _scanIncludeSystem;
                if (IncludeFilesToggle != null) IncludeFilesToggle.IsChecked = _scanIncludeFiles;
                if (MinFileSizeTextBox != null) MinFileSizeTextBox.Text = $"{(_scanMinFileSizeBytes / (1024.0 * 1024.0)):0.##}";
                if (ExcludedPathsTextBox != null) ExcludedPathsTextBox.Text = string.Join(Environment.NewLine, _excludedPaths);
                
                if (settings.TryGetValue("ShowFileTypeStats", out var showTypes) && bool.TryParse(showTypes, out var showT))
                    ShowFileTypeStatsCheckbox.IsChecked = showT;
                    
                if (settings.TryGetValue("ShowLargestFiles", out var showFiles) && bool.TryParse(showFiles, out var showF))
                    ShowLargestFilesCheckbox.IsChecked = showF;
                    
                if (settings.TryGetValue("ShowLargestFolders", out var showFolders) && bool.TryParse(showFolders, out var showFo))
                    ShowLargestFoldersCheckbox.IsChecked = showFo;
                    
                if (settings.TryGetValue("ShowDuplicates", out var showDupes) && bool.TryParse(showDupes, out var showD))
                    ShowDuplicatesCheckbox.IsChecked = showD;
                    
                if (settings.TryGetValue("ShowOldFiles", out var showOld) && bool.TryParse(showOld, out var showO))
                    ShowOldFilesCheckbox.IsChecked = showO;
                    
                if (settings.TryGetValue("ShowEmptyFolders", out var showEmpty) && bool.TryParse(showEmpty, out var showE))
                    ShowEmptyFoldersCheckbox.IsChecked = showE;
                
                UpdateStatisticsVisibility();
                ApplyFiltersToTreemap();
            }
            catch (Exception ex)
            {
                LogError("LoadSettings", ex);
            }
            finally
            {
                _isLoadingSettings = false;
            }
        }

        private static string GetSettingsPath()
        {
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(baseDir))
                baseDir = AppContext.BaseDirectory;
            return Path.Combine(baseDir, "Fyle", "settings.txt");
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

        #region Quick Preview

        public bool IsQuickPreviewEnabled => QuickPreviewToggle?.IsChecked == true;

        public void ShowQuickPreview(DirectoryNode node)
        {
            if (node == null || !IsQuickPreviewEnabled) return;
            
            try
            {
                // Set folder info
                PreviewFolderName.Text = node.Name;
                PreviewFolderSize.Text = $"{FormatBytes(node.Size)} • {node.Children.Count} items";
                
                // Get top items by size
                var items = node.Children
                    .OrderByDescending(c => c.Size)
                    .Take(10)
                    .Select(c => new
                    {
                        Icon = c.IsDirectory ? "📁" : GetFileIcon(c.Name),
                        Name = c.Name,
                        Size = FormatBytes(c.Size),
                        Percentage = node.Size > 0 ? $"{(c.Size * 100.0 / node.Size):F1}%" : "0%"
                    })
                    .ToList();
                
                PreviewItemsList.ItemsSource = items;
                
                // Show popup
                QuickPreviewPopup.IsOpen = true;
            }
            catch (Exception ex)
            {
                LogError("ShowQuickPreview", ex);
            }
        }

        public void HideQuickPreview()
        {
            if (QuickPreviewPopup != null)
            {
                QuickPreviewPopup.IsOpen = false;
            }
        }

        private string GetFileIcon(string filename)
        {
            var ext = System.IO.Path.GetExtension(filename).ToLowerInvariant();
            return ext switch
            {
                ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" => "🎬",
                ".mp3" or ".wav" or ".flac" or ".aac" or ".ogg" => "🎵",
                ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" => "🖼️",
                ".pdf" => "📕",
                ".doc" or ".docx" => "📘",
                ".xls" or ".xlsx" => "📗",
                ".ppt" or ".pptx" => "📙",
                ".txt" or ".md" => "📝",
                ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => "📦",
                ".exe" or ".msi" => "⚙️",
                ".dll" or ".sys" => "🔧",
                ".html" or ".htm" or ".css" or ".js" => "🌐",
                ".cs" or ".py" or ".java" or ".cpp" or ".h" => "💻",
                _ => "📄"
            };
        }

        #endregion

        #region View Mode Switching

        private void ViewModeChanged(object sender, RoutedEventArgs e)
        {
            if (TreemapViewControl == null) return; // Guard during init
            
            if (TreemapViewRadio.IsChecked == true)
            {
                TreemapViewControl.Visibility = Visibility.Visible;
                SunburstViewContainer.Visibility = Visibility.Collapsed;
                ListViewContainer.Visibility = Visibility.Collapsed;
            }
            else if (SunburstViewRadio.IsChecked == true)
            {
                TreemapViewControl.Visibility = Visibility.Collapsed;
                SunburstViewContainer.Visibility = Visibility.Visible;
                ListViewContainer.Visibility = Visibility.Collapsed;
                UpdateSunburstView();
            }
            else if (ListViewRadio.IsChecked == true)
            {
                TreemapViewControl.Visibility = Visibility.Collapsed;
                SunburstViewContainer.Visibility = Visibility.Collapsed;
                ListViewContainer.Visibility = Visibility.Visible;
                UpdateListView();
            }
        }

        private void UpdateSunburstView()
        {
            if (_currentRoot == null || SunburstCanvas == null) return;
            
            try
            {
                SunburstCanvas.Children.Clear();
                
                var currentNode = TreemapViewControl?.CurrentNode ?? _currentRoot;
                if (currentNode == null) return;
                
                double centerX = SunburstCanvas.ActualWidth / 2;
                double centerY = SunburstCanvas.ActualHeight / 2;
                double maxRadius = Math.Min(centerX, centerY) - 20;
                
                if (maxRadius < 50) return;
                
                var treemap = TreemapViewControl;
                var canGoBack = treemap?.CanGoBack() == true;

                // Draw center circle (clickable to go back)
                var centerCircle = new System.Windows.Shapes.Ellipse
                {
                    Width = 80,
                    Height = 80,
                    Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2D3748")),
                    Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4A5568")),
                    StrokeThickness = 2,
                    Cursor = canGoBack ? Cursors.Hand : Cursors.Arrow,
                    ToolTip = canGoBack ? "Click to go back" : currentNode.Name
                };
                centerCircle.MouseLeftButtonDown += (s, e) =>
                {
                    if (treemap?.CanGoBack() == true)
                    {
                        treemap.GoBack();
                        UpdateSunburstView();
                        UpdateBreadcrumb();
                        BackButton.IsEnabled = treemap.CanGoBack();
                    }
                };
                centerCircle.MouseEnter += (s, e) =>
                {
                    if (treemap?.CanGoBack() == true)
                        centerCircle.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3D4758"));
                };
                centerCircle.MouseLeave += (s, e) =>
                {
                    centerCircle.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2D3748"));
                };
                Canvas.SetLeft(centerCircle, centerX - 40);
                Canvas.SetTop(centerCircle, centerY - 40);
                SunburstCanvas.Children.Add(centerCircle);
                
                // Update center text
                SunburstCenterText.Text = $"{currentNode.Name}\n{FormatBytes(currentNode.Size)}";
                
                // Draw rings for children
                DrawSunburstRing(currentNode.Children.ToList(), centerX, centerY, 50, maxRadius, 0, 360, 0);
            }
            catch (Exception ex)
            {
                LogError("UpdateSunburstView", ex);
            }
        }

        private void DrawSunburstRing(List<DirectoryNode> nodes, double cx, double cy, 
            double innerRadius, double maxRadius, double startAngle, double sweepAngle, int depth)
        {
            // Limit depth to 3 levels for performance
            if (nodes == null || nodes.Count == 0 || depth > 3) return;
            
            double ringWidth = (maxRadius - innerRadius) / 4;
            double outerRadius = innerRadius + ringWidth;
            
            // Sort by size, limit to top 20 items per ring for performance
            var sortedNodes = nodes.Where(n => n.Size > 0)
                                   .OrderByDescending(n => n.Size)
                                   .Take(20)
                                   .ToList();
            long totalSize = sortedNodes.Sum(n => n.Size);
            if (totalSize == 0) return;
            
            double currentAngle = startAngle;
            var colors = new[] { "#4CAF50", "#2196F3", "#FF9800", "#9C27B0", "#F44336", "#00BCD4", "#FFEB3B", "#795548" };
            int colorIndex = 0;
            
            foreach (var node in sortedNodes)
            {
                double nodeSweep = (node.Size / (double)totalSize) * sweepAngle;
                if (nodeSweep < 2) continue; // Skip tiny segments (increased threshold)
                
                var color = (Color)ColorConverter.ConvertFromString(colors[colorIndex % colors.Length]);
                // Darken color based on depth
                var adjustedColor = Color.FromRgb(
                    (byte)(color.R * (1 - depth * 0.15)),
                    (byte)(color.G * (1 - depth * 0.15)),
                    (byte)(color.B * (1 - depth * 0.15)));
                
                var arc = CreateArcSegment(cx, cy, innerRadius, outerRadius, currentAngle, nodeSweep, adjustedColor, node);
                SunburstCanvas.Children.Add(arc);
                
                // Recursively draw children
                if (node.IsDirectory && node.Children.Count > 0)
                {
                    DrawSunburstRing(node.Children.ToList(), cx, cy, outerRadius, maxRadius, 
                        currentAngle, nodeSweep, depth + 1);
                }
                
                currentAngle += nodeSweep;
                colorIndex++;
            }
        }

        private System.Windows.Shapes.Path CreateArcSegment(double cx, double cy, double innerR, double outerR, 
            double startAngle, double sweepAngle, Color color, DirectoryNode node)
        {
            // Convert angles to radians
            double startRad = (startAngle - 90) * Math.PI / 180;
            double endRad = (startAngle + sweepAngle - 90) * Math.PI / 180;
            
            // Calculate points
            var innerStart = new Point(cx + innerR * Math.Cos(startRad), cy + innerR * Math.Sin(startRad));
            var innerEnd = new Point(cx + innerR * Math.Cos(endRad), cy + innerR * Math.Sin(endRad));
            var outerStart = new Point(cx + outerR * Math.Cos(startRad), cy + outerR * Math.Sin(startRad));
            var outerEnd = new Point(cx + outerR * Math.Cos(endRad), cy + outerR * Math.Sin(endRad));
            
            bool largeArc = sweepAngle > 180;
            
            var figure = new PathFigure { StartPoint = innerStart, IsClosed = true };
            
            // Inner arc
            figure.Segments.Add(new ArcSegment(innerEnd, new Size(innerR, innerR), 0, largeArc, SweepDirection.Clockwise, true));
            // Line to outer
            figure.Segments.Add(new LineSegment(outerEnd, true));
            // Outer arc (reverse direction)
            figure.Segments.Add(new ArcSegment(outerStart, new Size(outerR, outerR), 0, largeArc, SweepDirection.Counterclockwise, true));
            // Line back to start
            figure.Segments.Add(new LineSegment(innerStart, true));
            
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            
            var path = new System.Windows.Shapes.Path
            {
                Data = geometry,
                Fill = new SolidColorBrush(color),
                Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1A1A2E")),
                StrokeThickness = 1,
                Cursor = Cursors.Hand,
                ToolTip = $"{node.Name}\n{FormatBytes(node.Size)}\n{(node.IsDirectory ? $"{node.Children.Count} items" : "File")}",
                Tag = node
            };
            
            path.MouseEnter += (s, e) => {
                path.Fill = new SolidColorBrush(Color.FromArgb(255, 
                    (byte)Math.Min(255, color.R + 30),
                    (byte)Math.Min(255, color.G + 30),
                    (byte)Math.Min(255, color.B + 30)));
            };
            path.MouseLeave += (s, e) => {
                path.Fill = new SolidColorBrush(color);
            };
            path.MouseLeftButtonDown += SunburstSegment_Click;
            
            return path;
        }

        private void SunburstSegment_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is System.Windows.Shapes.Path path && path.Tag is DirectoryNode node)
            {
                if (node.IsDirectory && node.Children.Count > 0)
                {
                    TreemapViewControl.NavigateToNode(node);
                    UpdateSunburstView();
                    UpdateBreadcrumb();
                    BackButton.IsEnabled = TreemapViewControl.CanGoBack();
                }
            }
        }

        private string BuildListItemCountText(DirectoryNode node)
        {
            if (!node.IsDirectory) return "File";
            var count = node.Children?.Count ?? 0;
            return $"{count:N0} items";
        }

        private string BuildListDetailsText(DirectoryNode node)
        {
            var path = node.Path ?? "";
            if (string.IsNullOrWhiteSpace(path)) return "";

            var modified = GetLastWriteTimeSafe(path, node.IsDirectory);
            var accessed = GetLastAccessTimeSafe(path, node.IsDirectory);

            var parts = new List<string>();
            var ownerFilter = GetOwnerFilterValue();
            var owner = GetOwnerCached(path, node.IsDirectory);

            if (!string.IsNullOrWhiteSpace(owner))
                parts.Add($"Owner: {owner}");

            if (modified != DateTime.MinValue)
                parts.Add($"Modified: {modified:yyyy-MM-dd}");

            if (accessed != DateTime.MinValue)
                parts.Add($"Accessed: {accessed:yyyy-MM-dd}");

            if (!string.IsNullOrWhiteSpace(ownerFilter) && string.IsNullOrWhiteSpace(owner))
                parts.Add("Owner: Unknown");

            return string.Join(" • ", parts);
        }

        private void UpdateListView()
        {
            if (_currentRoot == null || FileListView == null) return;
            
            try
            {
                var currentNode = TreemapViewControl?.CurrentNode ?? _currentRoot;
                if (currentNode == null) return;
                
                var filteredChildren = (currentNode.Children?.ToList() ?? new List<DirectoryNode>())
                    .Where(c => c != null && PassesActiveFilters(c))
                    .ToList();

                var maxSize = filteredChildren.Count > 0 ? filteredChildren.Max(c => c.Size) : 1;
                
                var items = filteredChildren
                    .OrderByDescending(c => c.Size)
                    .Select(c => new
                    {
                        Node = c,
                        c.Name,
                        FormattedSize = FormatBytes(c.Size),
                        Percentage = currentNode.Size > 0 ? $"{(c.Size * 100.0 / currentNode.Size):F1}%" : "0%",
                        ItemCountText = BuildListItemCountText(c),
                        DetailsText = BuildListDetailsText(c),
                        Icon = c.IsDirectory ? "📁" : "📄",
                        BarWidth = maxSize > 0 ? (c.Size / (double)maxSize) * 100 : 0
                    })
                    .ToList();
                
                FileListView.ItemsSource = items;
            }
            catch (Exception ex)
            {
                LogError("UpdateListView", ex);
            }
        }

        private void ListItem_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext != null)
            {
                try
                {
                    dynamic item = element.DataContext;
                    if (item.Node is DirectoryNode node && node.IsDirectory && node.Children.Count > 0)
                    {
                        TreemapViewControl.NavigateToNode(node);
                        UpdateListView();
                        UpdateSunburstView();
                        UpdateBreadcrumb();
                        BackButton.IsEnabled = TreemapViewControl.CanGoBack();
                    }
                    else if (item.Node is DirectoryNode fileNode && !fileNode.IsDirectory && !string.IsNullOrWhiteSpace(fileNode.Path))
                    {
                        ShowInExplorer(fileNode.Path);
                    }
                }
                catch { }
            }
        }

        private void FileListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Not used anymore - using click handler instead
        }

        private void SunburstCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (SunburstViewRadio?.IsChecked == true)
            {
                UpdateSunburstView();
            }
        }

        #endregion

        private string TruncatePath(string path, int maxLength)
        {
            if (string.IsNullOrEmpty(path) || path.Length <= maxLength)
                return path;
            
            return "..." + path.Substring(path.Length - maxLength + 3);
        }
    }
}
