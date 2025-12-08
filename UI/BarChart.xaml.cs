using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Media;

namespace Fyle.UI
{
    public partial class BarChart : UserControl
    {
        public BarChart()
        {
            InitializeComponent();
        }

        public void SetData(List<BarItem> items, double maxBarWidth = 150)
        {
            if (items == null || items.Count == 0)
            {
                BarsContainer.ItemsSource = null;
                return;
            }

            double maxValue = items.Max(i => i.Value);
            if (maxValue <= 0) maxValue = 1;

            var displayItems = items.Select(item => new
            {
                item.Label,
                item.DisplayValue,
                BarWidth = (item.Value / maxValue) * maxBarWidth,
                BarColor = new SolidColorBrush(item.Color)
            }).ToList();

            BarsContainer.ItemsSource = displayItems;
        }
    }

    public class BarItem
    {
        public string Label { get; set; } = "";
        public double Value { get; set; }
        public string DisplayValue { get; set; } = "";
        public Color Color { get; set; }
    }
}

