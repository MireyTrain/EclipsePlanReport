using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using VMS.TPS.Common.Model.API;
using VMS.TPS.Common.Model.Types;

namespace EclipsePlanReport
{
    /// <summary>
    /// Nachgebaute Eclipse-Planungsansicht (Referenz: Eclipse-Druck "Planungsansicht"):
    /// 2x2-Raster mit transversal / sagittal / frontal durch das Zentrum der Zielstruktur
    /// plus Plan-Info-Panel im Stil des Eclipse-Dosis-Tabs. Mit Isodosen, Strukturkonturen
    /// (Mesh-Schnitt auf allen Ebenen), farbcodierten Schnittebenen, Linealen, Max-Dosis,
    /// Positionslabels und Orientierungsfigur.
    /// </summary>
    internal static class EclipseViewRenderer
    {
        private static readonly Color TransPlaneColor = Color.FromRgb(0, 200, 0);     // gruen
        private static readonly Color SagPlaneColor = Color.FromRgb(255, 165, 0);     // orange
        private static readonly Color FroPlaneColor = Color.FromRgb(30, 144, 255);    // blau

        public static void RenderViewPage(
            Patient patient,
            PlanningItem planningItem,
            StructureSet structureSet,
            ReportTemplate template,
            Structure target,
            List<string> visibleStructureIds,
            string filename,
            Action<string> log)
        {
            Image image = structureSet.Image;
            Dose dose = null;
            try
            {
                planningItem.DoseValuePresentation = DoseValuePresentation.Absolute;
                dose = planningItem.Dose;
            }
            catch
            {
            }

            // Zentrum der Zielstruktur (Fallback: Bildmitte)
            VVector center;
            try
            {
                center = (target != null && !target.IsEmpty)
                    ? target.CenterPoint
                    : GetImageCenter(image);
            }
            catch
            {
                center = GetImageCenter(image);
            }

            int xc = ClampIndex((int)Math.Round(DotFromOrigin(center, image, image.XDirection) / image.XRes), image.XSize);
            int yc = ClampIndex((int)Math.Round(DotFromOrigin(center, image, image.YDirection) / image.YRes), image.YSize);
            int zc = ClampIndex((int)Math.Round(DotFromOrigin(center, image, image.ZDirection) / image.ZRes), image.ZSize);

            double windowCenter, windowWidth;
            RenderUtils.GetWindowLevel(image, out windowCenter, out windowWidth);

            // ---- CT-Daten in einem Durchlauf extrahieren ----
            double[,] transHu = new double[image.XSize, image.YSize];
            double[,] sagHu = new double[image.YSize, image.ZSize];
            double[,] froHu = new double[image.XSize, image.ZSize];

            // CT-Volumen fuer synthetisches DRR, falls die Eclipse-DRRs nur "Live"
            // existieren und ESAPI keine Bilder liefert.
            CtVolume ctVolume = NeedsSyntheticDrr(planningItem as PlanSetup) ? new CtVolume(image) : null;

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            int[,] buffer = new int[image.XSize, image.YSize];
            for (int z = 0; z < image.ZSize; z++)
            {
                image.GetVoxels(z, buffer);

                for (int yIdx = 0; yIdx < image.YSize; yIdx++)
                    sagHu[yIdx, z] = image.VoxelToDisplayValue(buffer[xc, yIdx]);
                for (int xIdx = 0; xIdx < image.XSize; xIdx++)
                    froHu[xIdx, z] = image.VoxelToDisplayValue(buffer[xIdx, yc]);

                if (z == zc)
                {
                    for (int xIdx = 0; xIdx < image.XSize; xIdx++)
                        for (int yIdx = 0; yIdx < image.YSize; yIdx++)
                            transHu[xIdx, yIdx] = image.VoxelToDisplayValue(buffer[xIdx, yIdx]);
                }

                if (ctVolume != null)
                    ctVolume.FillPlane(image, z, buffer);
            }

            if (log != null)
                log(string.Format(RenderUtils.Num, "  Ansicht: CT-Extraktion {0:F1} s", stopwatch.Elapsed.TotalSeconds));

            BitmapSource transBmp = RenderUtils.MakeGrayBitmap(image.XSize, image.YSize, (c, r) => transHu[c, r], windowCenter, windowWidth);
            BitmapSource sagBmp = RenderUtils.MakeGrayBitmap(image.YSize, image.ZSize, (c, r) => sagHu[c, r], windowCenter, windowWidth);
            BitmapSource froBmp = RenderUtils.MakeGrayBitmap(image.XSize, image.ZSize, (c, r) => froHu[c, r], windowCenter, windowWidth);

            // ---- Dosisdaten extrahieren ----
            double[,] transDose = null, sagDose = null, froDose = null;
            int ktFix = 0, iFix = 0, jFix = 0;
            if (dose != null)
            {
                try
                {
                    ktFix = SliceRenderer.FindDosePlaneIndexForImageSlice(dose, image, zc);
                    transDose = SliceRenderer.GetDoseData(planningItem, ktFix);

                    iFix = ClampIndex((int)Math.Round(DotFromDoseOrigin(center, dose, dose.XDirection) / dose.XRes), dose.XSize);
                    jFix = ClampIndex((int)Math.Round(DotFromDoseOrigin(center, dose, dose.YDirection) / dose.YRes), dose.YSize);

                    sagDose = new double[dose.YSize, dose.ZSize];
                    froDose = new double[dose.XSize, dose.ZSize];
                    int[,] doseBuffer = new int[dose.XSize, dose.YSize];
                    for (int k = 0; k < dose.ZSize; k++)
                    {
                        dose.GetVoxels(k, doseBuffer);
                        for (int j = 0; j < dose.YSize; j++)
                            sagDose[j, k] = dose.VoxelToDoseValue(doseBuffer[iFix, j]).Dose;
                        for (int i = 0; i < dose.XSize; i++)
                            froDose[i, k] = dose.VoxelToDoseValue(doseBuffer[i, jFix]).Dose;
                    }
                }
                catch (Exception e)
                {
                    transDose = null;
                    sagDose = null;
                    froDose = null;
                    if (log != null)
                        log("  Ansichtsseite: Dosis konnte nicht gelesen werden: " + e.Message);
                }
            }

            // ---- Strukturkonturen via Mesh-Schnitt (alle drei Ebenen) ----
            List<Structure> visibleStructures = CollectVisibleStructures(structureSet, target, visibleStructureIds);

            var transSegs = new List<StructureSegments>();
            var sagSegs = new List<StructureSegments>();
            var froSegs = new List<StructureSegments>();
            foreach (Structure structure in visibleStructures)
            {
                Color color = Color.FromRgb(structure.Color.R, structure.Color.G, structure.Color.B);
                transSegs.Add(new StructureSegments { Color = color, Segments = MeshSlicer.Slice(structure, center, image.ZDirection) });
                sagSegs.Add(new StructureSegments { Color = color, Segments = MeshSlicer.Slice(structure, center, image.XDirection) });
                froSegs.Add(new StructureSegments { Color = color, Segments = MeshSlicer.Slice(structure, center, image.YDirection) });
            }

            // ---- Seite zeichnen ----
            int width = RenderUtils.PageWidthPx;
            int height = RenderUtils.PageHeightPx;
            var typeface = new Typeface("Segoe UI");
            var culture = RenderUtils.Num;

            string planId = PlanPageRenderer.GetPlanningItemId(planningItem);
            string approvalStatus = ReflectionUtils.GetStringProperty(planningItem, "ApprovalStatus");
            string seriesId = ReflectionUtils.GetNestedStringProperty(image, "Series", "Id");
            string titleSuffix = string.IsNullOrEmpty(approvalStatus) ? "" : " - " + approvalStatus;

            DrawingVisual visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Theme.PageBackground, null, new Rect(0, 0, width, height));

                var mutedBrush = Theme.MutedText;
                RenderUtils.DrawText(dc, string.Format("Planungsansicht - {0}", planId), 16, 10, 22, Theme.PrimaryText, typeface, culture);
                RenderUtils.DrawText(dc, RenderUtils.BuildPatientHeader(patient), 16, 42, 14, mutedBrush, typeface, culture);
                RenderUtils.DrawText(dc, "Nachgebaute Eclipse-Ansicht", width - 320, 42, 13, mutedBrush, typeface, culture);

                Rect q1 = new Rect(16, 68, 853, 556);   // transversal
                Rect q2 = new Rect(885, 68, 853, 556);  // sagittal
                Rect q3 = new Rect(16, 640, 853, 556);  // frontal
                Rect q4 = new Rect(885, 640, 853, 556); // Plan-Info

                string rawOrientation = ReflectionUtils.FirstNonEmpty(
                    ReflectionUtils.GetStringProperty(planningItem, "TreatmentOrientation"),
                    ReflectionUtils.GetStringProperty(image, "ImagingOrientation"));
                string positionCode = RenderUtils.GetPatientPositionCode(rawOrientation);
                string tableSideLabel = RenderUtils.GetTableSideLabel(positionCode);
                string transRightLabel = RenderUtils.GetTransversalRightLabelForTableDown(tableSideLabel, positionCode);
                string frontalRightLabel = RenderUtils.GetFrontalRightLabel(positionCode);

                RenderUtils.DisplayTransform transDisplay = RenderUtils.CreateDisplayTransform(image.XDirection, image.YDirection, transRightLabel, tableSideLabel);
                RenderUtils.DisplayTransform sagDisplay = RenderUtils.CreateDisplayTransform(image.YDirection, image.ZDirection, "P", "F");
                RenderUtils.DisplayTransform froDisplay = RenderUtils.CreateDisplayTransform(image.XDirection, image.ZDirection, frontalRightLabel, "F");
                if (log != null)
                {
                    log(string.Format("  Orientierung: Position={0}, X={1}, Y={2}, Z={3}",
                        string.IsNullOrEmpty(positionCode) ? rawOrientation : positionCode,
                        RenderUtils.GetLabelForDirection(image.XDirection),
                        RenderUtils.GetLabelForDirection(image.YDirection),
                        RenderUtils.GetLabelForDirection(image.ZDirection)));
                    log(string.Format("  Mapping Transversal: rechts={0}, unten={1} (Tisch unten).", transDisplay.RightLabel, transDisplay.BottomLabel));
                    log(string.Format("  Mapping Frontal: rechts={0}, unten={1}.", froDisplay.RightLabel, froDisplay.BottomLabel));
                    if (transDisplay.IsFallback)
                        log("  Warnung: Transversal-Mapping nicht sicher bestimmbar - native Bildachsen werden verwendet.");
                }

                double cxMm = DotFromOrigin(center, image, image.XDirection);
                double cyMm = DotFromOrigin(center, image, image.YDirection);
                double czMm = DotFromOrigin(center, image, image.ZDirection);

                // Position relativ zum User-Origin (DICOM-Achsen, cm)
                VVector userRel = ComputeUserRelCm(image, center);

                // --- transversal (oben links) ---
                Rect transContent = DrawPlanarView(dc, q1,
                    string.Format("{0}{1} - Transversal - {2}", planId, titleSuffix, seriesId),
                    transBmp,
                    image.XSize * image.XRes, image.YSize * image.YRes,
                    transDisplay,
                    (innerDc, penScale) =>
                    {
                        if (transDose != null)
                        {
                            DrawIsodoses(innerDc, transDose, planningItem, template, penScale, (u, v) =>
                            {
                                VVector p = DoseGridToPatient(dose, u, v, ktFix, PlaneAxis.Transversal);
                                return new Point(DotFromOrigin(p, image, image.XDirection), DotFromOrigin(p, image, image.YDirection));
                            });
                        }
                        DrawStructureSegments(innerDc, transSegs, penScale,
                            p => new Point(DotFromOrigin(p, image, image.XDirection), DotFromOrigin(p, image, image.YDirection)));
                        DrawCrosshair(innerDc, cxMm, cyMm, image.XSize * image.XRes, image.YSize * image.YRes, penScale, SagPlaneColor, FroPlaneColor);
                    },
                    string.Format(culture, "Z: {0:+0.00;-0.00;0.00} cm", SliceRenderer.ComputeEclipseZcm(image, zc)),
                    BuildMaxDoseLabel(transDose),
                    typeface);

                // --- sagittal (oben rechts) ---
                DrawPlanarView(dc, q2,
                    string.Format("{0}{1} - Sagittal - {2}", planId, titleSuffix, seriesId),
                    sagBmp,
                    image.YSize * image.YRes, image.ZSize * image.ZRes,
                    sagDisplay,
                    (innerDc, penScale) =>
                    {
                        if (sagDose != null)
                        {
                            DrawIsodoses(innerDc, sagDose, planningItem, template, penScale, (u, v) =>
                            {
                                VVector p = DoseGridToPatient(dose, u, v, iFix, PlaneAxis.Sagittal);
                                return new Point(DotFromOrigin(p, image, image.YDirection), DotFromOrigin(p, image, image.ZDirection));
                            });
                        }
                        DrawStructureSegments(innerDc, sagSegs, penScale,
                            p => new Point(DotFromOrigin(p, image, image.YDirection), DotFromOrigin(p, image, image.ZDirection)));
                        DrawCrosshair(innerDc, cyMm, czMm, image.YSize * image.YRes, image.ZSize * image.ZRes, penScale, FroPlaneColor, TransPlaneColor);
                    },
                    string.Format(culture, "X: {0:+0.00;-0.00;0.00} cm", userRel.x),
                    BuildMaxDoseLabel(sagDose),
                    typeface);

                // --- frontal (unten links) ---
                DrawPlanarView(dc, q3,
                    string.Format("{0}{1} - Frontal - {2}", planId, titleSuffix, seriesId),
                    froBmp,
                    image.XSize * image.XRes, image.ZSize * image.ZRes,
                    froDisplay,
                    (innerDc, penScale) =>
                    {
                        if (froDose != null)
                        {
                            DrawIsodoses(innerDc, froDose, planningItem, template, penScale, (u, v) =>
                            {
                                VVector p = DoseGridToPatient(dose, u, v, jFix, PlaneAxis.Frontal);
                                return new Point(DotFromOrigin(p, image, image.XDirection), DotFromOrigin(p, image, image.ZDirection));
                            });
                        }
                        DrawStructureSegments(innerDc, froSegs, penScale,
                            p => new Point(DotFromOrigin(p, image, image.XDirection), DotFromOrigin(p, image, image.ZDirection)));
                        DrawCrosshair(innerDc, cxMm, czMm, image.XSize * image.XRes, image.ZSize * image.ZRes, penScale, SagPlaneColor, TransPlaneColor);
                    },
                    string.Format(culture, "Y: {0:+0.00;-0.00;0.00} cm", userRel.y),
                    BuildMaxDoseLabel(froDose),
                    typeface);

                // --- BEV des Setup-Felds mit DRR + ZV-Silhouette, sonst Plan-Info-Panel (unten rechts) ---
                bool bevDrawn = false;
                try
                {
                    List<Structure> bevTargets = CollectBevTargetStructures(planningItem as PlanSetup, structureSet, target);
                    bevDrawn = TryDrawSetupFieldBev(dc, q4, planningItem as PlanSetup, bevTargets, ctVolume, typeface, log);
                }
                catch (Exception e)
                {
                    var aggregate = e as AggregateException;
                    Exception root = aggregate != null && aggregate.InnerExceptions.Count > 0
                        ? aggregate.InnerExceptions[0]
                        : (e.InnerException ?? e);
                    if (log != null)
                        log("  BEV-Ansicht fehlgeschlagen: " + root.Message);
                }
                if (!bevDrawn)
                    DrawPlanInfoPanel(dc, q4, patient, planningItem, template, target, typeface);

                // Isodosen-Legende ins transversale Fenster
                RenderUtils.DrawIsodoseLegend(dc, template, iso => SliceRenderer.ResolveIsodoseGy(iso, planningItem), transContent.X + 10, transContent.Y + 8);
            }

            RenderUtils.SaveVisualAsPng(visual, width, height, filename);
        }

        private class StructureSegments
        {
            public Color Color;
            public List<MeshSlicer.Segment> Segments;
        }

        /// <summary>
        /// Relevante PTVs fuer das DRR: alle Strukturen, deren Id "PTV" enthaelt und
        /// die im Plan eine untere Optimierungs-Zielvorgabe (Operator "Lower") haben.
        /// Fallback: die zentrierte Zielstruktur, falls keine Objectives vorliegen
        /// (z. B. nicht optimierte Plaene).
        /// </summary>
        private static List<Structure> CollectBevTargetStructures(PlanSetup planSetup, StructureSet structureSet, Structure fallbackTarget)
        {
            var result = new List<Structure>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (planSetup != null && structureSet != null)
            {
                try
                {
                    object optSetup = ReflectionUtils.GetPropertyValue(planSetup, "OptimizationSetup");
                    foreach (object objective in ReflectionUtils.GetEnumerableProperty(optSetup, "Objectives"))
                    {
                        string op = ReflectionUtils.GetStringProperty(objective, "Operator");
                        if (op.IndexOf("Lower", StringComparison.OrdinalIgnoreCase) < 0)
                            continue;

                        string structureId = ReflectionUtils.FirstNonEmpty(
                            ReflectionUtils.GetStringProperty(objective, "StructureId"),
                            ReflectionUtils.GetNestedStringProperty(objective, "Structure", "Id"));
                        if (string.IsNullOrEmpty(structureId))
                            continue;
                        if (structureId.IndexOf("PTV", StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        if (!seen.Add(structureId))
                            continue;

                        Structure s = structureSet.Structures.FirstOrDefault(x =>
                            !x.IsEmpty && x.Id.Equals(structureId, StringComparison.OrdinalIgnoreCase));
                        if (s != null)
                            result.Add(s);
                    }
                }
                catch
                {
                }
            }

            if (result.Count == 0 && fallbackTarget != null && !fallbackTarget.IsEmpty)
                result.Add(fallbackTarget);

            return result;
        }

        private static List<Structure> CollectVisibleStructures(StructureSet structureSet, Structure target, List<string> visibleStructureIds)
        {
            var result = new List<Structure>();
            if (target != null && !target.IsEmpty)
                result.Add(target);

            if (visibleStructureIds != null)
            {
                foreach (string id in visibleStructureIds)
                {
                    if (result.Count >= 8)
                        break;
                    Structure s = structureSet.Structures.FirstOrDefault(x =>
                        !x.IsEmpty && x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                    if (s != null && !result.Contains(s))
                        result.Add(s);
                }
            }

            return result;
        }

        private static string BuildMaxDoseLabel(double[,] doseMatrix)
        {
            if (doseMatrix == null)
                return "";
            double max = 0;
            foreach (double v in doseMatrix)
                if (v > max) max = v;
            return max > 0 ? string.Format(RenderUtils.Num, "Max: {0:F2} Gy", max) : "";
        }

        private enum PlaneAxis { Transversal, Sagittal, Frontal }

        /// <summary>Dosisgitter-Indizes (zwei variable, ein fixer) -> Patientenkoordinate (mm).</summary>
        private static VVector DoseGridToPatient(Dose dose, double u, double v, int fixedIndex, PlaneAxis plane)
        {
            double di, dj, dk;
            switch (plane)
            {
                case PlaneAxis.Sagittal:
                    di = fixedIndex; dj = u; dk = v;
                    break;
                case PlaneAxis.Frontal:
                    di = u; dj = fixedIndex; dk = v;
                    break;
                default:
                    di = u; dj = v; dk = fixedIndex;
                    break;
            }

            double xMm = di * dose.XRes;
            double yMm = dj * dose.YRes;
            double zMm = dk * dose.ZRes;

            return new VVector(
                dose.Origin.x + xMm * dose.XDirection.x + yMm * dose.YDirection.x + zMm * dose.ZDirection.x,
                dose.Origin.y + xMm * dose.XDirection.y + yMm * dose.YDirection.y + zMm * dose.ZDirection.y,
                dose.Origin.z + xMm * dose.XDirection.z + yMm * dose.YDirection.z + zMm * dose.ZDirection.z);
        }

        private static void DrawIsodoses(DrawingContext dc, double[,] doseMatrix, PlanningItem planningItem, ReportTemplate template, double penScale, Func<double, double, Point> map)
        {
            foreach (var iso in template.Isodoses)
            {
                double doseGy = SliceRenderer.ResolveIsodoseGy(iso, planningItem);
                if (doseGy <= 0)
                    continue;

                double thickness = Math.Max(0.6, iso.Thickness) / penScale;
                Pen pen = new Pen(new SolidColorBrush(iso.Color), thickness);
                RenderUtils.DrawIsoLines(dc, doseMatrix, doseGy, pen, map);
            }
        }

        private static void DrawStructureSegments(DrawingContext dc, List<StructureSegments> structures, double penScale, Func<VVector, Point> project)
        {
            foreach (var s in structures)
            {
                if (s.Segments == null || s.Segments.Count == 0)
                    continue;

                Pen pen = new Pen(new SolidColorBrush(s.Color), 1.4 / penScale);
                foreach (var segment in s.Segments)
                    dc.DrawLine(pen, project(segment.A), project(segment.B));
            }
        }

        private static void DrawCrosshair(DrawingContext dc, double xMm, double yMm, double wMm, double hMm, double penScale, Color verticalColor, Color horizontalColor)
        {
            var verticalPen = new Pen(new SolidColorBrush(Color.FromArgb(170, verticalColor.R, verticalColor.G, verticalColor.B)), 0.8 / penScale)
            {
                DashStyle = new DashStyle(new double[] { 6, 6 }, 0)
            };
            var horizontalPen = new Pen(new SolidColorBrush(Color.FromArgb(170, horizontalColor.R, horizontalColor.G, horizontalColor.B)), 0.8 / penScale)
            {
                DashStyle = new DashStyle(new double[] { 6, 6 }, 0)
            };
            dc.DrawLine(horizontalPen, new Point(0, yMm), new Point(wMm, yMm));
            dc.DrawLine(verticalPen, new Point(xMm, 0), new Point(xMm, hMm));
        }

        /// <summary>
        /// Zeichnet einen Viewport: Titelzeile, Bild (mm-Koordinaten, eingepasst und nach
        /// Anzeige-Konvention gespiegelt), Overlay, Orientierungslabels, Lineal, Max-Dosis,
        /// Positionslabel und Orientierungsfigur. Liefert das Inhalts-Rechteck zurueck.
        /// </summary>
        private static Rect DrawPlanarView(
            DrawingContext dc,
            Rect viewport,
            string title,
            BitmapSource bitmap,
            double contentWmm,
            double contentHmm,
            RenderUtils.DisplayTransform displayTransform,
            Action<DrawingContext, double> drawOverlayInMm,
            string positionLabel,
            string maxDoseLabel,
            Typeface typeface)
        {
            double titleHeight = 26;
            Pen borderPen = new Pen(Theme.SeparatorLine, 1.0);

            // Titelzeile
            Rect titleRect = new Rect(viewport.X, viewport.Y, viewport.Width, titleHeight);
            dc.DrawRectangle(Theme.ViewportTitleFill, null, titleRect);
            RenderUtils.DrawTextFit(dc, title, viewport.X + 8, viewport.Y + 5, viewport.Width - 16, 12, Brushes.White, typeface, RenderUtils.Num);

            // CT-/DRR-Inhalt bleibt auf schwarzem Grund (Graustufenbild)
            Rect content = new Rect(viewport.X, viewport.Y + titleHeight, viewport.Width, viewport.Height - titleHeight);
            dc.DrawRectangle(Theme.ImageBackground, null, content);

            double displayWmm = displayTransform.SwapsAxes ? contentHmm : contentWmm;
            double displayHmm = displayTransform.SwapsAxes ? contentWmm : contentHmm;
            double s = Math.Min(content.Width / displayWmm, content.Height / displayHmm);
            double offsetX = content.X + (content.Width - displayWmm * s) / 2.0;
            double offsetY = content.Y + (content.Height - displayHmm * s) / 2.0;

            dc.PushClip(new RectangleGeometry(content));
            dc.PushTransform(new TranslateTransform(offsetX, offsetY));
            dc.PushTransform(new ScaleTransform(s, s));
            dc.PushTransform(new MatrixTransform(RenderUtils.GetNaturalToDisplayMatrix(displayTransform, contentWmm, contentHmm)));

            dc.DrawImage(bitmap, new Rect(0, 0, contentWmm, contentHmm));
            if (drawOverlayInMm != null)
                drawOverlayInMm(dc, s);

            dc.Pop(); // flip
            dc.Pop(); // scale
            dc.Pop(); // translate
            dc.Pop(); // clip

            dc.DrawRectangle(null, borderPen, viewport);

            // Orientierungslabels
            double fontSize = RenderUtils.ScaleSliceFont(16);
            var topText = RenderUtils.CreateFormattedText(displayTransform.TopLabel, fontSize, Brushes.White, typeface, RenderUtils.Num);
            dc.DrawText(topText, new Point(content.X + content.Width / 2.0 - topText.Width / 2.0, content.Y + 4));

            var bottomText = RenderUtils.CreateFormattedText(displayTransform.BottomLabel, fontSize, Brushes.White, typeface, RenderUtils.Num);
            dc.DrawText(bottomText, new Point(content.X + content.Width / 2.0 - bottomText.Width / 2.0, content.Y + content.Height - bottomText.Height - 4));

            var leftText = RenderUtils.CreateFormattedText(displayTransform.LeftLabel, fontSize, Brushes.White, typeface, RenderUtils.Num);
            dc.DrawText(leftText, new Point(content.X + 6, content.Y + content.Height / 2.0 - leftText.Height / 2.0));

            var rightText = RenderUtils.CreateFormattedText(displayTransform.RightLabel, fontSize, Brushes.White, typeface, RenderUtils.Num);
            dc.DrawText(rightText, new Point(content.X + content.Width - rightText.Width - 6, content.Y + content.Height / 2.0 - rightText.Height / 2.0));

            // Max-Dosis (rot, oben rechts)
            if (!string.IsNullOrEmpty(maxDoseLabel))
            {
                var maxText = RenderUtils.CreateFormattedText(maxDoseLabel, RenderUtils.ScaleSliceFont(12), Brushes.Red, typeface, RenderUtils.Num);
                dc.DrawText(maxText, new Point(content.X + content.Width - maxText.Width - 8, content.Y + 6));
            }

            // Lineal (unten, weiss)
            double pxPerCm = s * 10.0;
            if (pxPerCm > 9)
            {
                double rulerX = content.X + 64;
                double rulerLength = content.Width - 150;
                RenderUtils.DrawRulerH(dc, rulerX, content.Y + content.Height - 22, rulerLength, pxPerCm, Brushes.White, typeface);
            }

            // Positionslabel (rot, unten links ueber dem Maennchen)
            if (!string.IsNullOrEmpty(positionLabel))
            {
                var posText = RenderUtils.CreateFormattedText(positionLabel, RenderUtils.ScaleSliceFont(12), Brushes.Red, typeface, RenderUtils.Num);
                dc.DrawText(posText, new Point(content.X + 8, content.Y + content.Height - posText.Height - 64));
            }

            // Orientierungsfigur
            RenderUtils.DrawManikin(dc, content.X + 10, content.Y + content.Height - 56, 46);

            return content;
        }

        // ---------- BEV des Setup-Felds (DRR + ZV-Silhouette + Feldrahmen) ----------

        /// <summary>
        /// Beam's-Eye-View des ersten Setup-Felds: DRR (Beam.ReferenceImage) als
        /// Hintergrund, Zielvolumen als divergent projizierte Mesh-Silhouette,
        /// Jaw-Feldrahmen zur Lagekontrolle. Liefert false, wenn kein Setup-Feld
        /// vorhanden ist (dann bleibt das Plan-Info-Panel stehen).
        /// HINWEIS: Vorzeichen-/Orientierungskonventionen am Eclipse-Rechner anhand
        /// des Feldrahmens verifizieren (wie bei HFS/FFS-Spiegelung).
        /// </summary>
        /// <summary>true, wenn kein Feld des Plans ein gespeichertes DRR hat (nur "Live"-DRRs).</summary>
        private static bool NeedsSyntheticDrr(PlanSetup planSetup)
        {
            if (planSetup == null)
                return false;
            try
            {
                var beams = planSetup.Beams.ToList();
                return beams.Count > 0 && beams.All(b => GetReferenceImage(b) == null);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryDrawSetupFieldBev(
            DrawingContext dc,
            Rect viewport,
            PlanSetup planSetup,
            List<Structure> targets,
            CtVolume ctVolume,
            Typeface typeface,
            Action<string> log)
        {
            if (planSetup == null)
                return false;

            Beam beam;
            try
            {
                List<Beam> allBeams = planSetup.Beams.ToList();

                // Diagnose: DRR-Status aller Felder ins Log
                if (log != null)
                {
                    foreach (Beam candidate in allBeams)
                    {
                        Image refImage = GetReferenceImage(candidate);
                        log(string.Format("  BEV-Diagnose: Feld '{0}' Setup={1} DRR={2}",
                            candidate.Id,
                            candidate.IsSetupField ? "ja" : "nein",
                            refImage != null
                                ? string.Format("{0}x{1}", refImage.XSize, refImage.YSize)
                                : "keins"));
                    }
                }

                // Setup-Felder bevorzugt: erst solche mit DRR, CBCT zuletzt (hat nie ein DRR)
                beam = allBeams
                    .Where(x => x.IsSetupField)
                    .OrderByDescending(x => GetReferenceImage(x) != null)
                    .ThenBy(x => x.Id.IndexOf("CBCT", StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 0)
                    .ThenBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();

                // Fallback: hat kein Setup-Feld ein DRR, aber ein Behandlungsfeld -> dieses nehmen
                if (beam != null && GetReferenceImage(beam) == null)
                {
                    Beam treatmentWithDrr = allBeams
                        .Where(x => !x.IsSetupField && GetReferenceImage(x) != null)
                        .OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                        .FirstOrDefault();
                    if (treatmentWithDrr != null)
                    {
                        if (log != null)
                            log(string.Format("  BEV: Setup-Felder ohne DRR - nutze Behandlungsfeld '{0}' mit DRR.", treatmentWithDrr.Id));
                        beam = treatmentWithDrr;
                    }
                }
            }
            catch
            {
                beam = null;
            }
            if (beam == null)
                return false;

            double gantryDeg = 0, collDeg = 0, couchDeg = 0;
            try
            {
                gantryDeg = beam.ControlPoints[0].GantryAngle;
                collDeg = beam.ControlPoints[0].CollimatorAngle;
                couchDeg = beam.ControlPoints[0].PatientSupportAngle;
            }
            catch
            {
            }

            VVector iso;
            try
            {
                iso = beam.IsocenterPosition;
            }
            catch
            {
                return false;
            }

            double sad = ReflectionUtils.GetNumericMember(ReflectionUtils.GetPropertyValue(beam, "TreatmentUnit"), "SourceAxisDistance") ?? 1000.0;
            if (sad < 200 || double.IsNaN(sad))
                sad = 1000.0;

            if (Math.Abs(NormalizeAngle180(couchDeg)) > 0.5 && log != null)
                log(string.Format(RenderUtils.Num, "  BEV: Tischrotation {0:F1}° wird nicht beruecksichtigt.", couchDeg));

            Image drr = GetReferenceImage(beam);
            bool forceSyntheticGantryZero = drr == null && ctVolume != null;
            double projectionGantryDeg = forceSyntheticGantryZero ? 0.0 : gantryDeg;
            if (forceSyntheticGantryZero && log != null && Math.Abs(NormalizeAngle180(gantryDeg)) > 0.5)
                log(string.Format(RenderUtils.Num, "  BEV: synthetischer DRR wird bei Gantry 0.0° gerendert (Feldgantry {0:F1}°).", gantryDeg));

            // Quellposition und BEV-Achsen in DICOM-Patientenkoordinaten.
            // HFS: Gantry 0 = Quelle anterior (y-), Gantry 90 = Quelle links (x+).
            // Feet-first und prone drehen die laterale BEV-Anzeige gegenueber HFS.
            string orientationName = ReflectionUtils.GetStringProperty(planSetup, "TreatmentOrientation");
            if (orientationName == null)
                orientationName = "";
            string positionCode = RenderUtils.GetPatientPositionCode(orientationName);
            bool flipLateralAxis = orientationName.StartsWith("FeetFirst", StringComparison.OrdinalIgnoreCase) ||
                                   RenderUtils.IsPronePosition(positionCode);
            double xSign = flipLateralAxis ? -1.0 : 1.0;
            double g = projectionGantryDeg * Math.PI / 180.0;

            VVector source = new VVector(iso.x + xSign * sad * Math.Sin(g), iso.y - sad * Math.Cos(g), iso.z);
            VVector bAxis = NormalizeVec(new VVector(iso.x - source.x, iso.y - source.y, iso.z - source.z));
            VVector uAxis = NormalizeVec(new VVector(xSign * Math.Cos(g), Math.Sin(g), 0));
            VVector vAxis = CrossVec(uAxis, bAxis); // zeigt fuer HFS nach kranial
            if (RenderUtils.UsesMirroredDisplayHandedness(positionCode))
                vAxis = RenderUtils.Negate(vAxis);
            if (log != null)
                log(string.Format("  BEV: Orientierung={0}, laterale Achse rechts={1}, vertikale Achse oben={2}.",
                    string.IsNullOrEmpty(positionCode) ? orientationName : positionCode,
                    RenderUtils.GetLabelForDirection(uAxis),
                    RenderUtils.GetLabelForDirection(vAxis)));

            // ---- DRR laden (falls in Eclipse erzeugt) ----
            BitmapSource background = null;
            double wMm = 0, hMm = 0;
            Func<VVector, Point> project = null;

            if (drr != null)
            {
                try
                {
                    int nx = drr.XSize, ny = drr.YSize;
                    if (nx > 4 && ny > 4)
                    {
                        int[,] buffer = new int[nx, ny];
                        drr.GetVoxels(0, buffer);

                        double[,] values = new double[nx, ny];
                        double min = double.MaxValue, max = double.MinValue;
                        for (int c = 0; c < nx; c++)
                        {
                            for (int r = 0; r < ny; r++)
                            {
                                double value = drr.VoxelToDisplayValue(buffer[c, r]);
                                values[c, r] = value;
                                if (value < min) min = value;
                                if (value > max) max = value;
                            }
                        }
                        if (max <= min)
                            max = min + 1;

                        background = RenderUtils.MakeGrayBitmap(nx, ny, (c, r) => values[c, r], (min + max) / 2.0, max - min);
                        wMm = nx * drr.XRes;
                        hMm = ny * drr.YRes;

                        // Exakte Projektion auf die DRR-Bildebene, sofern deren Geometrie
                        // plausibel im Patientensystem liegt (Normale ~ Strahlachse).
                        VVector origin = drr.Origin;
                        VVector xDir = NormalizeVec(drr.XDirection);
                        VVector yDir = NormalizeVec(drr.YDirection);
                        VVector normal = CrossVec(xDir, yDir);

                        if (Math.Abs(DotVec(normal, bAxis)) > 0.5)
                        {
                            double halfPxX = drr.XRes * 0.5;
                            double halfPxY = drr.YRes * 0.5;
                            project = p =>
                            {
                                VVector d = new VVector(p.x - source.x, p.y - source.y, p.z - source.z);
                                double denom = DotVec(d, normal);
                                if (Math.Abs(denom) < 1e-9)
                                    denom = 1e-9;
                                double t = DotVec(new VVector(origin.x - source.x, origin.y - source.y, origin.z - source.z), normal) / denom;
                                if (t < 0.05)
                                    t = 0.05;
                                VVector q = new VVector(source.x + d.x * t, source.y + d.y * t, source.z + d.z * t);
                                VVector rel = new VVector(q.x - origin.x, q.y - origin.y, q.z - origin.z);
                                return new Point(DotVec(rel, xDir) + halfPxX, DotVec(rel, yDir) + halfPxY);
                            };
                        }
                        else if (log != null)
                        {
                            log("  BEV: DRR-Ebenengeometrie unplausibel - nutze BEV-Naeherung (Isozentrumsebene).");
                        }
                    }
                }
                catch (Exception e)
                {
                    background = null;
                    if (log != null)
                        log("  BEV: DRR konnte nicht gelesen werden: " + e.Message);
                }
            }

            // ---- Synthetisches DRR per Raycasting durchs Planungs-CT ----
            // (Eclipse-"Live"-DRRs sind ueber ESAPI nicht als Bild verfuegbar.)
            bool syntheticDrr = false;
            if (background == null && ctVolume != null)
            {
                try
                {
                    var drrWatch = System.Diagnostics.Stopwatch.StartNew();
                    wMm = 420;
                    hMm = 420;
                    background = ComputeSyntheticDrr(ctVolume, source, iso, uAxis, vAxis, wMm, hMm, 384, 384);
                    syntheticDrr = background != null;
                    if (syntheticDrr && log != null)
                        log(string.Format(RenderUtils.Num, "  BEV: DRR fuer Feld {0} aus dem CT berechnet ({1:F1} s, Eclipse-DRR ist 'Live').", beam.Id, drrWatch.Elapsed.TotalSeconds));
                }
                catch (Exception e)
                {
                    background = null;
                    if (log != null)
                        log("  BEV: synthetisches DRR fehlgeschlagen: " + e.Message);
                }
            }

            // ---- Fallback: BEV-Koordinaten in der Isozentrumsebene ----
            if (project == null)
            {
                if (background == null)
                {
                    wMm = 320;
                    hMm = 320;
                }
                double cxMm = wMm / 2.0;
                double cyMm = hMm / 2.0;
                project = p =>
                {
                    VVector r = new VVector(p.x - iso.x, p.y - iso.y, p.z - iso.z);
                    double depth = DotVec(r, bAxis);
                    double a = DotVec(r, uAxis);
                    double e = DotVec(r, vAxis);
                    double k = sad / Math.Max(50, sad + depth);
                    return new Point(cxMm + a * k, cyMm - e * k);
                };
            }

            if (background == null)
            {
                // schwarzer Hintergrund, wenn kein DRR existiert
                background = RenderUtils.MakeGrayBitmap(2, 2, (c, r) => 0, 0, 1);
                if (log != null)
                    log(string.Format("  BEV: kein DRR fuer Setup-Feld {0} in Eclipse - zeichne nur Geometrie.", beam.Id));
            }

            // ---- Projizierte Umrisse aller relevanten PTVs (hochaufgeloeste Masken) ----
            double maskPxPerMm = 2.0; // 0.5 mm Aufloesung
            var outlines = new List<KeyValuePair<Color, double[,]>>();
            foreach (Structure structure in targets ?? new List<Structure>())
            {
                if (structure == null || structure.IsEmpty)
                    continue;
                double[,] mask = BuildBevOutlineMask(structure, project, wMm, hMm, maskPxPerMm);
                if (mask != null)
                    outlines.Add(new KeyValuePair<Color, double[,]>(
                        Color.FromRgb(structure.Color.R, structure.Color.G, structure.Color.B), mask));
                else if (log != null)
                    log(string.Format("  BEV: kein Umriss fuer {0} (MeshGeometry nicht verfuegbar?).", structure.Id));
            }

            // ---- Jaw-Feldrahmen (an der Isozentrumsebene, mit Kollimatorrotation) ----
            var jawCorners = BuildJawCorners(beam, iso, uAxis, vAxis, collDeg)
                .Select(project)
                .ToList();

            string drrLabel = drr != null ? " - DRR" : (syntheticDrr ? " - DRR (berechnet)" : " - kein DRR");
            string title = string.Format(RenderUtils.Num, "BEV {0}{1} - Gantry {2:F1}°{3}",
                beam.IsSetupField ? "Setup-Feld " : "Feld ",
                beam.Id, projectionGantryDeg, drrLabel);
            string ptvList = targets != null && targets.Count > 0
                ? "   ZV: " + string.Join(", ", targets.Where(t => t != null).Select(t => t.Id))
                : "";
            string positionLabel = string.Format(RenderUtils.Num, "Koll: {0:F1}°{1}", collDeg, ptvList);

            var bevDisplay = new RenderUtils.DisplayTransform
            {
                RightAxis = 0,
                RightSign = 1,
                DownAxis = 1,
                DownSign = 1,
                TopLabel = "",
                BottomLabel = "",
                LeftLabel = "",
                RightLabel = ""
            };

            DrawPlanarView(dc, viewport, title, background, wMm, hMm, bevDisplay,
                (innerDc, penScale) =>
                {
                    // Feldrahmen (gelb, wie Eclipse-Feldumriss)
                    if (jawCorners.Count == 4)
                    {
                        Pen jawPen = new Pen(Brushes.Yellow, 1.2 / penScale);
                        for (int i = 0; i < 4; i++)
                            innerDc.DrawLine(jawPen, jawCorners[i], jawCorners[(i + 1) % 4]);
                    }

                    // Fadenkreuz auf dem Zentralstrahl
                    Point axis = project(iso);
                    Pen crossPen = new Pen(new SolidColorBrush(Color.FromArgb(190, 255, 255, 0)), 0.7 / penScale);
                    innerDc.DrawLine(crossPen, new Point(axis.X - 12, axis.Y), new Point(axis.X + 12, axis.Y));
                    innerDc.DrawLine(crossPen, new Point(axis.X, axis.Y - 12), new Point(axis.X, axis.Y + 12));

                    // PTV-Umrisse: glatte Isolinien auf den hochaufgeloesten Projektionsmasken
                    foreach (var outline in outlines)
                    {
                        Pen targetPen = new Pen(new SolidColorBrush(outline.Key), 1.4 / penScale);
                        RenderUtils.DrawIsoLines(innerDc, outline.Value, 0.5, targetPen,
                            (u, v) => new Point((u + 0.5) / maskPxPerMm, (v + 0.5) / maskPxPerMm));
                    }
                },
                positionLabel,
                "",
                typeface);

            return true;
        }

        /// <summary>
        /// In x/y halbiertes CT-Volumen (HU als short) fuer das DRR-Raycasting.
        /// Wird waehrend des ohnehin noetigen Voxel-Durchlaufs befuellt.
        /// </summary>
        private class CtVolume
        {
            public readonly short[] Data;
            public readonly int Nx, Ny, Nz;
            public readonly double ResX, ResY, ResZ;
            public readonly VVector Origin, XDir, YDir, ZDir;
            private readonly double huIntercept;
            private readonly double huSlope;

            public CtVolume(Image image)
            {
                // volle Aufloesung - das halbe Volumen machte das DRR sichtbar unscharf
                Nx = image.XSize;
                Ny = image.YSize;
                Nz = image.ZSize;
                ResX = image.XRes;
                ResY = image.YRes;
                ResZ = image.ZRes;
                Origin = image.Origin;
                XDir = image.XDirection;
                YDir = image.YDirection;
                ZDir = image.ZDirection;
                Data = new short[(long)Nx * Ny * Nz];

                // VoxelToDisplayValue ist linear -> einmal Steigung/Achsenabschnitt
                // bestimmen statt Millionen Methodenaufrufe im Voxel-Loop.
                huIntercept = image.VoxelToDisplayValue(0);
                huSlope = image.VoxelToDisplayValue(1000) - huIntercept;
                huSlope /= 1000.0;
            }

            public void FillPlane(Image image, int z, int[,] buffer)
            {
                int planeOffset = z * Nx * Ny;
                for (int yi = 0; yi < Ny; yi++)
                {
                    int rowOffset = planeOffset + yi * Nx;
                    for (int xi = 0; xi < Nx; xi++)
                    {
                        double hu = huIntercept + huSlope * buffer[xi, yi];
                        if (hu < short.MinValue) hu = short.MinValue;
                        if (hu > short.MaxValue) hu = short.MaxValue;
                        Data[rowOffset + xi] = (short)hu;
                    }
                }
            }
        }

        /// <summary>
        /// Synthetisches DRR: Raycasting vom Strahlfokus durch das CT-Volumen auf
        /// die Isozentrumsebene (radiologische Weglaenge, Knochen erscheinen hell).
        /// </summary>
        private static BitmapSource ComputeSyntheticDrr(
            CtVolume vol, VVector source, VVector iso, VVector uAxis, VVector vAxis,
            double wMm, double hMm, int px, int py)
        {
            double[] pathLength = new double[px * py];
            double stepMm = 1.5;

            // Quellposition in Volumen-Indexkoordinaten (linear entlang des Strahls)
            VVector srcRel = new VVector(source.x - vol.Origin.x, source.y - vol.Origin.y, source.z - vol.Origin.z);
            double sx = DotVec(srcRel, vol.XDir) / vol.ResX;
            double sy = DotVec(srcRel, vol.YDir) / vol.ResY;
            double sz = DotVec(srcRel, vol.ZDir) / vol.ResZ;

            short[] data = vol.Data;
            int nx = vol.Nx, ny = vol.Ny, nz = vol.Nz;

            System.Threading.Tasks.Parallel.For(0, py, r =>
            {
                double e = hMm / 2.0 - (r + 0.5) / py * hMm;
                for (int c = 0; c < px; c++)
                {
                    double a = (c + 0.5) / px * wMm - wMm / 2.0;

                    // Zielpunkt in der Isozentrumsebene, Strahl von der Quelle dorthin
                    VVector p = new VVector(
                        iso.x + a * uAxis.x + e * vAxis.x,
                        iso.y + a * uAxis.y + e * vAxis.y,
                        iso.z + a * uAxis.z + e * vAxis.z);
                    VVector d = new VVector(p.x - source.x, p.y - source.y, p.z - source.z);
                    double rayLen = Math.Sqrt(d.x * d.x + d.y * d.y + d.z * d.z);
                    if (rayLen < 1)
                        continue;

                    // Strahl in Indexkoordinaten: idx(t) = s + g * t
                    double gx = DotVec(d, vol.XDir) / vol.ResX;
                    double gy = DotVec(d, vol.YDir) / vol.ResY;
                    double gz = DotVec(d, vol.ZDir) / vol.ResZ;

                    // Schnitt mit dem Volumenquader (Slab-Methode): nur dort abtasten,
                    // wo der Strahl das CT tatsaechlich durchlaeuft.
                    double tMin = 0.02, tMax = 1.98;
                    if (!ClipRayToBox(sx, gx, nx, ref tMin, ref tMax) ||
                        !ClipRayToBox(sy, gy, ny, ref tMin, ref tMax) ||
                        !ClipRayToBox(sz, gz, nz, ref tMin, ref tMax) ||
                        tMax <= tMin)
                        continue;

                    double tStep = stepMm / rayLen;
                    int steps = (int)((tMax - tMin) / tStep);
                    double ix = sx + gx * tMin;
                    double iy = sy + gy * tMin;
                    double iz = sz + gz * tMin;
                    double dix = gx * tStep, diy = gy * tStep, diz = gz * tStep;

                    double acc = 0;
                    for (int s = 0; s < steps; s++)
                    {
                        double hu = SampleTrilinear(data, nx, ny, nz, ix, iy, iz);
                        if (hu > -950)
                        {
                            // wasseraequivalente Dichte plus kV-typische Knochenbetonung
                            double weight = 1000.0 + hu;
                            if (hu > 100)
                                weight += (hu - 100) * 4.0;
                            acc += weight;
                        }
                        ix += dix;
                        iy += diy;
                        iz += diz;
                    }

                    pathLength[r * px + c] = acc * stepMm / 1000.0;
                }
            });

            // Fensterung ueber Perzentile (2% / 99.5%) statt Min/Max - sonst druecken
            // einzelne Extremstrahlen (Metall, Tischkante) den Kontrast weg.
            double[] sorted = (double[])pathLength.Clone();
            Array.Sort(sorted);
            double lo = sorted[(int)(sorted.Length * 0.02)];
            double hi = sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * 0.995))];
            if (hi <= lo)
                return null;

            // Gamma 0.6 hebt die Mitteltoene (weiches Gewebe) wie im Eclipse-DRR an
            return RenderUtils.MakeGrayBitmap(px, py, (c, r) =>
            {
                double norm = (pathLength[r * px + c] - lo) / (hi - lo);
                if (norm < 0) norm = 0;
                if (norm > 1) norm = 1;
                return Math.Pow(norm, 0.6);
            }, 0.5, 1.0);
        }

        /// <summary>Trilineare Interpolation im HU-Volumen (-1000 ausserhalb).</summary>
        private static double SampleTrilinear(short[] data, int nx, int ny, int nz, double x, double y, double z)
        {
            if (x < 0 || y < 0 || z < 0 || x > nx - 1 || y > ny - 1 || z > nz - 1)
                return -1000;

            int x0 = (int)x, y0 = (int)y, z0 = (int)z;
            if (x0 > nx - 2) x0 = nx - 2;
            if (y0 > ny - 2) y0 = ny - 2;
            if (z0 > nz - 2) z0 = nz - 2;
            double fx = x - x0, fy = y - y0, fz = z - z0;

            int i000 = (z0 * ny + y0) * nx + x0;
            int i100 = i000 + 1;
            int i010 = i000 + nx;
            int i110 = i010 + 1;
            int i001 = i000 + nx * ny;
            int i101 = i001 + 1;
            int i011 = i001 + nx;
            int i111 = i011 + 1;

            double c00 = data[i000] + (data[i100] - data[i000]) * fx;
            double c10 = data[i010] + (data[i110] - data[i010]) * fx;
            double c01 = data[i001] + (data[i101] - data[i001]) * fx;
            double c11 = data[i011] + (data[i111] - data[i011]) * fx;

            double c0 = c00 + (c10 - c00) * fy;
            double c1 = c01 + (c11 - c01) * fy;

            return c0 + (c1 - c0) * fz;
        }

        /// <summary>
        /// Slab-Clipping einer Achse: schraenkt [tMin, tMax] auf den Bereich ein,
        /// in dem idx(t) = start + g*t innerhalb [0, size-1] liegt.
        /// </summary>
        private static bool ClipRayToBox(double start, double g, int size, ref double tMin, ref double tMax)
        {
            if (Math.Abs(g) < 1e-12)
                return start >= 0 && start <= size - 1;

            double t0 = (0 - start) / g;
            double t1 = (size - 1 - start) / g;
            if (t0 > t1)
            {
                double swap = t0;
                t0 = t1;
                t1 = swap;
            }
            if (t0 > tMin) tMin = t0;
            if (t1 < tMax) tMax = t1;
            return tMax > tMin;
        }

        /// <summary>
        /// DRR des Felds: direkt ueber Beam.ReferenceImage, bei aelteren/anderen
        /// ESAPI-Versionen per Reflection - liefert null, wenn keins existiert.
        /// </summary>
        private static Image GetReferenceImage(Beam beam)
        {
            try
            {
                Image image = beam.ReferenceImage;
                if (image != null)
                    return image;
            }
            catch
            {
            }
            try
            {
                return ReflectionUtils.GetPropertyValue(beam, "ReferenceImage") as Image;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Eckpunkte des Jaw-Felds in Patientenkoordinaten (Isozentrumsebene, Kollimator beruecksichtigt).</summary>
        private static List<VVector> BuildJawCorners(Beam beam, VVector iso, VVector uAxis, VVector vAxis, double collDeg)
        {
            var corners = new List<VVector>();
            try
            {
                VRect<double> jaws = beam.ControlPoints[0].JawPositions;
                double cosC = Math.Cos(collDeg * Math.PI / 180.0);
                double sinC = Math.Sin(collDeg * Math.PI / 180.0);

                var bevCorners = new[]
                {
                    new Point(jaws.X1, jaws.Y1),
                    new Point(jaws.X2, jaws.Y1),
                    new Point(jaws.X2, jaws.Y2),
                    new Point(jaws.X1, jaws.Y2)
                };

                foreach (Point c in bevCorners)
                {
                    double a = c.X * cosC - c.Y * sinC;
                    double e = c.X * sinC + c.Y * cosC;
                    corners.Add(new VVector(
                        iso.x + a * uAxis.x + e * vAxis.x,
                        iso.y + a * uAxis.y + e * vAxis.y,
                        iso.z + a * uAxis.z + e * vAxis.z));
                }
            }
            catch
            {
                corners.Clear();
            }
            return corners;
        }

        /// <summary>
        /// Projektionsmaske des Strukturmeshes im BEV: alle Dreiecke werden divergent
        /// projiziert und in eine hochaufgeloeste Maske gerastert; deren 0.5-Isolinie
        /// (Marching Squares auf der weichgezeichneten Maske) ergibt einen glatten,
        /// geschlossenen Umriss ohne Mesh-Artefakte.
        /// </summary>
        private static double[,] BuildBevOutlineMask(Structure structure, Func<VVector, Point> project, double wMm, double hMm, double pxPerMm)
        {
            MeshGeometry3D mesh = null;
            try
            {
                mesh = structure.MeshGeometry;
            }
            catch
            {
            }
            if (mesh == null || mesh.Positions == null || mesh.TriangleIndices == null || mesh.TriangleIndices.Count < 3)
                return null;

            int mw = Math.Max(8, (int)(wMm * pxPerMm));
            int mh = Math.Max(8, (int)(hMm * pxPerMm));
            byte[] mask = new byte[mw * mh];

            var positions = mesh.Positions;
            // WPF-Mesh-Collections sind thread-gebunden -> vor dem Parallel.For
            // in ein normales Array kopieren, sonst InvalidOperationException.
            int[] indices = mesh.TriangleIndices.ToArray();
            int triangleCount = indices.Length / 3;

            // Eckpunkte einmal projizieren (Bild-mm -> Maskenpixel)
            var projected = new Point[positions.Count];
            for (int i = 0; i < positions.Count; i++)
            {
                Point3D p = positions[i];
                Point q = project(new VVector(p.X, p.Y, p.Z));
                projected[i] = new Point(q.X * pxPerMm, q.Y * pxPerMm);
            }

            // Dreiecke fuellen (parallel; konkurrierende Schreibzugriffe setzen
            // identische Werte und sind daher unkritisch)
            System.Threading.Tasks.Parallel.For(0, triangleCount, t =>
            {
                Point a = projected[indices[3 * t]];
                Point b = projected[indices[3 * t + 1]];
                Point c = projected[indices[3 * t + 2]];

                int minX = Math.Max(0, (int)Math.Floor(Math.Min(a.X, Math.Min(b.X, c.X))));
                int maxX = Math.Min(mw - 1, (int)Math.Ceiling(Math.Max(a.X, Math.Max(b.X, c.X))));
                int minY = Math.Max(0, (int)Math.Floor(Math.Min(a.Y, Math.Min(b.Y, c.Y))));
                int maxY = Math.Min(mh - 1, (int)Math.Ceiling(Math.Max(a.Y, Math.Max(b.Y, c.Y))));
                if (minX > maxX || minY > maxY)
                    return;

                double area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
                if (Math.Abs(area) < 1e-9)
                    return;

                for (int y = minY; y <= maxY; y++)
                {
                    double py = y + 0.5;
                    for (int x = minX; x <= maxX; x++)
                    {
                        double px = x + 0.5;
                        double w0 = ((b.X - a.X) * (py - a.Y) - (b.Y - a.Y) * (px - a.X)) / area;
                        double w1 = ((c.X - b.X) * (py - b.Y) - (c.Y - b.Y) * (px - b.X)) / area;
                        double w2 = ((a.X - c.X) * (py - c.Y) - (a.Y - c.Y) * (px - c.X)) / area;
                        if (w0 >= 0 && w1 >= 0 && w2 >= 0)
                            mask[y * mw + x] = 1;
                    }
                }
            });

            // 3x3-Boxfilter: macht die Marching-Squares-Isolinie subpixelglatt
            double[,] smooth = new double[mw, mh];
            for (int y = 1; y < mh - 1; y++)
            {
                for (int x = 1; x < mw - 1; x++)
                {
                    int sum =
                        mask[(y - 1) * mw + x - 1] + mask[(y - 1) * mw + x] + mask[(y - 1) * mw + x + 1] +
                        mask[y * mw + x - 1] + mask[y * mw + x] + mask[y * mw + x + 1] +
                        mask[(y + 1) * mw + x - 1] + mask[(y + 1) * mw + x] + mask[(y + 1) * mw + x + 1];
                    smooth[x, y] = sum / 9.0;
                }
            }

            return smooth;
        }

        private static double NormalizeAngle180(double degrees)
        {
            degrees = degrees % 360.0;
            if (degrees > 180.0) degrees -= 360.0;
            if (degrees < -180.0) degrees += 360.0;
            return degrees;
        }

        private static VVector CrossVec(VVector a, VVector b)
        {
            return new VVector(
                a.y * b.z - a.z * b.y,
                a.z * b.x - a.x * b.z,
                a.x * b.y - a.y * b.x);
        }

        private static double DotVec(VVector a, VVector b)
        {
            return a.x * b.x + a.y * b.y + a.z * b.z;
        }

        private static VVector NormalizeVec(VVector v)
        {
            double length = Math.Sqrt(v.x * v.x + v.y * v.y + v.z * v.z);
            if (length < 1e-9)
                return new VVector(0, 0, 1);
            return new VVector(v.x / length, v.y / length, v.z / length);
        }

        /// <summary>Plan-Info-Panel im Stil des Eclipse-Dosis-Tabs (4. Quadrant).</summary>
        private static void DrawPlanInfoPanel(DrawingContext dc, Rect viewport, Patient patient, PlanningItem planningItem, ReportTemplate template, Structure target, Typeface typeface)
        {
            var culture = RenderUtils.Num;
            var boldTypeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            Pen borderPen = new Pen(Theme.SeparatorLine, 1.0);
            Brush panelBg = Theme.PanelBackground;
            Brush headerBg = Theme.PanelHeaderFill;
            Brush labelBrush = Theme.MutedText;
            Pen rowPen = new Pen(Theme.TableRowLine, 0.8);

            dc.DrawRectangle(panelBg, borderPen, viewport);

            Rect headerRect = new Rect(viewport.X, viewport.Y, viewport.Width, 28);
            dc.DrawRectangle(headerBg, null, headerRect);
            RenderUtils.DrawText(dc, "Dosis", viewport.X + 10, viewport.Y + 5, 14, Brushes.White, boldTypeface, culture);

            PlanSetup planSetup = planningItem as PlanSetup;
            var rows = new List<KeyValuePair<string, string>>();

            rows.Add(new KeyValuePair<string, string>("Plan-ID", PlanPageRenderer.GetPlanningItemId(planningItem)));
            rows.Add(new KeyValuePair<string, string>("Kurs", ReflectionUtils.GetNestedStringProperty(planningItem, "Course", "Id")));
            rows.Add(new KeyValuePair<string, string>("Status", ReflectionUtils.GetStringProperty(planningItem, "ApprovalStatus")));

            if (planSetup != null)
            {
                rows.Add(new KeyValuePair<string, string>("Zielvolumenstruktur", planSetup.TargetVolumeID ?? ""));
                string dosePerFx = "", totalDose = "", fractions = "";
                try
                {
                    dosePerFx = planSetup.DosePerFraction.Dose.ToString("F3", culture);
                    totalDose = planSetup.TotalDose.Dose.ToString("F3", culture);
                    fractions = ReflectionUtils.GetStringProperty(planSetup, "NumberOfFractions");
                }
                catch
                {
                }
                rows.Add(new KeyValuePair<string, string>("Dosis pro Fraktion [Gy]", dosePerFx));
                rows.Add(new KeyValuePair<string, string>("Anzahl Fraktionen", fractions));
                rows.Add(new KeyValuePair<string, string>("Gesamtdosis [Gy]", totalDose));
                rows.Add(new KeyValuePair<string, string>("Normierungsmodus", ReflectionUtils.GetStringProperty(planSetup, "PlanNormalizationMethod")));
                rows.Add(new KeyValuePair<string, string>("Plannormierungswert [%]", ReflectionUtils.FormatNumber(ReflectionUtils.GetPropertyValue(planSetup, "PlanNormalizationValue"), "F1")));
                rows.Add(new KeyValuePair<string, string>("Primärer Referenzpunkt", ReflectionUtils.GetNestedStringProperty(planSetup, "PrimaryReferencePoint", "Id")));
                rows.Add(new KeyValuePair<string, string>("Algorithmus", ReflectionUtils.FirstNonEmpty(
                    ReflectionUtils.GetStringProperty(planSetup, "PhotonCalculationModel"),
                    ReflectionUtils.GetStringProperty(planSetup, "ElectronCalculationModel"),
                    ReflectionUtils.GetStringProperty(planSetup, "ProtonCalculationModel"))));
            }
            else
            {
                var subPlans = ReflectionUtils.GetEnumerableProperty(planningItem, "PlanSetups").ToList();
                rows.Add(new KeyValuePair<string, string>("Typ", string.Format("Summenplan aus {0} Plaenen", subPlans.Count)));
                foreach (object subPlan in subPlans.Take(6))
                {
                    string subId = ReflectionUtils.GetStringProperty(subPlan, "Id");
                    string subDose = "";
                    object totalDoseObj = ReflectionUtils.GetPropertyValue(subPlan, "TotalDose");
                    double? subTotal = ReflectionUtils.GetNumericMember(totalDoseObj, "Dose");
                    string subFx = ReflectionUtils.GetStringProperty(subPlan, "NumberOfFractions");
                    if (subTotal.HasValue && !double.IsNaN(subTotal.Value))
                        subDose = string.Format(culture, "{0:F3} Gy / {1} Fx", subTotal.Value, subFx);
                    rows.Add(new KeyValuePair<string, string>("  " + subId, subDose));
                }
            }

            if (target != null)
                rows.Add(new KeyValuePair<string, string>("Ansicht zentriert auf", target.Id));
            rows.Add(new KeyValuePair<string, string>("Isodosen-Template", template.DisplayName));

            double rowHeight = Math.Max(26, RenderUtils.ScaleFont(28));
            double y = viewport.Y + 38;
            double labelX = viewport.X + 14;
            double valueX = viewport.X + 290;
            double valueWidth = viewport.X + viewport.Width - valueX - 14;

            foreach (var row in rows)
            {
                if (y + rowHeight > viewport.Y + viewport.Height - 8)
                    break;
                RenderUtils.DrawText(dc, row.Key, labelX, y, 12.5, labelBrush, typeface, culture);
                RenderUtils.DrawTextFit(dc, row.Value ?? "", valueX, y, valueWidth, 12.5, Theme.PrimaryText, boldTypeface, culture);
                dc.DrawLine(rowPen, new Point(viewport.X + 8, y + rowHeight - 6), new Point(viewport.X + viewport.Width - 8, y + rowHeight - 6));
                y += rowHeight;
            }
        }

        private static VVector GetImageCenter(Image image)
        {
            double xMm = (image.XSize - 1) / 2.0 * image.XRes;
            double yMm = (image.YSize - 1) / 2.0 * image.YRes;
            double zMm = (image.ZSize - 1) / 2.0 * image.ZRes;

            return new VVector(
                image.Origin.x + xMm * image.XDirection.x + yMm * image.YDirection.x + zMm * image.ZDirection.x,
                image.Origin.y + xMm * image.XDirection.y + yMm * image.YDirection.y + zMm * image.ZDirection.y,
                image.Origin.z + xMm * image.XDirection.z + yMm * image.YDirection.z + zMm * image.ZDirection.z);
        }

        /// <summary>Punkt relativ zum User-Origin in cm (DICOM-Achsen).</summary>
        private static VVector ComputeUserRelCm(Image image, VVector point)
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
            return new VVector(
                (point.x - userOrigin.x) / 10.0,
                (point.y - userOrigin.y) / 10.0,
                (point.z - userOrigin.z) / 10.0);
        }

        private static double DotFromOrigin(VVector point, Image image, VVector axis)
        {
            return (point.x - image.Origin.x) * axis.x +
                   (point.y - image.Origin.y) * axis.y +
                   (point.z - image.Origin.z) * axis.z;
        }

        private static double DotFromDoseOrigin(VVector point, Dose dose, VVector axis)
        {
            return (point.x - dose.Origin.x) * axis.x +
                   (point.y - dose.Origin.y) * axis.y +
                   (point.z - dose.Origin.z) * axis.z;
        }

        private static int ClampIndex(int value, int size)
        {
            if (value < 0) return 0;
            if (value >= size) return size - 1;
            return value;
        }
    }
}
