using System;

namespace OneDrive_Simple_Management_Tool.Services
{
    public static class PreviewImageLayout
    {
        public static double Fit(double width, double height, double viewportWidth, double viewportHeight, double scale)
        {
            if (width <= 0 || height <= 0 || viewportWidth <= 0 || viewportHeight <= 0 || scale <= 0) return 1;
            return Math.Clamp(Math.Min(viewportWidth * scale / width, viewportHeight * scale / height), 0.001, 8);
        }

        public static int DecodeEdge(double width, double height, double zoom, bool svg)
        {
            // Keep a single decode below 40 MP and 8192 on either edge; SVG surfaces use the same budget.
            double edge = Math.Max(width, height);
            double ratio = Math.Min(width, height) / edge;
            double budgetEdge = Math.Min(8192, Math.Sqrt(40_000_000 / Math.Max(ratio, 0.00001)));
            return (int)Math.Clamp(Math.Ceiling(edge * Math.Min(zoom, svg ? 8 : 1)), 1, budgetEdge);
        }
    }
}
