using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Fyle.Core;
using Fyle.Services;
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
                Dispatcher.Invoke(() =>
                {
                    CurrentPathText.Text = TruncatePath(path, 40);
                    StatusText.Text = $"Scanning: {Path.GetFileName(path)}";
                });
            };

            _scanner.ProgressChanged += progress =>
            {
                Dispatcher.Invoke(() =>
                {
                    ScanProgressBar.Value = progress;
                    ProgressText.Text = $"{progress:F0}% • {_scanTimer.Elapsed:mm\\:ss}";
                });
            };

            _scanner.ScanCompleted += root =>
            {
                Log("ScanCompleted event fired");
                Dispatcher.Invoke(() =>
                {
                    try
                    {
                        Log("ScanCompleted - stopping timer");
                        _scanTimer.Stop();
                        _currentRoot = root;
                        
                        Log($"ScanCompleted - displaying node: {root?.Name}, children: {root?.Children?.Count}");
                        TreemapViewControl.DisplayNode(root, root);
                        Log("ScanCompleted - DisplayNode done");
                        
                        ScanProgressBar.Visibility = Visibility.Collapsed;
                        CancelButton.Visibility = Visibility.Collapsed;
                        ProgressText.Text = "";
                        RescanButton.IsEnabled = true;
                        SettingsExportButton.IsEnabled = true;
                        BackButton.IsEnabled = false;
                        
                        Log("ScanCompleted - updating breadcrumb");
                        UpdateBreadcrumb();
                        Log("ScanCompleted - updating stats");
                        UpdateStats(root);
                        
                        // These can be slow/crash on large drives, wrap in try-catch
                        Log("ScanCompleted - updating file type stats");
                        try { UpdateFileTypeStats(root); } catch (Exception ex) { LogError("UpdateFileTypeStats", ex); }
                        Log("ScanCompleted - updating largest files");
                        try { UpdateLargestFiles(root); } catch (Exception ex) { LogError("UpdateLargestFiles", ex); }
                        Log("ScanCompleted - updating statistics");
                        try { UpdateStatistics(); } catch (Exception ex) { LogError("UpdateStatistics", ex); }
                        Log("ScanCompleted - finding duplicates");
                        try { FindDuplicates(root); } catch (Exception ex) { LogError("FindDuplicates", ex); }
                        
                        if (!string.IsNullOrEmpty(_currentDrive) && !_recentScans.Contains(_currentDrive))
                        {
                            _recentScans.Insert(0, _currentDrive);
                            if (_recentScans.Count > 5)
                                _recentScans.RemoveAt(5);
                            RecentScansListBox.ItemsSource = _recentScans.ToList();
                        }
                        
                        ScanTimeText.Text = $"⏱ {_scanTimer.Elapsed:mm\\:ss}";
                        StatusText.Text = $"Scan complete • {root?.Children?.Count ?? 0} items found";
                        
                        // Enable buttons
                        SettingsSaveScanButton.IsEnabled = true;
                        
                        Log("ScanCompleted - all done");
                    }
                    catch (Exception ex)
                    {
                        LogError("ScanCompleted handler", ex);
                        StatusText.Text = $"Error: {ex.Message}";
                    }
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
            
            var allFiles = GetAllFiles(root, 10000)
                .Where(f => f != null)
                .OrderByDescending(f => f.Size)
                .Take(10)
                .Select(f => new
                {
                    Name = f.Name ?? "Unknown",
                    Path = f.Path ?? "",
                    Size = FormatBytes(f.Size),
                    Node = f
                })
                .ToList();

            LargestFilesList.ItemsSource = allFiles;
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
            _scanTimer.Restart();
            CurrentPathText.Text = "Starting scan...";
            StatusText.Text = "Initializing...";
            ScanProgressBar.Visibility = Visibility.Visible;
            ScanProgressBar.Value = 0;
            CancelButton.Visibility = Visibility.Visible;
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

            try
            {
                DirectoryNode? root = null;
                
                // Try MFT scanning if enabled
                if (_useMftScanning && MftScanner.IsMftAvailable(drivePath))
                {
                    StatusText.Text = "⚡ MFT Fast Scan mode...";
                    var mftScanner = new MftScanner();
                    
                    mftScanner.StatusChanged += status => Dispatcher.Invoke(() => StatusText.Text = $"⚡ {status}");
                    mftScanner.ProgressChanged += progress => Dispatcher.Invoke(() => 
                    {
                        ScanProgressBar.Value = progress;
                        ProgressText.Text = $"{progress}%";
                    });
                    
                    root = await mftScanner.ScanDriveAsync(drivePath);
                    
                    if (root == null)
                    {
                        // MFT scan failed, fall back to standard
                        StatusText.Text = "MFT unavailable, using standard scan...";
                        root = await _scanner.ScanDriveAsync(drivePath);
                    }
                }
                else
                {
                    // Standard scanning
                    root = await _scanner.ScanDriveAsync(drivePath);
                }
                
                if (root != null)
                {
                    _currentRoot = root;
                    
                    // Display the results (same as ScanCompleted handler)
                    _scanTimer.Stop();
                    Log($"MFT Scan complete - displaying node: {root.Name}, children: {root.Children.Count}");
                    
                    TreemapViewControl.DisplayNode(root, root);
                    
                    ScanProgressBar.Visibility = Visibility.Collapsed;
                    CancelButton.Visibility = Visibility.Collapsed;
                    ProgressText.Text = "";
                    RescanButton.IsEnabled = true;
                    SettingsExportButton.IsEnabled = true;
                    BackButton.IsEnabled = false;
                    
                    UpdateBreadcrumb();
                    UpdateStats(root);
                    
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
                    StatusText.Text = $"⚡ MFT Scan complete • {root.Children.Count} items found";
                    SettingsSaveScanButton.IsEnabled = true;
                    
                    // Start watching for changes
                    SetupFileWatcher(drivePath);
                }
            }
            catch (OperationCanceledException)
            {
                StatusText.Text = "Scan cancelled";
                ScanProgressBar.Visibility = Visibility.Collapsed;
                CancelButton.Visibility = Visibility.Collapsed;
                RescanButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                LogError("ScanDrive", ex);
                MessageBox.Show($"Error scanning drive: {ex.Message}", "Scan Error", MessageBoxButton.OK, MessageBoxImage.Error);
                ScanProgressBar.Visibility = Visibility.Collapsed;
                CancelButton.Visibility = Visibility.Collapsed;
                RescanButton.IsEnabled = true;
                StatusText.Text = "Scan failed";
            }
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
            _scanner.Cancel();
            StatusText.Text = "Cancelling...";
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

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentRoot == null) return;

            var dialog = new SaveFileDialog
            {
                Filter = "CSV File|*.csv|JSON File|*.json|HTML Report|*.html|XML File|*.xml",
                FileName = $"Fyle_Export_{DateTime.Now:yyyyMMdd_HHmmss}"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    StatusText.Text = "Exporting...";
                    
                    var ext = System.IO.Path.GetExtension(dialog.FileName).ToLower();
                    switch (ext)
                    {
                        case ".csv":
                            ExportService.ExportToCsv(_currentRoot, dialog.FileName);
                            break;
                        case ".json":
                            ExportService.ExportToJson(_currentRoot, dialog.FileName);
                            break;
                        case ".html":
                            ExportService.ExportToHtml(_currentRoot, dialog.FileName, $"Fyle Report - {_currentDrive}");
                            break;
                        case ".xml":
                            ExportService.ExportToXml(_currentRoot, dialog.FileName);
                            break;
                    }
                    
                    StatusText.Text = "Export completed!";
                    
                    // Ask to open the file
                    var result = MessageBox.Show("Export completed! Open the file?", "Export", 
                        MessageBoxButton.YesNo, MessageBoxImage.Information);
                    if (result == MessageBoxResult.Yes)
                    {
                        Process.Start(new ProcessStartInfo { FileName = dialog.FileName, UseShellExecute = true });
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Export failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    StatusText.Text = "Export failed";
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
            // Filter is applied in real-time via treemap
            // Could implement search highlighting here
        }

        private void FileTypeFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FileTypeFilter == null) return;
            // Apply filter to treemap view
            // This would require extending the treemap to support filtering
        }

        private void ClearFilterButton_Click(object sender, RoutedEventArgs e)
        {
            SearchBox.Text = "";
            FileTypeFilter.SelectedIndex = 0;
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

        private void TreemapViewControl_NodeSelected(DirectoryNode node)
        {
            UpdateBreadcrumb();
            BackButton.IsEnabled = TreemapViewControl.CanGoBack();
            ItemCountText.Text = $"{node.Children.Count} items • {TreemapViewControl.VisibleItemCount} visible";
            StatusText.Text = $"Viewing: {node.Name} ({node.FormattedSize})";
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
                var allFiles = new List<DirectoryNode>();
                var allFolders = new List<DirectoryNode>();
                CollectAllItems(_currentRoot, allFiles, allFolders);
                
                // Update largest files
                if (ShowLargestFilesCheckbox?.IsChecked == true)
                {
                    var largestFiles = allFiles
                        .OrderByDescending(f => f.Size)
                        .Take(_topItemsCount)
                        .Select(f => new { Name = f.Name, Path = f.Path, Size = FormatBytes(f.Size) })
                        .ToList();
                    LargestFilesList.ItemsSource = largestFiles;
                }
                
                // Update largest folders
                if (ShowLargestFoldersCheckbox?.IsChecked == true)
                {
                    var largestFolders = allFolders
                        .Where(f => f.IsDirectory)
                        .OrderByDescending(f => f.Size)
                        .Take(_topItemsCount)
                        .Select(f => new { Name = f.Name, Path = f.Path, Size = FormatBytes(f.Size) })
                        .ToList();
                    LargestFoldersList.ItemsSource = largestFolders;
                }
                
                // Update old files
                if (ShowOldFilesCheckbox?.IsChecked == true)
                {
                    var oneYearAgo = DateTime.Now.AddYears(-1);
                    var oldFiles = allFiles
                        .Where(f => GetFileLastModified(f.Path) < oneYearAgo)
                        .OrderBy(f => GetFileLastModified(f.Path))
                        .Take(_topItemsCount)
                        .Select(f => new { 
                            Name = f.Name, 
                            Path = f.Path, 
                            Size = FormatBytes(f.Size),
                            Age = GetFileAge(f.Path)
                        })
                        .ToList();
                    OldFilesList.ItemsSource = oldFiles;
                }
                
                // Update empty folders
                if (ShowEmptyFoldersCheckbox?.IsChecked == true)
                {
                    var emptyFolders = allFolders
                        .Where(f => f.IsDirectory && f.Size == 0 && f.Children.Count == 0)
                        .Take(_topItemsCount)
                        .Select(f => new { Name = f.Name, Path = f.Path })
                        .ToList();
                    EmptyFoldersList.ItemsSource = emptyFolders;
                    NoEmptyFoldersText.Visibility = emptyFolders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                }
                
                // Update age distribution chart
                UpdateAgeDistribution(allFiles);
                
                // Update compression stats
                UpdateCompressionStats(allFiles);
            }
            catch (Exception ex)
            {
                LogError("UpdateStatistics", ex);
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
                        Directory.Delete(path);
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

        private void SaveSettings()
        {
            try
            {
                var settingsPath = System.IO.Path.Combine(AppContext.BaseDirectory, "fyle_settings.txt");
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
                    $"ShowEmptyFolders={ShowEmptyFoldersCheckbox?.IsChecked ?? false}"
                };
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
                var settingsPath = System.IO.Path.Combine(AppContext.BaseDirectory, "fyle_settings.txt");
                if (!File.Exists(settingsPath)) return;
                
                var settings = File.ReadAllLines(settingsPath)
                    .Where(l => l.Contains('='))
                    .ToDictionary(
                        l => l.Split('=')[0],
                        l => l.Split('=')[1]
                    );
                
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
            }
            catch (Exception ex)
            {
                LogError("LoadSettings", ex);
            }
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
                
                // Draw center circle (clickable to go back)
                var centerCircle = new System.Windows.Shapes.Ellipse
                {
                    Width = 80,
                    Height = 80,
                    Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2D3748")),
                    Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4A5568")),
                    StrokeThickness = 2,
                    Cursor = TreemapViewControl.CanGoBack() ? Cursors.Hand : Cursors.Arrow,
                    ToolTip = TreemapViewControl.CanGoBack() ? "Click to go back" : currentNode.Name
                };
                centerCircle.MouseLeftButtonDown += (s, e) =>
                {
                    if (TreemapViewControl.CanGoBack())
                    {
                        TreemapViewControl.GoBack();
                        UpdateSunburstView();
                        UpdateBreadcrumb();
                        BackButton.IsEnabled = TreemapViewControl.CanGoBack();
                    }
                };
                centerCircle.MouseEnter += (s, e) =>
                {
                    if (TreemapViewControl.CanGoBack())
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

        private void UpdateListView()
        {
            if (_currentRoot == null || FileListView == null) return;
            
            try
            {
                var currentNode = TreemapViewControl?.CurrentNode ?? _currentRoot;
                if (currentNode == null) return;
                
                var maxSize = currentNode.Children.Count > 0 ? currentNode.Children.Max(c => c.Size) : 1;
                
                var items = currentNode.Children
                    .OrderByDescending(c => c.Size)
                    .Select(c => new
                    {
                        Node = c,
                        c.Name,
                        FormattedSize = FormatBytes(c.Size),
                        Percentage = currentNode.Size > 0 ? $"{(c.Size * 100.0 / currentNode.Size):F1}%" : "0%",
                        ItemCount = c.IsDirectory ? c.Children.Count.ToString() : "File",
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
