using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using VMS.TPS.Common.Model.API;
using VMS.TPS.Common.Model.Types;

namespace EclipsePlanReport
{
    /// <summary>
    /// DVH-Seite im Stil des Eclipse-DVH-Drucks: Plot mit doppelter X-Achse
    /// (relative Dosis oben, Gy unten), Strukturlegende und Planverordnung.
    /// Die Dosisstatistik wird auf eigene(n) Folgeseite(n) ausgegeben.
    /// </summary>
    internal static class DvhRenderer
    {
        public static List<string> RenderDvhPages(
            Patient patient,
            PlanningItem planningItem,
            StructureSet structureSet,
            ReportTemplate template,
            List<string> selectedStructureIds,
            string filename,
            Action<string> log)
        {
            var outputFiles = new List<string>();
            var structures = SelectDvhStructures(structureSet, template, selectedStructureIds);

            if (!structures.Any())
                return outputFiles;

            var curves = new List<DvhCurveInfo>();
            foreach (Structure structure in structures)
            {
                try
                {
                    DVHData dvh = planningItem.GetDVHCumulativeData(
                        structure,
                        DoseValuePresentation.Absolute,
                        VolumePresentation.Relative,
                        0.1);

                    if (dvh != null && dvh.CurveData != null && dvh.CurveData.Length > 0)
                    {
                        curves.Add(new DvhCurveInfo
                        {
                            Structure = structure,
                            Dvh = dvh,
                            Color = Color.FromRgb(structure.Color.R, structure.Color.G, structure.Color.B)
                        });
                    }
                }
                catch (Exception e)
                {
                    if (log != null)
                        log(string.Format("  DVH fuer {0} konnte nicht erstellt werden: {1}", structure.Id, e.Message));
                }
            }

            if (!curves.Any())
                return outputFiles;

            int width = RenderUtils.PageWidthPx;
            int height = RenderUtils.PageHeightPx;
            double legendWidth = 340;
            double left = 110;
            double right = 64 + legendWidth;
            double top = 170;
            double plotWidth = width - left - right;
            // Die Statistik liegt jetzt auf einer eigenen Seite -> der Plot darf
            // die volle Hoehe der Seite nutzen.
            double plotHeight = 760;
            double legendX = left + plotWidth + 56;

            PlanSetup planSetup = planningItem as PlanSetup;
            double totalDoseGy = 0;
            try
            {
                if (planSetup != null && planSetup.TotalDose.Dose > 0)
                    totalDoseGy = planSetup.TotalDose.Dose;
            }
            catch
            {
            }

            double maxTemplateDose = template.Isodoses
                .Select(i => SliceRenderer.ResolveIsodoseGy(i, planningItem))
                .DefaultIfEmpty(0)
                .Max();
            double maxDose = Math.Max(
                maxTemplateDose,
                curves.SelectMany(c => c.Dvh.CurveData).Max(p => p.DoseValue.Dose));
            maxDose = Math.Ceiling(maxDose / 5.0) * 5.0;
            if (maxDose <= 0)
                maxDose = 5.0;

            var culture = RenderUtils.Num;
            var typeface = new Typeface("Segoe UI");
            var boldTypeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

            double prescriptionBlockHeight = 100;

            DrawingVisual visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Theme.PageBackground, null, new Rect(0, 0, width, height));

                Pen gridPen = new Pen(Theme.GridLine, 1);
                Pen axisPen = new Pen(Theme.PrimaryText, 1.4);
                var mutedBrush = Theme.MutedText;

                RenderUtils.DrawText(dc, string.Format("Kumulatives Dosis-Volumen-Histogramm - {0} {1}", GetTypeText(planningItem), GetIdText(planningItem)), left, 12, 27, Theme.PrimaryText, typeface, culture);
                RenderUtils.DrawText(dc, RenderUtils.BuildPatientHeader(patient), left, 50, 18, mutedBrush, typeface, culture);
                RenderUtils.DrawText(dc, string.Format("Erstellt: {0:dd.MM.yyyy HH:mm}", DateTime.Now), width - 360, 50, 18, mutedBrush, typeface, culture);

                // obere X-Achse: relative Dosis (nur wenn Verordnung bekannt)
                if (totalDoseGy > 0)
                {
                    RenderUtils.DrawText(dc, "Relative Dosis [%]", left + plotWidth / 2.0 - 78, top - 66, 18, Theme.PrimaryText, typeface, culture);
                    for (int pct = 0; ; pct += 10)
                    {
                        double doseAtPct = totalDoseGy * pct / 100.0;
                        if (doseAtPct > maxDose + 0.001)
                            break;
                        double x = left + doseAtPct / maxDose * plotWidth;
                        dc.DrawLine(axisPen, new Point(x, top - 6), new Point(x, top));
                        RenderUtils.DrawText(dc, pct.ToString(culture), x - 14, top - 36, 16, Theme.PrimaryText, typeface, culture);
                    }
                }

                // Gitter + Achsen
                for (int i = 0; i <= 10; i++)
                {
                    double x = left + plotWidth * i / 10.0;
                    dc.DrawLine(gridPen, new Point(x, top), new Point(x, top + plotHeight));
                    double doseValue = maxDose * i / 10.0;
                    RenderUtils.DrawText(dc, doseValue.ToString("F0", culture), x - 12, top + plotHeight + 10, 17, Theme.PrimaryText, typeface, culture);
                }

                for (int i = 0; i <= 10; i++)
                {
                    double y = top + plotHeight * i / 10.0;
                    dc.DrawLine(gridPen, new Point(left, y), new Point(left + plotWidth, y));
                    double volume = 100.0 - i * 10.0;
                    RenderUtils.DrawText(dc, volume.ToString("F0", culture), 58, y - 10, 17, Theme.PrimaryText, typeface, culture);
                }

                dc.DrawLine(axisPen, new Point(left, top), new Point(left, top + plotHeight));
                dc.DrawLine(axisPen, new Point(left, top + plotHeight), new Point(left + plotWidth, top + plotHeight));
                RenderUtils.DrawText(dc, "Dosis [Gy]", left + plotWidth / 2.0 - 48, top + plotHeight + 40, 18, Theme.PrimaryText, typeface, culture);

                // gedrehte Y-Achsenbeschriftung (mit Abstand zum linken Seitenrand)
                double rotX = 48;
                double rotY = top + plotHeight / 2.0;
                dc.PushTransform(new RotateTransform(-90, rotX, rotY));
                RenderUtils.DrawText(dc, "Strukturvolumen [%]", rotX - 92, rotY - 26, 18, Theme.PrimaryText, typeface, culture);
                dc.Pop();

                // Kurven
                foreach (var curve in curves)
                {
                    Pen curvePen = new Pen(new SolidColorBrush(curve.Color), 2.0);
                    Point? previousPoint = null;
                    foreach (var point in curve.Dvh.CurveData)
                    {
                        double x = left + (point.DoseValue.Dose / maxDose) * plotWidth;
                        double y = top + (1.0 - point.Volume / 100.0) * plotHeight;
                        x = RenderUtils.Clamp(x, left, left + plotWidth);
                        y = RenderUtils.Clamp(y, top, top + plotHeight);

                        var screenPoint = new Point(x, y);
                        if (previousPoint.HasValue)
                            dc.DrawLine(curvePen, previousPoint.Value, screenPoint);
                        previousPoint = screenPoint;
                    }
                }

                // Legende rechts neben dem Plot
                RenderUtils.DrawText(dc, "Strukturen", legendX, top - 8, 20, Theme.PrimaryText, typeface, culture);
                double legendRowHeight = Math.Max(34, RenderUtils.ScaleFont(34));
                double legendY = top + 34;
                foreach (var curve in curves)
                {
                    if (legendY > top + plotHeight - 14)
                    {
                        RenderUtils.DrawText(dc, "...", legendX, legendY, 17, Theme.PrimaryText, typeface, culture);
                        break;
                    }
                    Pen curvePen = new Pen(new SolidColorBrush(curve.Color), 3.5);
                    dc.DrawLine(curvePen, new Point(legendX, legendY + RenderUtils.ScaleFont(11)), new Point(legendX + 36, legendY + RenderUtils.ScaleFont(11)));
                    RenderUtils.DrawTextFit(dc, curve.Structure.Id, legendX + 48, legendY, legendWidth - 60, 17, Theme.PrimaryText, typeface, culture);
                    legendY += legendRowHeight;
                }

                // Planverordnung unten (Statistik liegt jetzt auf eigener Seite)
                DrawPrescriptionBlock(dc, planningItem, 60, height - prescriptionBlockHeight, width - 120, typeface, boldTypeface, culture);
            }

            RenderUtils.SaveVisualAsPng(visual, width, height, filename);
            AddIfRendered(outputFiles, filename);

            // Dosisstatistik auf eigene(n) Seite(n) - unabhaengig vom DVH-Plot,
            // damit die Tabelle die volle Seitenbreite/-hoehe nutzen kann.
            RenderStatisticsPages(patient, planningItem, curves, filename, typeface, culture, totalDoseGy, outputFiles);

            return outputFiles;
        }

        /// <summary>Zeichnet die komplette Dosisstatistik auf eine oder mehrere eigene Seiten.</summary>
        private static void RenderStatisticsPages(
            Patient patient,
            PlanningItem planningItem,
            List<DvhCurveInfo> curves,
            string dvhFilename,
            Typeface typeface,
            CultureInfo culture,
            double totalDoseGy,
            List<string> outputFiles)
        {
            int width = RenderUtils.PageWidthPx;
            int height = RenderUtils.PageHeightPx;

            double tableX = 60;
            double tableWidth = width - 120;
            double tableTop = 150;
            double bottomMargin = 70;
            double rowHeight = Math.Max(32, RenderUtils.ScaleFont(34));
            double headerHeight = RenderUtils.ScaleFont(34);

            int rowsPerPage = Math.Max(5, (int)Math.Floor((height - tableTop - bottomMargin - headerHeight) / rowHeight));
            int pageCount = Math.Max(1, (int)Math.Ceiling(curves.Count / (double)rowsPerPage));

            for (int pageIndex = 0; pageIndex < pageCount; pageIndex++)
            {
                string statsFilename = BuildDvhStatisticsFilename(dvhFilename, pageIndex + 1, pageCount);
                DrawingVisual statsVisual = new DrawingVisual();
                using (DrawingContext dc = statsVisual.RenderOpen())
                {
                    dc.DrawRectangle(Theme.PageBackground, null, new Rect(0, 0, width, height));

                    var mutedBrush = Theme.MutedText;
                    string title = pageCount > 1
                        ? string.Format("Dosisstatistik (Seite {0}/{1}) - {2} {3}", pageIndex + 1, pageCount, GetTypeText(planningItem), GetIdText(planningItem))
                        : string.Format("Dosisstatistik - {0} {1}", GetTypeText(planningItem), GetIdText(planningItem));
                    RenderUtils.DrawText(dc, title, tableX, 28, 27, Theme.PrimaryText, typeface, culture);
                    RenderUtils.DrawText(dc, RenderUtils.BuildPatientHeader(patient), tableX, 72, 18, mutedBrush, typeface, culture);
                    RenderUtils.DrawText(dc, string.Format("Erstellt: {0:dd.MM.yyyy HH:mm}", DateTime.Now), width - 360, 72, 18, mutedBrush, typeface, culture);
                    if (totalDoseGy > 0)
                        RenderUtils.DrawText(dc, string.Format(culture, "D/V-Kennwerte bezogen auf Verordnung {0:F2} Gy", totalDoseGy), tableX, 104, 15, mutedBrush, typeface, culture);

                    DrawDvhStatisticsTable(dc, curves, tableX, tableTop, tableWidth, typeface, culture, totalDoseGy,
                        pageIndex * rowsPerPage, rowsPerPage, rowHeight);
                }

                RenderUtils.SaveVisualAsPng(statsVisual, width, height, statsFilename);
                AddIfRendered(outputFiles, statsFilename);
            }
        }

        private static void AddIfRendered(List<string> outputFiles, string filename)
        {
            VectorPdfPage page;
            if (File.Exists(filename) || VectorPdfPageStore.TryGet(filename, out page))
                outputFiles.Add(filename);
        }

        private static string GetTypeText(PlanningItem planningItem)
        {
            return planningItem is PlanSum ? "PlanSum" : "PlanSetup";
        }

        private static string GetIdText(PlanningItem planningItem)
        {
            PlanSum planSum = planningItem as PlanSum;
            if (planSum != null)
                return string.IsNullOrEmpty(planSum.Id) ? planSum.Name : planSum.Id;
            return ((PlanSetup)planningItem).Id;
        }

        private static string BuildDvhStatisticsFilename(string dvhFilename, int pageNumber, int pageCount)
        {
            string directory = Path.GetDirectoryName(dvhFilename) ?? "";
            string baseName = Path.GetFileNameWithoutExtension(dvhFilename);
            string suffix = pageCount > 1
                ? string.Format("_Statistik_{0:00}.png", pageNumber)
                : "_Statistik.png";
            return Path.Combine(directory, RenderUtils.MakeFilenameValid(baseName + suffix));
        }

        private static List<Structure> SelectDvhStructures(StructureSet structureSet, ReportTemplate template, List<string> selectedStructureIds)
        {
            if (selectedStructureIds != null && selectedStructureIds.Any())
            {
                return selectedStructureIds
                    .Select(id => structureSet.Structures.FirstOrDefault(s => !s.IsEmpty && s.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                    .Where(s => s != null)
                    .ToList();
            }

            var patterns = template.DvhPatterns.Any()
                ? template.DvhPatterns
                : template.TargetPatterns;

            return structureSet.Structures
                .Where(s => !s.IsEmpty && RenderUtils.MatchesAnyPattern(s.Id, patterns))
                .OrderBy(s => RenderUtils.GetPatternOrder(s.Id, patterns))
                .ThenByDescending(s => RenderUtils.MatchesAnyPattern(s.Id, template.TargetPatterns))
                .ThenByDescending(s => s.Volume)
                .Take(12)
                .ToList();
        }

        private static void DrawDvhStatisticsTable(
            DrawingContext dc,
            List<DvhCurveInfo> curves,
            double x,
            double y,
            double width,
            Typeface typeface,
            CultureInfo culture,
            double totalDoseGy,
            int startIndex,
            int maxRows,
            double rowHeight)
        {
            double headerHeight = RenderUtils.ScaleFont(34);
            var lineBrush = Theme.SeparatorLine;
            var headerBrush = Theme.PrimaryText;
            var textBrush = Theme.TableValueText;
            Pen thinPen = new Pen(lineBrush, 0.8);
            Pen rowPen = new Pen(Theme.TableRowLine, 0.6);

            // 13 Spalten ueber die volle Seitenbreite. Die Offsets sind als Anteil
            // der Tabellenbreite definiert, damit nichts ueberlaeuft.
            double[] fractions = { 0.000, 0.034, 0.230, 0.310, 0.380, 0.450, 0.520, 0.595, 0.670, 0.730, 0.805, 0.880, 0.940, 1.000 };
            double[] columns = fractions.Select(f => f * width).ToArray();
            string[] headers =
            {
                "DVH",
                "Struktur",
                "Abdeckung",
                "Vol. [cm³]",
                "Min [Gy]",
                "Max [Gy]",
                "Mittel [Gy]",
                "Median [Gy]",
                "StdAbw",
                "D95% [Gy]",
                "D2% [Gy]",
                "V95% [%]",
                "V107% [%]"
            };

            dc.DrawLine(thinPen, new Point(x, y - 4), new Point(x + width, y - 4));

            for (int i = 0; i < headers.Length; i++)
                RenderUtils.DrawTextFit(dc, headers[i], x + columns[i] + 4, y, RenderUtils.GetColumnWidth(columns, i) - 10, 16, headerBrush, typeface, culture);

            dc.DrawLine(thinPen, new Point(x, y + headerHeight), new Point(x + width, y + headerHeight));

            double rowY = y + headerHeight + RenderUtils.ScaleFont(7);
            foreach (DvhCurveInfo curve in curves.Skip(startIndex).Take(maxRows))
            {
                Pen curvePen = new Pen(new SolidColorBrush(curve.Color), 2.5);
                dc.DrawLine(curvePen, new Point(x + 6, rowY + RenderUtils.ScaleFont(11)), new Point(x + columns[1] - 6, rowY + RenderUtils.ScaleFont(11)));

                string[] values =
                {
                    "",
                    curve.Structure.Id,
                    FormatCoverage(curve.Dvh),
                    FormatVolume(curve.Dvh.Volume, culture),
                    FormatDose(curve.Dvh.MinDose, culture),
                    FormatDose(curve.Dvh.MaxDose, culture),
                    FormatDose(curve.Dvh.MeanDose, culture),
                    FormatDose(curve.Dvh.MedianDose, culture),
                    FormatDoseGy(curve.Dvh.StdDev, culture),
                    FormatOptional(DoseAtVolumePercent(curve.Dvh, 95.0), "F2", culture),
                    FormatOptional(DoseAtVolumePercent(curve.Dvh, 2.0), "F2", culture),
                    totalDoseGy > 0 ? FormatOptional(VolumeAtDoseGy(curve.Dvh, totalDoseGy * 0.95), "F1", culture) : "",
                    totalDoseGy > 0 ? FormatOptional(VolumeAtDoseGy(curve.Dvh, totalDoseGy * 1.07), "F1", culture) : ""
                };

                for (int i = 1; i < values.Length; i++)
                    RenderUtils.DrawTextFit(dc, values[i], x + columns[i] + 4, rowY, RenderUtils.GetColumnWidth(columns, i) - 10, 15, textBrush, typeface, culture);

                rowY += rowHeight;
                dc.DrawLine(rowPen, new Point(x, rowY - RenderUtils.ScaleFont(7)), new Point(x + width, rowY - RenderUtils.ScaleFont(7)));
            }
        }

        /// <summary>Planverordnung wie im Eclipse-DVH-Druck (Verordnungsdaten des Plans).</summary>
        private static void DrawPrescriptionBlock(DrawingContext dc, PlanningItem planningItem, double x, double y, double width, Typeface typeface, Typeface boldTypeface, CultureInfo culture)
        {
            var textBrush = Theme.TableValueText;
            Pen thinPen = new Pen(Theme.SeparatorLine, 0.8);

            dc.DrawLine(thinPen, new Point(x, y), new Point(x + width, y));
            RenderUtils.DrawText(dc, "Planverordnung", x, y + 6, 17, Theme.PrimaryText, boldTypeface, culture);

            PlanSetup planSetup = planningItem as PlanSetup;
            string line;
            if (planSetup != null)
            {
                string totalDose = "", dosePerFx = "", fractions = "";
                try
                {
                    totalDose = planSetup.TotalDose.Dose.ToString("F3", culture) + " Gy";
                    dosePerFx = planSetup.DosePerFraction.Dose.ToString("F3", culture) + " Gy";
                    fractions = ReflectionUtils.GetStringProperty(planSetup, "NumberOfFractions");
                }
                catch
                {
                }
                string normalization = ReflectionUtils.GetStringProperty(planSetup, "PlanNormalizationMethod");
                string normValue = ReflectionUtils.FormatNumber(ReflectionUtils.GetPropertyValue(planSetup, "PlanNormalizationValue"), "F1");

                line = string.Format(culture,
                    "Plan: {0}   Geplante Gesamtdosis: {1}   Dosis pro Fraktion: {2}   Anzahl Fraktionen: {3}   Normierungsmodus: {4}   Normierungswert: {5}{6}",
                    planSetup.Id, totalDose, dosePerFx, fractions,
                    string.IsNullOrEmpty(normalization) ? "-" : normalization,
                    string.IsNullOrEmpty(normValue) ? "-" : normValue,
                    string.IsNullOrEmpty(normValue) ? "" : " %");
            }
            else
            {
                var subPlans = ReflectionUtils.GetEnumerableProperty(planningItem, "PlanSetups")
                    .Select(p =>
                    {
                        string id = ReflectionUtils.GetStringProperty(p, "Id");
                        double? dose = ReflectionUtils.GetNumericMember(ReflectionUtils.GetPropertyValue(p, "TotalDose"), "Dose");
                        string fx = ReflectionUtils.GetStringProperty(p, "NumberOfFractions");
                        return dose.HasValue && !double.IsNaN(dose.Value)
                            ? string.Format(culture, "{0} ({1:F3} Gy / {2} Fx)", id, dose.Value, fx)
                            : id;
                    })
                    .ToList();
                line = "Plansumme aus: " + string.Join(";  ", subPlans);
            }

            RenderUtils.DrawTextFit(dc, line, x, y + 38, width, 15, textBrush, typeface, culture);
        }

        /// <summary>Abdeckung wie Eclipse: "Coverage / SamplingCoverage" in Prozent.</summary>
        private static string FormatCoverage(DVHData dvh)
        {
            double coverage = NormalizePercent(ReflectionUtils.GetNumericMember(dvh, "Coverage"));
            double sampling = NormalizePercent(ReflectionUtils.GetNumericMember(dvh, "SamplingCoverage"));

            if (coverage > 0 && sampling > 0)
                return string.Format(RenderUtils.Num, "{0:F1} / {1:F1}", coverage, sampling);
            if (sampling > 0)
                return sampling.ToString("F1", RenderUtils.Num) + "%";
            return "";
        }

        private static double NormalizePercent(double? value)
        {
            if (!value.HasValue || double.IsNaN(value.Value))
                return 0;
            // ESAPI reports Coverage/SamplingCoverage effectively as fractions in
            // some versions. Values near 1.0 can arrive with tiny numerical drift
            // and must still be shown as 100%, matching Eclipse's DVH print.
            return value.Value <= 1.5 ? value.Value * 100.0 : value.Value;
        }

        private static string FormatVolume(double volume, CultureInfo culture)
        {
            return volume.ToString("F1", culture);
        }

        private static string FormatDose(DoseValue dose, CultureInfo culture)
        {
            return dose.Dose.ToString("F2", culture);
        }

        private static string FormatDoseGy(double doseGy, CultureInfo culture)
        {
            return doseGy.ToString("F2", culture);
        }

        private static string FormatOptional(double? value, string format, CultureInfo culture)
        {
            return value.HasValue ? value.Value.ToString(format, culture) : "";
        }

        /// <summary>
        /// Dx%: Dosis, die mindestens x% des Strukturvolumens erhaelt -
        /// lineare Interpolation auf der kumulativen Kurve (Volumen relativ in %).
        /// </summary>
        private static double? DoseAtVolumePercent(DVHData dvh, double volumePct)
        {
            var points = dvh.CurveData;
            if (points == null || points.Length < 2)
                return null;
            if (volumePct > points[0].Volume)
                return null;

            for (int i = 1; i < points.Length; i++)
            {
                if (points[i].Volume <= volumePct)
                {
                    double v0 = points[i - 1].Volume, v1 = points[i].Volume;
                    double d0 = points[i - 1].DoseValue.Dose, d1 = points[i].DoseValue.Dose;
                    if (Math.Abs(v0 - v1) < 1e-9)
                        return d1;
                    double t = (v0 - volumePct) / (v0 - v1);
                    return d0 + t * (d1 - d0);
                }
            }
            return points[points.Length - 1].DoseValue.Dose;
        }

        /// <summary>
        /// V(d): Volumenanteil [%], der mindestens die Dosis d (Gy) erhaelt -
        /// lineare Interpolation auf der kumulativen Kurve.
        /// </summary>
        private static double? VolumeAtDoseGy(DVHData dvh, double doseGy)
        {
            var points = dvh.CurveData;
            if (points == null || points.Length < 2)
                return null;
            if (doseGy <= points[0].DoseValue.Dose)
                return points[0].Volume;
            if (doseGy > points[points.Length - 1].DoseValue.Dose)
                return 0.0;

            for (int i = 1; i < points.Length; i++)
            {
                if (points[i].DoseValue.Dose >= doseGy)
                {
                    double d0 = points[i - 1].DoseValue.Dose, d1 = points[i].DoseValue.Dose;
                    double v0 = points[i - 1].Volume, v1 = points[i].Volume;
                    if (Math.Abs(d1 - d0) < 1e-9)
                        return v1;
                    double t = (doseGy - d0) / (d1 - d0);
                    return v0 + t * (v1 - v0);
                }
            }
            return 0.0;
        }
    }

    /// <summary>Automatische OAR-Vorauswahl fuer das DVH anhand Prostata-/Mamma-Erkennung.</summary>
    internal static class DvhRecommendation
    {
        public static List<string> BuildRecommendedDvhStructureIds(PlanRequest plan, List<ReportTemplate> templates)
        {
            var patterns = new List<string>();

            ReportTemplate template = templates.FirstOrDefault(t => t.Id.Equals(plan.TemplateId, StringComparison.OrdinalIgnoreCase));
            if (template != null)
            {
                if (template.DvhPatterns.Any())
                    patterns.AddRange(template.DvhPatterns);
                else
                    patterns.AddRange(template.TargetPatterns);
            }

            if (IsPcaLikePlan(plan, template))
            {
                patterns.AddRange(new[]
                {
                    "PTV*", "CTV*",
                    "Bladder", "*BLADDER*", "*BLASE*",
                    "Rectum", "*RECTUM*", "*REKTUM*",
                    "RectumHW", "*RECTUMHW*", "*REKTUMHW*", "*RECTUM*HW*", "*REKTUM*HW*",
                    "Femur_R", "Femur_L", "Femur*R*", "Femur*L*",
                    "*FEMUR*RE*", "*FEMUR*LI*", "*FEMUR*R*", "*FEMUR*L*",
                    "*FEMORAL*R*", "*FEMORAL*L*"
                });
            }

            if (IsMammaLikePlan(plan, template))
            {
                patterns.AddRange(new[]
                {
                    "PTV*", "CTV*", "BOOST*",
                    "Lung_L", "Lung_R", "*LUNG*L*", "*LUNG*R*", "*LUNGE*LI*", "*LUNGE*RE*",
                    "Heart", "*HEART*", "*HERZ*",
                    "CoronarLAD", "*CORONAR*LAD*", "*CORONARY*LAD*", "*LAD*"
                });
            }

            return plan.AvailableDvhStructureIds
                .Where(id => RenderUtils.MatchesAnyPattern(id, patterns))
                .OrderBy(id => RenderUtils.GetPatternOrder(id, patterns))
                .ThenBy(id => id, StringComparer.OrdinalIgnoreCase)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static bool IsPcaLikePlan(PlanRequest plan, ReportTemplate template)
        {
            string haystack = BuildHaystack(plan, template);

            bool hasPcaWord =
                haystack.Contains("pca") ||
                haystack.Contains("prostata") ||
                haystack.Contains("prostate") ||
                haystack.Contains("properi") ||
                haystack.Contains("seminal") ||
                haystack.Contains("samenblasen");

            bool hasTypicalPcaOars =
                haystack.Contains("rectum") ||
                haystack.Contains("rektum") ||
                haystack.Contains("bladder") ||
                haystack.Contains("blase");

            bool hasFemur = haystack.Contains("femur") || haystack.Contains("femoral");

            return hasPcaWord || (hasTypicalPcaOars && hasFemur);
        }

        public static bool IsMammaLikePlan(PlanRequest plan, ReportTemplate template)
        {
            string haystack = BuildHaystack(plan, template);

            // Hinweis: kein blosses "lad"-Matching - das wuerde auch "Bladder" treffen.
            return haystack.Contains("mamma") ||
                   haystack.Contains("breast") ||
                   haystack.Contains("brust") ||
                   haystack.Contains("coronarlad") ||
                   haystack.Contains("coronarylad") ||
                   (haystack.Contains("lunge") && haystack.Contains("herz")) ||
                   (haystack.Contains("lung") && haystack.Contains("heart"));
        }

        private static string BuildHaystack(PlanRequest plan, ReportTemplate template)
        {
            return RenderUtils.NormalizeForMatch(string.Join(" ",
                new[]
                {
                    plan.PlanId,
                    plan.TemplateId,
                    template != null ? template.Id : null,
                    template != null ? template.DisplayName : null,
                    plan.SliceTargetId,
                    string.Join(" ", plan.AvailableSliceTargetIds),
                    string.Join(" ", plan.AvailableDvhStructureIds)
                }.Where(x => !string.IsNullOrEmpty(x))));
        }
    }
}
