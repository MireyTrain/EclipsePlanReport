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
    /// 2x2-Raster mit transversal / sagittal / frontal durch das Plan-Isozentrum
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
            List<string> displayStructureIds,
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

            // Point of View: bevorzugt Plan-Isozentrum, sonst Zielstrukturmitte, sonst Bildmitte.
            VVector center;
            string centerSource;
            if (TryGetPlanIsocenter(planningItem, out center))
            {
                centerSource = "Isozentrum";
            }
            else
            {
                try
                {
                    center = (target != null && !target.IsEmpty)
                        ? target.CenterPoint
                        : GetImageCenter(image);
                    centerSource = target != null && !target.IsEmpty
                        ? "Zielstruktur " + target.Id
                        : "Bildmitte";
                }
                catch
                {
                    center = GetImageCenter(image);
                    centerSource = "Bildmitte";
                }
            }
            if (log != null)
                log("  Ansichtsseite: Point of View = " + centerSource + ".");

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
            CtVolume ctVolume = planningItem is PlanSetup ? new CtVolume(image) : null;

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
            List<Structure> visibleStructures = CollectVisibleStructures(structureSet, target, displayStructureIds);

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
            string doseInfoLabel = BuildDoseInfoLabel(planningItem, structureSet, target, log);

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
                string sagittalRightLabel = RenderUtils.GetSagittalRightLabel(positionCode);
                string sagittalDownLabel = RenderUtils.GetSagittalDownLabel(positionCode);
                string frontalRightLabel = RenderUtils.GetFrontalRightLabel(positionCode);
                string frontalDownLabel = RenderUtils.GetFrontalDownLabel(positionCode);

                RenderUtils.DisplayTransform transDisplay = RenderUtils.CreateDisplayTransform(image.XDirection, image.YDirection, transRightLabel, tableSideLabel);
                RenderUtils.DisplayTransform sagDisplay = RenderUtils.CreateDisplayTransform(image.YDirection, image.ZDirection, sagittalRightLabel, sagittalDownLabel);
                RenderUtils.DisplayTransform froDisplay = RenderUtils.CreateDisplayTransform(image.XDirection, image.ZDirection, frontalRightLabel, frontalDownLabel);
                if (log != null)
                {
                    log(string.Format("  Orientierung: Position={0}, X={1}, Y={2}, Z={3}",
                        string.IsNullOrEmpty(positionCode) ? rawOrientation : positionCode,
                        RenderUtils.GetLabelForDirection(image.XDirection),
                        RenderUtils.GetLabelForDirection(image.YDirection),
                        RenderUtils.GetLabelForDirection(image.ZDirection)));
                    log(string.Format("  Mapping Transversal: rechts={0}, unten={1} (Tisch unten).", transDisplay.RightLabel, transDisplay.BottomLabel));
                    log(string.Format("  Mapping Sagittal: rechts={0}, unten={1}.", sagDisplay.RightLabel, sagDisplay.BottomLabel));
                    log(string.Format("  Mapping Frontal: rechts={0}, unten={1}.", froDisplay.RightLabel, froDisplay.BottomLabel));
                    if (transDisplay.IsFallback)
                        log("  Warnung: Transversal-Mapping nicht sicher bestimmbar - native Bildachsen werden verwendet.");
                }

                double cxMm = DotFromOrigin(center, image, image.XDirection);
                double cyMm = DotFromOrigin(center, image, image.YDirection);
                double czMm = DotFromOrigin(center, image, image.ZDirection);

                // Position relativ zum User-Origin in der Eclipse-Patientenlagerung.
                VVector userRel = RenderUtils.ComputeEclipseUserCoordinatesCm(image, center, positionCode);

                // --- transversal (oben links) ---
                Rect transContent = DrawPlanarView(dc, q1,
                    string.Format("{0}{1} - Transversal - {2}", planId, titleSuffix, seriesId),
                    transBmp,
                    image.XSize * image.XRes, image.YSize * image.YRes,
                    transDisplay,
                    (innerDc, penScale) =>
                    {
                        DrawStructureSegments(innerDc, transSegs, penScale,
                            p => new Point(DotFromOrigin(p, image, image.XDirection), DotFromOrigin(p, image, image.YDirection)));
                        if (transDose != null)
                        {
                            DrawIsodoses(innerDc, transDose, planningItem, template, penScale, (u, v) =>
                            {
                                VVector p = DoseGridToPatient(dose, u, v, ktFix, PlaneAxis.Transversal);
                                return new Point(DotFromOrigin(p, image, image.XDirection), DotFromOrigin(p, image, image.YDirection));
                            });
                        }
                        DrawCrosshair(innerDc, cxMm, cyMm, image.XSize * image.XRes, image.YSize * image.YRes, penScale, SagPlaneColor, FroPlaneColor);
                    },
                    string.Format(culture, "Z: {0:+0.00;-0.00;0.00} cm", SliceRenderer.ComputeEclipseZcm(image, zc, positionCode)),
                    "",
                    typeface,
                    RenderUtils.ManikinView.Transversal);

                // --- sagittal (oben rechts) ---
                DrawPlanarView(dc, q2,
                    string.Format("{0}{1} - Sagittal - {2}", planId, titleSuffix, seriesId),
                    sagBmp,
                    image.YSize * image.YRes, image.ZSize * image.ZRes,
                    sagDisplay,
                    (innerDc, penScale) =>
                    {
                        DrawStructureSegments(innerDc, sagSegs, penScale,
                            p => new Point(DotFromOrigin(p, image, image.YDirection), DotFromOrigin(p, image, image.ZDirection)));
                        if (sagDose != null)
                        {
                            DrawIsodoses(innerDc, sagDose, planningItem, template, penScale, (u, v) =>
                            {
                                VVector p = DoseGridToPatient(dose, u, v, iFix, PlaneAxis.Sagittal);
                                return new Point(DotFromOrigin(p, image, image.YDirection), DotFromOrigin(p, image, image.ZDirection));
                            });
                        }
                        DrawCrosshair(innerDc, cyMm, czMm, image.YSize * image.YRes, image.ZSize * image.ZRes, penScale, FroPlaneColor, TransPlaneColor);
                    },
                    string.Format(culture, "X: {0:+0.00;-0.00;0.00} cm", userRel.x),
                    "",
                    typeface,
                    RenderUtils.ManikinView.Sagittal);

                // --- frontal (unten links) ---
                DrawPlanarView(dc, q3,
                    string.Format("{0}{1} - Frontal - {2}", planId, titleSuffix, seriesId),
                    froBmp,
                    image.XSize * image.XRes, image.ZSize * image.ZRes,
                    froDisplay,
                    (innerDc, penScale) =>
                    {
                        DrawStructureSegments(innerDc, froSegs, penScale,
                            p => new Point(DotFromOrigin(p, image, image.XDirection), DotFromOrigin(p, image, image.ZDirection)));
                        if (froDose != null)
                        {
                            DrawIsodoses(innerDc, froDose, planningItem, template, penScale, (u, v) =>
                            {
                                VVector p = DoseGridToPatient(dose, u, v, jFix, PlaneAxis.Frontal);
                                return new Point(DotFromOrigin(p, image, image.XDirection), DotFromOrigin(p, image, image.ZDirection));
                            });
                        }
                        DrawCrosshair(innerDc, cxMm, czMm, image.XSize * image.XRes, image.ZSize * image.ZRes, penScale, SagPlaneColor, TransPlaneColor);
                    },
                    string.Format(culture, "Y: {0:+0.00;-0.00;0.00} cm", userRel.y),
                    "",
                    typeface,
                    RenderUtils.ManikinView.Frontal);

                // --- BEV des Setup-Felds mit DRR + ZV-Silhouette, sonst Plan-Info-Panel (unten rechts) ---
                bool bevDrawn = false;
                try
                {
                    List<Structure> bevTargets = CollectBevTargetStructures(planningItem as PlanSetup, structureSet, target, displayStructureIds);
                    bevDrawn = TryDrawSetupFieldBev(dc, q4, planningItem as PlanSetup, bevTargets, ctVolume, doseInfoLabel, typeface, log);
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
        private static List<Structure> CollectBevTargetStructures(PlanSetup planSetup, StructureSet structureSet, Structure fallbackTarget, List<string> displayStructureIds)
        {
            var result = new List<Structure>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (displayStructureIds != null)
            {
                foreach (string id in displayStructureIds)
                {
                    if (string.IsNullOrEmpty(id) || !seen.Add(id))
                        continue;

                    Structure s = structureSet.Structures.FirstOrDefault(x =>
                        !x.IsEmpty && x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                    if (s != null)
                        result.Add(s);
                }
                return result;
            }

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
            if (visibleStructureIds == null)
            {
                if (target != null && !target.IsEmpty)
                    result.Add(target);
                return result;
            }

            foreach (string id in visibleStructureIds)
            {
                if (string.IsNullOrEmpty(id))
                    continue;
                Structure s = structureSet.Structures.FirstOrDefault(x =>
                    !x.IsEmpty && x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                if (s != null && !result.Any(existing => existing.Id.Equals(s.Id, StringComparison.OrdinalIgnoreCase)))
                    result.Add(s);
            }

            return result;
        }

        private static string BuildDoseInfoLabel(PlanningItem planningItem, StructureSet structureSet, Structure fallbackTarget, Action<string> log)
        {
            if (planningItem == null || structureSet == null)
                return "";

            var lines = new List<string>();

            Structure body = FindBodyStructure(structureSet);
            DVHData bodyDvh = TryGetDvh(planningItem, body, "BODY", log);
            if (bodyDvh != null)
                lines.Add(string.Format(RenderUtils.Num, "Bodymax: {0:F2} Gy", bodyDvh.MaxDose.Dose));

            Structure normalizedTarget = FindNormalizedTargetStructure(planningItem as PlanSetup, structureSet, fallbackTarget);
            DVHData targetDvh = TryGetDvh(planningItem, normalizedTarget, "normiertes PTV", log);
            if (targetDvh != null)
            {
                string targetName = normalizedTarget != null ? normalizedTarget.Id : "PTV";
                lines.Add(string.Format(RenderUtils.Num, "Max ({0}): {1:F2} Gy", targetName, targetDvh.MaxDose.Dose));
                lines.Add(string.Format(RenderUtils.Num, "Min ({0}): {1:F2} Gy", targetName, targetDvh.MinDose.Dose));
                lines.Add(string.Format(RenderUtils.Num, "Mittel ({0}): {1:F2} Gy", targetName, targetDvh.MeanDose.Dose));
            }

            return string.Join("\n", lines);
        }

        private static Structure FindBodyStructure(StructureSet structureSet)
        {
            if (structureSet == null)
                return null;

            return structureSet.Structures.FirstOrDefault(s =>
                !s.IsEmpty &&
                (s.Id.ToLowerInvariant().StartsWith("body") ||
                 s.Id.ToLowerInvariant().StartsWith("körper") ||
                 s.Id.ToLowerInvariant().StartsWith("koerper") ||
                 s.Id.ToLowerInvariant().StartsWith("outer")));
        }

        private static Structure FindNormalizedTargetStructure(PlanSetup planSetup, StructureSet structureSet, Structure fallbackTarget)
        {
            if (planSetup != null && structureSet != null && !string.IsNullOrEmpty(planSetup.TargetVolumeID))
            {
                Structure target = structureSet.Structures.FirstOrDefault(s =>
                    !s.IsEmpty && s.Id.Equals(planSetup.TargetVolumeID, StringComparison.OrdinalIgnoreCase));
                if (target != null)
                    return target;
            }

            return fallbackTarget != null && !fallbackTarget.IsEmpty ? fallbackTarget : null;
        }

        private static DVHData TryGetDvh(PlanningItem planningItem, Structure structure, string label, Action<string> log)
        {
            if (planningItem == null || structure == null || structure.IsEmpty)
                return null;

            try
            {
                DVHData dvh = planningItem.GetDVHCumulativeData(
                    structure,
                    DoseValuePresentation.Absolute,
                    VolumePresentation.Relative,
                    0.1);

                if (dvh != null && dvh.CurveData != null && dvh.CurveData.Length > 0)
                    return dvh;
            }
            catch (Exception e)
            {
                if (log != null)
                    log(string.Format("  Ansichtsseite: DVH-Statistik fuer {0} ({1}) nicht verfuegbar: {2}", label, structure.Id, e.Message));
            }

            return null;
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
            Typeface typeface,
            RenderUtils.ManikinView manikinView,
            RenderUtils.DisplayTransform manikinDisplayTransform = null)
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
                dc.DrawText(posText, new Point(content.X + 8, content.Y + content.Height - posText.Height - 108));
            }

            // Orientierungsfigur
            RenderUtils.DrawManikin(dc, content.X + 8, content.Y + content.Height - 102, 70, manikinView, manikinDisplayTransform ?? displayTransform);

            return content;
        }

        // ---------- BEV des Setup-Felds (DRR + ZV-Silhouette + Feldrahmen) ----------

        /// <summary>
        /// Beam's-Eye-View des ersten Setup-Felds: synthetisches DRR aus dem Planungs-CT
        /// immer aus Gantry-0-Richtung, Zielvolumen als divergent projizierte Mesh-
        /// Silhouette, Jaw-Feldrahmen zur Lagekontrolle. Liefert false, wenn kein
        /// Setup-Feld vorhanden ist (dann bleibt das Plan-Info-Panel stehen).
        /// </summary>
        private static bool TryDrawSetupFieldBev(
            DrawingContext dc,
            Rect viewport,
            PlanSetup planSetup,
            List<Structure> targets,
            CtVolume ctVolume,
            string doseInfoLabel,
            Typeface typeface,
            Action<string> log)
        {
            if (planSetup == null)
                return false;

            Beam beam;
            try
            {
                List<Beam> allBeams = planSetup.Beams.ToList();

                // Setup-Felder bevorzugt, CBCT zuletzt. Das DRR wird immer synthetisch
                // aus Gantry 0 berechnet; gespeicherte ReferenceImages werden bewusst
                // nicht verwendet.
                beam = allBeams
                    .Where(x => x.IsSetupField)
                    .OrderBy(x => x.Id.IndexOf("CBCT", StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 0)
                    .ThenBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
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

            double projectionGantryDeg = 0.0;
            if (log != null && Math.Abs(NormalizeAngle180(gantryDeg)) > 0.5)
                log(string.Format(RenderUtils.Num, "  BEV: DRR wird bewusst aus Gantry 0.0° berechnet (Feldgantry {0:F1}°).", gantryDeg));

            // Quellposition und BEV-Achsen in DICOM-Patientenkoordinaten.
            // Gantry 0 ist raum-/tischbezogen: Quelle kommt von der Seite
            // gegenueber der Tischseite. Bei Decubitus ist das in
            // DICOM-Patientenachsen lateral, nicht anterior/posterior.
            // Die Tischseitenregel deckt HFS/FFS/HFP/FFP und Decubitus ab.
            string orientationName = ReflectionUtils.GetStringProperty(planSetup, "TreatmentOrientation");
            if (orientationName == null)
                orientationName = "";
            string positionCode = RenderUtils.GetPatientPositionCode(orientationName);
            string tableSideLabel = RenderUtils.GetTableSideLabel(positionCode);
            VVector bAxis = NormalizeVec(RenderUtils.GetPatientDirectionVector(tableSideLabel));
            VVector source = new VVector(
                iso.x - sad * bAxis.x,
                iso.y - sad * bAxis.y,
                iso.z - sad * bAxis.z);

            bool feetFirst = positionCode.StartsWith("FF", StringComparison.OrdinalIgnoreCase);
            VVector vAxis = feetFirst
                ? new VVector(0, 0, -1) // Panel bleibt raumfest: Feet-First erscheint kopf/fuss-invertiert zu Head-First.
                : new VVector(0, 0, 1);
            if (Math.Abs(DotVec(vAxis, bAxis)) > 0.95)
                vAxis = new VVector(0, 1, 0);
            VVector uAxis = NormalizeVec(CrossVec(bAxis, vAxis));
            vAxis = NormalizeVec(CrossVec(uAxis, bAxis));
            if (log != null)
                log(string.Format("  BEV: Orientierung={0}, Tischseite={1}, Achse rechts={2}, Achse oben={3}.",
                    string.IsNullOrEmpty(positionCode) ? orientationName : positionCode,
                    tableSideLabel,
                    RenderUtils.GetLabelForDirection(uAxis),
                    RenderUtils.GetLabelForDirection(vAxis)));

            // ---- DRR synthetisch aus dem Planungs-CT berechnen ----
            BitmapSource background = null;
            double wMm = 0, hMm = 0;
            Func<VVector, Point> project = null;

            bool syntheticDrr = false;
            if (ctVolume != null)
            {
                try
                {
                    var drrWatch = System.Diagnostics.Stopwatch.StartNew();
                    wMm = 420;
                    hMm = 420;
                    background = ComputeSyntheticDrr(ctVolume, source, iso, uAxis, vAxis, wMm, hMm, 384, 384);
                    syntheticDrr = background != null;
                    if (syntheticDrr && log != null)
                        log(string.Format(RenderUtils.Num, "  BEV: Gantry-0-DRR fuer Feld {0} aus dem CT berechnet ({1:F1} s).", beam.Id, drrWatch.Elapsed.TotalSeconds));
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
                    log(string.Format("  BEV: synthetisches DRR fuer Setup-Feld {0} nicht verfuegbar - zeichne nur Geometrie.", beam.Id));
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

            string drrLabel = syntheticDrr ? " - DRR (berechnet)" : " - kein DRR";
            string title = string.Format(RenderUtils.Num, "BEV {0}{1} - Gantry {2:F1}°{3}",
                beam.IsSetupField ? "Setup-Feld " : "Feld ",
                beam.Id, projectionGantryDeg, drrLabel);
            string ptvList = targets != null && targets.Count > 0
                ? "   Strukturen: " + string.Join(", ", targets.Where(t => t != null).Select(t => t.Id))
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

            var bevManikinDisplay = new RenderUtils.DisplayTransform
            {
                RightAxis = 0,
                RightSign = 1,
                DownAxis = 1,
                DownSign = 1,
                RightLabel = RenderUtils.GetLabelForDirection(uAxis),
                LeftLabel = RenderUtils.GetLabelForDirection(RenderUtils.Negate(uAxis)),
                TopLabel = RenderUtils.GetLabelForDirection(vAxis),
                BottomLabel = RenderUtils.GetLabelForDirection(RenderUtils.Negate(vAxis))
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
                doseInfoLabel,
                typeface,
                RenderUtils.ManikinView.Frontal,
                bevManikinDisplay);

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

            VVector panelIsocenter;
            if (TryGetPlanIsocenter(planningItem, out panelIsocenter))
                rows.Add(new KeyValuePair<string, string>("Ansicht zentriert auf", "Isozentrum"));
            else if (target != null)
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

        private static bool TryGetPlanIsocenter(PlanningItem planningItem, out VVector isocenter)
        {
            isocenter = new VVector();

            PlanSetup planSetup = planningItem as PlanSetup;
            if (planSetup == null)
                return false;

            try
            {
                Beam beam = planSetup.Beams
                    .Where(b => b.IsSetupField)
                    .FirstOrDefault();
                if (beam == null)
                    beam = planSetup.Beams
                        .Where(b => !b.IsSetupField)
                        .FirstOrDefault();
                if (beam == null)
                    return false;

                isocenter = beam.IsocenterPosition;
                return IsFinitePoint(isocenter);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsFinitePoint(VVector point)
        {
            return !double.IsNaN(point.x) && !double.IsInfinity(point.x) &&
                   !double.IsNaN(point.y) && !double.IsInfinity(point.y) &&
                   !double.IsNaN(point.z) && !double.IsInfinity(point.z);
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
