using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;

namespace Fyle.Services
{
    public static class AdminElevation
    {
        public static bool IsAdministrator()
        {
            var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        public static bool RequiresElevation(string path)
        {
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(path) ?? path);
                return drive.Name.StartsWith("C:", StringComparison.OrdinalIgnoreCase) ||
                       path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase) ||
                       path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public static bool RequestElevation()
        {
            if (IsAdministrator())
                return true;

            try
            {
                var exePath = GetExecutablePath();
                var startInfo = new ProcessStartInfo
                {
                    UseShellExecute = true,
                    WorkingDirectory = Environment.CurrentDirectory,
                    FileName = exePath,
                    Verb = "runas"
                };

                Process.Start(startInfo);
                Application.Current.Shutdown();
                return true;
            }
            catch
            {
                MessageBox.Show(
                    "Administrator privileges are required to scan this drive. Please run the application as administrator.",
                    "Elevation Required",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }
        }

        public static void RestartWithElevation()
        {
            try
            {
                var exePath = GetExecutablePath();
                var startInfo = new ProcessStartInfo
                {
                    UseShellExecute = true,
                    WorkingDirectory = Environment.CurrentDirectory,
                    FileName = exePath,
                    Verb = "runas"
                };

                Process.Start(startInfo);
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to restart with admin privileges: {ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private static string GetExecutablePath()
        {
            var exePath = System.AppContext.BaseDirectory + "Fyle.exe";
            if (!System.IO.File.Exists(exePath))
            {
                exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "Fyle.exe";
            }
            return exePath;
        }
    }
}

