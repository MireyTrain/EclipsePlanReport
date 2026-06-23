using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VMS.TPS.Common.Model.API;
using VMS.TPS.Common.Model.Types;

namespace EclipsePlanReport
{
    /// <summary>
    /// Transversale CT-Schichten mit Zielkonturen und Isodosen im Stil des
    /// Eclipse-Schichtdrucks: weisser Hintergrund, Kopfzeile (Patient/Bild/Max-Dosis),
    /// Haekchen-Legende, Lineale, Z-Position, Skalierungsfaktor, User-Origin-Offset.
    /// </summary>
    internal static class SliceRenderer
    {
        // ---------- Dosis-Helfer ----------

        /// <summary>CT-Schichtindex -> naechstliegender Dosisgitter-Ebenenindex.</summary>
        public static int FindDosePlaneIndexForImageSlice(Dose dose, Image image, int ctSliceIndex)
        {
            if (dose == null)
                throw new InvalidOperationException("Keine Dosis im Plan verfuegbar.");

            VVector ctSlicePoint = new VVector(
                image.Origin.x + ctSliceIndex * image.ZRes * image.ZDirection.x,
                image.Origin.y + ctSliceIndex * image.ZRes * image.ZDirection.y,
                image.Origin.z + ctSliceIndex * image.ZRes * image.ZDirection.z);

            VVector rel = new VVector(
                ctSlicePoint.x - dose.Origin.x,
                ctSlicePoint.y - dose.Origin.y,
                ctSlicePoint.z - dose.Origin.z);

            double coordAlongDoseZ =
                rel.x * dose.ZDirection.x +
                rel.y * dose.ZDirection.y +
                rel.z * dose.ZDirection.z;

            int k = (int)Math.Round(coordAlongDoseZ / dose.ZRes);
            if (k < 0) k = 0;
            if (k >= dose.ZSize) k = dose.ZSize - 1;
            return k;
        }

        /// <summary>Absolute Dosismatrix [x,y] einer Dosisgitter-Ebene in Gy.</summary>
        public static double[,] GetDoseData(PlanningItem planningItem, int planeIndex)
        {
            planningItem.DoseValuePresentation = DoseValuePresentation.Absolute;
            var dose = planningItem.Dose;
            if (dose == null)
                throw new InvalidOperationException("Keine Dosisdaten im Plan verfuegbar.");

            var data = new int[dose.XSize, dose.YSize];
            dose.GetVoxels(planeIndex, data);

            var doseMatrix = new double[dose.XSize, dose.YSize];
            for (int i = 0; i < dose.XSize; i++)
                for (int j = 0; j < dose.YSize; j++)
                    doseMatrix[i, j] = dose.VoxelToDoseValue(data[i, j]).Dose;
            return doseMatrix;
        }

        /// <summary>
        /// Loest einen Isodosen-Level in Gy auf. Relative Templates beziehen sich auf die
        /// verordnete Gesamtdosis. Fuer Summenplaene sind relative Level gesperrt (liefert 0).
        /// </summary>
        public static double ResolveIsodoseGy(IsodoseLevel iso, PlanningItem planningItem)
        {
            if (iso.DoseGy > 0)
                return iso.DoseGy;

            if (iso.RelativeDosePercent <= 0)
                return 0;

            PlanSetup planSetup = planningItem as PlanSetup;
            if (planSetup != null)
            {
                try
                {
                    if (planSetup.TotalDose.Dose > 0)
                        return planSetup.TotalDose.Dose * iso.RelativeDosePercent / 100.0;
                }
                catch
                {
                }
            }

            // PlanSum: keine eindeutige 100%-Referenz -> relativer Level wird nicht gezeichnet.
            return 0;
        }

        // ---------- Schichtlisten / Bounding-Boxen ----------

        public static int GetMiddleSlice(Image image, Structure structure)
        {
            var contourSlices = GetStructureSlices(image, structure);
            if (!contourSlices.Any())
                return -1;
            return contourSlices[contourSlices.Count / 2];
        }

        public static List<int> GetStructureSlices(Image image, Structure structure)
        {
            if (structure == null)
                return new List<int>();
            return Enumerable.Range(0, image.ZSize)
                .Where(z => structure.GetContoursOnImagePlane(z).Any())
                .ToList();
        }

        /// <summary>Sichtfenster um die BODY-Kontur der Schicht (Druck-Seitenverhaeltnis, nichts abgeschnitten).</summary>
        public static Rect? GetSliceBodyViewBounds(Structure body, Image image, int sliceZ)
        {
            if (body == null || body.IsEmpty)
                return null;

            Rect bodyBounds = GetBoundingBox(body, image, sliceZ);
            if (bodyBounds.IsEmpty || bodyBounds.Width <= 0 || bodyBounds.Height <= 0)
                return null;

            const double viewAspect = 1425.0 / 1002.0; // Bildbereich der Druckseite
            double padding = 28.0;

            double centerX = bodyBounds.X + bodyBounds.Width / 2.0;
            double centerY = bodyBounds.Y + bodyBounds.Height / 2.0;
            double viewWidth = bodyBounds.Width + 2.0 * padding;
            double viewHeight = bodyBounds.Height + 2.0 * padding;

            if (viewWidth / viewHeight < viewAspect)
                viewWidth = viewHeight * viewAspect;
            else
                viewHeight = viewWidth / viewAspect;

            viewWidth = Math.Min(viewWidth, image.XSize);
            viewHeight = Math.Min(viewHeight, image.YSize);

            double x = RenderUtils.Clamp(centerX - viewWidth / 2.0, 0, image.XSize - viewWidth);
            double y = RenderUtils.Clamp(centerY - viewHeight / 2.0, 0, image.YSize - viewHeight);

            return new Rect(x, y, viewWidth, viewHeight);
        }

        private static Rect GetBoundingBox(Structure body, Image image, int sliceZ)
        {
            var contours = body.GetContoursOnImagePlane(sliceZ);
            if (!contours.Any())
                return new Rect(0, 0, image.XSize, image.YSize);

            double minX = double.MaxValue, maxX = double.MinValue;
            double minY = double.MaxValue, maxY = double.MinValue;

            foreach (var segment in contours)
            {
                foreach (var point in segment)
                {
                    Point pix = RenderUtils.ProjectToImagePixel(point, image);
                    if (pix.X < minX) minX = pix.X;
                    if (pix.X > maxX) maxX = pix.X;
                    if (pix.Y < minY) minY = pix.Y;
                    if (pix.Y > maxY) maxY = pix.Y;
                }
            }

            double padding = 5;
            minX = Math.Max(0, minX - padding);
            minY = Math.Max(0, minY - padding);
            maxX = Math.Min(image.XSize - 1, maxX + padding);
            maxY = Math.Min(image.YSize - 1, maxY + padding);

            if (maxX <= minX || maxY <= minY)
                return new Rect(0, 0, image.XSize, image.YSize);

            return new Rect(minX, minY, maxX - minX, maxY - minY);
        }

        // ---------- Seitenrendering (Eclipse-Schichtdruck-Stil) ----------

        public static void RenderSlicePage(
            Image image,
            int sliceZ,
            StructureSet structureSet,
            PlanningItem planningItem,
            ReportTemplate template,
            Structure sliceTarget,
            List<string> displayStructureIds,
            Structure body,
            string filename,
            Rect? bodyBounds,
            SlicePageContext context,
            Action<string> log)
        {
            int width = image.XSize;
            int height = image.YSize;

            int dosePlaneIndex = FindDosePlaneIndexForImageSlice(planningItem.Dose, image, sliceZ);
            double[,] doseData = GetDoseData(planningItem, dosePlaneIndex);

            Rect? viewBounds = bodyBounds.HasValue
                ? NormalizeImageBounds(bodyBounds.Value, image)
                : (Rect?)null;
            double currentWidth = viewBounds.HasValue ? viewBounds.Value.Width : width;
            double currentHeight = viewBounds.HasValue ? viewBounds.Value.Height : height;

            string positionCode = context != null ? context.PatientPositionCode : "";
            string tableSideLabel = RenderUtils.GetTableSideLabel(positionCode);
            string desiredRightLabel = RenderUtils.GetTransversalRightLabelForTableDown(tableSideLabel, positionCode);
            RenderUtils.DisplayTransform displayTransform = RenderUtils.CreateDisplayTransform(
                image.XDirection,
                image.YDirection,
                desiredRightLabel,
                tableSideLabel);
            if (displayTransform.IsFallback && log != null)
                log(string.Format("  Warnung: Transversal-Orientierung fuer {0} nicht sicher bestimmbar - verwende native Bildachsen.", positionCode));

            BitmapSource ctBitmap = CreateCTSliceBitmap(
                image,
                sliceZ,
                viewBounds.HasValue ? (int)viewBounds.Value.X : 0,
                viewBounds.HasValue ? (int)viewBounds.Value.Y : 0,
                (int)currentWidth,
                (int)currentHeight);

            // ---- CT als Raster, Zielkonturen + Isodosen als Vektorpfade rendern ----
            var dose = planningItem.Dose;
            bool relativeSkipped = false;

            // ---- Weisse Druckseite im Eclipse-Stil aufbauen ----
            int pageW = RenderUtils.PageWidthPx;
            int pageH = RenderUtils.PageHeightPx;
            var typeface = new Typeface("Segoe UI");
            var boldTypeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            var culture = RenderUtils.Num;

            DrawingVisual pageVisual = new DrawingVisual();
            using (DrawingContext dc = pageVisual.RenderOpen())
            {
                dc.DrawRectangle(Theme.PageBackground, null, new Rect(0, 0, pageW, pageH));

                Brush textBrush = Theme.PrimaryText;
                Brush mutedBrush = Theme.MutedText;
                Pen linePen = new Pen(Theme.SeparatorLine, 0.8);

                // --- Kopfzeile (3 Spalten) ---
                Patient patient = context != null ? context.Patient : null;
                string patientLine = patient != null
                    ? string.Format("{0}, {1} ({2})", patient.LastName, patient.FirstName, patient.Id).Trim(' ', ',')
                    : "";

                double headerY = 14;
                RenderUtils.DrawText(dc, "Patientenname:", 24, headerY, 11.5, mutedBrush, typeface, culture);
                RenderUtils.DrawTextFit(dc, patientLine, 150, headerY, 440, 11.5, textBrush, boldTypeface, culture);
                RenderUtils.DrawText(dc, "Klinik:", 24, headerY + 24, 11.5, mutedBrush, typeface, culture);
                RenderUtils.DrawTextFit(dc, context != null ? context.Hospital ?? "" : "", 150, headerY + 24, 440, 11.5, textBrush, typeface, culture);
                RenderUtils.DrawText(dc, "Plan:", 24, headerY + 48, 11.5, mutedBrush, typeface, culture);
                RenderUtils.DrawTextFit(dc, context != null ? context.PlanLabel ?? "" : "", 150, headerY + 48, 440, 11.5, textBrush, typeface, culture);

                string seriesId = ReflectionUtils.GetNestedStringProperty(image, "Series", "Id");
                RenderUtils.DrawText(dc, "Bild:", 660, headerY, 11.5, mutedBrush, typeface, culture);
                RenderUtils.DrawTextFit(dc, image.Id ?? "", 740, headerY, 380, 11.5, textBrush, typeface, culture);
                RenderUtils.DrawText(dc, "Serie:", 660, headerY + 24, 11.5, mutedBrush, typeface, culture);
                RenderUtils.DrawTextFit(dc, seriesId, 740, headerY + 24, 380, 11.5, textBrush, typeface, culture);
                RenderUtils.DrawText(dc, "Lagerung:", 660, headerY + 48, 11.5, mutedBrush, typeface, culture);
                RenderUtils.DrawTextFit(dc, context != null ? context.OrientationText ?? "" : "", 740, headerY + 48, 380, 11.5, textBrush, typeface, culture);

                if (context != null && context.MaxDose3DGy > 0)
                {
                    RenderUtils.DrawText(dc, "Max. Dosis:", 1260, headerY, 11.5, mutedBrush, typeface, culture);
                    RenderUtils.DrawText(dc, string.Format(culture, "{0:F3} Gy", context.MaxDose3DGy), 1360, headerY, 11.5, textBrush, boldTypeface, culture);
                }
                RenderUtils.DrawText(dc, "Template:", 1260, headerY + 24, 11.5, mutedBrush, typeface, culture);
                RenderUtils.DrawTextFit(dc, template.DisplayName, 1360, headerY + 24, 370, 11.5, textBrush, typeface, culture);

                dc.DrawLine(linePen, new Point(24, 100), new Point(pageW - 24, 100));

                // --- Legende links (vergroessert fuer bessere Lesbarkeit) ---
                RenderUtils.DrawIsodoseLegendThemed(dc, template, iso => ResolveIsodoseGy(iso, planningItem), 24, 128, 1.45);

                // --- Bildbereich ---
                Rect imageArea = new Rect(235, 118, 1425, 1002);
                double displayWidth = displayTransform.SwapsAxes ? currentHeight : currentWidth;
                double displayHeight = displayTransform.SwapsAxes ? currentWidth : currentHeight;
                double pageScale = Math.Min(imageArea.Width / displayWidth, imageArea.Height / displayHeight);
                double targetW = displayWidth * pageScale;
                double targetH = displayHeight * pageScale;
                double targetX = imageArea.X + (imageArea.Width - targetW) / 2.0;
                double targetY = imageArea.Y + (imageArea.Height - targetH) / 2.0;

                relativeSkipped = DrawVectorSliceContent(
                    dc,
                    ctBitmap,
                    image,
                    sliceZ,
                    structureSet,
                    planningItem,
                    template,
                    sliceTarget,
                    displayStructureIds,
                    body,
                    viewBounds,
                    dose,
                    doseData,
                    dosePlaneIndex,
                    new Rect(targetX, targetY, targetW, targetH),
                    displayTransform,
                    currentWidth,
                    currentHeight,
                    pageScale,
                    log);

                // Orientierungsbuchstaben und Z-Label direkt im gezeichneten CT-Bild
                // platzieren. Der Bildbereich kann groesser sein als das tatsaechlich
                // eingepasste CT; deshalb beziehen sich alle Positionen auf target*.
                double labelPadding = Math.Max(10.0, RenderUtils.ScaleSliceFont(8));
                double labelBgPadX = Math.Max(4.0, RenderUtils.ScaleSliceFont(3));
                double labelBgPadY = Math.Max(2.0, RenderUtils.ScaleSliceFont(2));
                double oriSize = RenderUtils.ScaleSliceFont(18);
                double imgCenterX = targetX + targetW / 2.0;
                double imgCenterY = targetY + targetH / 2.0;
                Brush imageLabelBrush = Brushes.White;
                var topText = RenderUtils.CreateFormattedText(displayTransform.TopLabel, oriSize, imageLabelBrush, boldTypeface, culture);
                DrawImageOverlayText(dc, topText, imgCenterX - topText.Width / 2.0, targetY + labelPadding, labelBgPadX, labelBgPadY);
                var bottomText = RenderUtils.CreateFormattedText(displayTransform.BottomLabel, oriSize, imageLabelBrush, boldTypeface, culture);
                DrawImageOverlayText(dc, bottomText, imgCenterX - bottomText.Width / 2.0, targetY + targetH - bottomText.Height - labelPadding, labelBgPadX, labelBgPadY);
                var leftText = RenderUtils.CreateFormattedText(displayTransform.LeftLabel, oriSize, imageLabelBrush, boldTypeface, culture);
                DrawImageOverlayText(dc, leftText, targetX + labelPadding, imgCenterY - leftText.Height / 2.0, labelBgPadX, labelBgPadY);
                var rightText = RenderUtils.CreateFormattedText(displayTransform.RightLabel, oriSize, imageLabelBrush, boldTypeface, culture);
                DrawImageOverlayText(dc, rightText, targetX + targetW - rightText.Width - labelPadding, imgCenterY - rightText.Height / 2.0, labelBgPadX, labelBgPadY);

                // Z-Position oben links im Bild statt am cm-Lineal.
                double zEclipseCm = ComputeEclipseZcm(image, sliceZ);
                var zText = RenderUtils.CreateFormattedText(
                    string.Format(culture, "Z: {0:+0.00;-0.00;0.00} cm", zEclipseCm),
                    RenderUtils.ScaleSliceFont(16), Brushes.Red, boldTypeface, culture);
                DrawImageOverlayText(dc, zText, targetX + labelPadding, targetY + labelPadding, labelBgPadX, labelBgPadY);

                // Lineale (unten und rechts am CT)
                double displayWidthMm = displayTransform.SwapsAxes
                    ? currentHeight * image.YRes
                    : currentWidth * image.XRes;
                double pxPerMm = targetW / displayWidthMm;
                double pxPerCm = pxPerMm * 10.0;
                RenderUtils.DrawRulerH(dc, targetX, Math.Min(pageH - 96, targetY + targetH + 26), targetW - 40, pxPerCm, textBrush, typeface);
                RenderUtils.DrawRulerV(dc, Math.Min(pageW - 64, targetX + targetW + 28), targetY, targetH - 40, pxPerCm, textBrush, typeface);

                // --- Fussblock: Skalierung + User-Origin (Z steht oben am Bild) ---
                double infoY = 1162;
                double scaleFactorPercent = pxPerMm * (297.0 / pageW) * 100.0;
                string infoLine = string.Format(culture, "Skalierungsfaktor: {0:F0}%", scaleFactorPercent);
                VVector userOriginCm = GetUserOriginCm(image);
                string originLine = string.Format(culture,
                    "Benutzerursprung DICOM Offset = ({0:F2} cm, {1:F2} cm, {2:F2} cm)",
                    userOriginCm.x, userOriginCm.y, userOriginCm.z);

                var infoText = RenderUtils.CreateFormattedText(infoLine, RenderUtils.ScaleSliceFont(11), mutedBrush, typeface, culture);
                dc.DrawText(infoText, new Point(pageW / 2.0 - infoText.Width / 2.0, infoY));
                var originText = RenderUtils.CreateFormattedText(originLine, RenderUtils.ScaleSliceFont(11), mutedBrush, typeface, culture);
                dc.DrawText(originText, new Point(pageW / 2.0 - originText.Width / 2.0, infoY + 22));

                // Orientierungsfigur
                RenderUtils.DrawManikin(dc, 34, 1104, 96, RenderUtils.ManikinView.Transversal, displayTransform);

                // --- Fusszeile ---
                dc.DrawLine(linePen, new Point(24, 1206), new Point(pageW - 24, 1206));
                string footerLeft = patient != null
                    ? string.Format("Patient: {0}   Bild: {1}   Plan: {2}", patient.Id, image.Id, context.PlanLabel)
                    : "";
                RenderUtils.DrawTextFit(dc, footerLeft, 24, 1214, 900, 10, mutedBrush, typeface, culture);

                string footerRight = string.Format(culture,
                    "Eclipse PlanReport - Gedruckt {0:dd.MM.yyyy HH:mm} von {1}    Seite {2}/{3}",
                    DateTime.Now,
                    Environment.UserName,
                    context != null ? context.PageNumber : 1,
                    context != null ? context.PageCount : 1);
                var footerText = RenderUtils.CreateFormattedText(footerRight, RenderUtils.ScaleFont(10), mutedBrush, typeface, culture);
                dc.DrawText(footerText, new Point(pageW - 24 - footerText.Width, 1214));
            }

            RenderUtils.SaveVisualAsPng(pageVisual, pageW, pageH, filename);

            if (relativeSkipped && log != null)
                log("  Hinweis: relative Isodosen werden bei Summenplaenen nicht gezeichnet.");
        }

        private static bool DrawVectorSliceContent(
            DrawingContext dc,
            BitmapSource ctBitmap,
            Image image,
            int sliceZ,
            StructureSet structureSet,
            PlanningItem planningItem,
            ReportTemplate template,
            Structure sliceTarget,
            List<string> displayStructureIds,
            Structure body,
            Rect? viewBounds,
            Dose dose,
            double[,] doseData,
            int dosePlaneIndex,
            Rect targetRect,
            RenderUtils.DisplayTransform displayTransform,
            double currentWidth,
            double currentHeight,
            double drawScale,
            Action<string> log)
        {
            bool relativeSkipped = false;

            dc.PushClip(new RectangleGeometry(targetRect));
            dc.PushTransform(new TranslateTransform(targetRect.X, targetRect.Y));
            dc.PushTransform(new ScaleTransform(drawScale, drawScale));
            dc.PushTransform(new MatrixTransform(RenderUtils.GetNaturalToDisplayMatrix(displayTransform, currentWidth, currentHeight)));

            Geometry bodyClip = BuildBodyClip(body, image, sliceZ, viewBounds);
            if (bodyClip != null)
                dc.PushClip(bodyClip);

            dc.DrawImage(ctBitmap, new Rect(0, 0, currentWidth, currentHeight));

            Func<double, double, Point> mapDoseToView = (ix, iy) =>
            {
                double offXmm = ix * dose.XRes;
                double offYmm = iy * dose.YRes;
                double offZmm = dosePlaneIndex * dose.ZRes;

                VVector patientPoint = new VVector(
                    dose.Origin.x + offXmm * dose.XDirection.x + offYmm * dose.YDirection.x + offZmm * dose.ZDirection.x,
                    dose.Origin.y + offXmm * dose.XDirection.y + offYmm * dose.YDirection.y + offZmm * dose.ZDirection.y,
                    dose.Origin.z + offXmm * dose.XDirection.z + offYmm * dose.YDirection.z + offZmm * dose.ZDirection.z);

                Point p = RenderUtils.ProjectToImagePixel(patientPoint, image);
                if (viewBounds.HasValue)
                    return new Point(p.X - viewBounds.Value.X, p.Y - viewBounds.Value.Y);
                return p;
            };

            foreach (var iso in template.Isodoses)
            {
                double doseGy = ResolveIsodoseGy(iso, planningItem);
                if (doseGy <= 0)
                {
                    if (iso.RelativeDosePercent > 0 && planningItem is PlanSum)
                        relativeSkipped = true;
                    continue;
                }

                double thickness = Math.Max(0.6, iso.Thickness) / drawScale;
                Pen pen = new Pen(new SolidColorBrush(iso.Color), thickness);
                RenderUtils.DrawIsoLines(dc, doseData, doseGy, pen, mapDoseToView);
            }

            int contourSegmentCount = 0;
            if (structureSet != null)
            {
                foreach (var structure in GetSliceContourStructures(structureSet, sliceTarget, template, displayStructureIds, sliceZ))
                    contourSegmentCount += DrawStructureContours(dc, structure, image, sliceZ, viewBounds, drawScale);
            }
            if (contourSegmentCount == 0 && log != null)
            {
                string targetId = sliceTarget != null ? sliceTarget.Id : "(kein Ziel)";
                log(string.Format("  Hinweis: keine Anzeige-Kontur auf CT-Schicht {0} gezeichnet (Schicht-Ziel: {1}).", sliceZ, targetId));
            }

            if (bodyClip != null)
                dc.Pop(); // Body-Clip
            dc.Pop(); // display transform
            dc.Pop(); // scale
            dc.Pop(); // translate
            dc.Pop(); // target clip

            return relativeSkipped;
        }

        private static List<Structure> GetSliceContourStructures(
            StructureSet structureSet,
            Structure sliceTarget,
            ReportTemplate template,
            List<string> displayStructureIds,
            int sliceZ)
        {
            var result = new List<Structure>();
            if (displayStructureIds != null)
            {
                foreach (string id in displayStructureIds)
                {
                    if (string.IsNullOrEmpty(id))
                        continue;
                    Structure structure = structureSet.Structures.FirstOrDefault(s =>
                        !s.IsEmpty && s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                    AddVisibleSliceStructure(result, structure, sliceZ);
                }
                return result;
            }

            AddVisibleSliceStructure(result, sliceTarget, sliceZ);

            if (structureSet == null || template == null)
                return result;

            foreach (Structure structure in structureSet.Structures
                .Where(s => !s.IsEmpty &&
                    (RenderUtils.MatchesAnyPattern(s.Id, template.TargetPatterns) || LooksLikeTargetStructure(s.Id))))
            {
                AddVisibleSliceStructure(result, structure, sliceZ);
            }

            return result;
        }

        private static void AddVisibleSliceStructure(List<Structure> result, Structure structure, int sliceZ)
        {
            if (structure == null || structure.IsEmpty)
                return;
            if (!structure.GetContoursOnImagePlane(sliceZ).Any())
                return;
            if (result.Any(s => string.Equals(s.Id, structure.Id, StringComparison.OrdinalIgnoreCase)))
                return;

            result.Add(structure);
        }

        private static bool LooksLikeTargetStructure(string id)
        {
            string value = (id ?? "").ToUpperInvariant();
            return value.Contains("PTV") || value.Contains("CTV") || value.Contains("GTV") || value.Contains("BOOST");
        }

        private static int DrawStructureContours(
            DrawingContext dc,
            Structure structure,
            Image image,
            int sliceZ,
            Rect? viewBounds,
            double drawScale)
        {
            if (structure == null || structure.IsEmpty)
                return 0;

            Color structureColor = Color.FromRgb(structure.Color.R, structure.Color.G, structure.Color.B);
            Pen colorPen = new Pen(new SolidColorBrush(structureColor), 2.1 / drawScale);
            int drawn = 0;

            foreach (var segment in structure.GetContoursOnImagePlane(sliceZ))
            {
                if (segment == null || segment.Length <= 1)
                    continue;

                Point previous = ConvertToZoomedPoint(segment[0], image, viewBounds);
                for (int i = 1; i < segment.Length; i++)
                {
                    Point current = ConvertToZoomedPoint(segment[i], image, viewBounds);
                    dc.DrawLine(colorPen, previous, current);
                    previous = current;
                    drawn++;
                }

                Point first = ConvertToZoomedPoint(segment[0], image, viewBounds);
                dc.DrawLine(colorPen, previous, first);
                drawn++;
            }

            return drawn;
        }

        private static void DrawImageOverlayText(DrawingContext dc, FormattedText text, double x, double y, double padX, double padY)
        {
            dc.DrawText(text, new Point(x, y));
        }

        /// <summary>Z-Koordinate der Schicht relativ zum User-Origin in cm (Eclipse-Anzeige).</summary>
        public static double ComputeEclipseZcm(Image image, int sliceZ)
        {
            VVector userOrigin;
            try
            {
                userOrigin = image.UserOrigin;
            }
            catch
            {
                userOrigin = image.Origin;
            }

            VVector sliceOrigin = new VVector(
                image.Origin.x + sliceZ * image.ZRes * image.ZDirection.x,
                image.Origin.y + sliceZ * image.ZRes * image.ZDirection.y,
                image.Origin.z + sliceZ * image.ZRes * image.ZDirection.z);

            // Eclipse zeigt die transversale Z-Position als Patienten-Superior/Inferior-
            // Koordinate relativ zum User-Origin. Die gespeicherte Slice-Richtung darf
            // das Vorzeichen nicht drehen, sonst erscheinen HFS-Schichten invertiert.
            return (sliceOrigin.z - userOrigin.z) / 10.0;
        }

        private static VVector GetUserOriginCm(Image image)
        {
            try
            {
                VVector uo = image.UserOrigin;
                return new VVector(uo.x / 10.0, uo.y / 10.0, uo.z / 10.0);
            }
            catch
            {
                return new VVector(0, 0, 0);
            }
        }

        public static void DrawCTSlice(DrawingContext dc, Image image, int sliceZ, int startX, int startY, int width, int height)
        {
            BitmapSource bitmap = CreateCTSliceBitmap(image, sliceZ, startX, startY, width, height);
            dc.DrawImage(bitmap, new Rect(0, 0, width, height));
        }

        private static BitmapSource CreateCTSliceBitmap(Image image, int sliceZ, int startX, int startY, int width, int height)
        {
            int[,] buffer = new int[image.XSize, image.YSize];
            image.GetVoxels(sliceZ, buffer);

            double windowCenter, windowWidth;
            RenderUtils.GetWindowLevel(image, out windowCenter, out windowWidth);

            byte[] pixels = new byte[width * height];
            double windowMin = windowCenter - windowWidth / 2.0;

            for (int x = startX; x < startX + width; x++)
            {
                if (x < 0 || x >= image.XSize) continue;

                for (int y = startY; y < startY + height; y++)
                {
                    if (y < 0 || y >= image.YSize) continue;

                    double hu = image.VoxelToDisplayValue(buffer[x, y]);
                    double intensity = (hu - windowMin) / windowWidth * 255.0;

                    int localX = x - startX;
                    int localY = y - startY;
                    if (localX < 0 || localX >= width || localY < 0 || localY >= height)
                        continue;

                    pixels[localY * width + localX] = (byte)Math.Max(0, Math.Min(255, intensity));
                }
            }

            BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width);
            RenderOptions.SetBitmapScalingMode(bitmap, BitmapScalingMode.HighQuality);
            return bitmap;
        }

        private static Rect NormalizeImageBounds(Rect bounds, Image image)
        {
            int x0 = Math.Max(0, (int)Math.Floor(bounds.X));
            int y0 = Math.Max(0, (int)Math.Floor(bounds.Y));
            int x1 = Math.Min(image.XSize, (int)Math.Ceiling(bounds.X + bounds.Width));
            int y1 = Math.Min(image.YSize, (int)Math.Ceiling(bounds.Y + bounds.Height));

            if (x1 <= x0 || y1 <= y0)
                return new Rect(0, 0, image.XSize, image.YSize);

            return new Rect(x0, y0, x1 - x0, y1 - y0);
        }

        /// <summary>
        /// Clip-Geometrie aus den BODY-Konturen der Schicht (im gezoomten Bild-Pixelraum).
        /// EvenOdd-Fuellregel, damit innen liegende Konturen als Loecher wirken.
        /// Liefert null, wenn keine BODY-Kontur vorliegt (dann kein Clipping).
        /// </summary>
        private static Geometry BuildBodyClip(Structure body, Image image, int sliceZ, Rect? bodyBounds)
        {
            if (body == null || body.IsEmpty)
                return null;

            var contours = body.GetContoursOnImagePlane(sliceZ);
            if (contours == null || !contours.Any())
                return null;

            StreamGeometry geometry = new StreamGeometry { FillRule = FillRule.EvenOdd };
            using (StreamGeometryContext sgc = geometry.Open())
            {
                foreach (var segment in contours)
                {
                    if (segment.Length < 3)
                        continue;
                    sgc.BeginFigure(ConvertToZoomedPoint(segment[0], image, bodyBounds), true, true);
                    sgc.PolyLineTo(
                        segment.Skip(1).Select(p => ConvertToZoomedPoint(p, image, bodyBounds)).ToArray(),
                        true,
                        false);
                }
            }

            geometry.Freeze();
            return geometry;
        }

        private static Point ConvertToZoomedPoint(VVector point, Image image, Rect? bodyBounds)
        {
            Point p = RenderUtils.ProjectToImagePixel(point, image);
            if (bodyBounds.HasValue)
                return new Point(p.X - bodyBounds.Value.X, p.Y - bodyBounds.Value.Y);
            return p;
        }
    }
}
