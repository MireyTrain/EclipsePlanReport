using System.Collections.Generic;
using System.Linq;
using VMS.TPS.Common.Model.API;
using VMS.TPS.Common.Model.Types;

namespace EclipsePlanReport
{
    /// <summary>Auswahl und Optionen fuer einen Plan bzw. Summenplan.</summary>
    internal class PlanRequest
    {
        public string PatientId { get; set; }
        public string CourseId { get; set; }
        public string PlanType { get; set; }   // "PlanSetup" oder "PlanSum"
        public string PlanId { get; set; }
        public string TemplateId { get; set; }
        public bool Selected { get; set; }
        public bool ExportDvh { get; set; }
        public bool ExportSlices { get; set; }
        public string SliceTargetId { get; set; }
        public string PrescriptionInfo { get; set; }
        public string StatusInfo { get; set; }
        public bool DisplaySelectionCustomized { get; set; }
        public bool DvhSelectionCustomized { get; set; }
        public List<string> AvailableSliceTargetIds { get; set; }
        public List<string> AvailableDisplayStructureIds { get; set; }
        public List<string> AvailableDvhStructureIds { get; set; }
        public List<string> SelectedDisplayStructureIds { get; set; }
        public List<string> SelectedDvhStructureIds { get; set; }

        /// <summary>Verordnete Gesamtdosis in Gy (0 = unbekannt, z. B. Summenplan) - fuer %-Gy-Umrechnung im Isodosen-Editor.</summary>
        public double TotalDoseGy { get; set; }

        /// <summary>Benutzerdefinierte Isodosen; null = Template unveraendert verwenden.</summary>
        public List<IsodoseLevel> CustomIsodoses { get; set; }

        public bool IsPlanSum { get { return PlanType == "PlanSum"; } }

        public PlanRequest()
        {
            AvailableSliceTargetIds = new List<string>();
            AvailableDisplayStructureIds = new List<string>();
            AvailableDvhStructureIds = new List<string>();
            SelectedDisplayStructureIds = new List<string>();
            SelectedDvhStructureIds = new List<string>();
            Selected = true;
            ExportDvh = true;
            ExportSlices = true;
        }
    }

    internal class ReportTemplate
    {
        public string Id { get; set; }
        public string DisplayName { get; set; }
        public double SliceStepMm { get; set; }
        public List<string> TargetPatterns { get; set; }
        public List<string> DvhPatterns { get; set; }
        public List<IsodoseLevel> Isodoses { get; set; }

        /// <summary>true, wenn mindestens ein Level relativ (in %) definiert ist.</summary>
        public bool IsRelative { get { return Isodoses.Any(i => i.RelativeDosePercent > 0); } }

        public ReportTemplate()
        {
            TargetPatterns = new List<string>();
            DvhPatterns = new List<string>();
            Isodoses = new List<IsodoseLevel>();
        }
    }

    internal class IsodoseLevel
    {
        public double DoseGy { get; set; }
        public double RelativeDosePercent { get; set; }
        public System.Windows.Media.Color Color { get; set; }
        public double Thickness { get; set; }

        public IsodoseLevel Clone()
        {
            return new IsodoseLevel
            {
                DoseGy = DoseGy,
                RelativeDosePercent = RelativeDosePercent,
                Color = Color,
                Thickness = Thickness
            };
        }
    }

    internal class DvhCurveInfo
    {
        public Structure Structure { get; set; }
        public DVHData Dvh { get; set; }
        public System.Windows.Media.Color Color { get; set; }
    }

    internal class PdfImageInfo
    {
        public byte[] JpegBytes { get; set; }
        public int PixelWidth { get; set; }
        public int PixelHeight { get; set; }
    }

    /// <summary>Kontext fuer den Eclipse-artigen Schichtdruck (Kopf-/Fusszeile, Seitenzahl).</summary>
    internal class SlicePageContext
    {
        public Patient Patient { get; set; }
        public string PlanLabel { get; set; }
        public string Hospital { get; set; }
        public string OrientationText { get; set; }
        public string PatientPositionCode { get; set; }
        public double MaxDose3DGy { get; set; }
        public int PageNumber { get; set; }
        public int PageCount { get; set; }
    }

    internal class BeamReportRow
    {
        public string FieldId { get; set; }
        public string MachineScale { get; set; }
        public string EnergyDoseRate { get; set; }
        public string Technique { get; set; }
        public string FieldSize { get; set; }
        public string Gantry { get; set; }
        public string Collimator { get; set; }
        public string Table { get; set; }
        public string Isocenter { get; set; }
        public string MlcWedge { get; set; }
        public string BolusSsd { get; set; }
        public string Dose { get; set; }
        public string Mu { get; set; }
    }
}
