using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VMS.TPS.Common.Model.API;
using VMS.TPS.Common.Model.Types;
using System.Collections.Generic;

namespace EclipsePlanReport
{
    /// <summary>
    /// Zentrales Farbschema fuer alle Berichtsseiten - umschaltbar zwischen Hell
    /// (weisser Druck) und Dunkel. CT-/DRR-Bildkacheln bleiben in beiden Modi
    /// schwarz, da Graustufenbilder dort am besten lesbar sind.
    /// </summary>
    internal static class Theme
    {
        /// <summary>true = Dark Mode, false = heller Druck (Standard).</summary>
        public static bool Dark = false;

        private static SolidColorBrush Frozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        public static Brush PageBackground { get { return Dark ? Frozen(18, 18, 18) : Brushes.White; } }
        public static Brush PrimaryText { get { return Dark ? Brushes.White : Brushes.Black; } }
        public static Brush MutedText { get { return Dark ? Frozen(190, 190, 190) : Frozen(80, 80, 80); } }
        public static Brush FaintText { get { return Dark ? Frozen(150, 150, 150) : Frozen(110, 110, 110); } }

        public static Brush SeparatorLine { get { return Dark ? Frozen(95, 95, 95) : Frozen(150, 150, 150); } }
        public static Brush GridLine { get { return Dark ? Frozen(70, 70, 70) : Frozen(185, 185, 185); } }
        public static Brush TableRowLine { get { return Dark ? Frozen(55, 55, 55) : Frozen(210, 210, 210); } }

        public static Brush SectionFill { get { return Dark ? Frozen(45, 45, 45) : Frozen(235, 235, 235); } }
        public static Brush PanelBackground { get { return Dark ? Frozen(34, 34, 34) : Frozen(240, 240, 240); } }
        public static Brush PanelHeaderFill { get { return Dark ? Frozen(40, 64, 96) : Frozen(51, 84, 125); } }
        public static Brush TableValueText { get { return Dark ? Frozen(225, 225, 225) : Brushes.Black; } }

        // Bild-Viewports (CT/DRR) - immer schwarz, unabhaengig vom Theme.
        public static Brush ImageBackground { get { return Brushes.Black; } }
        public static Brush ViewportTitleFill { get { return Dark ? Frozen(48, 48, 48) : Frozen(70, 90, 120); } }
    }

    /// <summary>Gemeinsame Konstanten, Text-, Geometrie- und Bitmap-Helfer fuer alle Berichtsseiten.</summary>
    internal static class RenderUtils
    {
        // Logische A4-Querseite. Die PNG-Ausgabe wird darunter hochskaliert,
        // damit Text und Linien im fertigen PDF schaerfer sind.
        public const int PageWidthPx = 1754;
        public const int PageHeightPx = 1240;
        public const double OutputScale = 2.0;
        public const double OutputDpi = 192.0;
        private static readonly bool WriteIntermediatePngFiles = false;

        public static readonly CultureInfo Num = CultureInfo.InvariantCulture;

        /// <summary>Globale Schriftskalierung (GUI-Einstellung).</summary>
        public static double FontScale = 1.0;

        [ThreadStatic]
        private static List<VectorPdfTextRun> currentPdfTextCapture;

        public static double ScaleFont(double fontSize)
        {
            return fontSize * FontScale;
        }

        /// <summary>Gedaempfte Skalierung fuer Beschriftungen direkt im CT-Bild.</summary>
        public static double ScaleSliceFont(double fontSize)
        {
            double dampedScale = 1.0 + (FontScale - 1.0) * 0.45;
            return fontSize * dampedScale;
        }

        public static void DrawText(DrawingContext dc, string text, double x, double y, double fontSize, Brush brush, Typeface typeface, CultureInfo culture)
        {
            double effectiveFontSize = ScaleFont(fontSize);
            dc.DrawText(CreateFormattedText(text, effectiveFontSize, brush, typeface, culture), new Point(x, y));
            CapturePdfText(text, x, y, effectiveFontSize, typeface);
        }

        /// <summary>Zeichnet Text und verkleinert die Schrift, bis er in maxWidth passt (lesbar, nichts abgeschnitten).</summary>
        public static void DrawTextFit(DrawingContext dc, string text, double x, double y, double maxWidth, double fontSize, Brush brush, Typeface typeface, CultureInfo culture)
        {
            double effectiveFontSize = ScaleFont(fontSize);
            double minFontSize = Math.Max(8.0, ScaleFont(9.0));
            FormattedText formattedText = CreateFormattedText(text, effectiveFontSize, brush, typeface, culture);

            while (formattedText.Width > maxWidth && effectiveFontSize > minFontSize)
            {
                effectiveFontSize -= 0.5;
                formattedText = CreateFormattedText(text, effectiveFontSize, brush, typeface, culture);
            }

            // Passt der Text selbst bei minimaler Schrift nicht in die Spalte,
            // wird er hart auf die Breite begrenzt und mit "..." gekuerzt.
            // So koennen sich keine Zeichen mehr ueber Spaltengrenzen schieben.
            if (formattedText.Width > maxWidth)
            {
                formattedText.MaxTextWidth = Math.Max(1.0, maxWidth);
                formattedText.MaxLineCount = 1;
                formattedText.Trimming = TextTrimming.CharacterEllipsis;
            }

            dc.DrawText(formattedText, new Point(x, y));
            CapturePdfText(text, x, y, effectiveFontSize, typeface);
        }

        public static FormattedText CreateFormattedText(string text, double fontSize, Brush brush, Typeface typeface, CultureInfo culture)
        {
            return new FormattedText(
                text ?? "",
                culture,
                FlowDirection.LeftToRight,
                typeface,
                fontSize,
                brush,
                1.0);
        }

        public static double GetColumnWidth(double[] columns, int index)
        {
            if (index < 0 || index >= columns.Length - 1)
                return 80;
            return columns[index + 1] - columns[index];
        }

        public static IDisposable BeginPdfTextCapture(List<VectorPdfTextRun> textRuns)
        {
            return new PdfTextCaptureScope(textRuns);
        }

        private static void CapturePdfText(string text, double x, double y, double fontSize, Typeface typeface)
        {
            if (currentPdfTextCapture == null || string.IsNullOrEmpty(text))
                return;

            currentPdfTextCapture.Add(new VectorPdfTextRun
            {
                Text = text.Replace('\r', ' ').Replace('\n', ' '),
                X = x,
                Y = y,
                FontSize = fontSize,
                Bold = typeface != null && typeface.Weight.ToOpenTypeWeight() >= FontWeights.SemiBold.ToOpenTypeWeight()
            });
        }

        private class PdfTextCaptureScope : IDisposable
        {
            private readonly List<VectorPdfTextRun> previousCapture;

            public PdfTextCaptureScope(List<VectorPdfTextRun> textRuns)
            {
                previousCapture = currentPdfTextCapture;
                currentPdfTextCapture = textRuns;
            }

            public void Dispose()
            {
                currentPdfTextCapture = previousCapture;
            }
        }

        public static void SaveVisualAsPng(DrawingVisual visual, int width, int height, string filename)
        {
            SaveVisualAsPng(visual, width, height, filename, null);
        }

        public static void SaveVisualAsPng(DrawingVisual visual, int width, int height, string filename, List<VectorPdfTextRun> textRuns)
        {
            if (visual.Drawing != null)
                VectorPdfPageStore.Register(filename, visual.Drawing.Clone(), width, height, textRuns);

            if (!WriteIntermediatePngFiles)
                return;

            int outputWidth = (int)Math.Round(width * OutputScale);
            int outputHeight = (int)Math.Round(height * OutputScale);

            RenderTargetBitmap bitmap = new RenderTargetBitmap(outputWidth, outputHeight, OutputDpi, OutputDpi, PixelFormats.Pbgra32);
            bitmap.Render(visual);

            using (var fileStream = new FileStream(filename, FileMode.Create))
            {
                PngBitmapEncoder encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                encoder.Save(fileStream);
            }
        }

        public static string MakeFilenameValid(string s)
        {
            char[] invalidChars = Path.GetInvalidFileNameChars();
            foreach (char ch in invalidChars)
                s = s.Replace(ch, '_');
            return s;
        }

        // ---------- Pattern-Matching (PTV*, *REKTUM*, ...) ----------

        public static bool MatchesAnyPattern(string value, IEnumerable<string> patterns)
        {
            return patterns.Any(pattern => MatchesPattern(value, pattern));
        }

        public static bool MatchesPattern(string value, string pattern)
        {
            if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(value))
                return false;

            string regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
            return Regex.IsMatch(value, regexPattern, RegexOptions.IgnoreCase);
        }

        public static int GetPatternOrder(string structureId, IList<string> patterns)
        {
            for (int i = 0; i < patterns.Count; i++)
            {
                if (MatchesPattern(structureId, patterns[i]))
                    return i;
            }
            return int.MaxValue;
        }

        public static string NormalizeForMatch(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";
            return new string(value
                .ToLowerInvariant()
                .Where(char.IsLetterOrDigit)
                .ToArray());
        }

        // ---------- Patientenkopfzeile ----------

        public static string BuildPatientHeader(Patient patient)
        {
            string birthDate = GetPatientBirthDate(patient);
            string name = string.Format("{0}, {1}", patient.LastName, patient.FirstName).Trim(' ', ',');
            if (!string.IsNullOrEmpty(birthDate))
                return string.Format("Patient: {0}   {1}   Geb.: {2}", patient.Id, name, birthDate);
            return string.Format("Patient: {0}   {1}", patient.Id, name);
        }

        public static string GetPatientBirthDate(Patient patient)
        {
            try
            {
                object value = ReflectionUtils.GetPropertyValue(patient, "DateOfBirth");
                if (value == null)
                    return "";
                if (value is DateTime)
                    return ((DateTime)value).ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
                DateTime parsed;
                if (DateTime.TryParse(value.ToString(), out parsed))
                    return parsed.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
            }
            catch
            {
            }
            return "";
        }

        // ---------- Geometrie ----------

        /// <summary>Patientenpunkt (mm) -> Pixelkoordinate in der transversalen Bildebene.</summary>
        public static Point ProjectToImagePixel(VVector point, Image image)
        {
            double dx = point.x - image.Origin.x;
            double dy = point.y - image.Origin.y;
            double dz = point.z - image.Origin.z;

            double iMm = dx * image.XDirection.x + dy * image.XDirection.y + dz * image.XDirection.z;
            double jMm = dx * image.YDirection.x + dy * image.YDirection.y + dz * image.YDirection.z;

            return new Point(iMm / image.XRes, jMm / image.YRes);
        }

        public static double Clamp(double value, double min, double max)
        {
            if (max < min)
                return min;
            if (value < min)
                return min;
            if (value > max)
                return max;
            return value;
        }

        /// <summary>Anatomisches Label (L/R/A/P/H/F) fuer eine Richtung im Patientensystem.</summary>
        public static string GetLabelForDirection(VVector v)
        {
            double ax = Math.Abs(v.x);
            double ay = Math.Abs(v.y);
            double az = Math.Abs(v.z);

            if (ax >= ay && ax >= az)
                return v.x > 0 ? "L" : "R";
            if (ay >= ax && ay >= az)
                return v.y > 0 ? "P" : "A";
            return v.z > 0 ? "H" : "F";
        }

        public static VVector Negate(VVector v)
        {
            return new VVector(-v.x, -v.y, -v.z);
        }

        /// <summary>
        /// Spiegelung, damit die Anzeige der Eclipse-/Radiologie-Konvention entspricht
        /// (unabhaengig von HFS/FFS/HFP usw.).
        /// horizontalDir: Patientenrichtung, die im Pixelarray nach rechts zeigt.
        /// verticalDir: Patientenrichtung, die im Pixelarray nach unten zeigt.
        /// </summary>
        public static bool NeedsFlip(VVector axisDir, string desiredLabel)
        {
            return GetLabelForDirection(axisDir) != desiredLabel;
        }

        internal class DisplayTransform
        {
            public int RightAxis { get; set; }  // 0 = U, 1 = V
            public int RightSign { get; set; }
            public int DownAxis { get; set; }   // 0 = U, 1 = V
            public int DownSign { get; set; }
            public string TopLabel { get; set; }
            public string BottomLabel { get; set; }
            public string LeftLabel { get; set; }
            public string RightLabel { get; set; }
            public string DesiredRightLabel { get; set; }
            public string DesiredDownLabel { get; set; }
            public bool IsFallback { get; set; }

            public bool SwapsAxes
            {
                get { return RightAxis == 1; }
            }
        }

        public static DisplayTransform CreateDisplayTransform(VVector uDir, VVector vDir, string desiredRightLabel, string desiredDownLabel)
        {
            var candidates = new[]
            {
                new DisplayTransform { RightAxis = 0, RightSign =  1, DownAxis = 1, DownSign =  1 },
                new DisplayTransform { RightAxis = 0, RightSign = -1, DownAxis = 1, DownSign =  1 },
                new DisplayTransform { RightAxis = 0, RightSign =  1, DownAxis = 1, DownSign = -1 },
                new DisplayTransform { RightAxis = 0, RightSign = -1, DownAxis = 1, DownSign = -1 },
                new DisplayTransform { RightAxis = 1, RightSign =  1, DownAxis = 0, DownSign =  1 },
                new DisplayTransform { RightAxis = 1, RightSign = -1, DownAxis = 0, DownSign =  1 },
                new DisplayTransform { RightAxis = 1, RightSign =  1, DownAxis = 0, DownSign = -1 },
                new DisplayTransform { RightAxis = 1, RightSign = -1, DownAxis = 0, DownSign = -1 }
            };

            foreach (DisplayTransform candidate in candidates)
            {
                FillDisplayLabels(candidate, uDir, vDir, desiredRightLabel, desiredDownLabel);
                if (candidate.RightLabel == desiredRightLabel && candidate.BottomLabel == desiredDownLabel)
                    return candidate;
            }

            DisplayTransform fallback = candidates[0];
            FillDisplayLabels(fallback, uDir, vDir, desiredRightLabel, desiredDownLabel);
            fallback.IsFallback = true;
            return fallback;
        }

        public static DisplayTransform CreateIdentityDisplayTransform(VVector uDir, VVector vDir)
        {
            DisplayTransform transform = new DisplayTransform { RightAxis = 0, RightSign = 1, DownAxis = 1, DownSign = 1 };
            FillDisplayLabels(transform, uDir, vDir, GetLabelForDirection(uDir), GetLabelForDirection(vDir));
            return transform;
        }

        public static string GetPatientPositionCode(string rawOrientation)
        {
            string text = NormalizeForMatch(rawOrientation);
            if (text.Contains("hfs") || text.Contains("headfirstsupine")) return "HFS";
            if (text.Contains("ffs") || text.Contains("feetfirstsupine")) return "FFS";
            if (text.Contains("hfp") || text.Contains("headfirstprone")) return "HFP";
            if (text.Contains("ffp") || text.Contains("feetfirstprone")) return "FFP";
            if (text.Contains("hfdl") || text.Contains("headfirstdecubitusleft")) return "HFDL";
            if (text.Contains("hfdr") || text.Contains("headfirstdecubitusright")) return "HFDR";
            if (text.Contains("ffdl") || text.Contains("feetfirstdecubitusleft")) return "FFDL";
            if (text.Contains("ffdr") || text.Contains("feetfirstdecubitusright")) return "FFDR";
            return "";
        }

        public static string GetTableSideLabel(string patientPositionCode)
        {
            switch (patientPositionCode)
            {
                case "HFP":
                case "FFP":
                    return "A";
                case "HFDL":
                case "FFDL":
                    return "L";
                case "HFDR":
                case "FFDR":
                    return "R";
                case "HFS":
                case "FFS":
                default:
                    return "P";
            }
        }

        public static string GetTransversalRightLabelForTableDown(string tableSideLabel)
        {
            switch (tableSideLabel)
            {
                case "A": return "R";
                case "L": return "A";
                case "R": return "P";
                case "P":
                default:
                    return "L";
            }
        }

        public static string GetTransversalRightLabelForTableDown(string tableSideLabel, string patientPositionCode)
        {
            if (IsFeetFirstSupinePosition(patientPositionCode))
                return "R";

            if (IsFeetFirstDecubitusPosition(patientPositionCode))
            {
                switch (tableSideLabel)
                {
                    case "L": return "P";
                    case "R": return "A";
                }
            }

            return GetTransversalRightLabelForTableDown(tableSideLabel);
        }

        public static bool TryGetPatientDirectionVector(string label, out VVector vector)
        {
            switch (label)
            {
                case "L":
                    vector = new VVector(1, 0, 0);
                    return true;
                case "R":
                    vector = new VVector(-1, 0, 0);
                    return true;
                case "P":
                    vector = new VVector(0, 1, 0);
                    return true;
                case "A":
                    vector = new VVector(0, -1, 0);
                    return true;
                case "H":
                    vector = new VVector(0, 0, 1);
                    return true;
                case "F":
                    vector = new VVector(0, 0, -1);
                    return true;
                default:
                    vector = new VVector(0, 0, 0);
                    return false;
            }
        }

        public static VVector GetPatientDirectionVector(string label)
        {
            VVector vector;
            return TryGetPatientDirectionVector(label, out vector) ? vector : new VVector(0, 1, 0);
        }

        public static bool IsPronePosition(string patientPositionCode)
        {
            return patientPositionCode == "HFP" || patientPositionCode == "FFP";
        }

        public static bool IsFeetFirstDecubitusPosition(string patientPositionCode)
        {
            return patientPositionCode == "FFDL" || patientPositionCode == "FFDR";
        }

        public static bool IsFeetFirstSupinePosition(string patientPositionCode)
        {
            return patientPositionCode == "FFS";
        }

        public static bool UsesMirroredDisplayHandedness(string patientPositionCode)
        {
            return IsPronePosition(patientPositionCode) || IsFeetFirstDecubitusPosition(patientPositionCode);
        }

        public static VVector ComputeEclipseUserCoordinatesCm(Image image, VVector point, string patientPositionCode)
        {
            VVector userOrigin = GetImageUserOriginOrOrigin(image);
            VVector rel = new VVector(
                point.x - userOrigin.x,
                point.y - userOrigin.y,
                point.z - userOrigin.z);

            VVector xAxis, yAxis, zAxis;
            GetEclipseUserCoordinateAxes(patientPositionCode, out xAxis, out yAxis, out zAxis);

            return new VVector(
                Dot(rel, xAxis) / 10.0,
                Dot(rel, yAxis) / 10.0,
                Dot(rel, zAxis) / 10.0);
        }

        public static double ComputeEclipseSliceZcm(Image image, int sliceZ, string patientPositionCode)
        {
            if (image == null)
                return 0;

            VVector sliceOrigin = new VVector(
                image.Origin.x + sliceZ * image.ZRes * image.ZDirection.x,
                image.Origin.y + sliceZ * image.ZRes * image.ZDirection.y,
                image.Origin.z + sliceZ * image.ZRes * image.ZDirection.z);

            return ComputeEclipseUserCoordinatesCm(image, sliceOrigin, patientPositionCode).z;
        }

        private static VVector GetImageUserOriginOrOrigin(Image image)
        {
            if (image == null)
                return new VVector();

            try
            {
                return image.UserOrigin;
            }
            catch
            {
                return image.Origin;
            }
        }

        private static void GetEclipseUserCoordinateAxes(string patientPositionCode, out VVector xAxis, out VVector yAxis, out VVector zAxis)
        {
            switch (patientPositionCode)
            {
                case "FFDR":
                    xAxis = GetPatientDirectionVector("A");
                    yAxis = GetPatientDirectionVector("R");
                    zAxis = GetPatientDirectionVector("F");
                    return;
                case "FFDL":
                    xAxis = GetPatientDirectionVector("P");
                    yAxis = GetPatientDirectionVector("L");
                    zAxis = GetPatientDirectionVector("F");
                    return;
                default:
                    xAxis = GetPatientDirectionVector("L");
                    yAxis = GetPatientDirectionVector("H");
                    zAxis = GetPatientDirectionVector("A");
                    return;
            }
        }

        public static string GetFrontalRightLabel(string patientPositionCode)
        {
            if (IsFeetFirstSupinePosition(patientPositionCode))
                return "R";

            if (IsFeetFirstDecubitusPosition(patientPositionCode))
                return GetTransversalRightLabelForTableDown(GetTableSideLabel(patientPositionCode), patientPositionCode);

            return UsesMirroredDisplayHandedness(patientPositionCode) ? "R" : "L";
        }

        public static string GetFrontalDownLabel(string patientPositionCode)
        {
            return IsFeetFirstSupinePosition(patientPositionCode) || IsFeetFirstDecubitusPosition(patientPositionCode) ? "H" : "F";
        }

        public static string GetSagittalRightLabel(string patientPositionCode)
        {
            if (patientPositionCode == "HFS" || IsFeetFirstSupinePosition(patientPositionCode))
                return "A";

            return "P";
        }

        public static string GetSagittalDownLabel(string patientPositionCode)
        {
            return IsFeetFirstSupinePosition(patientPositionCode) ? "H" : "F";
        }

        public static Matrix GetNaturalToDisplayMatrix(DisplayTransform transform, double width, double height)
        {
            if (transform.RightAxis == 0 && transform.RightSign == 1 && transform.DownSign == 1)
                return new Matrix(1, 0, 0, 1, 0, 0);
            if (transform.RightAxis == 0 && transform.RightSign == -1 && transform.DownSign == 1)
                return new Matrix(-1, 0, 0, 1, width, 0);
            if (transform.RightAxis == 0 && transform.RightSign == 1 && transform.DownSign == -1)
                return new Matrix(1, 0, 0, -1, 0, height);
            if (transform.RightAxis == 0 && transform.RightSign == -1 && transform.DownSign == -1)
                return new Matrix(-1, 0, 0, -1, width, height);
            if (transform.RightAxis == 1 && transform.RightSign == 1 && transform.DownSign == 1)
                return new Matrix(0, 1, 1, 0, 0, 0);
            if (transform.RightAxis == 1 && transform.RightSign == -1 && transform.DownSign == 1)
                return new Matrix(0, 1, -1, 0, height, 0);
            if (transform.RightAxis == 1 && transform.RightSign == 1 && transform.DownSign == -1)
                return new Matrix(0, -1, 1, 0, 0, width);

            return new Matrix(0, -1, -1, 0, height, width);
        }

        public static BitmapSource ApplyDisplayTransform(BitmapSource source, DisplayTransform transform)
        {
            int outputWidth = transform.SwapsAxes ? source.PixelHeight : source.PixelWidth;
            int outputHeight = transform.SwapsAxes ? source.PixelWidth : source.PixelHeight;

            DrawingVisual visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.PushTransform(new MatrixTransform(GetNaturalToDisplayMatrix(transform, source.PixelWidth, source.PixelHeight)));
                dc.DrawImage(source, new Rect(0, 0, source.PixelWidth, source.PixelHeight));
                dc.Pop();
            }

            RenderTargetBitmap bitmap = new RenderTargetBitmap(outputWidth, outputHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            return bitmap;
        }

        private static void FillDisplayLabels(DisplayTransform transform, VVector uDir, VVector vDir, string desiredRightLabel, string desiredDownLabel)
        {
            VVector rightVector = SignedAxis(transform.RightAxis == 0 ? uDir : vDir, transform.RightSign);
            VVector downVector = SignedAxis(transform.DownAxis == 0 ? uDir : vDir, transform.DownSign);
            transform.RightLabel = GetLabelForDirection(rightVector);
            transform.LeftLabel = GetLabelForDirection(Negate(rightVector));
            transform.BottomLabel = GetLabelForDirection(downVector);
            transform.TopLabel = GetLabelForDirection(Negate(downVector));
            transform.DesiredRightLabel = desiredRightLabel;
            transform.DesiredDownLabel = desiredDownLabel;
        }

        private static VVector SignedAxis(VVector axis, int sign)
        {
            return sign >= 0 ? axis : Negate(axis);
        }

        /// <summary>Fensterung (Center/Width) - Kopf-Serien enger gefenstert.</summary>
        public static void GetWindowLevel(Image image, out double windowCenter, out double windowWidth)
        {
            string seriesComment = "";
            try
            {
                seriesComment = (image.Series != null && image.Series.Comment != null)
                    ? image.Series.Comment.ToLowerInvariant()
                    : "";
            }
            catch
            {
            }

            bool head = seriesComment.Contains("kopf") || seriesComment.Contains("head") || seriesComment.Contains("brain");
            windowCenter = head ? 80 : 0;
            windowWidth = head ? 350 : 900;
        }

        /// <summary>Graustufen-Bitmap aus HU-Werten. huAt(col, row) liefert den HU-Wert.</summary>
        public static BitmapSource MakeGrayBitmap(int width, int height, Func<int, int, double> huAt, double windowCenter, double windowWidth)
        {
            byte[] pixels = new byte[width * height];
            double windowMin = windowCenter - windowWidth / 2.0;

            for (int row = 0; row < height; row++)
            {
                for (int col = 0; col < width; col++)
                {
                    double intensity = (huAt(col, row) - windowMin) / windowWidth * 255.0;
                    pixels[row * width + col] = (byte)Math.Max(0, Math.Min(255, intensity));
                }
            }

            BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width);
            RenderOptions.SetBitmapScalingMode(bitmap, BitmapScalingMode.HighQuality);
            return bitmap;
        }

        // ---------- Marching Squares (Isolinien auf beliebigen 2D-Matrizen) ----------

        /// <summary>
        /// Zeichnet Isolinien fuer einen Schwellwert auf einer 2D-Matrix.
        /// map(u, v) bildet (fraktionale) Matrixindizes auf Zeichenkoordinaten ab.
        /// </summary>
        public static void DrawIsoLines(DrawingContext dc, double[,] data, double level, Pen pen, Func<double, double, Point> map)
        {
            int nu = data.GetLength(0);
            int nv = data.GetLength(1);

            for (int v = 0; v < nv - 1; v++)
            {
                for (int u = 0; u < nu - 1; u++)
                {
                    double d00 = data[u, v];
                    double d10 = data[u + 1, v];
                    double d11 = data[u + 1, v + 1];
                    double d01 = data[u, v + 1];

                    int code = 0;
                    if (d00 >= level) code |= 1;
                    if (d10 >= level) code |= 2;
                    if (d11 >= level) code |= 4;
                    if (d01 >= level) code |= 8;

                    if (code == 0 || code == 15)
                        continue;

                    var pts = new List<Point>(4);

                    if ((d01 >= level) != (d11 >= level))
                    {
                        double t = (level - d01) / (d11 - d01);
                        pts.Add(map(u + t, v + 1));
                    }
                    if ((d10 >= level) != (d11 >= level))
                    {
                        double t = (level - d10) / (d11 - d10);
                        pts.Add(map(u + 1, v + t));
                    }
                    if ((d00 >= level) != (d10 >= level))
                    {
                        double t = (level - d00) / (d10 - d00);
                        pts.Add(map(u + t, v));
                    }
                    if ((d00 >= level) != (d01 >= level))
                    {
                        double t = (level - d00) / (d01 - d00);
                        pts.Add(map(u, v + t));
                    }

                    if (pts.Count == 2)
                    {
                        dc.DrawLine(pen, pts[0], pts[1]);
                    }
                    else if (pts.Count == 4)
                    {
                        dc.DrawLine(pen, pts[0], pts[1]);
                        dc.DrawLine(pen, pts[2], pts[3]);
                    }
                }
            }
        }

        /// <summary>Isodosen-Legende passend zum aktiven Theme (dunkel/hell).</summary>
        public static void DrawIsodoseLegendThemed(DrawingContext dc, ReportTemplate template, Func<IsodoseLevel, double> resolveGy, double x, double y, double fontScale = 1.0)
        {
            if (Theme.Dark)
                DrawIsodoseLegend(dc, template, resolveGy, x, y, fontScale);
            else
                DrawIsodoseLegendLight(dc, template, resolveGy, x, y, fontScale);
        }

        /// <summary>Zeichnet eine Isodosen-Legende (Templatewerte, Punkt als Dezimaltrennzeichen).</summary>
        public static void DrawIsodoseLegend(DrawingContext dc, ReportTemplate template, Func<IsodoseLevel, double> resolveGy, double x, double y, double fontScale = 1.0)
        {
            var typeface = new Typeface("Segoe UI");

            var headerText = CreateFormattedText("Isodosen [Gy]", Num, typeface, ScaleSliceFont(16) * fontScale, Brushes.White);
            dc.DrawText(headerText, new Point(x, y));
            y += headerText.Height + 6;

            foreach (var iso in template.Isodoses)
            {
                double doseGy = resolveGy(iso);
                string label;
                if (iso.RelativeDosePercent > 0)
                {
                    label = doseGy > 0
                        ? string.Format(Num, "{0:F0}% = {1:F1} Gy", iso.RelativeDosePercent, doseGy)
                        : string.Format(Num, "{0:F0}%", iso.RelativeDosePercent);
                }
                else
                {
                    label = string.Format(Num, "{0:F1} Gy", doseGy);
                }

                double fontSize = ScaleSliceFont(14) * fontScale;

                // dunkle Farben mit weissem Saum lesbar halten
                bool needsOutline = iso.Color.R + iso.Color.G + iso.Color.B < 250;
                if (needsOutline)
                {
                    var offsets = new[] { new Point(-1, 0), new Point(1, 0), new Point(0, -1), new Point(0, 1) };
                    foreach (var o in offsets)
                    {
                        var outline = CreateFormattedText(label, Num, typeface, fontSize, Brushes.White);
                        dc.DrawText(outline, new Point(x + o.X, y + o.Y));
                    }
                }

                var mainText = CreateFormattedText(label, Num, typeface, fontSize, new SolidColorBrush(iso.Color));
                dc.DrawText(mainText, new Point(x, y));
                y += mainText.Height + 4;
            }
        }

        private static FormattedText CreateFormattedText(string text, CultureInfo culture, Typeface typeface, double fontSize, Brush brush)
        {
            return new FormattedText(text, culture, FlowDirection.LeftToRight, typeface, fontSize, brush, 1.0);
        }

        /// <summary>
        /// Isodosen-Legende fuer weissen Hintergrund (Eclipse-Schichtdruck-Stil):
        /// roter Titel "Isodosenlinien [Gy]", Eintraege mit Haekchen, helle Farben mit dunklem Saum.
        /// </summary>
        public static double DrawIsodoseLegendLight(DrawingContext dc, ReportTemplate template, Func<IsodoseLevel, double> resolveGy, double x, double y, double fontScale = 1.0)
        {
            var typeface = new Typeface("Segoe UI");
            var boldTypeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

            var headerText = CreateFormattedText("Isodosenlinien [Gy]", Num, boldTypeface, ScaleSliceFont(15) * fontScale, Brushes.Red);
            dc.DrawText(headerText, new Point(x, y));
            y += headerText.Height + 5;

            foreach (var iso in template.Isodoses)
            {
                double doseGy = resolveGy(iso);
                string label;
                if (iso.RelativeDosePercent > 0)
                {
                    label = doseGy > 0
                        ? string.Format(Num, "{0:F1}% ({1:F3})", iso.RelativeDosePercent, doseGy)
                        : string.Format(Num, "{0:F1}%", iso.RelativeDosePercent);
                }
                else
                {
                    label = doseGy.ToString("F3", Num);
                }

                double fontSize = ScaleSliceFont(13) * fontScale;
                string line = "✓ " + label;

                // helle Farben (Gelb, Cyan, Rosa) auf Weiss mit dunklem Saum lesbar halten
                double luminance = (0.299 * iso.Color.R + 0.587 * iso.Color.G + 0.114 * iso.Color.B) / 255.0;
                if (luminance > 0.62)
                {
                    var offsets = new[] { new Point(-1, 0), new Point(1, 0), new Point(0, -1), new Point(0, 1) };
                    foreach (var o in offsets)
                    {
                        var outline = CreateFormattedText(line, Num, typeface, fontSize, Brushes.DimGray);
                        dc.DrawText(outline, new Point(x + o.X, y + o.Y));
                    }
                }

                var mainText = CreateFormattedText(line, Num, typeface, fontSize, new SolidColorBrush(iso.Color));
                dc.DrawText(mainText, new Point(x, y));
                y += mainText.Height + 3;
            }

            return y;
        }

        internal enum ManikinView
        {
            ThreeD,
            Frontal,
            Sagittal,
            Transversal
        }

        /// <summary>Kleine gruene Patientenfigur (Eclipse-Orientierungsmaennchen).</summary>
        public static void DrawManikin(DrawingContext dc, double x, double y, double height)
        {
            DrawManikin(dc, x, y, height, ManikinView.ThreeD);
        }

        public static void DrawManikin(DrawingContext dc, double x, double y, double height, ManikinView view)
        {
            string rightLabel;
            string downLabel;
            if (TryGetDefaultManikinDisplay(view, out rightLabel, out downLabel))
            {
                DrawManikin(dc, x, y, height, view, rightLabel, downLabel);
                return;
            }

            if (GlbManikinRenderer.TryDraw(dc, x, y, height, view))
                return;

            DrawFallbackManikin(dc, x, y, height, view);
        }

        public static void DrawManikin(DrawingContext dc, double x, double y, double height, ManikinView view, DisplayTransform displayTransform)
        {
            if (displayTransform == null)
            {
                DrawManikin(dc, x, y, height, view);
                return;
            }

            DrawManikin(dc, x, y, height, view, displayTransform.RightLabel, displayTransform.BottomLabel);
        }

        public static void DrawManikin(DrawingContext dc, double x, double y, double height, ManikinView view, string rightLabel, string downLabel)
        {
            if (GlbManikinRenderer.TryDraw(dc, x, y, height, rightLabel, downLabel))
                return;

            DrawFallbackManikinAligned(dc, x, y, height, view, rightLabel, downLabel);
        }

        private static bool TryGetDefaultManikinDisplay(ManikinView view, out string rightLabel, out string downLabel)
        {
            switch (view)
            {
                case ManikinView.Frontal:
                    rightLabel = "L";
                    downLabel = "F";
                    return true;
                case ManikinView.Sagittal:
                    rightLabel = "P";
                    downLabel = "F";
                    return true;
                case ManikinView.Transversal:
                    rightLabel = "L";
                    downLabel = "P";
                    return true;
                default:
                    rightLabel = "";
                    downLabel = "";
                    return false;
            }
        }

        private static void DrawFallbackManikinAligned(DrawingContext dc, double x, double y, double height, ManikinView view, string rightLabel, string downLabel)
        {
            string baseRightLabel;
            string baseDownLabel;
            VVector baseRight, baseDown, targetRight, targetDown;
            if (!TryGetDefaultManikinDisplay(view, out baseRightLabel, out baseDownLabel) ||
                !TryGetPatientDirectionVector(baseRightLabel, out baseRight) ||
                !TryGetPatientDirectionVector(baseDownLabel, out baseDown) ||
                !TryGetPatientDirectionVector(rightLabel, out targetRight) ||
                !TryGetPatientDirectionVector(downLabel, out targetDown))
            {
                DrawFallbackManikin(dc, x, y, height, view);
                return;
            }

            double m11 = Dot(baseRight, targetRight);
            double m12 = Dot(baseRight, targetDown);
            double m21 = Dot(baseDown, targetRight);
            double m22 = Dot(baseDown, targetDown);
            if (Math.Abs(m11) + Math.Abs(m12) + Math.Abs(m21) + Math.Abs(m22) < 0.5)
            {
                DrawFallbackManikin(dc, x, y, height, view);
                return;
            }

            double cx = x + height * 0.5;
            double cy = y + height * 0.5;
            Matrix matrix = new Matrix(
                m11,
                m12,
                m21,
                m22,
                cx - (cx * m11 + cy * m21),
                cy - (cx * m12 + cy * m22));

            dc.PushTransform(new MatrixTransform(matrix));
            DrawFallbackManikin(dc, x, y, height, view);
            dc.Pop();
        }

        private static double Dot(VVector a, VVector b)
        {
            return a.x * b.x + a.y * b.y + a.z * b.z;
        }

        private static void DrawFallbackManikin(DrawingContext dc, double x, double y, double height, ManikinView view)
        {
            switch (view)
            {
                case ManikinView.Frontal:
                    DrawManikinFrontal(dc, x, y, height);
                    break;
                case ManikinView.Sagittal:
                    DrawManikinSagittal(dc, x, y, height);
                    break;
                case ManikinView.Transversal:
                    DrawManikinTransversal(dc, x, y, height);
                    break;
                case ManikinView.ThreeD:
                default:
                    DrawManikinThreeD(dc, x, y, height);
                    break;
            }
        }

        private static void DrawManikinThreeD(DrawingContext dc, double x, double y, double height)
        {
            Brush green = new SolidColorBrush(Color.FromRgb(0, 205, 32));
            Brush light = new SolidColorBrush(Color.FromRgb(96, 255, 86));
            Brush cyan = new SolidColorBrush(Color.FromRgb(20, 235, 235));
            Pen limbPen = new Pen(green, Math.Max(3.0, height * 0.095));
            limbPen.StartLineCap = limbPen.EndLineCap = PenLineCap.Round;

            double cx = x + height * 0.34;
            double cy = y + height * 0.48;
            dc.DrawEllipse(green, null, new Point(cx, cy), height * 0.16, height * 0.20);
            dc.DrawEllipse(light, null, new Point(cx - height * 0.05, cy - height * 0.07), height * 0.045, height * 0.045);
            dc.DrawEllipse(green, null, new Point(cx + height * 0.04, y + height * 0.20), height * 0.095, height * 0.095);

            dc.DrawLine(limbPen, new Point(cx - height * 0.12, cy - height * 0.03), new Point(x + height * 0.08, y + height * 0.43));
            dc.DrawLine(limbPen, new Point(cx + height * 0.12, cy - height * 0.03), new Point(x + height * 0.55, y + height * 0.43));
            dc.DrawLine(limbPen, new Point(cx - height * 0.08, cy + height * 0.15), new Point(x + height * 0.16, y + height * 0.82));
            dc.DrawLine(limbPen, new Point(cx + height * 0.07, cy + height * 0.16), new Point(x + height * 0.48, y + height * 0.82));

            DrawJoint(dc, cyan, x + height * 0.08, y + height * 0.43, height);
            DrawJoint(dc, cyan, x + height * 0.55, y + height * 0.43, height);
            DrawJoint(dc, cyan, x + height * 0.16, y + height * 0.82, height);
            DrawJoint(dc, cyan, x + height * 0.48, y + height * 0.82, height);
        }

        private static void DrawManikinFrontal(DrawingContext dc, double x, double y, double height)
        {
            DrawStandingManikin(dc, x + height * 0.16, y + height * 0.42, height * 0.48, false);
        }

        private static void DrawManikinSagittal(DrawingContext dc, double x, double y, double height)
        {
            Brush green = new SolidColorBrush(Color.FromRgb(0, 205, 32));
            Brush light = new SolidColorBrush(Color.FromRgb(96, 255, 86));
            Brush cyan = new SolidColorBrush(Color.FromRgb(20, 235, 235));
            Pen pen = new Pen(green, Math.Max(3.0, height * 0.075));
            pen.StartLineCap = pen.EndLineCap = PenLineCap.Round;

            double cx = x + height * 0.46;
            dc.DrawEllipse(green, null, new Point(cx, y + height * 0.49), height * 0.11, height * 0.17);
            dc.DrawEllipse(light, null, new Point(cx - height * 0.03, y + height * 0.43), height * 0.035, height * 0.035);
            dc.DrawEllipse(green, null, new Point(cx, y + height * 0.28), height * 0.075, height * 0.075);
            dc.DrawLine(pen, new Point(cx, y + height * 0.61), new Point(cx, y + height * 0.81));
            DrawJoint(dc, cyan, cx, y + height * 0.81, height);
        }

        private static void DrawManikinTransversal(DrawingContext dc, double x, double y, double height)
        {
            Brush green = new SolidColorBrush(Color.FromRgb(0, 205, 32));
            Brush light = new SolidColorBrush(Color.FromRgb(96, 255, 86));
            Brush cyan = new SolidColorBrush(Color.FromRgb(20, 235, 235));
            Pen limbPen = new Pen(green, Math.Max(3.0, height * 0.08));
            limbPen.StartLineCap = limbPen.EndLineCap = PenLineCap.Round;

            Point body = new Point(x + height * 0.45, y + height * 0.58);
            dc.DrawEllipse(green, null, body, height * 0.20, height * 0.15);
            dc.DrawEllipse(light, null, new Point(body.X - height * 0.07, body.Y - height * 0.04), height * 0.05, height * 0.04);
            dc.DrawLine(limbPen, new Point(body.X - height * 0.17, body.Y), new Point(x + height * 0.10, y + height * 0.53));
            dc.DrawLine(limbPen, new Point(body.X + height * 0.17, body.Y), new Point(x + height * 0.78, y + height * 0.53));
            dc.DrawLine(limbPen, new Point(body.X - height * 0.09, body.Y + height * 0.11), new Point(x + height * 0.27, y + height * 0.82));
            dc.DrawLine(limbPen, new Point(body.X + height * 0.09, body.Y + height * 0.11), new Point(x + height * 0.62, y + height * 0.82));
            DrawJoint(dc, cyan, x + height * 0.10, y + height * 0.53, height);
            DrawJoint(dc, cyan, x + height * 0.78, y + height * 0.53, height);
        }

        private static void DrawStandingManikin(DrawingContext dc, double x, double y, double height, bool narrow)
        {
            Brush green = new SolidColorBrush(Color.FromRgb(0, 205, 32));
            Brush light = new SolidColorBrush(Color.FromRgb(96, 255, 86));
            Brush cyan = new SolidColorBrush(Color.FromRgb(20, 235, 235));
            Pen pen = new Pen(green, Math.Max(3.0, height * 0.115));
            pen.StartLineCap = pen.EndLineCap = PenLineCap.Round;

            double cx = x + height * 0.36;
            double top = y;
            double arm = narrow ? height * 0.12 : height * 0.23;
            dc.DrawEllipse(green, null, new Point(cx, top + height * 0.10), height * 0.10, height * 0.10);
            dc.DrawEllipse(green, null, new Point(cx, top + height * 0.38), height * 0.15, height * 0.20);
            dc.DrawEllipse(light, null, new Point(cx - height * 0.05, top + height * 0.30), height * 0.04, height * 0.04);
            dc.DrawLine(pen, new Point(cx - height * 0.11, top + height * 0.34), new Point(cx - arm, top + height * 0.55));
            dc.DrawLine(pen, new Point(cx + height * 0.11, top + height * 0.34), new Point(cx + arm, top + height * 0.55));
            dc.DrawLine(pen, new Point(cx - height * 0.07, top + height * 0.55), new Point(cx - height * 0.15, top + height * 0.86));
            dc.DrawLine(pen, new Point(cx + height * 0.07, top + height * 0.55), new Point(cx + height * 0.15, top + height * 0.86));
            DrawJoint(dc, cyan, cx - arm, top + height * 0.55, height);
            DrawJoint(dc, cyan, cx + arm, top + height * 0.55, height);
            DrawJoint(dc, cyan, cx - height * 0.15, top + height * 0.86, height);
            DrawJoint(dc, cyan, cx + height * 0.15, top + height * 0.86, height);
        }

        private static void DrawJoint(DrawingContext dc, Brush brush, double x, double y, double height)
        {
            double r = Math.Max(2.0, height * 0.035);
            dc.DrawEllipse(brush, null, new Point(x, y), r, r);
        }

        /// <summary>Horizontales Lineal mit cm-Ticks (laengerer Tick + Beschriftung alle 5 cm).</summary>
        public static void DrawRulerH(DrawingContext dc, double x, double y, double lengthPx, double pxPerCm, Brush brush, Typeface typeface)
        {
            if (pxPerCm <= 1)
                return;
            Pen pen = new Pen(brush, 1.2);
            dc.DrawLine(pen, new Point(x, y), new Point(x + lengthPx, y));
            int cm = 0;
            for (double t = 0; t <= lengthPx + 0.5; t += pxPerCm, cm++)
            {
                bool major = cm % 5 == 0;
                double tick = major ? 10 : 5;
                dc.DrawLine(pen, new Point(x + t, y), new Point(x + t, y + tick));
                if (major && cm > 0)
                {
                    var label = CreateFormattedText(cm.ToString(Num), Num, typeface, ScaleSliceFont(10), brush);
                    dc.DrawText(label, new Point(x + t - label.Width / 2.0, y + tick + 1));
                }
            }
            var unit = CreateFormattedText("cm", Num, typeface, ScaleSliceFont(10), brush);
            dc.DrawText(unit, new Point(x + lengthPx + 6, y - 4));
        }

        /// <summary>Vertikales Lineal mit cm-Ticks.</summary>
        public static void DrawRulerV(DrawingContext dc, double x, double y, double lengthPx, double pxPerCm, Brush brush, Typeface typeface)
        {
            if (pxPerCm <= 1)
                return;
            Pen pen = new Pen(brush, 1.2);
            dc.DrawLine(pen, new Point(x, y), new Point(x, y + lengthPx));
            int cm = 0;
            for (double t = 0; t <= lengthPx + 0.5; t += pxPerCm, cm++)
            {
                bool major = cm % 5 == 0;
                double tick = major ? 10 : 5;
                dc.DrawLine(pen, new Point(x, y + t), new Point(x + tick, y + t));
                if (major && cm > 0)
                {
                    var label = CreateFormattedText(cm.ToString(Num), Num, typeface, ScaleSliceFont(10), brush);
                    dc.DrawText(label, new Point(x + tick + 2, y + t - label.Height / 2.0));
                }
            }
        }

        /// <summary>Deutsche Lagerungsbezeichnung wie im Eclipse-Druck ("Kopf voraus - Rueckenlage").</summary>
        public static string GetGermanOrientationText(string orientationEnumName)
        {
            switch (orientationEnumName ?? "")
            {
                case "HeadFirstSupine": return "Kopf voraus - Rückenlage";
                case "HeadFirstProne": return "Kopf voraus - Bauchlage";
                case "FeetFirstSupine": return "Füße voraus - Rückenlage";
                case "FeetFirstProne": return "Füße voraus - Bauchlage";
                case "HeadFirstDecubitusLeft": return "Kopf voraus - Linksseitenlage";
                case "HeadFirstDecubitusRight": return "Kopf voraus - Rechtsseitenlage";
                case "FeetFirstDecubitusLeft": return "Füße voraus - Linksseitenlage";
                case "FeetFirstDecubitusRight": return "Füße voraus - Rechtsseitenlage";
                case "Sitting": return "Sitzend";
                case "NoOrientation": return "";
                default: return orientationEnumName ?? "";
            }
        }
    }
}
