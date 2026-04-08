using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Fyle.Core;

namespace Fyle.Services
{
    /// <summary>
    /// Squarified Treemap Layout Algorithm
    /// Based on "Squarified Treemaps" by Bruls, Huizing, and van Wijk
    /// </summary>
    public class TreemapLayout
    {
        public class TreemapItem
        {
            public DirectoryNode Node { get; set; } = null!;
            public Rect Bounds { get; set; }
        }

        public static List<TreemapItem> CalculateLayout(DirectoryNode node, Rect space, double minSize = 1.0)
        {
            var result = new List<TreemapItem>();

            try
            {
                if (node == null) return result;
                if (node.Children == null || space.Width <= 0 || space.Height <= 0) 
                {
                    if (node != null)
                        result.Add(new TreemapItem { Node = node, Bounds = space });
                    return result;
                }

                var items = node.Children
                    .Where(c => c != null && c.Size > 0)
                    .OrderByDescending(c => c.Size)
                    .ToList();

                if (items.Count == 0)
                {
                    result.Add(new TreemapItem { Node = node, Bounds = space });
                    return result;
                }

                return CalculateLayout(items, space, minSize);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CalculateLayout error: {ex.Message}");
                if (node != null)
                    result.Add(new TreemapItem { Node = node, Bounds = space });
            }

            return result;
        }

        public static List<TreemapItem> CalculateLayout(IEnumerable<DirectoryNode> items, Rect space, double minSize = 1.0)
        {
            var result = new List<TreemapItem>();

            try
            {
                if (items == null) return result;
                if (space.Width <= 0 || space.Height <= 0) return result;

                var list = items
                    .Where(c => c != null && c.Size > 0)
                    .OrderByDescending(c => c.Size)
                    .ToList();

                if (list.Count == 0) return result;

                double totalSize = list.Sum(i => i.Size);
                if (totalSize <= 0) totalSize = 1;

                Squarify(list, 0, list.Count, space, totalSize, result, minSize);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CalculateLayout(items) error: {ex.Message}");
            }

            return result;
        }

        private static void Squarify(List<DirectoryNode> items, int start, int end, Rect rect, double totalSize, List<TreemapItem> result, double minSize)
        {
            if (start >= end) return;

            if (end - start == 1)
            {
                // Single item - give it the whole rectangle
                result.Add(new TreemapItem { Node = items[start], Bounds = rect });
                return;
            }

            if (rect.Width <= minSize || rect.Height <= minSize)
            {
                // Rectangle too small - just stack remaining items
                LayoutRemaining(items, start, end, rect, totalSize, result, minSize);
                return;
            }

            // Calculate the total size of items in this range
            double rangeSize = 0;
            for (int i = start; i < end; i++)
            {
                rangeSize += items[i].Size;
            }
            if (rangeSize <= 0) rangeSize = 1;

            // Determine layout direction - lay out along the SHORTER side
            // If width >= height, we create a column on the LEFT (items stacked vertically)
            // If height > width, we create a row at the TOP (items side by side)
            bool layoutVertically = rect.Width >= rect.Height;
            double shortSide = layoutVertically ? rect.Height : rect.Width;

            // Find the optimal row/column
            int rowEnd = start;
            double rowSize = 0;
            double lastWorst = double.MaxValue;

            for (int i = start; i < end; i++)
            {
                double testRowSize = rowSize + items[i].Size;
                double testWorst = WorstAspectRatio(items, start, i + 1, testRowSize, shortSide, rangeSize, rect);

                if (i == start || testWorst <= lastWorst)
                {
                    rowEnd = i + 1;
                    rowSize = testRowSize;
                    lastWorst = testWorst;
                }
                else
                {
                    break;
                }
            }

            // Layout the row/column
            Rect remaining = LayoutRow(items, start, rowEnd, rect, rowSize, rangeSize, layoutVertically, result, minSize);

            // Recurse on remaining items
            double remainingSize = rangeSize - rowSize;
            if (rowEnd < end && remaining.Width > minSize && remaining.Height > minSize)
            {
                Squarify(items, rowEnd, end, remaining, totalSize, result, minSize);
            }
            else if (rowEnd < end)
            {
                // Still have items but space is small - stack them
                LayoutRemaining(items, rowEnd, end, remaining, totalSize, result, minSize);
            }
        }

        private static double WorstAspectRatio(List<DirectoryNode> items, int start, int end, double rowSize, double shortSide, double totalSize, Rect rect)
        {
            if (end <= start || rowSize <= 0 || shortSide <= 0) return double.MaxValue;

            double totalArea = rect.Width * rect.Height;
            double rowArea = (rowSize / totalSize) * totalArea;
            double rowLength = rowArea / shortSide;

            double worst = 0;
            for (int i = start; i < end; i++)
            {
                double itemArea = (items[i].Size / rowSize) * rowArea;
                double itemLength = itemArea / rowLength;
                
                if (itemLength <= 0 || rowLength <= 0) continue;
                
                double ratio = Math.Max(rowLength / itemLength, itemLength / rowLength);
                worst = Math.Max(worst, ratio);
            }

            return worst;
        }

        private static Rect LayoutRow(List<DirectoryNode> items, int start, int end, Rect rect, double rowSize, double totalSize, bool layoutVertically, List<TreemapItem> result, double minSize)
        {
            if (end <= start) return rect;
            if (rect.Width <= 0 || rect.Height <= 0) return new Rect(rect.X, rect.Y, 0, 0);
            if (rowSize <= 0) rowSize = 1;
            if (totalSize <= 0) totalSize = 1;

            double totalArea = rect.Width * rect.Height;
            double rowArea = (rowSize / totalSize) * totalArea;

            if (layoutVertically)
            {
                // Create a column on the LEFT - items stacked vertically
                double colWidth = rect.Height > 0 ? rowArea / rect.Height : minSize;
                colWidth = Math.Max(minSize, Math.Min(colWidth, rect.Width));

                double y = rect.Y;
                double remainingHeight = rect.Height;

                for (int i = start; i < end; i++)
                {
                    if (remainingHeight <= 0) break;
                    
                    double itemProportion = items[i].Size / rowSize;
                    double itemHeight;

                    if (i == end - 1)
                    {
                        itemHeight = Math.Max(minSize, remainingHeight);
                    }
                    else
                    {
                        itemHeight = itemProportion * rect.Height;
                        itemHeight = Math.Max(minSize, Math.Min(itemHeight, remainingHeight));
                    }

                    // Ensure positive dimensions
                    double finalWidth = Math.Max(minSize, colWidth);
                    double finalHeight = Math.Max(minSize, itemHeight);

                    result.Add(new TreemapItem
                    {
                        Node = items[i],
                        Bounds = new Rect(rect.X, y, finalWidth, finalHeight)
                    });

                    y += itemHeight;
                    remainingHeight = Math.Max(0, remainingHeight - itemHeight);
                }

                // Return remaining rectangle (to the right)
                double newWidth = Math.Max(0, rect.Width - colWidth);
                return new Rect(rect.X + colWidth, rect.Y, newWidth, rect.Height);
            }
            else
            {
                // Create a row at the TOP - items side by side
                double rowHeight = rect.Width > 0 ? rowArea / rect.Width : minSize;
                rowHeight = Math.Max(minSize, Math.Min(rowHeight, rect.Height));

                double x = rect.X;
                double remainingWidth = rect.Width;

                for (int i = start; i < end; i++)
                {
                    if (remainingWidth <= 0) break;
                    
                    double itemProportion = items[i].Size / rowSize;
                    double itemWidth;

                    if (i == end - 1)
                    {
                        itemWidth = Math.Max(minSize, remainingWidth);
                    }
                    else
                    {
                        itemWidth = itemProportion * rect.Width;
                        itemWidth = Math.Max(minSize, Math.Min(itemWidth, remainingWidth));
                    }

                    // Ensure positive dimensions
                    double finalWidth = Math.Max(minSize, itemWidth);
                    double finalHeight = Math.Max(minSize, rowHeight);

                    result.Add(new TreemapItem
                    {
                        Node = items[i],
                        Bounds = new Rect(x, rect.Y, finalWidth, finalHeight)
                    });

                    x += itemWidth;
                    remainingWidth = Math.Max(0, remainingWidth - itemWidth);
                }

                // Return remaining rectangle (below)
                double newHeight = Math.Max(0, rect.Height - rowHeight);
                return new Rect(rect.X, rect.Y + rowHeight, rect.Width, newHeight);
            }
        }

        private static void LayoutRemaining(List<DirectoryNode> items, int start, int end, Rect rect, double totalSize, List<TreemapItem> result, double minSize)
        {
            if (start >= end) return;
            
            // Ensure we have positive dimensions
            double width = Math.Max(minSize, rect.Width);
            double height = Math.Max(minSize, rect.Height);

            double rangeSize = 0;
            for (int i = start; i < end; i++)
            {
                rangeSize += items[i].Size;
            }
            if (rangeSize <= 0) rangeSize = 1;

            bool horizontal = width >= height;
            double position = 0;
            double dimension = horizontal ? height : width;
            double otherDimension = horizontal ? width : height;

            for (int i = start; i < end; i++)
            {
                double itemProportion = items[i].Size / rangeSize;
                double itemDim = itemProportion * dimension;
                itemDim = Math.Max(minSize, itemDim);

                if (i == end - 1)
                {
                    itemDim = Math.Max(minSize, dimension - position);
                }

                // Ensure positive dimensions for the rect
                double finalItemDim = Math.Max(minSize, itemDim);
                double finalOtherDim = Math.Max(minSize, otherDimension);

                Rect itemRect;
                if (horizontal)
                {
                    itemRect = new Rect(rect.X, rect.Y + position, finalOtherDim, finalItemDim);
                }
                else
                {
                    itemRect = new Rect(rect.X + position, rect.Y, finalItemDim, finalOtherDim);
                }

                result.Add(new TreemapItem { Node = items[i], Bounds = itemRect });
                position += itemDim;
            }
        }
    }
}
