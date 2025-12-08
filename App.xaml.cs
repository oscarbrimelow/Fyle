using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Fyle.Core;
using Fyle.Services;

namespace Fyle
{
    public partial class App : Application
    {
        // Command line arguments
        public static string? AutoScanPath { get; private set; }
        public static string? ExportPath { get; private set; }
        public static bool SilentMode { get; private set; }
        public static bool UseMftScanning { get; private set; }

        public App()
        {
            // Catch all unhandled exceptions
            DispatcherUnhandledException += App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
            
            Logger.Log("App starting...");
        }

        private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Logger.LogError("DispatcherUnhandledException", e.Exception);
            
            if (!SilentMode)
            {
                MessageBox.Show(
                    $"An error occurred:\n\n{e.Exception.Message}\n\nCheck log file at:\n{Logger.GetLogPath()}",
                    "Fyle Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            
            e.Handled = true; // Prevent crash
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                Logger.LogError("UnhandledException", ex);
            }
            else
            {
                Logger.Log($"UnhandledException (non-Exception): {e.ExceptionObject}");
            }
        }

        private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            Logger.LogError("UnobservedTaskException", e.Exception);
            e.SetObserved(); // Prevent crash
        }

        protected override async void OnStartup(StartupEventArgs e)
        {
            Logger.Log("OnStartup called");
            
            // Parse command line arguments
            var args = e.Args;
            
            if (args.Contains("--help") || args.Contains("-h") || args.Contains("/?"))
            {
                ShowHelp();
                Shutdown();
                return;
            }

            // Parse arguments
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLower())
                {
                    case "--scan":
                    case "-s":
                        if (i + 1 < args.Length)
                            AutoScanPath = args[++i];
                        break;
                    case "--export":
                    case "-e":
                        if (i + 1 < args.Length)
                            ExportPath = args[++i];
                        break;
                    case "--silent":
                    case "-q":
                        SilentMode = true;
                        break;
                    case "--mft":
                    case "-m":
                        UseMftScanning = true;
                        break;
                }
            }

            // Silent mode with scan and export
            if (SilentMode && !string.IsNullOrEmpty(AutoScanPath) && !string.IsNullOrEmpty(ExportPath))
            {
                await RunSilentScan();
                Shutdown();
                return;
            }

            base.OnStartup(e);
            Logger.Log("OnStartup complete");
        }

        private void ShowHelp()
        {
            var help = @"
Fyle - Advanced Disk Space Analyzer
====================================

Usage: Fyle.exe [options]

Options:
  --scan, -s <path>     Scan the specified drive or folder
                        Example: --scan C:\
                        
  --export, -e <file>   Export results to file (CSV, JSON, HTML, XML)
                        Example: --export report.csv
                        
  --silent, -q          Run without GUI (requires --scan and --export)
                        Example: --silent --scan C:\ --export report.csv
                        
  --mft, -m             Use MFT fast scanning (NTFS only, requires admin)
                        Example: --mft --scan C:\
                        
  --help, -h, /?        Show this help message

Examples:
  Fyle.exe --scan D:\
      Open GUI and automatically scan D: drive
      
  Fyle.exe --silent --scan C:\ --export C:\report.csv
      Scan C: drive and export to CSV without showing GUI
      
  Fyle.exe --mft --silent --scan C:\ --export report.html
      Fast MFT scan of C: and export HTML report

Notes:
  - MFT scanning requires Administrator privileges
  - MFT scanning only works on NTFS drives
  - Export format is determined by file extension (.csv, .json, .html, .xml)
";
            Console.WriteLine(help);
            
            // Also show message box for non-console usage
            MessageBox.Show(help, "Fyle - Command Line Help", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private async Task RunSilentScan()
        {
            Console.WriteLine($"Fyle Silent Scan: {AutoScanPath}");
            Logger.Log($"Silent scan starting: {AutoScanPath}");

            try
            {
                DirectoryNode? root = null;

                if (UseMftScanning && MftScanner.IsMftAvailable(AutoScanPath!))
                {
                    Console.WriteLine("Using MFT fast scanning...");
                    var mftScanner = new MftScanner();
                    mftScanner.StatusChanged += status => Console.WriteLine($"  {status}");
                    mftScanner.ProgressChanged += progress => Console.Write($"\r  Progress: {progress}%  ");
                    
                    root = await mftScanner.ScanDriveAsync(AutoScanPath!);
                }

                if (root == null)
                {
                    Console.WriteLine("Using standard scanning...");
                    var scanner = new Scanner();
                    scanner.CurrentPathChanged += status => Console.WriteLine($"  {status}");
                    scanner.ProgressChanged += progress => Console.Write($"\r  Progress: {progress:F0}%  ");
                    
                    root = await scanner.ScanDriveAsync(AutoScanPath!);
                }

                if (root == null)
                {
                    Console.WriteLine("\nScan failed!");
                    return;
                }

                Console.WriteLine($"\nScan complete: {root.Children.Count} items, {FormatSize(root.Size)}");

                // Export
                if (!string.IsNullOrEmpty(ExportPath))
                {
                    Console.WriteLine($"Exporting to: {ExportPath}");
                    
                    var ext = Path.GetExtension(ExportPath).ToLower();
                    switch (ext)
                    {
                        case ".csv":
                            ExportService.ExportToCsv(root, ExportPath);
                            break;
                        case ".json":
                            ExportService.ExportToJson(root, ExportPath);
                            break;
                        case ".html":
                            ExportService.ExportToHtml(root, ExportPath, $"Fyle Report - {AutoScanPath}");
                            break;
                        case ".xml":
                            ExportService.ExportToXml(root, ExportPath);
                            break;
                        default:
                            Console.WriteLine($"Unknown export format: {ext}");
                            break;
                    }

                    Console.WriteLine("Export complete!");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                Logger.LogError("SilentScan", ex);
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
    }
}
