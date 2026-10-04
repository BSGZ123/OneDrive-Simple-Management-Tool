using Microsoft.UI.Xaml.Controls;
using System;
using Windows.Foundation;

namespace OneDrive_Simple_Management_Tool.Controls
{
    // Keep each button's measured width; VariableSizedWrapGrid sizes cells from
    // the first child and can clip longer labels in another language.
    public sealed class WrapPanel : Panel
    {
        protected override Size MeasureOverride(Size availableSize)
        {
            double x = 0, y = 0, lineHeight = 0, width = 0;
            foreach (var child in Children)
            {
                child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
                Size size = child.DesiredSize;
                if (x > 0 && x + size.Width > availableSize.Width)
                {
                    width = Math.Max(width, x);
                    y += lineHeight;
                    x = lineHeight = 0;
                }
                x += size.Width;
                lineHeight = Math.Max(lineHeight, size.Height);
            }
            return new Size(Math.Max(width, x), y + lineHeight);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double x = 0, y = 0, lineHeight = 0;
            foreach (var child in Children)
            {
                Size size = child.DesiredSize;
                if (x > 0 && x + size.Width > finalSize.Width)
                {
                    y += lineHeight;
                    x = lineHeight = 0;
                }
                child.Arrange(new Rect(x, y, size.Width, size.Height));
                x += size.Width;
                lineHeight = Math.Max(lineHeight, size.Height);
            }
            return finalSize;
        }
    }
}
