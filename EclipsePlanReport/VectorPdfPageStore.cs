using System.Collections.Generic;
using System.Windows.Media;

namespace EclipsePlanReport
{
    internal class VectorPdfPage
    {
        public string ImagePath { get; set; }
        public Drawing Drawing { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public List<VectorPdfTextRun> TextRuns { get; set; }
    }

    internal class VectorPdfTextRun
    {
        public string Text { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double FontSize { get; set; }
        public bool Bold { get; set; }
    }

    internal static class VectorPdfPageStore
    {
        private static readonly Dictionary<string, VectorPdfPage> Pages = new Dictionary<string, VectorPdfPage>();

        public static void Register(string imagePath, Drawing drawing, double width, double height)
        {
            Register(imagePath, drawing, width, height, null);
        }

        public static void Register(string imagePath, Drawing drawing, double width, double height, List<VectorPdfTextRun> textRuns)
        {
            if (string.IsNullOrEmpty(imagePath) || drawing == null)
                return;

            Pages[imagePath] = new VectorPdfPage
            {
                ImagePath = imagePath,
                Drawing = drawing,
                Width = width,
                Height = height,
                TextRuns = textRuns ?? new List<VectorPdfTextRun>()
            };
        }

        public static bool TryGet(string imagePath, out VectorPdfPage page)
        {
            return Pages.TryGetValue(imagePath, out page);
        }

        public static void Clear()
        {
            Pages.Clear();
        }
    }
}
