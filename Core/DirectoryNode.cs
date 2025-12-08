using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;

namespace Fyle.Core
{
    public class DirectoryNode
    {
        public string Path { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public long Size { get; set; }
        public long CompressedSize { get; set; } // Actual disk usage (for NTFS compressed files)
        public bool IsDirectory { get; set; }
        public DirectoryNode? Parent { get; set; }
        public ObservableCollection<DirectoryNode> Children { get; set; } = new();
        public int FileCount { get; set; }
        public int DirectoryCount { get; set; }

        public double SizeInGB => Size / (1024.0 * 1024.0 * 1024.0);
        public double SizeInMB => Size / (1024.0 * 1024.0);
        
        /// <summary>
        /// True if file is NTFS compressed (uses less disk space than logical size)
        /// </summary>
        public bool IsCompressed => CompressedSize > 0 && CompressedSize < Size;
        
        /// <summary>
        /// Compression ratio (1.0 = no compression, 0.5 = 50% compression)
        /// </summary>
        public double CompressionRatio => Size > 0 && CompressedSize > 0 ? CompressedSize / (double)Size : 1.0;
        
        /// <summary>
        /// Space saved by compression
        /// </summary>
        public long SpaceSaved => IsCompressed ? Size - CompressedSize : 0;

        public string FormattedSize
        {
            get
            {
                if (SizeInGB >= 1.0)
                    return $"{SizeInGB:F2} GB";
                else if (SizeInMB >= 1.0)
                    return $"{SizeInMB:F2} MB";
                else
                    return $"{Size / 1024.0:F2} KB";
            }
        }
        
        public string FormattedCompressedSize
        {
            get
            {
                if (CompressedSize <= 0) return FormattedSize;
                
                var sizeInGb = CompressedSize / (1024.0 * 1024.0 * 1024.0);
                var sizeInMb = CompressedSize / (1024.0 * 1024.0);
                
                if (sizeInGb >= 1.0)
                    return $"{sizeInGb:F2} GB";
                else if (sizeInMb >= 1.0)
                    return $"{sizeInMb:F2} MB";
                else
                    return $"{CompressedSize / 1024.0:F2} KB";
            }
        }
        
        public string SizeTooltip
        {
            get
            {
                if (IsCompressed)
                    return $"Logical: {FormattedSize}\nOn Disk: {FormattedCompressedSize}\nSaved: {FormatBytes(SpaceSaved)} ({(1 - CompressionRatio) * 100:F0}%)";
                return FormattedSize;
            }
        }

        public double GetPercentageOf(DirectoryNode parent)
        {
            if (parent == null || parent.Size == 0) return 0;
            return (Size / (double)parent.Size) * 100.0;
        }
        
        private static string FormatBytes(long bytes)
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
        
        #region Native API for Compressed Size
        
        [DllImport("kernel32.dll")]
        private static extern uint GetCompressedFileSizeW(
            [MarshalAs(UnmanagedType.LPWStr)] string lpFileName,
            out uint lpFileSizeHigh);
        
        /// <summary>
        /// Get the actual disk usage for a file (respects NTFS compression)
        /// </summary>
        public static long GetCompressedFileSize(string path)
        {
            try
            {
                uint low = GetCompressedFileSizeW(path, out uint high);
                if (low == 0xFFFFFFFF && Marshal.GetLastWin32Error() != 0)
                    return -1;
                return ((long)high << 32) | low;
            }
            catch
            {
                return -1;
            }
        }
        
        #endregion
    }
}

