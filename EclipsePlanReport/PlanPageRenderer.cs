using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using VMS.TPS.Common.Model.API;
using VMS.TPS.Common.Model.Types;

namespace EclipsePlanReport
{
    /// <summary>
    /// Nachgebaute Eclipse-Planberichtseite. Es werden ausschliesslich Werte gedruckt,
    /// die sicher ueber ESAPI verfuegbar sind - alles andere bleibt leer.
    /// </summary>
    internal static class PlanPageRenderer
    {
        public static void RenderPlanReportPage(Patient patient, Course course, PlanningItem planningItem, StructureSet structureSet, string filename)
        {
            int width = RenderUtils.PageWidthPx;
            int height = RenderUtils.PageHeightPx;
            var visual = new DrawingVisual();
            CultureInfo culture = RenderUtils.Num;
            Typeface regular = new Typeface("Arial");
            Typeface bold = new Typeface(new FontFamily("Arial"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            Brush textBrush = Theme.PrimaryText;
            Brush mutedBrush = Theme.MutedText;
            Brush headerFill = Theme.SectionFill;
            Pen linePen = new Pen(Theme.SeparatorLine, 1.0);
            Pen lightPen = new Pen(Theme.GridLine, 0.8);

            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Theme.PageBackground, null, new Rect(0, 0, width, height));

                double margin = 58;
                double y = 42;
                string patientName = string.Format("{0}, {1}", patient.LastName, patient.FirstName).Trim(' ', ',');
                string birthDate = RenderUtils.GetPatientBirthDate(patient);
                string patientLine = string.IsNullOrEmpty(birthDate)
                    ? string.Format("{0} ({1})", patientName, patient.Id)
                    : string.Format("{0} ({1}), {2}", patientName, patient.Id, birthDate);

                RenderUtils.DrawText(dc, "Bestrahlungsplan", margin, y, 26, textBrush, bold, culture);
                RenderUtils.DrawText(dc, patientLine, margin, y + 38, 15, textBrush, regular, culture);
                RenderUtils.DrawText(dc, string.Format("Erstellt: {0:dd.MM.yyyy HH:mm}", DateTime.Now), width - 300, y + 8, 12, mutedBrush, regular, culture);
                dc.DrawLine(linePen, new Point(margin, y + 78), new Point(width - margin, y + 78));

                y += 110;
                PlanSetup planSetup = planningItem as PlanSetup;
                DrawSectionTitle(dc, "Plan", margin, y, width - 2 * margin, headerFill, linePen, bold, culture);
                y += 38;
                DrawLabelValue(dc, "Course ID", course != null ? course.Id : "", margin, y, 190, regular, bold, culture);
                DrawLabelValue(dc, "Plan ID", GetPlanningItemId(planningItem), margin + 520, y, 170, regular, bold, culture);
                y += 30;
                DrawLabelValue(dc, "Plan Name", ReflectionUtils.GetStringProperty(planningItem, "Name"), margin, y, 190, regular, bold, culture);
                DrawLabelValue(dc, "Typ", planningItem is PlanSum ? "PlanSum" : "PlanSetup", margin + 520, y, 170, regular, bold, culture);

                y += 30;
                string orientationText = RenderUtils.GetGermanOrientationText(ReflectionUtils.FirstNonEmpty(
                    ReflectionUtils.GetStringProperty(planningItem, "TreatmentOrientation"),
                    ReflectionUtils.GetStringProperty(structureSet != null ? (object)structureSet.Image : null, "ImagingOrientation")));
                string imageId = structureSet != null && structureSet.Image != null ? structureSet.Image.Id : "";
                string seriesId = structureSet != null ? ReflectionUtils.GetNestedStringProperty(structureSet.Image, "Series", "Id") : "";
                DrawLabelValue(dc, "Orientation", orientationText, margin, y, 190, regular, bold, culture);
                DrawLabelValue(dc, "Image ID", imageId, margin + 520, y, 170, regular, bold, culture);
                DrawLabelValue(dc, "Serie / CT", seriesId, margin + 1040, y, 190, regular, bold, culture);

                y += 54;
                DrawSectionTitle(dc, "Dose Prescription", margin, y, width - 2 * margin, headerFill, linePen, bold, culture);
                y += 38;
                string algorithm = ReflectionUtils.FirstNonEmpty(
                    ReflectionUtils.GetStringProperty(planningItem, "PhotonCalculationModel"),
                    ReflectionUtils.GetStringProperty(planningItem, "ElectronCalculationModel"),
                    ReflectionUtils.GetStringProperty(planningItem, "ProtonCalculationModel"));
                string prescribedDose = planSetup != null ? FormatDoseValue(planSetup.TotalDose) : "";
                string dosePerFraction = planSetup != null ? FormatDoseValue(planSetup.DosePerFraction) : "";
                string fractionCount = planSetup != null ? ReflectionUtils.GetStringProperty(planSetup, "NumberOfFractions") : "";
                string normalization = BuildNormalizationText(planningItem);
                string referencePoint = ReflectionUtils.GetNestedStringProperty(planningItem, "PrimaryReferencePoint", "Id");
                string targetVolume = planSetup != null ? planSetup.TargetVolumeID : "";

                DrawLabelValue(dc, "Algorithm", algorithm, margin, y, 190, regular, bold, culture);
                DrawLabelValue(dc, "Prescribed Dose", prescribedDose, margin + 520, y, 190, regular, bold, culture);
                DrawLabelValue(dc, "No. of Fractions", fractionCount, margin + 1040, y, 190, regular, bold, culture);
                y += 30;
                string gridSize = "";
                double? doseXRes = ReflectionUtils.GetNumericMember(ReflectionUtils.GetPropertyValue(planningItem, "Dose"), "XRes");
                if (doseXRes.HasValue && doseXRes.Value > 0)
                    gridSize = (doseXRes.Value / 10.0).ToString("F2", culture) + " cm";

                DrawLabelValue(dc, "Dose per Fraction", dosePerFraction, margin, y, 190, regular, bold, culture);
                DrawLabelValue(dc, "Normalization Method", normalization, margin + 520, y, 190, regular, bold, culture);
                DrawLabelValue(dc, "Grid Size", gridSize, margin + 1040, y, 190, regular, bold, culture);
                y += 30;
                DrawLabelValue(dc, "Reference Point", referencePoint, margin, y, 190, regular, bold, culture);
                DrawLabelValue(dc, "Target Volume", targetVolume, margin + 520, y, 190, regular, bold, culture);

                string planComment = ReflectionUtils.GetStringProperty(planningItem, "Comment");
                if (!string.IsNullOrWhiteSpace(planComment))
                {
                    y += 30;
                    RenderUtils.DrawText(dc, "Plan Comment:", margin, y, 11.5, Theme.MutedText, bold, culture);
                    RenderUtils.DrawTextFit(dc, planComment.Replace('\n', ' ').Replace("\r", ""), margin + 190, y, width - 2 * margin - 200, 11.5, Theme.PrimaryText, regular, culture);
                }

                y += 58;
                DrawSectionTitle(dc, "Fields", margin, y, width - 2 * margin, headerFill, linePen, bold, culture);
                y += 36;
                List<BeamReportRow> rows = BuildBeamReportRows(planningItem, GetUserOrigin(structureSet));
                double tableWidth = width - 2 * margin;
                double[] col = { 0, 110, 245, 430, 625, 805, 930, 1035, 1138, 1300, 1415, 1510, tableWidth };
                string[] headers =
                {
                    "Field ID",
                    "Machine Scale",
                    "Energy/DoseRate",
                    "Technique",
                    "Field Size / Jaws",
                    "Gantry",
                    "Collimator",
                    "Table",
                    "Isocenter",
                    "MLC/Wedge",
                    "Bolus/SSD",
                    "MU"
                };

                dc.DrawRectangle(headerFill, null, new Rect(margin, y, tableWidth, 30));
                for (int i = 0; i < headers.Length; i++)
                    RenderUtils.DrawTextFit(dc, headers[i], margin + col[i] + 4, y + 7, RenderUtils.GetColumnWidth(col, i) - 8, 10.5, textBrush, bold, culture);
                dc.DrawLine(linePen, new Point(margin, y), new Point(margin + tableWidth, y));
                dc.DrawLine(linePen, new Point(margin, y + 30), new Point(margin + tableWidth, y + 30));
                foreach (double x in col)
                    dc.DrawLine(lightPen, new Point(margin + x, y), new Point(margin + x, height - 365));

                y += 30;
                double rowHeight = 58;
                int maxRows = Math.Max(0, (int)((height - 380 - y) / rowHeight));
                foreach (BeamReportRow row in rows.Take(maxRows))
                {
                    string[] values =
                    {
                        row.FieldId,
                        row.MachineScale,
                        row.EnergyDoseRate,
                        row.Technique,
                        row.FieldSize,
                        row.Gantry,
                        row.Collimator,
                        row.Table,
                        row.Isocenter,
                        row.MlcWedge,
                        row.BolusSsd,
                        row.Mu
                    };
                    dc.DrawLine(lightPen, new Point(margin, y + rowHeight), new Point(margin + tableWidth, y + rowHeight));
                    for (int i = 0; i < values.Length; i++)
                        DrawCellLines(dc, values[i], margin + col[i] + 4, y + 7, RenderUtils.GetColumnWidth(col, i) - 8, 10.5, regular, culture);
                    y += rowHeight;
                }

                if (rows.Count > maxRows)
                    RenderUtils.DrawText(dc, string.Format("Weitere Felder nicht auf dieser Berichtseite dargestellt: {0}", rows.Count - maxRows), margin, y + 10, 11, mutedBrush, regular, culture);

                // ---------- Freigabe + Unterschriften ----------
                string createdBy = ReflectionUtils.GetStringProperty(planningItem, "CreationUserName");
                string createdDate = ReflectionUtils.GetStringProperty(planningItem, "CreationDateTime");
                string planningApprover = ReflectionUtils.FirstNonEmpty(
                    ReflectionUtils.GetStringProperty(planningItem, "PlanningApproverDisplayName"),
                    ReflectionUtils.GetStringProperty(planningItem, "PlanningApprover"));
                string planningDate = ReflectionUtils.FirstNonEmpty(
                    ReflectionUtils.GetStringProperty(planningItem, "PlanningApprovalDate"),
                    ReflectionUtils.GetStringProperty(planningItem, "PlanningDate"));
                string treatmentApprover = ReflectionUtils.FirstNonEmpty(
                    ReflectionUtils.GetStringProperty(planningItem, "TreatmentApproverDisplayName"),
                    ReflectionUtils.GetStringProperty(planningItem, "TreatmentApprover"));
                string treatmentDate = ReflectionUtils.GetStringProperty(planningItem, "TreatmentApprovalDate");

                // Fallback: ApprovalHistory (neuere ESAPI-Versionen) durchsuchen
                FillFromApprovalHistory(planningItem,
                    ref planningApprover, ref planningDate,
                    ref treatmentApprover, ref treatmentDate);

                double approvalY = height - 360;
                DrawSectionTitle(dc, "Freigabe / Approval", margin, approvalY, tableWidth, headerFill, linePen, bold, culture);
                approvalY += 38;
                DrawLabelValue(dc, "Plan erstellt", JoinDateAndUser(createdDate, createdBy), margin, approvalY, 190, regular, bold, culture);
                DrawLabelValue(dc, "Planung freigegeben", JoinDateAndUser(planningDate, planningApprover), margin + 520, approvalY, 190, regular, bold, culture);
                DrawLabelValue(dc, "Bestrahlung freigegeben", JoinDateAndUser(treatmentDate, treatmentApprover), margin + 1040, approvalY, 210, regular, bold, culture);
                approvalY += 56;
                DrawLabelValue(dc, "Approval Status", ReflectionUtils.GetStringProperty(planningItem, "ApprovalStatus"), margin, approvalY, 190, regular, bold, culture);

                // Unterschriftenfelder (Name/Datum aus ESAPI vorbelegt, Unterschrift handschriftlich)
                double boxY = height - 190;
                double boxHeight = 110;
                double gap = 24;
                double boxWidth = (tableWidth - 2 * gap) / 3.0;
                DrawSignatureBox(dc, "Erstellt von", createdBy, createdDate, margin, boxY, boxWidth, boxHeight, regular, bold, lightPen, culture);
                DrawSignatureBox(dc, "Geprüft von (Physik)", planningApprover, planningDate, margin + boxWidth + gap, boxY, boxWidth, boxHeight, regular, bold, lightPen, culture);
                DrawSignatureBox(dc, "Freigegeben von (Arzt)", treatmentApprover, treatmentDate, margin + 2 * (boxWidth + gap), boxY, boxWidth, boxHeight, regular, bold, lightPen, culture);

                dc.DrawLine(linePen, new Point(margin, height - 58), new Point(width - margin, height - 58));
                RenderUtils.DrawText(dc, "Planbericht aus ESAPI-Daten. Nicht verfuegbare Felder bleiben leer.", margin, height - 40, 10.5, mutedBrush, regular, culture);
            }

            RenderUtils.SaveVisualAsPng(visual, width, height, filename);
        }

        public static string GetPlanningItemId(PlanningItem planningItem)
        {
            PlanSetup planSetup = planningItem as PlanSetup;
            if (planSetup != null)
                return planSetup.Id;

            PlanSum planSum = planningItem as PlanSum;
            if (planSum != null)
                return string.IsNullOrEmpty(planSum.Id) ? planSum.Name : planSum.Id;

            return planningItem.ToString();
        }

        // ---------- Zeichen-Helfer ----------

        private static void DrawSectionTitle(DrawingContext dc, string title, double x, double y, double width, Brush fill, Pen border, Typeface typeface, CultureInfo culture)
        {
            dc.DrawRectangle(fill, border, new Rect(x, y, width, 28));
            RenderUtils.DrawText(dc, title, x + 8, y + 5, 14, Theme.PrimaryText, typeface, culture);
        }

        private static void DrawLabelValue(DrawingContext dc, string label, string value, double x, double y, double labelWidth, Typeface regular, Typeface bold, CultureInfo culture)
        {
            RenderUtils.DrawText(dc, label + ":", x, y, 11.5, Theme.MutedText, bold, culture);
            RenderUtils.DrawTextFit(dc, value ?? "", x + labelWidth, y, 310, 11.5, Theme.PrimaryText, regular, culture);
        }

        private static void DrawCellLines(DrawingContext dc, string text, double x, double y, double width, double fontSize, Typeface typeface, CultureInfo culture)
        {
            string[] lines = (text ?? "").Split(new[] { '\n' }, StringSplitOptions.None);
            double lineHeight = RenderUtils.ScaleFont(fontSize + 3);
            for (int i = 0; i < Math.Min(lines.Length, 3); i++)
                RenderUtils.DrawTextFit(dc, lines[i], x, y + i * lineHeight, width, fontSize, Theme.PrimaryText, typeface, culture);
        }

        // ---------- Felder-Tabelle ----------

        /// <summary>User-Origin des Planungsbildes (fuer Eclipse-konforme Isozentrum-Angaben).</summary>
        private static VVector? GetUserOrigin(StructureSet structureSet)
        {
            try
            {
                if (structureSet != null && structureSet.Image != null)
                    return structureSet.Image.UserOrigin;
            }
            catch
            {
            }
            return null;
        }

        /// <summary>
        /// Felderzeilen wie im Eclipse-Druck: Behandlungsfelder zuerst, Setup-Felder
        /// (z.B. kVCBCT) am Ende mit der Kennzeichnung "Setup-Feld" statt MU.
        /// </summary>
        private static List<BeamReportRow> BuildBeamReportRows(PlanningItem planningItem, VVector? userOrigin)
        {
            var beams = ReflectionUtils.GetEnumerableProperty(planningItem, "Beams").ToList();
            return beams
                .Where(beam => !ReflectionUtils.GetBoolProperty(beam, "IsSetupField"))
                .Concat(beams.Where(beam => ReflectionUtils.GetBoolProperty(beam, "IsSetupField")))
                .Select(beam => BuildBeamReportRow(beam, userOrigin))
                .ToList();
        }

        private static BeamReportRow BuildBeamReportRow(object beam, VVector? userOrigin)
        {
            bool isSetupField = ReflectionUtils.GetBoolProperty(beam, "IsSetupField");
            object firstControlPoint = ReflectionUtils.GetFirstEnumerableItem(ReflectionUtils.GetPropertyValue(beam, "ControlPoints"));
            object lastControlPoint = ReflectionUtils.GetLastEnumerableItem(ReflectionUtils.GetPropertyValue(beam, "ControlPoints"));
            object jawPositions = ReflectionUtils.GetPropertyValue(firstControlPoint, "JawPositions");
            object isocenter = ReflectionUtils.GetPropertyValue(beam, "IsocenterPosition");

            string energy = ReflectionUtils.GetStringProperty(beam, "EnergyModeDisplayName");
            string doseRate = ReflectionUtils.FormatNumber(ReflectionUtils.GetPropertyValue(beam, "DoseRate"), "F0");
            string energyDoseRate = energy;
            if (!string.IsNullOrEmpty(doseRate))
                energyDoseRate = string.IsNullOrEmpty(energy) ? doseRate : energy + "\n" + doseRate + " MU/min";

            string mlc = ReflectionUtils.GetStringProperty(beam, "MLCPlanType");
            if (mlc == "NotDefined")
                mlc = "";
            string wedge = ReflectionUtils.JoinEnumerableIds(beam, "Wedges");
            string mlcWedge = ReflectionUtils.FirstNonEmpty(mlc, wedge);
            if (!string.IsNullOrEmpty(mlc) && !string.IsNullOrEmpty(wedge))
                mlcWedge = mlc + "\n" + wedge;

            string bolus = ReflectionUtils.JoinEnumerableIds(beam, "Boluses");
            string ssd = FormatSsd(beam);
            string bolusSsd = bolus;
            if (!string.IsNullOrEmpty(ssd))
                bolusSsd = string.IsNullOrEmpty(bolus) ? ssd : bolus + "\n" + ssd;

            return new BeamReportRow
            {
                FieldId = ReflectionUtils.GetStringProperty(beam, "Id"),
                MachineScale = GetMachineScaleText(beam),
                EnergyDoseRate = energyDoseRate,
                Technique = ReflectionUtils.FirstNonEmpty(
                    ReflectionUtils.GetNestedStringProperty(beam, "Technique", "Id"),
                    ReflectionUtils.GetStringProperty(beam, "TechniqueId")),
                FieldSize = FormatJawPositions(jawPositions),
                Gantry = FormatGantry(firstControlPoint, lastControlPoint, beam),
                Collimator = FormatAngle(ReflectionUtils.GetPropertyValue(firstControlPoint, "CollimatorAngle")),
                Table = FormatAngle(ReflectionUtils.GetPropertyValue(firstControlPoint, "PatientSupportAngle")),
                Isocenter = FormatIsocenterEclipse(isocenter, userOrigin),
                MlcWedge = mlcWedge,
                BolusSsd = bolusSsd,
                Dose = isSetupField ? "" : FormatDoseValue(ReflectionUtils.GetPropertyValue(beam, "Dose")),
                Mu = isSetupField ? "Setup-Feld" : FormatMetersetValue(ReflectionUtils.GetPropertyValue(beam, "Meterset"))
            };
        }

        /// <summary>
        /// Isozentrum wie im Eclipse-Planbericht: relativ zum User-Origin,
        /// X (lat) = DICOM x, Y (lng) = DICOM z, Z (vrt) = -DICOM y (HFS-Konvention).
        /// </summary>
        private static string FormatIsocenterEclipse(object vector, VVector? userOrigin)
        {
            double? x = ReflectionUtils.GetNumericMember(vector, "x");
            double? y = ReflectionUtils.GetNumericMember(vector, "y");
            double? z = ReflectionUtils.GetNumericMember(vector, "z");
            if (!x.HasValue || !y.HasValue || !z.HasValue)
                return "";
            if (double.IsNaN(x.Value) || double.IsNaN(y.Value) || double.IsNaN(z.Value))
                return "";

            double relX = x.Value, relY = y.Value, relZ = z.Value;
            if (userOrigin.HasValue)
            {
                relX -= userOrigin.Value.x;
                relY -= userOrigin.Value.y;
                relZ -= userOrigin.Value.z;
            }

            return string.Format(RenderUtils.Num,
                "X: {0:0.00} cm (lat)\nY: {1:0.00} cm (lng)\nZ: {2:0.00} cm (vrt)",
                relX / 10.0,
                relZ / 10.0,
                -relY / 10.0);
        }

        private static string BuildNormalizationText(object planningItem)
        {
            string method = ReflectionUtils.GetStringProperty(planningItem, "PlanNormalizationMethod");
            string value = ReflectionUtils.FormatNumber(ReflectionUtils.GetPropertyValue(planningItem, "PlanNormalizationValue"), "F1");
            if (string.IsNullOrEmpty(value))
                return method;
            if (string.IsNullOrEmpty(method))
                return value + "%";
            return value + "% " + method;
        }

        private static string GetMachineScaleText(object beam)
        {
            object machine = ReflectionUtils.GetPropertyValue(beam, "TreatmentUnit");
            string unit = ReflectionUtils.FirstNonEmpty(
                ReflectionUtils.GetNestedStringProperty(beam, "TreatmentUnit", "Id"),
                "");
            string scale = ReflectionUtils.FirstNonEmpty(
                ReflectionUtils.GetStringProperty(beam, "MachineScale"),
                ReflectionUtils.GetStringProperty(machine, "MachineScale"));
            if (!string.IsNullOrEmpty(unit) && !string.IsNullOrEmpty(scale))
                return unit + "\n" + scale;
            return ReflectionUtils.FirstNonEmpty(unit, scale);
        }

        private static string FormatJawPositions(object jaws)
        {
            double? x1 = ReflectionUtils.GetNumericMember(jaws, "X1");
            double? x2 = ReflectionUtils.GetNumericMember(jaws, "X2");
            double? y1 = ReflectionUtils.GetNumericMember(jaws, "Y1");
            double? y2 = ReflectionUtils.GetNumericMember(jaws, "Y2");
            if (!x1.HasValue || !x2.HasValue || !y1.HasValue || !y2.HasValue)
                return "";

            double widthCm = Math.Abs(x2.Value - x1.Value) / 10.0;
            double heightCm = Math.Abs(y2.Value - y1.Value) / 10.0;
            return string.Format(RenderUtils.Num,
                "{0:0.0} cm x {1:0.0} cm\nX {2:0.0}/{3:0.0} cm\nY {4:0.0}/{5:0.0} cm",
                widthCm,
                heightCm,
                x1.Value / 10.0,
                x2.Value / 10.0,
                y1.Value / 10.0,
                y2.Value / 10.0);
        }

        private static string FormatGantry(object firstControlPoint, object lastControlPoint, object beam)
        {
            string first = FormatAngle(ReflectionUtils.GetPropertyValue(firstControlPoint, "GantryAngle"));
            string last = FormatAngle(ReflectionUtils.GetPropertyValue(lastControlPoint, "GantryAngle"));
            string direction = TranslateGantryDirection(ReflectionUtils.GetStringProperty(beam, "GantryDirection"));
            if (!string.IsNullOrEmpty(first) && !string.IsNullOrEmpty(last) && first != last)
                return first + "\nStop " + last + (string.IsNullOrEmpty(direction) ? "" : "\n" + direction);
            return string.IsNullOrEmpty(direction) ? first : first + "\n" + direction;
        }

        /// <summary>Drehrichtung wie im Eclipse-Druck: UZ (Uhrzeigersinn) / GUZ (gegen Uhrzeigersinn).</summary>
        private static string TranslateGantryDirection(string direction)
        {
            switch (direction ?? "")
            {
                case "Clockwise": return "UZ";
                case "CounterClockwise": return "GUZ";
                default: return "";
            }
        }

        private static string FormatSsd(object beam)
        {
            object fieldReferencePoint = ReflectionUtils.GetFirstEnumerableItem(ReflectionUtils.GetPropertyValue(beam, "FieldReferencePoints"));
            object ssdValue = ReflectionUtils.GetPropertyValue(fieldReferencePoint, "SSD");
            double? ssdMm = ReflectionUtils.TryConvertDouble(ssdValue);
            if (!ssdMm.HasValue || double.IsNaN(ssdMm.Value) || double.IsInfinity(ssdMm.Value))
                return "";
            return (ssdMm.Value / 10.0).ToString("F1", RenderUtils.Num) + " cm";
        }

        private static string FormatAngle(object value)
        {
            string number = ReflectionUtils.FormatNumber(value, "F1");
            return string.IsNullOrEmpty(number) ? "" : number + "°";
        }

        private static string FormatDoseValue(object value)
        {
            if (value == null)
                return "";
            double? dose = ReflectionUtils.GetNumericMember(value, "Dose");
            if (!dose.HasValue)
                dose = ReflectionUtils.TryConvertDouble(value);
            if (!dose.HasValue || double.IsNaN(dose.Value))
                return "";
            string unit = ReflectionUtils.GetStringProperty(value, "Unit");
            if (string.IsNullOrEmpty(unit))
                unit = "Gy";
            return dose.Value.ToString("F3", RenderUtils.Num) + " " + unit;
        }

        private static string FormatMetersetValue(object value)
        {
            if (value == null)
                return "";
            double? meterset = ReflectionUtils.GetNumericMember(value, "Value");
            if (!meterset.HasValue)
                meterset = ReflectionUtils.TryConvertDouble(value);
            if (!meterset.HasValue || double.IsNaN(meterset.Value))
                return "";
            string unit = ReflectionUtils.GetStringProperty(value, "Unit");
            return meterset.Value.ToString("F1", RenderUtils.Num) + (string.IsNullOrEmpty(unit) ? "" : " " + unit);
        }

        /// <summary>
        /// Ergaenzt fehlende Freigabedaten aus der ApprovalHistory (sofern die
        /// ESAPI-Version sie anbietet): letzter "PlanningApproved"- bzw.
        /// "TreatmentApproved"-Eintrag mit Benutzer und Zeitstempel.
        /// </summary>
        private static void FillFromApprovalHistory(
            object planningItem,
            ref string planningApprover, ref string planningDate,
            ref string treatmentApprover, ref string treatmentDate)
        {
            foreach (object entry in ReflectionUtils.GetEnumerableProperty(planningItem, "ApprovalHistory"))
            {
                string status = ReflectionUtils.FirstNonEmpty(
                    ReflectionUtils.GetStringProperty(entry, "ApprovalStatus"),
                    ReflectionUtils.GetStringProperty(entry, "Status"));
                string user = ReflectionUtils.FirstNonEmpty(
                    ReflectionUtils.GetStringProperty(entry, "UserDisplayName"),
                    ReflectionUtils.GetStringProperty(entry, "UserId"),
                    ReflectionUtils.GetStringProperty(entry, "UserName"));
                string date = ReflectionUtils.FirstNonEmpty(
                    ReflectionUtils.GetStringProperty(entry, "ApprovalDateTime"),
                    ReflectionUtils.GetStringProperty(entry, "Date"));

                if (string.IsNullOrEmpty(status))
                    continue;

                // spaetere Eintraege ueberschreiben fruehere -> letzter Stand gewinnt
                if (status.IndexOf("PlanningApproved", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (!string.IsNullOrEmpty(user)) planningApprover = ReflectionUtils.FirstNonEmpty(planningApprover, user);
                    if (!string.IsNullOrEmpty(date)) planningDate = ReflectionUtils.FirstNonEmpty(planningDate, date);
                }
                else if (status.IndexOf("TreatmentApproved", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         status.IndexOf("ExternallyApproved", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (!string.IsNullOrEmpty(user)) treatmentApprover = ReflectionUtils.FirstNonEmpty(treatmentApprover, user);
                    if (!string.IsNullOrEmpty(date)) treatmentDate = ReflectionUtils.FirstNonEmpty(treatmentDate, date);
                }
            }
        }

        /// <summary>Unterschriftenfeld: Rolle, vorbelegter Name/Datum aus ESAPI, Linie fuer Handzeichen.</summary>
        private static void DrawSignatureBox(
            DrawingContext dc, string role, string name, string date,
            double x, double y, double width, double height,
            Typeface regular, Typeface bold, Pen borderPen, CultureInfo culture)
        {
            dc.DrawRectangle(null, borderPen, new Rect(x, y, width, height));
            RenderUtils.DrawText(dc, role, x + 10, y + 8, 12, Theme.PrimaryText, bold, culture);

            string prefill = JoinDateAndUser(date, name).Replace("\n", "   ");
            if (!string.IsNullOrWhiteSpace(prefill))
                RenderUtils.DrawTextFit(dc, prefill, x + 10, y + 32, width - 20, 11, Theme.MutedText, regular, culture);

            double lineY = y + height - 30;
            dc.DrawLine(borderPen, new Point(x + 10, lineY), new Point(x + width - 10, lineY));
            RenderUtils.DrawText(dc, "Datum / Unterschrift", x + 10, lineY + 4, 10, Theme.FaintText, regular, culture);
        }

        private static string JoinDateAndUser(string date, string user)
        {
            if (string.IsNullOrEmpty(date))
                return user ?? "";
            if (string.IsNullOrEmpty(user))
                return date;
            return date + "\n" + user;
        }
    }
}
