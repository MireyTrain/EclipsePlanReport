using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EclipsePlanReport
{
    /// <summary>
    /// Hybrid-PDF-Writer: bevorzugt echte PDF-Vektoren aus den WPF-DrawingVisuals.
    /// Rasterbilder (CT/DRR/Screenshots) bleiben eingebettete JPEGs. Falls fuer eine
    /// Seite kein Drawing verfuegbar ist, wird auf den bisherigen Rasterweg
    /// zurueckgefallen.
    /// </summary>
    internal static class PdfWriter
    {
        private const double PageWidthPt = 841.89;  // A4 quer
        private const double PageHeightPt = 595.28;

        public static void CreatePdfFromImages(List<string> imagePaths, string pdfPath, Action<string> log)
        {
            try
            {
                List<VectorPdfPage> vectorPages = new List<VectorPdfPage>();
                foreach (string path in imagePaths.Where(File.Exists))
                {
                    VectorPdfPage page;
                    if (!VectorPdfPageStore.TryGet(path, out page))
                    {
                        if (log != null)
                            log("Vektor-PDF: nicht alle Seiten sind als Drawing verfuegbar - nutze Raster-Fallback.");
                        CreateRasterPdfFromImages(imagePaths, pdfPath, log);
                        return;
                    }
                    vectorPages.Add(page);
                }

                if (vectorPages.Count == 0)
                    return;

                CreateVectorPdf(vectorPages, pdfPath, log);
                if (log != null)
                    log("Vektor-PDF erstellt: Text, Linien, Tabellen und Kurven als PDF-Pfade; CT/DRR als Rasterbilder.");
            }
            catch (Exception e)
            {
                if (log != null)
                    log("Vektor-PDF fehlgeschlagen, nutze Raster-Fallback: " + e.Message);
                CreateRasterPdfFromImages(imagePaths, pdfPath, log);
            }
            finally
            {
                VectorPdfPageStore.Clear();
            }
        }

        private static void CreateVectorPdf(List<VectorPdfPage> pages, string pdfPath, Action<string> log)
        {
            int nextObjectId = 3;
            int regularFontObjectId = nextObjectId++;
            int boldFontObjectId = nextObjectId++;
            List<VectorPdfPageObject> pageObjects = new List<VectorPdfPageObject>();

            foreach (VectorPdfPage page in pages)
            {
                VectorPdfPageObject pageObject = new VectorPdfPageObject
                {
                    PageObjectId = nextObjectId++,
                    ContentObjectId = nextObjectId++
                };

                double sx = PageWidthPt / page.Width;
                double sy = PageHeightPt / page.Height;
                VectorContentBuilder builder = new VectorContentBuilder(pageObject, () => nextObjectId++, sx, sy);
                pageObject.Content = builder.BuildPageContent(page);
                pageObjects.Add(pageObject);
            }

            int objectCount = nextObjectId - 1;
            long[] offsets = new long[objectCount + 1];

            using (FileStream stream = new FileStream(pdfPath, FileMode.Create, FileAccess.Write))
            {
                WriteAscii(stream, "%PDF-1.4\n%");
                stream.Write(new byte[] { 0xE2, 0xE3, 0xCF, 0xD3 }, 0, 4);
                WriteAscii(stream, "\n");

                offsets[1] = stream.Position;
                WriteAscii(stream, "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

                offsets[2] = stream.Position;
                string kids = string.Join(" ", pageObjects.Select(p => p.PageObjectId.ToString(RenderUtils.Num) + " 0 R").ToArray());
                WriteAscii(stream, string.Format(RenderUtils.Num,
                    "2 0 obj\n<< /Type /Pages /Kids [{0}] /Count {1} >>\nendobj\n",
                    kids,
                    pageObjects.Count));

                offsets[regularFontObjectId] = stream.Position;
                WriteAscii(stream, string.Format(RenderUtils.Num,
                    "{0} 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj\n",
                    regularFontObjectId));

                offsets[boldFontObjectId] = stream.Position;
                WriteAscii(stream, string.Format(RenderUtils.Num,
                    "{0} 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>\nendobj\n",
                    boldFontObjectId));

                foreach (VectorPdfPageObject pageObject in pageObjects)
                {
                    offsets[pageObject.PageObjectId] = stream.Position;
                    StringBuilder xobjects = new StringBuilder();
                    foreach (PdfImageXObject image in pageObject.Images)
                        xobjects.AppendFormat(RenderUtils.Num, "/{0} {1} 0 R ", image.Name, image.ObjectId);

                    WriteAscii(stream, string.Format(RenderUtils.Num,
                        "{0} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {1:0.##} {2:0.##}] /Resources << /XObject << {3} >> /Font << /F1 {4} 0 R /F2 {5} 0 R >> >> /Contents {6} 0 R >>\nendobj\n",
                        pageObject.PageObjectId,
                        PageWidthPt,
                        PageHeightPt,
                        xobjects.ToString(),
                        regularFontObjectId,
                        boldFontObjectId,
                        pageObject.ContentObjectId));

                    foreach (PdfImageXObject image in pageObject.Images)
                    {
                        offsets[image.ObjectId] = stream.Position;
                        WriteAscii(stream, string.Format(RenderUtils.Num,
                            "{0} 0 obj\n<< /Type /XObject /Subtype /Image /Width {1} /Height {2} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {3} >>\nstream\n",
                            image.ObjectId,
                            image.Info.PixelWidth,
                            image.Info.PixelHeight,
                            image.Info.JpegBytes.Length));
                        stream.Write(image.Info.JpegBytes, 0, image.Info.JpegBytes.Length);
                        WriteAscii(stream, "\nendstream\nendobj\n");
                    }

                    byte[] contentBytes = Encoding.ASCII.GetBytes(pageObject.Content);
                    offsets[pageObject.ContentObjectId] = stream.Position;
                    WriteAscii(stream, string.Format(RenderUtils.Num,
                        "{0} 0 obj\n<< /Length {1} >>\nstream\n",
                        pageObject.ContentObjectId,
                        contentBytes.Length));
                    stream.Write(contentBytes, 0, contentBytes.Length);
                    WriteAscii(stream, "endstream\nendobj\n");
                }

                WriteCrossReference(stream, offsets, objectCount);
            }
        }

        private static void CreateRasterPdfFromImages(List<string> imagePaths, string pdfPath, Action<string> log)
        {
            List<PdfImageInfo> images = imagePaths
                .Where(File.Exists)
                .Select(path => EncodeImageForPdf(path, log))
                .Where(x => x != null)
                .ToList();

            if (images.Count == 0)
                return;

            int objectCount = 2 + images.Count * 3;
            long[] offsets = new long[objectCount + 1];

            using (FileStream stream = new FileStream(pdfPath, FileMode.Create, FileAccess.Write))
            {
                WriteAscii(stream, "%PDF-1.4\n%");
                stream.Write(new byte[] { 0xE2, 0xE3, 0xCF, 0xD3 }, 0, 4);
                WriteAscii(stream, "\n");

                offsets[1] = stream.Position;
                WriteAscii(stream, "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

                StringBuilder kids = new StringBuilder();
                for (int i = 0; i < images.Count; i++)
                    kids.AppendFormat(RenderUtils.Num, "{0} 0 R ", 3 + i * 3);

                offsets[2] = stream.Position;
                WriteAscii(stream, string.Format(RenderUtils.Num,
                    "2 0 obj\n<< /Type /Pages /Kids [{0}] /Count {1} >>\nendobj\n",
                    kids.ToString(),
                    images.Count));

                for (int i = 0; i < images.Count; i++)
                {
                    PdfImageInfo image = images[i];
                    int pageObject = 3 + i * 3;
                    int imageObject = pageObject + 1;
                    int contentObject = pageObject + 2;

                    offsets[pageObject] = stream.Position;
                    WriteAscii(stream, string.Format(RenderUtils.Num,
                        "{0} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {1:0.##} {2:0.##}] /Resources << /XObject << /Im{3} {4} 0 R >> >> /Contents {5} 0 R >>\nendobj\n",
                        pageObject,
                        PageWidthPt,
                        PageHeightPt,
                        i + 1,
                        imageObject,
                        contentObject));

                    offsets[imageObject] = stream.Position;
                    WriteAscii(stream, string.Format(RenderUtils.Num,
                        "{0} 0 obj\n<< /Type /XObject /Subtype /Image /Width {1} /Height {2} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {3} >>\nstream\n",
                        imageObject,
                        image.PixelWidth,
                        image.PixelHeight,
                        image.JpegBytes.Length));
                    stream.Write(image.JpegBytes, 0, image.JpegBytes.Length);
                    WriteAscii(stream, "\nendstream\nendobj\n");

                    string content = BuildRasterImageContent(image, i + 1);
                    byte[] contentBytes = Encoding.ASCII.GetBytes(content);

                    offsets[contentObject] = stream.Position;
                    WriteAscii(stream, string.Format(RenderUtils.Num,
                        "{0} 0 obj\n<< /Length {1} >>\nstream\n",
                        contentObject,
                        contentBytes.Length));
                    stream.Write(contentBytes, 0, contentBytes.Length);
                    WriteAscii(stream, "endstream\nendobj\n");
                }

                WriteCrossReference(stream, offsets, objectCount);
            }
        }

        private static string BuildRasterImageContent(PdfImageInfo image, int imageNumber)
        {
            double imageAspect = image.PixelWidth / (double)image.PixelHeight;
            double pageAspect = PageWidthPt / PageHeightPt;
            double drawWidth = PageWidthPt;
            double drawHeight = PageHeightPt;
            double x = 0;
            double y = 0;

            if (imageAspect > pageAspect)
            {
                drawHeight = PageWidthPt / imageAspect;
                y = (PageHeightPt - drawHeight) / 2.0;
            }
            else if (imageAspect < pageAspect)
            {
                drawWidth = PageHeightPt * imageAspect;
                x = (PageWidthPt - drawWidth) / 2.0;
            }

            return string.Format(RenderUtils.Num,
                "q {0:0.###} 0 0 {1:0.###} {2:0.###} {3:0.###} cm /Im{4} Do Q\n",
                drawWidth,
                drawHeight,
                x,
                y,
                imageNumber);
        }

        private static PdfImageInfo EncodeImageForPdf(string imagePath, Action<string> log)
        {
            try
            {
                BitmapDecoder decoder = BitmapDecoder.Create(
                    new Uri(imagePath, UriKind.Absolute),
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);

                return EncodeBitmapSource(decoder.Frames[0]);
            }
            catch (Exception e)
            {
                if (log != null)
                    log(string.Format("PDF-Seite konnte nicht gelesen werden ({0}): {1}", imagePath, e.Message));
                return null;
            }
        }

        private static PdfImageInfo EncodeImageSource(ImageSource imageSource)
        {
            BitmapSource source = imageSource as BitmapSource;
            if (source == null)
            {
                int width = Math.Max(1, (int)Math.Ceiling(imageSource.Width));
                int height = Math.Max(1, (int)Math.Ceiling(imageSource.Height));
                DrawingVisual visual = new DrawingVisual();
                using (DrawingContext dc = visual.RenderOpen())
                    dc.DrawImage(imageSource, new Rect(0, 0, width, height));
                RenderTargetBitmap bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                source = bitmap;
            }

            return EncodeBitmapSource(source);
        }

        private static PdfImageInfo EncodeBitmapSource(BitmapSource source)
        {
            BitmapSource rgbSource = source.Format == PixelFormats.Bgr24
                ? source
                : new FormatConvertedBitmap(source, PixelFormats.Bgr24, null, 0);

            JpegBitmapEncoder encoder = new JpegBitmapEncoder();
            encoder.QualityLevel = 95;
            encoder.Frames.Add(BitmapFrame.Create(rgbSource));

            using (MemoryStream memoryStream = new MemoryStream())
            {
                encoder.Save(memoryStream);
                return new PdfImageInfo
                {
                    JpegBytes = memoryStream.ToArray(),
                    PixelWidth = rgbSource.PixelWidth,
                    PixelHeight = rgbSource.PixelHeight
                };
            }
        }

        private static void WriteCrossReference(FileStream stream, long[] offsets, int objectCount)
        {
            long xrefOffset = stream.Position;
            WriteAscii(stream, string.Format(RenderUtils.Num, "xref\n0 {0}\n", objectCount + 1));
            WriteAscii(stream, "0000000000 65535 f \n");
            for (int i = 1; i <= objectCount; i++)
                WriteAscii(stream, offsets[i].ToString("D10", RenderUtils.Num) + " 00000 n \n");

            WriteAscii(stream, string.Format(RenderUtils.Num,
                "trailer\n<< /Size {0} /Root 1 0 R >>\nstartxref\n{1}\n%%EOF\n",
                objectCount + 1,
                xrefOffset));
        }

        private static void WriteAscii(Stream stream, string text)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
        }

        private class VectorPdfPageObject
        {
            public int PageObjectId;
            public int ContentObjectId;
            public string Content;
            public readonly List<PdfImageXObject> Images = new List<PdfImageXObject>();
        }

        private class PdfImageXObject
        {
            public int ObjectId;
            public string Name;
            public PdfImageInfo Info;
        }

        private class VectorContentBuilder
        {
            private readonly VectorPdfPageObject pageObject;
            private readonly Func<int> allocateObjectId;
            private readonly double scaleX;
            private readonly double scaleY;
            private readonly StringBuilder sb = new StringBuilder();

            public VectorContentBuilder(VectorPdfPageObject pageObject, Func<int> allocateObjectId, double scaleX, double scaleY)
            {
                this.pageObject = pageObject;
                this.allocateObjectId = allocateObjectId;
                this.scaleX = scaleX;
                this.scaleY = scaleY;
            }

            public string BuildPageContent(VectorPdfPage page)
            {
                sb.AppendFormat(RenderUtils.Num, "q {0:0.######} 0 0 -{1:0.######} 0 {2:0.###} cm\n", scaleX, scaleY, PageHeightPt);
                WriteDrawing(page.Drawing);
                WriteTextLayer(page.TextRuns);
                sb.Append("Q\n");
                return sb.ToString();
            }

            private void WriteDrawing(Drawing drawing)
            {
                DrawingGroup group = drawing as DrawingGroup;
                if (group != null)
                {
                    WriteGroup(group);
                    return;
                }

                GeometryDrawing geometryDrawing = drawing as GeometryDrawing;
                if (geometryDrawing != null)
                {
                    WriteGeometryDrawing(geometryDrawing);
                    return;
                }

                GlyphRunDrawing glyphRunDrawing = drawing as GlyphRunDrawing;
                if (glyphRunDrawing != null)
                {
                    WriteGlyphRun(glyphRunDrawing);
                    return;
                }

                ImageDrawing imageDrawing = drawing as ImageDrawing;
                if (imageDrawing != null)
                    WriteImage(imageDrawing);
            }

            private void WriteGroup(DrawingGroup group)
            {
                sb.Append("q\n");
                if (group.Transform != null && group.Transform.Value != Matrix.Identity)
                    WriteMatrix(group.Transform.Value);

                if (group.ClipGeometry != null)
                {
                    WriteGeometryPath(group.ClipGeometry);
                    sb.Append("W n\n");
                }

                foreach (Drawing child in group.Children)
                    WriteDrawing(child);

                sb.Append("Q\n");
            }

            private void WriteGeometryDrawing(GeometryDrawing drawing)
            {
                bool hasFill = TryWriteFillBrush(drawing.Brush);
                bool hasStroke = TryWritePen(drawing.Pen);
                if (!hasFill && !hasStroke)
                    return;

                WriteGeometryPath(drawing.Geometry);
                if (hasFill && hasStroke)
                    sb.Append(GetFillRule(drawing.Geometry) == FillRule.EvenOdd ? "B*\n" : "B\n");
                else if (hasFill)
                    sb.Append(GetFillRule(drawing.Geometry) == FillRule.EvenOdd ? "f*\n" : "f\n");
                else
                    sb.Append("S\n");
            }

            private void WriteGlyphRun(GlyphRunDrawing drawing)
            {
                if (!TryWriteFillBrush(drawing.ForegroundBrush))
                    return;

                Geometry textGeometry = drawing.GlyphRun.BuildGeometry();
                WriteGeometryPath(textGeometry);
                sb.Append("f\n");
            }

            private void WriteTextLayer(List<VectorPdfTextRun> textRuns)
            {
                if (textRuns == null || textRuns.Count == 0)
                    return;

                sb.Append("q\nBT\n3 Tr\n");
                foreach (VectorPdfTextRun run in textRuns)
                {
                    if (run == null || string.IsNullOrWhiteSpace(run.Text) || run.FontSize <= 0)
                        continue;

                    string encodedText = EscapeWinAnsiString(run.Text);
                    if (string.IsNullOrEmpty(encodedText))
                        continue;

                    double baselineY = run.Y + run.FontSize * 0.82;
                    sb.AppendFormat(RenderUtils.Num,
                        "/{0} {1:0.###} Tf\n1 0 0 1 {2:0.###} {3:0.###} Tm\n({4}) Tj\n",
                        run.Bold ? "F2" : "F1",
                        run.FontSize,
                        run.X,
                        baselineY,
                        encodedText);
                }
                sb.Append("ET\nQ\n");
            }

            private string EscapeWinAnsiString(string text)
            {
                byte[] bytes;
                try
                {
                    bytes = Encoding.GetEncoding(1252).GetBytes(text ?? "");
                }
                catch
                {
                    bytes = Encoding.ASCII.GetBytes(text ?? "");
                }

                StringBuilder escaped = new StringBuilder(bytes.Length);
                foreach (byte b in bytes)
                {
                    if (b == (byte)'(' || b == (byte)')' || b == (byte)'\\')
                    {
                        escaped.Append('\\');
                        escaped.Append((char)b);
                    }
                    else if (b >= 32 && b <= 126)
                    {
                        escaped.Append((char)b);
                    }
                    else
                    {
                        escaped.Append('\\');
                        escaped.Append(Convert.ToString(b, 8).PadLeft(3, '0'));
                    }
                }
                return escaped.ToString();
            }

            private void WriteImage(ImageDrawing drawing)
            {
                if (drawing.ImageSource == null || drawing.Rect.IsEmpty)
                    return;

                PdfImageInfo image = EncodeImageSource(drawing.ImageSource);
                if (image == null)
                    return;

                string name = "Im" + (pageObject.Images.Count + 1).ToString(RenderUtils.Num);
                pageObject.Images.Add(new PdfImageXObject
                {
                    ObjectId = allocateObjectId(),
                    Name = name,
                    Info = image
                });

                Rect r = drawing.Rect;
                sb.AppendFormat(RenderUtils.Num,
                    "q {0:0.###} 0 0 -{1:0.###} {2:0.###} {3:0.###} cm /{4} Do Q\n",
                    r.Width,
                    r.Height,
                    r.X,
                    r.Y + r.Height,
                    name);
            }

            private bool TryWriteFillBrush(Brush brush)
            {
                SolidColorBrush solid = brush as SolidColorBrush;
                if (solid == null || solid.Color.A == 0)
                    return false;

                WriteFillColor(solid.Color);
                return true;
            }

            private bool TryWritePen(Pen pen)
            {
                if (pen == null || pen.Thickness <= 0)
                    return false;

                SolidColorBrush solid = pen.Brush as SolidColorBrush;
                if (solid == null || solid.Color.A == 0)
                    return false;

                WriteStrokeColor(solid.Color);
                sb.AppendFormat(RenderUtils.Num, "{0:0.###} w\n", pen.Thickness);
                sb.Append("1 J 1 j\n");
                if (pen.DashStyle != null && pen.DashStyle.Dashes != null && pen.DashStyle.Dashes.Count > 0)
                {
                    string[] dashes = pen.DashStyle.Dashes.Select(d => (d * pen.Thickness).ToString("0.###", RenderUtils.Num)).ToArray();
                    sb.AppendFormat(RenderUtils.Num, "[{0}] {1:0.###} d\n", string.Join(" ", dashes), pen.DashStyle.Offset * pen.Thickness);
                }
                else
                {
                    sb.Append("[] 0 d\n");
                }

                return true;
            }

            private void WriteFillColor(Color color)
            {
                sb.AppendFormat(RenderUtils.Num, "{0:0.######} {1:0.######} {2:0.######} rg\n", color.R / 255.0, color.G / 255.0, color.B / 255.0);
            }

            private void WriteStrokeColor(Color color)
            {
                sb.AppendFormat(RenderUtils.Num, "{0:0.######} {1:0.######} {2:0.######} RG\n", color.R / 255.0, color.G / 255.0, color.B / 255.0);
            }

            private void WriteMatrix(Matrix m)
            {
                sb.AppendFormat(RenderUtils.Num, "{0:0.######} {1:0.######} {2:0.######} {3:0.######} {4:0.###} {5:0.###} cm\n",
                    m.M11,
                    m.M12,
                    m.M21,
                    m.M22,
                    m.OffsetX,
                    m.OffsetY);
            }

            private void WriteGeometryPath(Geometry geometry)
            {
                if (geometry == null)
                    return;

                PathGeometry path = geometry.GetFlattenedPathGeometry(0.10, ToleranceType.Absolute);
                foreach (PathFigure figure in path.Figures)
                {
                    sb.AppendFormat(RenderUtils.Num, "{0:0.###} {1:0.###} m\n", figure.StartPoint.X, figure.StartPoint.Y);
                    foreach (PathSegment segment in figure.Segments)
                    {
                        LineSegment line = segment as LineSegment;
                        if (line != null)
                        {
                            sb.AppendFormat(RenderUtils.Num, "{0:0.###} {1:0.###} l\n", line.Point.X, line.Point.Y);
                            continue;
                        }

                        PolyLineSegment polyLine = segment as PolyLineSegment;
                        if (polyLine != null)
                        {
                            foreach (Point point in polyLine.Points)
                                sb.AppendFormat(RenderUtils.Num, "{0:0.###} {1:0.###} l\n", point.X, point.Y);
                            continue;
                        }

                        BezierSegment bezier = segment as BezierSegment;
                        if (bezier != null)
                        {
                            sb.AppendFormat(RenderUtils.Num, "{0:0.###} {1:0.###} {2:0.###} {3:0.###} {4:0.###} {5:0.###} c\n",
                                bezier.Point1.X, bezier.Point1.Y,
                                bezier.Point2.X, bezier.Point2.Y,
                                bezier.Point3.X, bezier.Point3.Y);
                            continue;
                        }

                        PolyBezierSegment polyBezier = segment as PolyBezierSegment;
                        if (polyBezier != null)
                        {
                            for (int i = 0; i + 2 < polyBezier.Points.Count; i += 3)
                            {
                                Point p1 = polyBezier.Points[i];
                                Point p2 = polyBezier.Points[i + 1];
                                Point p3 = polyBezier.Points[i + 2];
                                sb.AppendFormat(RenderUtils.Num, "{0:0.###} {1:0.###} {2:0.###} {3:0.###} {4:0.###} {5:0.###} c\n",
                                    p1.X, p1.Y, p2.X, p2.Y, p3.X, p3.Y);
                            }
                            continue;
                        }

                        QuadraticBezierSegment quadratic = segment as QuadraticBezierSegment;
                        if (quadratic != null)
                        {
                            Point current = GetLastPoint(figure.StartPoint, figure.Segments, segment);
                            Point c1 = new Point(current.X + (2.0 / 3.0) * (quadratic.Point1.X - current.X), current.Y + (2.0 / 3.0) * (quadratic.Point1.Y - current.Y));
                            Point c2 = new Point(quadratic.Point2.X + (2.0 / 3.0) * (quadratic.Point1.X - quadratic.Point2.X), quadratic.Point2.Y + (2.0 / 3.0) * (quadratic.Point1.Y - quadratic.Point2.Y));
                            sb.AppendFormat(RenderUtils.Num, "{0:0.###} {1:0.###} {2:0.###} {3:0.###} {4:0.###} {5:0.###} c\n",
                                c1.X, c1.Y, c2.X, c2.Y, quadratic.Point2.X, quadratic.Point2.Y);
                        }
                    }
                    if (figure.IsClosed)
                        sb.Append("h\n");
                }
            }

            private FillRule GetFillRule(Geometry geometry)
            {
                PathGeometry path = geometry as PathGeometry;
                if (path != null)
                    return path.FillRule;
                GeometryGroup group = geometry as GeometryGroup;
                if (group != null)
                    return group.FillRule;
                return FillRule.EvenOdd;
            }

            private Point GetLastPoint(Point start, PathSegmentCollection segments, PathSegment beforeSegment)
            {
                Point current = start;
                foreach (PathSegment segment in segments)
                {
                    if (ReferenceEquals(segment, beforeSegment))
                        return current;

                    LineSegment line = segment as LineSegment;
                    if (line != null)
                    {
                        current = line.Point;
                        continue;
                    }

                    PolyLineSegment polyLine = segment as PolyLineSegment;
                    if (polyLine != null && polyLine.Points.Count > 0)
                    {
                        current = polyLine.Points[polyLine.Points.Count - 1];
                        continue;
                    }

                    BezierSegment bezier = segment as BezierSegment;
                    if (bezier != null)
                    {
                        current = bezier.Point3;
                        continue;
                    }

                    PolyBezierSegment polyBezier = segment as PolyBezierSegment;
                    if (polyBezier != null && polyBezier.Points.Count > 0)
                        current = polyBezier.Points[polyBezier.Points.Count - 1];
                }
                return current;
            }
        }
    }
}
