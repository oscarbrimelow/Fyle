using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Fyle.UI
{
    public partial class PieChart : UserControl
    {
        public PieChart()
        {
            InitializeComponent();
        }

        public void SetData(List<PieSlice> slices)
        {
            PieCanvas.Children.Clear();

            if (slices == null || slices.Count == 0) return;

            double total = 0;
            foreach (var slice in slices)
                total += slice.Value;

            if (total <= 0) return;

            double centerX = PieCanvas.Width / 2;
            double centerY = PieCanvas.Height / 2;
            double radius = Math.Min(centerX, centerY) - 10;
            double startAngle = 0;

            foreach (var slice in slices)
            {
                double sweepAngle = (slice.Value / total) * 360;
                
                if (sweepAngle < 0.5) continue; // Skip tiny slices

                var path = CreatePieSlice(centerX, centerY, radius, startAngle, sweepAngle, slice.Color);
                path.ToolTip = $"{slice.Label}: {slice.DisplayValue} ({(slice.Value / total * 100):F1}%)";
                path.Cursor = System.Windows.Input.Cursors.Hand;
                path.MouseEnter += (s, e) => path.Opacity = 0.8;
                path.MouseLeave += (s, e) => path.Opacity = 1.0;
                
                PieCanvas.Children.Add(path);
                startAngle += sweepAngle;
            }
        }

        private Path CreatePieSlice(double centerX, double centerY, double radius, double startAngle, double sweepAngle, Color color)
        {
            double startRad = startAngle * Math.PI / 180;
            double endRad = (startAngle + sweepAngle) * Math.PI / 180;

            double x1 = centerX + radius * Math.Cos(startRad);
            double y1 = centerY + radius * Math.Sin(startRad);
            double x2 = centerX + radius * Math.Cos(endRad);
            double y2 = centerY + radius * Math.Sin(endRad);

            bool isLargeArc = sweepAngle > 180;

            var figure = new PathFigure
            {
                StartPoint = new Point(centerX, centerY),
                IsClosed = true
            };

            figure.Segments.Add(new LineSegment(new Point(x1, y1), true));
            figure.Segments.Add(new ArcSegment(
                new Point(x2, y2),
                new Size(radius, radius),
                0,
                isLargeArc,
                SweepDirection.Clockwise,
                true));

            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);

            return new Path
            {
                Fill = new SolidColorBrush(color),
                Data = geometry,
                Stroke = new SolidColorBrush(Colors.White),
                StrokeThickness = 1
            };
        }
    }

    public class PieSlice
    {
        public string Label { get; set; } = "";
        public double Value { get; set; }
        public string DisplayValue { get; set; } = "";
        public Color Color { get; set; }
    }
}

