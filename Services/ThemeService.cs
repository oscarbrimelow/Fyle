using System.Windows;
using System.Windows.Media;

namespace Fyle.Services
{
    public class ThemeService
    {
        public enum Theme
        {
            Light,
            Dark
        }

        private Theme _currentTheme = Theme.Light;

        public Theme CurrentTheme => _currentTheme;

        public void ToggleTheme()
        {
            _currentTheme = _currentTheme == Theme.Light ? Theme.Dark : Theme.Light;
            ApplyTheme();
        }

        public void SetTheme(Theme theme)
        {
            _currentTheme = theme;
            ApplyTheme();
        }

        private void ApplyTheme()
        {
            var app = Application.Current;
            if (app == null) return;

            var resources = app.Resources;

            if (_currentTheme == Theme.Dark)
            {
                // Refined dark theme - darker and more cohesive
                resources["BackgroundBrush"] = new SolidColorBrush(Color.FromRgb(18, 18, 22));
                resources["SurfaceBrush"] = new SolidColorBrush(Color.FromRgb(26, 26, 32));
                resources["SurfaceSecondaryBrush"] = new SolidColorBrush(Color.FromRgb(32, 32, 40));
                resources["BorderBrush"] = new SolidColorBrush(Color.FromRgb(50, 50, 60));
                resources["TextBrush"] = new SolidColorBrush(Color.FromRgb(240, 240, 245));
                resources["TextSecondaryBrush"] = new SolidColorBrush(Color.FromRgb(140, 140, 155));
                resources["AccentBrush"] = new SolidColorBrush(Color.FromRgb(99, 179, 237)); // Soft blue
                resources["AccentHoverBrush"] = new SolidColorBrush(Color.FromRgb(66, 153, 225));
                resources["HoverBrush"] = new SolidColorBrush(Color.FromRgb(40, 40, 50));
                resources["SelectedBrush"] = new SolidColorBrush(Color.FromRgb(66, 153, 225));
                resources["DangerBrush"] = new SolidColorBrush(Color.FromRgb(245, 101, 101));
                resources["SuccessBrush"] = new SolidColorBrush(Color.FromRgb(72, 187, 120));
                resources["WarningBrush"] = new SolidColorBrush(Color.FromRgb(236, 201, 75));
            }
            else
            {
                // Clean light theme
                resources["BackgroundBrush"] = new SolidColorBrush(Color.FromRgb(245, 247, 250));
                resources["SurfaceBrush"] = new SolidColorBrush(Color.FromRgb(255, 255, 255));
                resources["SurfaceSecondaryBrush"] = new SolidColorBrush(Color.FromRgb(237, 242, 247));
                resources["BorderBrush"] = new SolidColorBrush(Color.FromRgb(226, 232, 240));
                resources["TextBrush"] = new SolidColorBrush(Color.FromRgb(26, 32, 44));
                resources["TextSecondaryBrush"] = new SolidColorBrush(Color.FromRgb(113, 128, 150));
                resources["AccentBrush"] = new SolidColorBrush(Color.FromRgb(49, 130, 206));
                resources["AccentHoverBrush"] = new SolidColorBrush(Color.FromRgb(43, 108, 176));
                resources["HoverBrush"] = new SolidColorBrush(Color.FromRgb(237, 242, 247));
                resources["SelectedBrush"] = new SolidColorBrush(Color.FromRgb(49, 130, 206));
                resources["DangerBrush"] = new SolidColorBrush(Color.FromRgb(229, 62, 62));
                resources["SuccessBrush"] = new SolidColorBrush(Color.FromRgb(56, 161, 105));
                resources["WarningBrush"] = new SolidColorBrush(Color.FromRgb(214, 158, 46));
            }
        }
    }
}
