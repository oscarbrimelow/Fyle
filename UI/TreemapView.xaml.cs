using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualBasic.FileIO;
using Fyle.Core;
using Fyle.Services;
using static Fyle.Services.Logger;

namespace Fyle.UI
{
    public partial class TreemapView : UserControl
    {
        private DirectoryNode? _currentNode;
        private DirectoryNode? _rootNode;
        private readonly Stack<DirectoryNode> _navigationStack = new();
        private int _colorMode = 0; // 0=Size, 1=Type, 2=Age
        private string _filterText = "";
        private int _filterTypeIndex;
        private long _minSizeBytes;
        private bool _includeFiles = true;
        private string _ownerFilter = "";
        private readonly HashSet<string> _excludedPaths = new(StringComparer.OrdinalIgnoreCase);
        private int _maxItemsToRender = 5000;
        private readonly Dictionary<string, string> _ownerCache = new(StringComparer.OrdinalIgnoreCase);

        public event Action<DirectoryNode>? NodeSelected;
        public event Action<string>? ExcludePathRequested;
        
        public void SetColorMode(int mode)
        {
            _colorMode = mode;
        }

        public void SetFilter(string? filterText, int filterTypeIndex, long minSizeBytes, bool includeFiles, string? ownerFilter, IEnumerable<string>? excludedPaths, int maxItemsToRender)
        {
            _filterText = (filterText ?? "").Trim();
            _filterTypeIndex = filterTypeIndex;
            _minSizeBytes = Math.Max(0, minSizeBytes);
            _includeFiles = includeFiles;
            _ownerFilter = (ownerFilter ?? "").Trim();
            _maxItemsToRender = maxItemsToRender <= 0 ? 5000 : maxItemsToRender;

            _excludedPaths.Clear();
            if (excludedPaths != null)
            {
                foreach (var p in excludedPaths)
                {
                    if (string.IsNullOrWhiteSpace(p)) continue;
                    var normalized = p.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (normalized.Length == 0) continue;
                    _excludedPaths.Add(normalized);
                }
            }
        }

        public TreemapView()
        {
            InitializeComponent();
            SizeChanged += TreemapView_SizeChanged;
        }

        public void DisplayNode(DirectoryNode node, DirectoryNode? root = null)
        {
            Log($"DisplayNode called: {node?.Name}");
            try
            {
                _currentNode = node;
                if (root != null) _rootNode = root;
                if (_rootNode == null) _rootNode = node;
                UpdateTreemap();
                Log("DisplayNode complete");
            }
            catch (Exception ex)
            {
                LogError("DisplayNode", ex);
            }
        }

        /// <summary>
        /// Navigate to a node with history tracking (for back button support)
        /// </summary>
        public void NavigateToNode(DirectoryNode node)
        {
            if (_currentNode != null && _currentNode != node)
            {
                _navigationStack.Push(_currentNode);
            }
            DisplayNode(node);
            NodeSelected?.Invoke(node);
        }

        public DirectoryNode? CurrentNode => _currentNode;
        
        public int VisibleItemCount { get; private set; }

        public bool CanGoBack()
        {
            return _navigationStack.Count > 0;
        }

        public void GoBack()
        {
            if (_navigationStack.Count > 0)
            {
                _currentNode = _navigationStack.Pop();
                UpdateTreemap();
            }
        }

        private void TreemapView_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_currentNode != null)
                UpdateTreemap();
        }

        private void UpdateTreemap()
        {
            try
            {
                TreemapCanvas.Children.Clear();

                if (_currentNode == null) return;

                var availableSpace = new System.Windows.Rect(8, 8, ActualWidth - 16, ActualHeight - 16);
                if (availableSpace.Width <= 0 || availableSpace.Height <= 0) return;

                var items = TreemapLayout.CalculateLayout(GetFilteredChildren(_currentNode), availableSpace, 1);
                if (items == null) return;
                
                VisibleItemCount = items.Count;

                // Calculate max size for relative density calculation
                long maxSize = 1;
                try
                {
                    if (_currentNode.Children != null && _currentNode.Children.Count > 0)
                        maxSize = _currentNode.Children.Where(c => c != null).Max(c => c.Size);
                    else
                        maxSize = _currentNode.Size > 0 ? _currentNode.Size : 1;
                }
                catch { maxSize = 1; }
                
                long totalSize = _currentNode.Size > 0 ? _currentNode.Size : 1;

            foreach (var item in items)
            {
                try
                {
                    if (item?.Node == null) continue;
                    if (item.Bounds.Width < 1 || item.Bounds.Height < 1) continue;

                    // Get color based on mode
                    Color color;
                    try
                    {
                        color = _colorMode switch
                        {
                            1 => GetFileTypeColor(item.Node),
                            2 => GetAgeColor(item.Node),
                            _ => GetDensityColor((double)item.Node.Size / totalSize, item.Node.Size, maxSize)
                        };
                    }
                    catch
                    {
                        color = Color.FromRgb(128, 128, 128);
                    }

                var border = new Border
                {
                    Width = item.Bounds.Width,
                    Height = item.Bounds.Height,
                    Background = new SolidColorBrush(color),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                    Cursor = Cursors.Hand
                };

                Canvas.SetLeft(border, item.Bounds.Left);
                Canvas.SetTop(border, item.Bounds.Top);

                // Tooltip with more info
                try
                {
                    var percentOfParent = _currentNode?.Size > 0 
                        ? (item.Node.Size / (double)_currentNode.Size) * 100 
                        : 0;
                    
                    var childCount = item.Node.Children?.Count ?? 0;
                    var tooltip = new ToolTip
                    {
                        Content = $"{item.Node.Name ?? "Unknown"}\n" +
                                  $"Size: {item.Node.FormattedSize ?? "N/A"}\n" +
                                  $"{percentOfParent:F1}% of current view\n" +
                                  $"{(item.Node.IsDirectory ? $"{childCount} items" : "File")}",
                        Background = (Brush)FindResource("SurfaceBrush"),
                        Foreground = (Brush)FindResource("TextBrush"),
                        BorderBrush = (Brush)FindResource("BorderBrush"),
                        BorderThickness = new Thickness(1),
                        Padding = new Thickness(10, 8, 10, 8)
                    };
                    border.ToolTip = tooltip;
                }
                catch { }

                // Context Menu
                border.ContextMenu = CreateContextMenu(item.Node);

                // Left click to zoom in
                border.MouseLeftButtonDown += (s, e) =>
                {
                    e.Handled = true;
                    if (item.Node != null && item.Node.IsDirectory && (item.Node.Children?.Count ?? 0) > 0)
                    {
                        _navigationStack.Push(_currentNode!);
                        DisplayNode(item.Node);
                        NodeSelected?.Invoke(item.Node);
                    }
                };

                // Hover effects
                var originalColor = color;
                border.MouseEnter += (s, e) =>
                {
                    border.Background = new SolidColorBrush(LightenColor(originalColor, 0.15));
                    border.BorderBrush = new SolidColorBrush(Colors.White);
                    border.BorderThickness = new Thickness(2);
                    
                    // Show quick preview if enabled (only for folders with children)
                    if (item.Node != null && item.Node.IsDirectory && (item.Node.Children?.Count ?? 0) > 0)
                    {
                        var mainWindow = Window.GetWindow(this) as MainWindow;
                        mainWindow?.ShowQuickPreview(item.Node);
                    }
                };

                border.MouseLeave += (s, e) =>
                {
                    border.Background = new SolidColorBrush(originalColor);
                    border.BorderBrush = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0));
                    border.BorderThickness = new Thickness(1);
                    
                    // Hide quick preview
                    var mainWindow = Window.GetWindow(this) as MainWindow;
                    mainWindow?.HideQuickPreview();
                };

                // Text label
                if (item.Bounds.Width > 45 && item.Bounds.Height > 22)
                {
                    var stackPanel = new StackPanel
                    {
                        Margin = new Thickness(6, 4, 6, 4),
                        VerticalAlignment = VerticalAlignment.Top
                    };

                    var nameBlock = new TextBlock
                    {
                        Text = TruncateText(item.Node?.Name ?? "", item.Bounds.Width - 12),
                        Foreground = new SolidColorBrush(Colors.White),
                        FontSize = Math.Max(10, Math.Min(13, item.Bounds.Width / 12)),
                        FontWeight = FontWeights.SemiBold,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        Effect = new System.Windows.Media.Effects.DropShadowEffect
                        {
                            Color = Colors.Black,
                            BlurRadius = 4,
                            ShadowDepth = 1,
                            Opacity = 0.8
                        }
                    };
                    stackPanel.Children.Add(nameBlock);

                    if (item.Bounds.Height > 38 && item.Bounds.Width > 60)
                    {
                        var sizeBlock = new TextBlock
                        {
                            Text = item.Node?.FormattedSize ?? "",
                            Foreground = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)),
                            FontSize = Math.Max(9, Math.Min(11, item.Bounds.Width / 14)),
                            TextTrimming = TextTrimming.CharacterEllipsis,
                            Effect = new System.Windows.Media.Effects.DropShadowEffect
                            {
                                Color = Colors.Black,
                                BlurRadius = 3,
                                ShadowDepth = 1,
                                Opacity = 0.6
                            }
                        };
                        stackPanel.Children.Add(sizeBlock);
                    }

                    border.Child = stackPanel;
                }

                    TreemapCanvas.Children.Add(border);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Item render error: {ex.Message}");
                    continue;
                }
            }
            }
            catch (Exception ex)
            {
                LogError("UpdateTreemap", ex);
            }
        }

        private IEnumerable<DirectoryNode> GetFilteredChildren(DirectoryNode node)
        {
            if (node.Children == null || node.Children.Count == 0) return Array.Empty<DirectoryNode>();

            IEnumerable<DirectoryNode> children = node.Children;
            children = children.Where(c => c != null && c.Size > 0);
            children = children.Where(c => !IsExcluded(c.Path));

            if (!_includeFiles)
            {
                children = children.Where(c => c.IsDirectory);
            }

            if (_minSizeBytes > 0)
            {
                children = children.Where(c => c.IsDirectory || c.Size >= _minSizeBytes);
            }

            if (_filterTypeIndex == 1)
            {
                children = children.Where(c => c.IsDirectory);
            }
            else if (_filterTypeIndex > 1)
            {
                children = children.Where(c => c.IsDirectory ? HasMatchingDescendant(c) : IsCategoryMatch(c));
            }

            if (!string.IsNullOrWhiteSpace(_ownerFilter))
            {
                children = children.Where(c => c.IsDirectory ? HasMatchingDescendant(c) : OwnerMatches(c));
            }

            if (!string.IsNullOrWhiteSpace(_filterText))
            {
                children = children.Where(c => c.IsDirectory ? HasMatchingDescendant(c) : NameMatches(c));
            }

            children = children.OrderByDescending(c => c.Size);
            if (_maxItemsToRender > 0)
            {
                children = children.Take(_maxItemsToRender);
            }
            return children.ToList();
        }

        private bool NameMatches(DirectoryNode node)
        {
            if (string.IsNullOrWhiteSpace(_filterText)) return true;
            return (node.Name ?? "").Contains(_filterText, StringComparison.OrdinalIgnoreCase);
        }

        private bool HasMatchingDescendant(DirectoryNode node)
        {
            if (node == null) return false;
            if (_filterTypeIndex <= 1 && string.IsNullOrWhiteSpace(_ownerFilter))
            {
                if (!string.IsNullOrWhiteSpace(_filterText) && NameMatches(node)) return true;
            }

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

                if (depth > 0)
                {
                    if (current.IsDirectory)
                    {
                        if (_filterTypeIndex <= 1 && string.IsNullOrWhiteSpace(_ownerFilter))
                        {
                            if (!string.IsNullOrWhiteSpace(_filterText) && NameMatches(current)) return true;
                        }
                    }
                    else
                    {
                        if (MatchesFileWithActiveFilters(current)) return true;
                    }
                }

                if (depth >= maxDepth) continue;
                if (current.Children == null || current.Children.Count == 0) continue;

                foreach (var child in current.Children)
                {
                    if (child == null) continue;
                    if (!_includeFiles && !child.IsDirectory) continue;
                    if (_minSizeBytes > 0 && !child.IsDirectory && child.Size < _minSizeBytes) continue;
                    stack.Push((child, depth + 1));
                }
            }

            return false;
        }

        private bool MatchesFileWithActiveFilters(DirectoryNode node)
        {
            if (node.IsDirectory) return false;

            if (!_includeFiles) return false;
            if (_minSizeBytes > 0 && node.Size < _minSizeBytes) return false;
            if (!string.IsNullOrWhiteSpace(_ownerFilter) && !OwnerMatches(node)) return false;

            if (!string.IsNullOrWhiteSpace(_filterText))
            {
                if (!NameMatches(node)) return false;
            }

            if (_filterTypeIndex > 1)
            {
                return IsCategoryMatch(node);
            }

            return true;
        }

        private bool IsCategoryMatch(DirectoryNode node)
        {
            if (node.IsDirectory) return false;
            var ext = Path.GetExtension(node.Name ?? "").ToLowerInvariant();
            return _filterTypeIndex switch
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

        private bool OwnerMatches(DirectoryNode node)
        {
            if (string.IsNullOrWhiteSpace(_ownerFilter)) return true;
            if (string.IsNullOrWhiteSpace(node.Path)) return false;
            var owner = GetOwnerCached(node.Path, node.IsDirectory);
            return string.Equals(owner, _ownerFilter, StringComparison.OrdinalIgnoreCase);
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

        /// <summary>
        /// Get color based on density - Green (dense/large) to Red (sparse/small)
        /// </summary>
        private Color GetDensityColor(double density, long size, long maxSize)
        {
            // Use a combination of relative density and absolute size ratio
            double sizeRatio = maxSize > 0 ? (double)size / maxSize : 0;
            
            // Blend density and size ratio
            double value = (density * 0.4 + sizeRatio * 0.6);
            
            // Clamp to 0-1
            value = Math.Max(0, Math.Min(1, value));
            
            // Create gradient from Red (low) -> Yellow (medium) -> Green (high)
            Color color;
            
            if (value < 0.5)
            {
                // Red to Yellow (0 to 0.5)
                double t = value * 2; // 0 to 1
                color = Color.FromRgb(
                    (byte)(220 - (int)(40 * t)),  // Red: 220 -> 180
                    (byte)(80 + (int)(140 * t)),  // Green: 80 -> 220
                    (byte)(80 + (int)(20 * t))    // Blue: 80 -> 100
                );
            }
            else
            {
                // Yellow to Green (0.5 to 1)
                double t = (value - 0.5) * 2; // 0 to 1
                color = Color.FromRgb(
                    (byte)(180 - (int)(120 * t)), // Red: 180 -> 60
                    (byte)(220 - (int)(30 * t)),  // Green: 220 -> 190
                    (byte)(100 - (int)(20 * t))   // Blue: 100 -> 80
                );
            }
            
            // Add slight variation based on hash of name for visual distinction
            int hash = size.GetHashCode();
            int variation = (hash % 30) - 15;
            
            color = Color.FromRgb(
                (byte)Math.Max(0, Math.Min(255, color.R + variation)),
                (byte)Math.Max(0, Math.Min(255, color.G + variation / 2)),
                (byte)Math.Max(0, Math.Min(255, color.B + variation / 3))
            );
            
            return color;
        }

        private Color GetFileTypeColor(DirectoryNode node)
        {
            try
            {
                if (node == null) return Color.FromRgb(128, 128, 128);
                
                if (node.IsDirectory)
                    return Color.FromRgb(113, 128, 150); // Gray for folders
                
                if (string.IsNullOrEmpty(node.Name))
                    return Color.FromRgb(160, 174, 192);
                    
                var ext = System.IO.Path.GetExtension(node.Name)?.ToLower() ?? "";
                return ext switch
                {
                    ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".flv" or ".webm" => Color.FromRgb(229, 62, 62), // Red - Video
                    ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".svg" or ".webp" => Color.FromRgb(56, 161, 105), // Green - Image
                    ".mp3" or ".wav" or ".flac" or ".aac" or ".ogg" or ".wma" => Color.FromRgb(49, 130, 206), // Blue - Audio
                    ".pdf" or ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx" or ".txt" => Color.FromRgb(214, 158, 46), // Yellow - Docs
                    ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => Color.FromRgb(128, 90, 213), // Purple - Archives
                    ".exe" or ".msi" or ".dll" => Color.FromRgb(237, 137, 54), // Orange - Apps
                    _ => Color.FromRgb(160, 174, 192) // Light gray - Other
                };
            }
            catch
            {
                return Color.FromRgb(128, 128, 128);
            }
        }

        private Color GetAgeColor(DirectoryNode node)
        {
            // Simplified - don't access file system during rendering (causes crashes)
            // Just use a hash of the path for variation
            try
            {
                if (node?.Path == null) return Color.FromRgb(128, 128, 128);
                
                int hash = Math.Abs(node.Path.GetHashCode());
                int colorIndex = hash % 4;
                
                return colorIndex switch
                {
                    0 => Color.FromRgb(72, 187, 120),  // Green
                    1 => Color.FromRgb(236, 201, 75),  // Yellow
                    2 => Color.FromRgb(237, 137, 54),  // Orange
                    _ => Color.FromRgb(229, 62, 62)    // Red
                };
            }
            catch
            {
                return Color.FromRgb(128, 128, 128);
            }
        }

        private Color LightenColor(Color color, double amount)
        {
            return Color.FromRgb(
                (byte)Math.Min(255, color.R + (255 - color.R) * amount),
                (byte)Math.Min(255, color.G + (255 - color.G) * amount),
                (byte)Math.Min(255, color.B + (255 - color.B) * amount)
            );
        }

        private ContextMenu CreateContextMenu(DirectoryNode node)
        {
            var menu = new ContextMenu();

            // Open / Explore
            if (node.IsDirectory)
            {
                var openItem = new MenuItem { Header = "📂 Open in Explorer" };
                openItem.Click += (s, e) => OpenInExplorer(node.Path);
                menu.Items.Add(openItem);

                var zoomItem = new MenuItem { Header = "🔍 Zoom Into Folder" };
                zoomItem.Click += (s, e) =>
                {
                    if (node.Children.Count > 0)
                    {
                        _navigationStack.Push(_currentNode!);
                        DisplayNode(node);
                        NodeSelected?.Invoke(node);
                    }
                };
                menu.Items.Add(zoomItem);

                var excludeItem = new MenuItem { Header = "🚫 Exclude Folder" };
                excludeItem.Click += (s, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(node.Path))
                    {
                        ExcludePathRequested?.Invoke(node.Path);
                    }
                };
                menu.Items.Add(excludeItem);
            }
            else
            {
                var openFileItem = new MenuItem { Header = "📄 Open File" };
                openFileItem.Click += (s, e) => OpenFile(node.Path);
                menu.Items.Add(openFileItem);
            }

            menu.Items.Add(new Separator());

            // Show in Explorer (select the item)
            var showItem = new MenuItem { Header = "📍 Show in Explorer" };
            showItem.Click += (s, e) => ShowInExplorer(node.Path);
            menu.Items.Add(showItem);

            // Copy Path
            var copyPathItem = new MenuItem { Header = "📋 Copy Path" };
            copyPathItem.Click += (s, e) => Clipboard.SetText(node.Path);
            menu.Items.Add(copyPathItem);

            // Copy Name
            var copyNameItem = new MenuItem { Header = "📝 Copy Name" };
            copyNameItem.Click += (s, e) => Clipboard.SetText(node.Name);
            menu.Items.Add(copyNameItem);

            menu.Items.Add(new Separator());

            // Properties
            var propsItem = new MenuItem { Header = "ℹ️ Properties" };
            propsItem.Click += (s, e) => ShowProperties(node.Path);
            menu.Items.Add(propsItem);

            menu.Items.Add(new Separator());

            // Delete (with confirmation)
            var deleteItem = new MenuItem 
            { 
                Header = "🗑️ Delete (Recycle Bin)", 
                Foreground = new SolidColorBrush(Color.FromRgb(248, 113, 113))
            };
            deleteItem.Click += (s, e) => DeleteItem(node);
            menu.Items.Add(deleteItem);

            return menu;
        }

        private void OpenInExplorer(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not open folder: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenFile(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not open file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
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
            catch (Exception ex)
            {
                MessageBox.Show($"Could not show in Explorer: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ShowProperties(string path)
        {
            try
            {
                NativeMethods.ShowFileProperties(path);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not show properties: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void DeleteItem(DirectoryNode node)
        {
            var result = MessageBox.Show(
                $"Move to Recycle Bin?\n\n{node.Path}\n\nSize: {node.FormattedSize}",
                "Confirm Delete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    if (node.IsDirectory)
                    {
                        FileSystem.DeleteDirectory(node.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                    }
                    else
                    {
                        FileSystem.DeleteFile(node.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                    }

                    // Remove from parent and refresh
                    if (_currentNode != null)
                    {
                        var parent = _currentNode;
                        var toRemove = parent.Children.FirstOrDefault(c => c.Path == node.Path);
                        if (toRemove != null)
                        {
                            parent.Children.Remove(toRemove);
                            parent.Size -= node.Size;
                        }
                        UpdateTreemap();
                    }

                    MessageBox.Show("Item deleted successfully.", "Deleted", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Could not delete: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private string TruncateText(string text, double maxWidth)
        {
            if (string.IsNullOrEmpty(text)) return "";
            int maxChars = Math.Max(3, (int)(maxWidth / 7));
            if (text.Length <= maxChars) return text;
            return text.Substring(0, maxChars - 2) + "..";
        }
    }

    // Native methods for showing properties dialog
    internal static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private struct SHELLEXECUTEINFO
        {
            public int cbSize;
            public uint fMask;
            public IntPtr hwnd;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPTStr)]
            public string lpVerb;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPTStr)]
            public string lpFile;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPTStr)]
            public string lpParameters;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPTStr)]
            public string lpDirectory;
            public int nShow;
            public IntPtr hInstApp;
            public IntPtr lpIDList;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPTStr)]
            public string lpClass;
            public IntPtr hkeyClass;
            public uint dwHotKey;
            public IntPtr hIcon;
            public IntPtr hProcess;
        }

        private const uint SEE_MASK_INVOKEIDLIST = 12;

        public static void ShowFileProperties(string path)
        {
            SHELLEXECUTEINFO info = new SHELLEXECUTEINFO();
            info.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(info);
            info.lpVerb = "properties";
            info.lpFile = path;
            info.nShow = 5;
            info.fMask = SEE_MASK_INVOKEIDLIST;
            ShellExecuteEx(ref info);
        }
    }
}
