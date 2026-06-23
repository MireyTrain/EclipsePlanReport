using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VMS.TPS.Common.Model.API;
using VMS.TPS.Common.Model.Types;

namespace EclipsePlanReport
{
    /// <summary>
    /// Erstellt den kompletten Planreport fuer einen bereits geoeffneten Patienten:
    /// pro Plan Berichtseite, Ansichtsseite, DVH + Statistik, CT-Schichten -
    /// anschliessend alles in ein PDF (A4 quer) zusammengefuehrt.
    /// </summary>
    internal class ReportEngine
    {
        private readonly Action<string> log;
        private readonly Action<double> progress;

        public ReportEngine(Action<string> log, Action<double> progress)
        {
            this.log = log ?? (msg => { });
            this.progress = progress ?? (fraction => { });
        }

        /// <summary>Erzeugt alle Seiten und das PDF. Liefert den PDF-Pfad oder null.</summary>
        public string Generate(Patient patient, List<PlanRequest> requests, List<ReportTemplate> templates, string outputDirectory)
        {
            var selected = requests.Where(r => r.Selected).ToList();
            if (selected.Count == 0)
            {
                log("Keine Plaene ausgewaehlt.");
                return null;
            }

            string runFolder = Path.Combine(
                outputDirectory,
                RenderUtils.MakeFilenameValid(string.Format("Planreport_{0}_{1:yyyyMMdd_HHmmss}", patient.Id, DateTime.Now)));
            Directory.CreateDirectory(runFolder);

            var generatedImagePaths = new List<string>();
            int processed = 0;

            for (int planIndex = 0; planIndex < selected.Count; planIndex++)
            {
                PlanRequest request = selected[planIndex];
                double planBase = planIndex / (double)selected.Count;
                double planShare = 1.0 / selected.Count;

                try
                {
                    Course course = patient.Courses.FirstOrDefault(c =>
                        !c.Id.Equals("PD", StringComparison.OrdinalIgnoreCase) &&
                        c.Id.Equals(request.CourseId, StringComparison.OrdinalIgnoreCase));
                    if (course == null)
                    {
                        log(string.Format("Kurs {0} nicht gefunden - {1} uebersprungen.", request.CourseId, request.PlanId));
                        continue;
                    }

                    PlanningItem planningItem = FindPlanningItem(course, request.PlanId);
                    if (planningItem == null)
                    {
                        log(string.Format("Plan {0} in Kurs {1} nicht gefunden.", request.PlanId, request.CourseId));
                        continue;
                    }

                    ReportTemplate template = ResolveTemplate(templates, planningItem, request.TemplateId);
                    if (planningItem is PlanSum && template.IsRelative)
                    {
                        ReportTemplate absolute = templates.FirstOrDefault(t => !t.IsRelative);
                        log(string.Format(
                            "Template '{0}' ist relativ und fuer Summenplaene gesperrt - benutze '{1}'.",
                            template.DisplayName,
                            absolute != null ? absolute.DisplayName : template.DisplayName));
                        if (absolute != null)
                            template = absolute;
                    }

                    // Benutzerdefinierte Isodosen aus dem Editor ueberschreiben das Template
                    // (Kopie, damit das Original fuer andere Plaene unveraendert bleibt).
                    if (request.CustomIsodoses != null && request.CustomIsodoses.Any())
                    {
                        template = CloneTemplateWithIsodoses(template, request.CustomIsodoses);
                        log(string.Format("  Angepasste Isodosen aktiv ({0} Level).", request.CustomIsodoses.Count));
                        if (planningItem is PlanSum && template.Isodoses.Any(i => i.DoseGy <= 0 && i.RelativeDosePercent > 0))
                            log("  Warnung: rein relative Level werden bei Summenplaenen nicht gezeichnet.");
                    }

                    StructureSet ss = planningItem.StructureSet;
                    if (ss == null || ss.Image == null)
                    {
                        log(string.Format("{0}: kein gueltiges StructureSet/Bild - uebersprungen.", request.PlanId));
                        continue;
                    }

                    Structure body = ss.Structures.FirstOrDefault(s =>
                        s.Id.ToLowerInvariant().StartsWith("body") ||
                        s.Id.ToLowerInvariant().StartsWith("körper") ||
                        s.Id.ToLowerInvariant().StartsWith("koerper") ||
                        s.Id.ToLowerInvariant().StartsWith("outer"));

                    Structure target = SelectSliceTargetStructure(ss, request.SliceTargetId, planningItem as PlanSetup, template);

                    string planItemId = PlanPageRenderer.GetPlanningItemId(planningItem);
                    log(string.Format("[{0}/{1}] {2} {3} (Template {4})...", planIndex + 1, selected.Count, request.PlanType, planItemId, template.DisplayName));

                    // 1) Planberichtseite (bei Summenplaenen nicht sinnvoll - Felder/
                    //    Verordnung haengen an den Einzelplaenen)
                    if (!(planningItem is PlanSum))
                    {
                        string planReportPath = Path.Combine(runFolder, RenderUtils.MakeFilenameValid(string.Format("{0}_{1}_01_Planbericht.png", patient.Id, planItemId)));
                        PlanPageRenderer.RenderPlanReportPage(patient, course, planningItem, ss, planReportPath);
                        AddIfExists(generatedImagePaths, planReportPath);
                    }
                    progress(planBase + planShare * 0.15);

                    // 2) Ansichtsseite (nachgebaute Eclipse-Planungsansicht)
                    var phaseWatch = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        string viewPath = Path.Combine(runFolder, RenderUtils.MakeFilenameValid(string.Format("{0}_{1}_02_Ansicht.png", patient.Id, planItemId)));
                        EclipseViewRenderer.RenderViewPage(patient, planningItem, ss, template, target, request.SelectedDisplayStructureIds, viewPath, log);
                        AddIfExists(generatedImagePaths, viewPath);
                    }
                    catch (Exception e)
                    {
                        log("  Ansichtsseite uebersprungen: " + e.Message);
                    }
                    log(string.Format(RenderUtils.Num, "  Zeit Ansichtsseite: {0:F1} s", phaseWatch.Elapsed.TotalSeconds));
                    progress(planBase + planShare * 0.35);

                    // 3) DVH + Dosisstatistik
                    if (request.ExportDvh)
                    {
                        phaseWatch.Restart();
                        try
                        {
                            string dvhPath = Path.Combine(runFolder, RenderUtils.MakeFilenameValid(string.Format("{0}_{1}_03_DVH.png", patient.Id, planItemId)));
                            List<string> dvhPages = DvhRenderer.RenderDvhPages(patient, planningItem, ss, template, request.SelectedDvhStructureIds, dvhPath, log);
                            foreach (string dvhPage in dvhPages)
                                AddIfExists(generatedImagePaths, dvhPage);
                            if (dvhPages.Count == 0)
                                log("  Kein DVH erstellt (keine passenden Strukturen).");
                        }
                        catch (Exception e)
                        {
                            log("  DVH uebersprungen: " + e.Message);
                        }
                        log(string.Format(RenderUtils.Num, "  Zeit DVH + Statistik: {0:F1} s", phaseWatch.Elapsed.TotalSeconds));
                    }
                    progress(planBase + planShare * 0.5);

                    // 4) CT-Schichten ueber den gesamten Bereich der Zielstruktur
                    if (request.ExportSlices)
                    {
                        if (target == null || target.IsEmpty)
                        {
                            log("  Keine Zielstruktur fuer den Schichtdruck gefunden - Schichten uebersprungen.");
                        }
                        else
                        {
                            phaseWatch.Restart();
                            SlicePageContext sliceContext = BuildSlicePageContext(patient, planningItem, ss, request.CourseId, planItemId);
                            RenderSliceSeries(patient, planningItem, ss, template, target, request.SelectedDisplayStructureIds, body, runFolder, planItemId, sliceContext, generatedImagePaths, planBase + planShare * 0.5, planShare * 0.5);
                            log(string.Format(RenderUtils.Num, "  Zeit CT-Schichten: {0:F1} s", phaseWatch.Elapsed.TotalSeconds));
                        }
                    }

                    processed++;
                    progress(planBase + planShare);
                }
                catch (Exception e)
                {
                    log(string.Format("Fehler bei {0}: {1}", request.PlanId, e.Message));
                }
            }

            if (generatedImagePaths.Count == 0)
            {
                log("Es wurden keine Seiten erzeugt.");
                return null;
            }

            string pdfPath = Path.Combine(runFolder, RenderUtils.MakeFilenameValid(string.Format("PlanReport_{0}_{1:yyyyMMdd_HHmmss}.pdf", patient.Id, DateTime.Now)));
            PdfWriter.CreatePdfFromImages(generatedImagePaths, pdfPath, log);
            log(string.Format("PDF erstellt: {0} ({1} Seiten, {2} Plan/Plaene)", pdfPath, generatedImagePaths.Count, processed));
            progress(1.0);

            return pdfPath;
        }

        /// <summary>Kopf-/Fusszeilendaten fuer den Eclipse-artigen Schichtdruck.</summary>
        private static SlicePageContext BuildSlicePageContext(Patient patient, PlanningItem planningItem, StructureSet ss, string courseId, string planItemId)
        {
            double maxDose3D = 0;
            try
            {
                planningItem.DoseValuePresentation = DoseValuePresentation.Absolute;
                object doseObj = planningItem.Dose;
                double? dm = ReflectionUtils.GetNumericMember(ReflectionUtils.GetPropertyValue(doseObj, "DoseMax3D"), "Dose");
                if (dm.HasValue && !double.IsNaN(dm.Value))
                    maxDose3D = dm.Value;
            }
            catch
            {
            }

            string rawOrientation = ReflectionUtils.FirstNonEmpty(
                ReflectionUtils.GetStringProperty(planningItem, "TreatmentOrientation"),
                ReflectionUtils.GetStringProperty(ss.Image, "ImagingOrientation"));
            string orientationText = RenderUtils.GetGermanOrientationText(rawOrientation);

            return new SlicePageContext
            {
                Patient = patient,
                PlanLabel = string.IsNullOrEmpty(courseId) ? planItemId : string.Format("{0} ({1})", planItemId, courseId),
                Hospital = ReflectionUtils.GetNestedStringProperty(patient, "Hospital", "Name"),
                OrientationText = orientationText,
                PatientPositionCode = RenderUtils.GetPatientPositionCode(rawOrientation),
                MaxDose3DGy = maxDose3D,
                PageNumber = 1,
                PageCount = 1
            };
        }

        private void RenderSliceSeries(
            Patient patient,
            PlanningItem planningItem,
            StructureSet ss,
            ReportTemplate template,
            Structure target,
            List<string> displayStructureIds,
            Structure body,
            string runFolder,
            string planItemId,
            SlicePageContext sliceContext,
            List<string> generatedImagePaths,
            double progressBase,
            double progressShare)
        {
            var targetSlices = SliceRenderer.GetStructureSlices(ss.Image, target);
            if (targetSlices.Count == 0)
            {
                log("  Zielstruktur hat keine Konturen - Schichten uebersprungen.");
                return;
            }

            int firstSlice = targetSlices.Min();
            int lastSlice = targetSlices.Max();

            double stepMm = template.SliceStepMm > 0 ? template.SliceStepMm : 10.0;
            int stepSlices = Math.Max(1, (int)Math.Round(stepMm / ss.Image.ZRes));

            var sliceIndices = new List<int>();
            for (int z = firstSlice; z <= lastSlice; z += stepSlices)
                sliceIndices.Add(z);
            if (!sliceIndices.Contains(lastSlice))
                sliceIndices.Add(lastSlice);
            string patientPositionCode = sliceContext != null ? sliceContext.PatientPositionCode : "";
            sliceIndices = sliceIndices
                .OrderByDescending(z => SliceRenderer.ComputeEclipseZcm(ss.Image, z, patientPositionCode))
                .ToList();
            var seriesViewBounds = SliceRenderer.GetSliceSeriesBodyViewBounds(body, ss.Image, sliceIndices);

            for (int i = 0; i < sliceIndices.Count; i++)
            {
                int z = sliceIndices[i];
                double zCm = SliceRenderer.ComputeEclipseZcm(ss.Image, z, patientPositionCode);
                string sliceFilename = RenderUtils.MakeFilenameValid(string.Format(
                    RenderUtils.Num,
                    "{0}_{1}_04_{2}_Schicht{3:00}_Z{4:+0.00;-0.00;0.00}cm.png",
                    patient.Id, planItemId, target.Id, i + 1, zCm));
                string slicePath = Path.Combine(runFolder, sliceFilename);

                try
                {
                    sliceContext.PageNumber = i + 1;
                    sliceContext.PageCount = sliceIndices.Count;
                    SliceRenderer.RenderSlicePage(ss.Image, z, ss, planningItem, template, target, displayStructureIds, body, slicePath, seriesViewBounds, sliceContext, log);
                    AddIfExists(generatedImagePaths, slicePath);
                }
                catch (Exception e)
                {
                    log(string.Format("  Schicht Z={0:F2} cm uebersprungen: {1}", zCm, e.Message));
                }

                progress(progressBase + progressShare * (i + 1) / (double)sliceIndices.Count);
            }

            log(string.Format("  {0} CT-Schichten gedruckt (Z {1:+0.00;-0.00;0.00} bis {2:+0.00;-0.00;0.00} cm).",
                sliceIndices.Count,
                SliceRenderer.ComputeEclipseZcm(ss.Image, sliceIndices.First(), patientPositionCode),
                SliceRenderer.ComputeEclipseZcm(ss.Image, sliceIndices.Last(), patientPositionCode)));
        }

        private static void AddIfExists(List<string> paths, string path)
        {
            VectorPdfPage page;
            if (File.Exists(path) || VectorPdfPageStore.TryGet(path, out page))
                paths.Add(path);
        }

        // ---------- Plan-/Template-/Zielstruktur-Aufloesung ----------

        public static PlanningItem FindPlanningItem(Course course, string planId)
        {
            PlanSetup planSetup = course.PlanSetups
                .FirstOrDefault(x => x.Id.Equals(planId, StringComparison.OrdinalIgnoreCase));
            if (planSetup != null)
                return planSetup;

            return course.PlanSums
                .FirstOrDefault(x =>
                    x.Id.Equals(planId, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrEmpty(x.Name) && x.Name.Equals(planId, StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>Kopie des Templates mit ersetzten Isodosen (fuer planindividuelle Anpassungen).</summary>
        private static ReportTemplate CloneTemplateWithIsodoses(ReportTemplate template, List<IsodoseLevel> isodoses)
        {
            return new ReportTemplate
            {
                Id = template.Id,
                DisplayName = template.DisplayName + " (angepasst)",
                SliceStepMm = template.SliceStepMm,
                TargetPatterns = template.TargetPatterns.ToList(),
                DvhPatterns = template.DvhPatterns.ToList(),
                Isodoses = isodoses.Select(i => i.Clone()).ToList()
            };
        }

        public static ReportTemplate ResolveTemplate(List<ReportTemplate> templates, PlanningItem planningItem, string requestedTemplateId)
        {
            string normalizedRequest = RenderUtils.NormalizeForMatch(requestedTemplateId);
            ReportTemplate selected = templates.FirstOrDefault(t =>
                RenderUtils.NormalizeForMatch(t.Id).Equals(normalizedRequest) ||
                RenderUtils.NormalizeForMatch(t.DisplayName).Equals(normalizedRequest));
            if (selected != null)
                return selected;

            string planId = RenderUtils.NormalizeForMatch(PlanPageRenderer.GetPlanningItemId(planningItem));
            selected = templates.FirstOrDefault(t => planId.Contains(RenderUtils.NormalizeForMatch(t.Id)));
            if (selected != null)
                return selected;

            selected = InferTemplateFromPlanId(templates, planId);
            if (selected != null)
                return selected;

            return templates.First();
        }

        public static ReportTemplate InferTemplateFromPlanId(List<ReportTemplate> templates, string normalizedPlanId)
        {
            if (normalizedPlanId.Contains("standard") || normalizedPlanId.Contains("voss"))
                return templates.FirstOrDefault(t => RenderUtils.NormalizeForMatch(t.Id).Contains("standardvoss"));
            if (normalizedPlanId.Contains("chhip"))
                return templates.FirstOrDefault(t => RenderUtils.NormalizeForMatch(t.Id).Contains("pcachhip"));
            if (normalizedPlanId.Contains("grp2") || normalizedPlanId.Contains("gruppe2"))
                return templates.FirstOrDefault(t => RenderUtils.NormalizeForMatch(t.Id).Contains("pcagrp2"));
            if (normalizedPlanId.Contains("grp3") || normalizedPlanId.Contains("gruppe3"))
                return templates.FirstOrDefault(t => RenderUtils.NormalizeForMatch(t.Id).Contains("pcagrp3"));
            if (normalizedPlanId.Contains("15") && normalizedPlanId.Contains("4"))
                return templates.FirstOrDefault(t => RenderUtils.NormalizeForMatch(t.Id).Contains("mamma") && RenderUtils.NormalizeForMatch(t.Id).Contains("154"));
            if (normalizedPlanId.Contains("15") && normalizedPlanId.Contains("5"))
                return templates.FirstOrDefault(t => RenderUtils.NormalizeForMatch(t.Id).Contains("mamma") && RenderUtils.NormalizeForMatch(t.Id).Contains("155"));
            if (normalizedPlanId.Contains("16") && normalizedPlanId.Contains("4"))
                return templates.FirstOrDefault(t => RenderUtils.NormalizeForMatch(t.Id).Contains("mamma") && RenderUtils.NormalizeForMatch(t.Id).Contains("164"));
            return null;
        }

        public static string InferTemplateIdForPlan(List<ReportTemplate> templates, string planId, bool isPlanSum)
        {
            ReportTemplate template = InferTemplateFromPlanId(templates, RenderUtils.NormalizeForMatch(planId));
            if (template != null && !(isPlanSum && template.IsRelative))
                return template.Id;

            ReportTemplate first = isPlanSum
                ? templates.FirstOrDefault(t => !t.IsRelative) ?? templates.FirstOrDefault()
                : templates.FirstOrDefault();
            return first != null ? first.Id : "";
        }

        public static Structure SelectSliceTargetStructure(StructureSet structureSet, string selectedTargetId, PlanSetup planSetup, ReportTemplate template)
        {
            if (!string.IsNullOrEmpty(selectedTargetId))
            {
                Structure selected = structureSet.Structures.FirstOrDefault(x =>
                    !x.IsEmpty && x.Id.Equals(selectedTargetId, StringComparison.OrdinalIgnoreCase));
                if (selected != null)
                    return selected;
            }

            if (planSetup != null && !string.IsNullOrEmpty(planSetup.TargetVolumeID))
            {
                Structure target = structureSet.Structures.FirstOrDefault(x =>
                    x.Id.Equals(planSetup.TargetVolumeID, StringComparison.OrdinalIgnoreCase));
                if (target != null && !target.IsEmpty)
                    return target;
            }

            return structureSet.Structures
                .Where(x => !x.IsEmpty && RenderUtils.MatchesAnyPattern(x.Id, template.TargetPatterns))
                .OrderByDescending(x => x.Id.ToUpperInvariant().StartsWith("PTV"))
                .ThenByDescending(x => x.Volume)
                .FirstOrDefault();
        }
    }
}
