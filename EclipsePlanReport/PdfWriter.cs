using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EclipsePlanReport
{
    /// <summary>
    /// Minimaler PDF-Writer ohne externe Bibliothek: jede PNG-Seite wird als JPEG
    /// auf einer A4-Querseite eingebettet.
    /// </summary>
    internal static class PdfWriter
    {
        public static void CreatePdfFromImages(List<string> imagePaths, string pdfPath, Action<string> log)
        {
            List<PdfImageInfo> images = imagePaths
                .Where(File.Exists)
                .Select(path => EncodeImageForPdf(path, log))
                .Where(x => x != null)
                .ToList();

            if (images.Count == 0)
                return;

            const double pageWidthPt = 841.89;  // A4 quer
            const double pageHeightPt = 595.28;

            int objectCount = 2 + images.Count * 3;
            long[] offsets = new long[objectCount + 1];

            using (FileStream stream = new FileStream(pdfPath, FileMode.Create, FileAccess.Write))
            {
                WriteAscii(stream, "%PDF-1.4\n%");
                stream.Write(new byte[] { 0xE2, 0xE3, 0xCF, 0xD3 }, 0, 4); // Binaer-Marker
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
                        pageWidthPt,
                        pageHeightPt,
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

                    double imageAspect = image.PixelWidth / (double)image.PixelHeight;
                    double pageAspect = pageWidthPt / pageHeightPt;
                    double drawWidth = pageWidthPt;
                    double drawHeight = pageHeightPt;
                    double x = 0;
                    double y = 0;

                    if (imageAspect > pageAspect)
                    {
                        drawHeight = pageWidthPt / imageAspect;
                        y = (pageHeightPt - drawHeight) / 2.0;
                    }
                    else if (imageAspect < pageAspect)
                    {
                        drawWidth = pageHeightPt * imageAspect;
                        x = (pageWidthPt - drawWidth) / 2.0;
                    }

                    string content = string.Format(RenderUtils.Num,
                        "q {0:0.###} 0 0 {1:0.###} {2:0.###} {3:0.###} cm /Im{4} Do Q\n",
                        drawWidth,
                        drawHeight,
                        x,
                        y,
                        i + 1);
                    byte[] contentBytes = Encoding.ASCII.GetBytes(content);

                    offsets[contentObject] = stream.Position;
                    WriteAscii(stream, string.Format(RenderUtils.Num,
                        "{0} 0 obj\n<< /Length {1} >>\nstream\n",
                        contentObject,
                        contentBytes.Length));
                    stream.Write(contentBytes, 0, contentBytes.Length);
                    WriteAscii(stream, "endstream\nendobj\n");
                }

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
        }

        private static PdfImageInfo EncodeImageForPdf(string imagePath, Action<string> log)
        {
            try
            {
                BitmapDecoder decoder = BitmapDecoder.Create(
                    new Uri(imagePath, UriKind.Absolute),
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);

                BitmapSource source = decoder.Frames[0];
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
            catch (Exception e)
            {
                if (log != null)
                    log(string.Format("PDF-Seite konnte nicht gelesen werden ({0}): {1}", imagePath, e.Message));
                return null;
            }
        }

        private static void WriteAscii(Stream stream, string text)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
        }
    }
}
