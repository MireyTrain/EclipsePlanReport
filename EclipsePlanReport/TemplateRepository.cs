using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Xml.Linq;

namespace EclipsePlanReport
{
    /// <summary>Laedt die Isodosen-Templates aus report_templates.xml.</summary>
    internal static class TemplateRepository
    {
        private const string TemplateFileName = "report_templates.xml";

        public static List<ReportTemplate> Load(Action<string> log)
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, TemplateFileName);
            if (!File.Exists(path))
                path = TemplateFileName;

            if (!File.Exists(path))
            {
                if (log != null)
                    log("report_templates.xml nicht gefunden, benutze Standard-Isodosen.");
                return CreateFallbackTemplates();
            }

            XDocument document = XDocument.Load(path);
            List<ReportTemplate> templates = document.Root
                .Elements("Template")
                .Select(t => new ReportTemplate
                {
                    Id = (string)t.Attribute("id") ?? "default",
                    DisplayName = (string)t.Attribute("displayName") ?? (string)t.Attribute("id") ?? "Default",
                    SliceStepMm = ReadDoubleAttribute(t, "sliceStepMm", 10.0),
                    TargetPatterns = SplitPatterns((string)t.Attribute("targetPatterns"), "PTV*"),
                    DvhPatterns = SplitPatterns((string)t.Attribute("dvhPatterns"), ""),
                    Isodoses = t.Elements("Isodose")
                        .Select(i => new IsodoseLevel
                        {
                            DoseGy = ReadDoubleAttribute(i, "doseGy", 0.0),
                            RelativeDosePercent = ReadDoubleAttribute(i, "relativeDosePercent", 0.0),
                            Color = ParseColor((string)i.Attribute("color") ?? "#FFFF00"),
                            Thickness = ReadDoubleAttribute(i, "thickness", 1.0)
                        })
                        .Where(i => i.DoseGy > 0 || i.RelativeDosePercent > 0)
                        .OrderByDescending(i => i.DoseGy > 0 ? i.DoseGy : i.RelativeDosePercent)
                        .ToList()
                })
                .Where(t => t.Isodoses.Count > 0)
                .ToList();

            if (templates.Count == 0)
                return CreateFallbackTemplates();

            return templates;
        }

        /// <summary>
        /// Speichert ein Template in report_templates.xml (gleiche Id wird ersetzt).
        /// Liefert true bei Erfolg.
        /// </summary>
        public static bool Save(ReportTemplate template, Action<string> log)
        {
            try
            {
                string path = GetTemplatePath();
                XDocument document = File.Exists(path)
                    ? XDocument.Load(path)
                    : new XDocument(new XElement("ReportTemplates"));

                if (document.Root == null)
                    document.Add(new XElement("ReportTemplates"));

                document.Root
                    .Elements("Template")
                    .Where(t => string.Equals((string)t.Attribute("id"), template.Id, StringComparison.OrdinalIgnoreCase))
                    .Remove();

                document.Root.Add(ToElement(template));
                document.Save(path);
                if (log != null)
                    log(string.Format("Template '{0}' in {1} gespeichert.", template.DisplayName, TemplateFileName));
                return true;
            }
            catch (Exception e)
            {
                if (log != null)
                    log("Template konnte nicht gespeichert werden: " + e.Message);
                return false;
            }
        }

        /// <summary>Speichert die komplette Templateliste in der uebergebenen Reihenfolge.</summary>
        public static bool SaveAll(IEnumerable<ReportTemplate> templates, Action<string> log)
        {
            try
            {
                List<ReportTemplate> list = templates != null
                    ? templates.Where(t => t != null).ToList()
                    : new List<ReportTemplate>();
                if (list.Count == 0)
                {
                    if (log != null)
                        log("Keine Templates zum Speichern vorhanden.");
                    return false;
                }

                string path = GetTemplatePath();
                var document = new XDocument(
                    new XDeclaration("1.0", "utf-8", null),
                    new XElement("ReportTemplates", list.Select(ToElement)));
                document.Save(path);

                if (log != null)
                    log(string.Format("{0} Template(s) in {1} gespeichert.", list.Count, TemplateFileName));
                return true;
            }
            catch (Exception e)
            {
                if (log != null)
                    log("Templates konnten nicht gespeichert werden: " + e.Message);
                return false;
            }
        }

        private static string GetTemplatePath()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, TemplateFileName);
        }

        private static XElement ToElement(ReportTemplate template)
        {
            var element = new XElement("Template",
                new XAttribute("id", template.Id),
                new XAttribute("displayName", template.DisplayName),
                new XAttribute("sliceStepMm", template.SliceStepMm.ToString("F1", CultureInfo.InvariantCulture)),
                new XAttribute("targetPatterns", string.Join(",", template.TargetPatterns)),
                new XAttribute("dvhPatterns", string.Join(",", template.DvhPatterns)));

            foreach (IsodoseLevel iso in template.Isodoses)
            {
                var isoElement = new XElement("Isodose",
                    new XAttribute("color", string.Format("#{0:X2}{1:X2}{2:X2}", iso.Color.R, iso.Color.G, iso.Color.B)),
                    new XAttribute("thickness", iso.Thickness.ToString("F1", CultureInfo.InvariantCulture)));
                if (iso.DoseGy > 0)
                    isoElement.Add(new XAttribute("doseGy", iso.DoseGy.ToString("F3", CultureInfo.InvariantCulture)));
                if (iso.RelativeDosePercent > 0)
                    isoElement.Add(new XAttribute("relativeDosePercent", iso.RelativeDosePercent.ToString("F1", CultureInfo.InvariantCulture)));
                element.Add(isoElement);
            }

            return element;
        }

        private static List<string> SplitPatterns(string raw, string fallback)
        {
            return (raw ?? fallback)
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .ToList();
        }

        private static List<ReportTemplate> CreateFallbackTemplates()
        {
            return new List<ReportTemplate>
            {
                new ReportTemplate
                {
                    Id = "default",
                    DisplayName = "Default",
                    SliceStepMm = 10.0,
                    TargetPatterns = new List<string> { "PTV*" },
                    DvhPatterns = new List<string> { "PTV*" },
                    Isodoses = new List<IsodoseLevel>
                    {
                        new IsodoseLevel { DoseGy = 77.0, Color = Color.FromRgb(255, 0, 0), Thickness = 1.0 },
                        new IsodoseLevel { DoseGy = 70.0, Color = Color.FromRgb(255, 255, 0), Thickness = 2.0 },
                        new IsodoseLevel { DoseGy = 60.0, Color = Color.FromRgb(0, 255, 0), Thickness = 1.0 },
                        new IsodoseLevel { DoseGy = 50.0, Color = Color.FromRgb(0, 0, 255), Thickness = 1.0 },
                        new IsodoseLevel { DoseGy = 30.0, Color = Color.FromRgb(0, 255, 255), Thickness = 1.0 }
                    }
                }
            };
        }

        private static double ReadDoubleAttribute(XElement element, string name, double fallback)
        {
            string raw = (string)element.Attribute(name);
            double value;
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                return value;
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.GetCultureInfo("de-DE"), out value))
                return value;
            return fallback;
        }

        private static Color ParseColor(string value)
        {
            try
            {
                return (Color)ColorConverter.ConvertFromString(value);
            }
            catch
            {
                return Colors.Yellow;
            }
        }
    }
}
