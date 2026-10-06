using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Foundation;

namespace OneDrive_Simple_Management_Tool.Controls
{
    // Equal-width columns that fit the available width. Unlike a uniform grid, each row
    // takes the height of its tallest child, so a card showing an error is not clipped.
    public sealed class AdaptiveGridPanel : Panel
    {
        public double MinItemWidth { get; set; } = 240;
        public double ColumnSpacing { get; set; }
        public double RowSpacing { get; set; }

        protected override Size MeasureOverride(Size availableSize)
        {
            var items = Children.Where(child => child.Visibility == Visibility.Visible).ToList();
            if (items.Count == 0) return new Size(0, 0);
            var (columns, itemWidth) = Columns(availableSize.Width, items.Count);
            foreach (var child in items) child.Measure(new Size(itemWidth, double.PositiveInfinity));
            double height = 0;
            for (int start = 0; start < items.Count; start += columns)
                height += RowHeight(items, start, columns) + (start > 0 ? RowSpacing : 0);
            return new Size(columns * itemWidth + (columns - 1) * ColumnSpacing, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var items = Children.Where(child => child.Visibility == Visibility.Visible).ToList();
            if (items.Count == 0) return finalSize;
            var (columns, itemWidth) = Columns(finalSize.Width, items.Count);
            double y = 0;
            for (int start = 0; start < items.Count; start += columns)
            {
                double rowHeight = RowHeight(items, start, columns);
                for (int index = start; index < Math.Min(start + columns, items.Count); index++)
                    items[index].Arrange(new Rect((index - start) * (itemWidth + ColumnSpacing), y, itemWidth, rowHeight));
                y += rowHeight + RowSpacing;
            }
            return finalSize;
        }

        private (int Columns, double ItemWidth) Columns(double width, int count)
        {
            if (double.IsInfinity(width)) return (count, MinItemWidth);
            int columns = Math.Clamp((int)((width + ColumnSpacing) / (MinItemWidth + ColumnSpacing)), 1, count);
            // Balance wrapped rows (5 items in 4 columns become 3 + 2) instead of leaving one orphan.
            columns = (int)Math.Ceiling(count / Math.Ceiling(count / (double)columns));
            return (columns, Math.Max(0, (width - (columns - 1) * ColumnSpacing) / columns));
        }

        private static double RowHeight(List<UIElement> items, int start, int columns) =>
            items.Skip(start).Take(columns).Max(child => child.DesiredSize.Height);
    }
}
