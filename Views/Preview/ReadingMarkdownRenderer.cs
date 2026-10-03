using CommunityToolkit.Common.Parsers.Markdown;
using CommunityToolkit.Common.Parsers.Markdown.Blocks;
using CommunityToolkit.Common.Parsers.Markdown.Render;
using CommunityToolkit.WinUI.UI.Controls.Markdown.Render;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;

namespace OneDrive_Simple_Management_Tool.Views.Preview
{
    // Deliberately use the installed parser's extension point; no dependency upgrade or visual-tree patching.
#pragma warning disable CS0618
    public sealed class ReadingMarkdownRenderer : MarkdownRenderer
    {
        public ReadingMarkdownRenderer(MarkdownDocument document, ILinkRegister links, IImageResolver images, ICodeBlockResolver code)
            : base(document, links, images, code) { }

        protected override void RenderTable(TableBlock element, IRenderContext context)
        {
            base.RenderTable(element, context);
            var children = ((UIElementCollectionRenderContext)context).BlockUIElementCollection;
            var table = (FrameworkElement)children[children.Count - 1];
            children.RemoveAt(children.Count - 1);
            table.MinWidth = Math.Min(2400, element.ColumnDefinitions.Count * 140);
            children.Add(new ScrollViewer
            {
                Content = table, HorizontalScrollMode = ScrollMode.Enabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
            });
        }
    }
#pragma warning restore CS0618
}
