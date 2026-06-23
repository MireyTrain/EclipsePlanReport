using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using VMS.TPS.Common.Model.API;
using Forms = System.Windows.Forms;

namespace EclipsePlanReport
{
    /// <summary>
    /// Ein-Fenster-Bedienung: Patient laden, Plaene konfigurieren (Template, Schicht-PTV,
    /// DVH-Strukturen), Bericht erstellen - mit Fortschritt und Protokoll im selben Fenster.
    /// </summary>
    internal class MainForm : Forms.Form
    {
        private Application esapiApp;
        private Patient currentPatient;
        private List<ReportTemplate> templates = new List<ReportTemplate>();
        private List<PlanRequest> planRequests = new List<PlanRequest>();
        private PlanRequest currentDisplayPlan;
        private PlanRequest currentDvhPlan;
        private bool displayListLoading;
        private bool dvhListLoading;
        private string lastPdfPath;

        private Forms.TextBox txtPatientId;
        private Forms.Button btnLoad;
        private Forms.TextBox txtOutputDir;
        private Forms.Button btnBrowse;
        private Forms.NumericUpDown numFontScale;
        private Forms.CheckBox chkDarkMode;
        private Forms.Label lblPatientInfo;
        private Forms.DataGridView grid;
        private Forms.Label lblDisplayTitle;
        private Forms.CheckedListBox clbDisplay;
        private Forms.Button btnDisplayRecommended;
        private Forms.Button btnDisplayAll;
        private Forms.Button btnDisplayNone;
        private Forms.Label lblDvhTitle;
        private Forms.CheckedListBox clbDvh;
        private Forms.Button btnDvhRecommended;
        private Forms.Button btnDvhAll;
        private Forms.Button btnDvhNone;
        private Forms.TextBox txtLog;
        private Forms.ProgressBar progressBar;
        private Forms.Button btnStart;
        private Forms.Button btnOpenPdf;
        private Forms.Button btnOpenFolder;
        private Forms.Button btnTemplateUp;
        private Forms.Button btnTemplateDown;
        private Forms.Button btnDeleteTemplate;

        private static readonly string SettingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "planreport_settings.txt");

        public MainForm()
        {
            BuildLayout();
            LoadSettings();
            templates = TemplateRepository.Load(Log);
            Log(string.Format("{0} Isodosen-Template(s) geladen.", templates.Count));
            Log("Patient-ID eingeben und 'Patient laden' klicken.");
        }

        // ---------- Layout ----------

        private void BuildLayout()
        {
            Text = "Eclipse PlanReport";
            StartPosition = Forms.FormStartPosition.CenterScreen;
            ClientSize = new System.Drawing.Size(1440, 820);
            MinimumSize = new System.Drawing.Size(1280, 720);

            // Kopfzeile
            var lblPatient = new Forms.Label { Text = "Patient-ID", Left = 12, Top = 18, Width = 70 };
            txtPatientId = new Forms.TextBox { Left = 86, Top = 14, Width = 160 };
            btnLoad = new Forms.Button { Text = "Patient laden", Left = 256, Top = 12, Width = 110 };

            var lblOut = new Forms.Label { Text = "Ausgabeordner", Left = 392, Top = 18, Width = 95 };
            txtOutputDir = new Forms.TextBox
            {
                Left = 490,
                Top = 14,
                Width = 540,
                Anchor = Forms.AnchorStyles.Top | Forms.AnchorStyles.Left | Forms.AnchorStyles.Right,
                Text = @"C:\EclipsePlanReport"
            };
            btnBrowse = new Forms.Button
            {
                Text = "...",
                Left = 1036,
                Top = 12,
                Width = 36,
                Anchor = Forms.AnchorStyles.Top | Forms.AnchorStyles.Right
            };

            var lblFont = new Forms.Label
            {
                Text = "Schrift",
                Left = 1092,
                Top = 18,
                Width = 50,
                Anchor = Forms.AnchorStyles.Top | Forms.AnchorStyles.Right
            };
            numFontScale = new Forms.NumericUpDown
            {
                Left = 1146,
                Top = 14,
                Width = 64,
                DecimalPlaces = 2,
                Increment = 0.05M,
                Minimum = 0.75M,
                Maximum = 1.60M,
                Value = 1.00M,
                Anchor = Forms.AnchorStyles.Top | Forms.AnchorStyles.Right
            };

            chkDarkMode = new Forms.CheckBox
            {
                Text = "Dark Mode",
                Left = 1116,
                Top = 44,
                Width = 96,
                Anchor = Forms.AnchorStyles.Top | Forms.AnchorStyles.Right
            };

            lblPatientInfo = new Forms.Label
            {
                Left = 12,
                Top = 46,
                Width = 880,
                Anchor = Forms.AnchorStyles.Top | Forms.AnchorStyles.Left | Forms.AnchorStyles.Right,
                Text = "Kein Patient geladen.",
                ForeColor = System.Drawing.Color.DimGray
            };

            // Planliste
            grid = new Forms.DataGridView
            {
                Left = 12,
                Top = 108,
                Width = ClientSize.Width - 12 - 520 - 12,
                Height = 478,
                Anchor = Forms.AnchorStyles.Top | Forms.AnchorStyles.Bottom | Forms.AnchorStyles.Left | Forms.AnchorStyles.Right,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AutoSizeColumnsMode = Forms.DataGridViewAutoSizeColumnsMode.Fill,
                SelectionMode = Forms.DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                RowHeadersVisible = false
            };
            grid.Columns.Add(new Forms.DataGridViewCheckBoxColumn { Name = "Selected", HeaderText = "Drucken", FillWeight = 9 });
            grid.Columns.Add(new Forms.DataGridViewTextBoxColumn { Name = "CourseId", HeaderText = "Kurs", ReadOnly = true, FillWeight = 13 });
            grid.Columns.Add(new Forms.DataGridViewTextBoxColumn { Name = "PlanType", HeaderText = "Typ", ReadOnly = true, FillWeight = 11 });
            grid.Columns.Add(new Forms.DataGridViewTextBoxColumn { Name = "PlanId", HeaderText = "Plan / Summe", ReadOnly = true, FillWeight = 22 });
            grid.Columns.Add(new Forms.DataGridViewTextBoxColumn { Name = "Prescription", HeaderText = "Verordnung", ReadOnly = true, FillWeight = 17 });
            grid.Columns.Add(new Forms.DataGridViewTextBoxColumn { Name = "Status", HeaderText = "Status", ReadOnly = true, FillWeight = 14 });
            grid.Columns.Add(new Forms.DataGridViewComboBoxColumn { Name = "SliceTargetId", HeaderText = "Schicht-PTV", FillWeight = 20, FlatStyle = Forms.FlatStyle.Flat });
            grid.Columns.Add(new Forms.DataGridViewComboBoxColumn { Name = "TemplateId", HeaderText = "Isodosen-Template", FillWeight = 22, FlatStyle = Forms.FlatStyle.Flat });
            grid.Columns.Add(new Forms.DataGridViewButtonColumn { Name = "EditIsodoses", HeaderText = "Isodosen", FillWeight = 12, Text = "Anpassen...", UseColumnTextForButtonValue = false });
            grid.Columns.Add(new Forms.DataGridViewCheckBoxColumn { Name = "ExportDvh", HeaderText = "DVH", FillWeight = 8 });
            grid.Columns.Add(new Forms.DataGridViewCheckBoxColumn { Name = "ExportSlices", HeaderText = "Schichten", FillWeight = 11 });

            grid.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (grid.IsCurrentCellDirty)
                    grid.CommitEdit(Forms.DataGridViewDataErrorContexts.Commit);
            };
            grid.CellValueChanged += Grid_CellValueChanged;
            grid.CellContentClick += Grid_CellContentClick;
            grid.SelectionChanged += (s, e) => RefreshStructurePanels();
            grid.DataError += (s, e) => { e.ThrowException = false; };

            var lblTemplateTools = new Forms.Label
            {
                Text = "Template",
                Left = 12,
                Top = 82,
                Width = 66,
                Height = 20
            };
            btnTemplateUp = new Forms.Button
            {
                Text = "Hoch",
                Left = 84,
                Top = 78,
                Width = 68,
                Height = 24
            };
            btnTemplateDown = new Forms.Button
            {
                Text = "Runter",
                Left = 158,
                Top = 78,
                Width = 72,
                Height = 24
            };
            btnDeleteTemplate = new Forms.Button
            {
                Text = "Loeschen",
                Left = 236,
                Top = 78,
                Width = 86,
                Height = 24
            };

            // Struktur-Panels rechts
            int panelLeft = ClientSize.Width - 520;
            int listWidth = 248;
            int dvhLeft = panelLeft + listWidth + 12;
            lblDisplayTitle = new Forms.Label
            {
                Left = panelLeft,
                Top = 76,
                Width = listWidth,
                Height = 34,
                Anchor = Forms.AnchorStyles.Top | Forms.AnchorStyles.Right,
                Text = "Anzeige-Strukturen"
            };
            clbDisplay = new Forms.CheckedListBox
            {
                Left = panelLeft,
                Top = 112,
                Width = listWidth,
                Height = 436,
                Anchor = Forms.AnchorStyles.Top | Forms.AnchorStyles.Bottom | Forms.AnchorStyles.Right,
                CheckOnClick = true,
                IntegralHeight = false
            };
            clbDisplay.ItemCheck += ClbDisplay_ItemCheck;

            btnDisplayRecommended = new Forms.Button
            {
                Text = "Empfohlen",
                Left = panelLeft,
                Top = 554,
                Width = 92,
                Anchor = Forms.AnchorStyles.Bottom | Forms.AnchorStyles.Right
            };
            btnDisplayAll = new Forms.Button
            {
                Text = "Alle",
                Left = panelLeft + 98,
                Top = 554,
                Width = 64,
                Anchor = Forms.AnchorStyles.Bottom | Forms.AnchorStyles.Right
            };
            btnDisplayNone = new Forms.Button
            {
                Text = "Keine",
                Left = panelLeft + 168,
                Top = 554,
                Width = 64,
                Anchor = Forms.AnchorStyles.Bottom | Forms.AnchorStyles.Right
            };

            lblDvhTitle = new Forms.Label
            {
                Left = dvhLeft,
                Top = 76,
                Width = listWidth,
                Height = 34,
                Anchor = Forms.AnchorStyles.Top | Forms.AnchorStyles.Right,
                Text = "DVH-Strukturen"
            };
            clbDvh = new Forms.CheckedListBox
            {
                Left = dvhLeft,
                Top = 112,
                Width = listWidth,
                Height = 436,
                Anchor = Forms.AnchorStyles.Top | Forms.AnchorStyles.Bottom | Forms.AnchorStyles.Right,
                CheckOnClick = true,
                IntegralHeight = false
            };
            clbDvh.ItemCheck += ClbDvh_ItemCheck;

            btnDvhRecommended = new Forms.Button
            {
                Text = "Empfohlen",
                Left = dvhLeft,
                Top = 554,
                Width = 92,
                Anchor = Forms.AnchorStyles.Bottom | Forms.AnchorStyles.Right
            };
            btnDvhAll = new Forms.Button
            {
                Text = "Alle",
                Left = dvhLeft + 98,
                Top = 554,
                Width = 64,
                Anchor = Forms.AnchorStyles.Bottom | Forms.AnchorStyles.Right
            };
            btnDvhNone = new Forms.Button
            {
                Text = "Keine",
                Left = dvhLeft + 168,
                Top = 554,
                Width = 64,
                Anchor = Forms.AnchorStyles.Bottom | Forms.AnchorStyles.Right
            };
            btnDisplayRecommended.Click += (s, e) => ApplyRecommendedDisplay();
            btnDisplayAll.Click += (s, e) => SetAllDisplayChecks(true);
            btnDisplayNone.Click += (s, e) => SetAllDisplayChecks(false);
            btnDvhRecommended.Click += (s, e) => ApplyRecommendedDvh();
            btnDvhAll.Click += (s, e) => SetAllDvhChecks(true);
            btnDvhNone.Click += (s, e) => SetAllDvhChecks(false);
            btnTemplateUp.Click += (s, e) => MoveSelectedTemplate(-1);
            btnTemplateDown.Click += (s, e) => MoveSelectedTemplate(1);
            btnDeleteTemplate.Click += (s, e) => DeleteSelectedTemplate();

            // Protokoll + Fortschritt + Aktionen
            txtLog = new Forms.TextBox
            {
                Left = 12,
                Top = 596,
                Width = ClientSize.Width - 24,
                Height = 140,
                Anchor = Forms.AnchorStyles.Bottom | Forms.AnchorStyles.Left | Forms.AnchorStyles.Right,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = Forms.ScrollBars.Vertical,
                BackColor = System.Drawing.Color.White
            };

            progressBar = new Forms.ProgressBar
            {
                Left = 12,
                Top = 744,
                Width = ClientSize.Width - 24,
                Height = 16,
                Anchor = Forms.AnchorStyles.Bottom | Forms.AnchorStyles.Left | Forms.AnchorStyles.Right,
                Minimum = 0,
                Maximum = 1000
            };

            btnOpenFolder = new Forms.Button
            {
                Text = "Ordner oeffnen",
                Left = ClientSize.Width - 12 - 150 - 130 - 130 - 16,
                Top = 770,
                Width = 122,
                Anchor = Forms.AnchorStyles.Bottom | Forms.AnchorStyles.Right,
                Enabled = false
            };
            btnOpenPdf = new Forms.Button
            {
                Text = "PDF oeffnen",
                Left = ClientSize.Width - 12 - 150 - 130 - 8,
                Top = 770,
                Width = 122,
                Anchor = Forms.AnchorStyles.Bottom | Forms.AnchorStyles.Right,
                Enabled = false
            };
            btnStart = new Forms.Button
            {
                Text = "Bericht erstellen",
                Left = ClientSize.Width - 12 - 150,
                Top = 770,
                Width = 150,
                Anchor = Forms.AnchorStyles.Bottom | Forms.AnchorStyles.Right,
                Enabled = false
            };

            btnLoad.Click += (s, e) => LoadPatient();
            btnBrowse.Click += (s, e) => BrowseOutputDir();
            btnStart.Click += (s, e) => RunReport();
            btnOpenPdf.Click += (s, e) => OpenPath(lastPdfPath);
            btnOpenFolder.Click += (s, e) => OpenPath(Path.GetDirectoryName(lastPdfPath));
            txtPatientId.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Forms.Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    LoadPatient();
                }
            };
            FormClosed += (s, e) => CleanupEsapi();

            Controls.AddRange(new Forms.Control[]
            {
                lblPatient, txtPatientId, btnLoad,
                lblOut, txtOutputDir, btnBrowse,
                lblFont, numFontScale, chkDarkMode,
                lblPatientInfo,
                lblTemplateTools, btnTemplateUp, btnTemplateDown, btnDeleteTemplate,
                grid,
                lblDisplayTitle, clbDisplay, btnDisplayRecommended, btnDisplayAll, btnDisplayNone,
                lblDvhTitle, clbDvh, btnDvhRecommended, btnDvhAll, btnDvhNone,
                txtLog, progressBar,
                btnOpenFolder, btnOpenPdf, btnStart
            });
        }

        // ---------- Einstellungen ----------

        private void LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsPath))
                    return;
                foreach (string line in File.ReadAllLines(SettingsPath))
                {
                    int idx = line.IndexOf('=');
                    if (idx <= 0)
                        continue;
                    string key = line.Substring(0, idx).Trim();
                    string value = line.Substring(idx + 1).Trim();
                    if (key == "output" && !string.IsNullOrEmpty(value))
                        txtOutputDir.Text = value;
                    if (key == "fontscale")
                    {
                        decimal scale;
                        if (decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out scale) &&
                            scale >= numFontScale.Minimum && scale <= numFontScale.Maximum)
                            numFontScale.Value = scale;
                    }
                    if (key == "darkmode")
                        chkDarkMode.Checked = value.Equals("1") || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
            }
        }

        private void SaveSettings()
        {
            try
            {
                File.WriteAllLines(SettingsPath, new[]
                {
                    "output=" + txtOutputDir.Text.Trim(),
                    "fontscale=" + ((double)numFontScale.Value).ToString("F2", CultureInfo.InvariantCulture),
                    "darkmode=" + (chkDarkMode.Checked ? "1" : "0")
                });
            }
            catch
            {
            }
        }

        // ---------- ESAPI ----------

        private bool EnsureEsapi()
        {
            if (esapiApp != null)
                return true;

            try
            {
                Log("Verbinde mit Eclipse (ESAPI)...");
                Forms.Application.DoEvents();
                esapiApp = Application.CreateApplication();
                Log("Verbindung hergestellt.");
                return true;
            }
            catch (Exception e)
            {
                Log("ESAPI-Verbindung fehlgeschlagen: " + e.Message);
                Forms.MessageBox.Show(this,
                    "Verbindung zu Eclipse/ESAPI fehlgeschlagen:\n" + e.Message,
                    "Eclipse PlanReport", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Error);
                return false;
            }
        }

        private void CleanupEsapi()
        {
            try
            {
                if (esapiApp != null)
                {
                    if (currentPatient != null)
                        esapiApp.ClosePatient();
                    esapiApp.Dispose();
                }
            }
            catch
            {
            }
            esapiApp = null;
            currentPatient = null;
        }

        // ---------- Patient laden ----------

        private void LoadPatient()
        {
            string patientId = txtPatientId.Text.Trim();
            if (string.IsNullOrEmpty(patientId))
            {
                Forms.MessageBox.Show(this, "Bitte eine Patient-ID eingeben.", "Eclipse PlanReport", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Warning);
                return;
            }

            if (!EnsureEsapi())
                return;

            UseWaitCursor = true;
            btnLoad.Enabled = false;
            try
            {
                if (currentPatient != null)
                {
                    esapiApp.ClosePatient();
                    currentPatient = null;
                    grid.Rows.Clear();
                    planRequests.Clear();
                    ClearStructurePanels();
                }

                currentPatient = esapiApp.OpenPatientById(patientId);
                if (currentPatient == null)
                {
                    Log(string.Format("Patient {0} nicht gefunden.", patientId));
                    Forms.MessageBox.Show(this, string.Format("Patient {0} wurde nicht gefunden.", patientId), "Eclipse PlanReport", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Warning);
                    return;
                }

                lblPatientInfo.Text = RenderUtils.BuildPatientHeader(currentPatient);
                lblPatientInfo.ForeColor = System.Drawing.Color.Black;
                Log("Patient geladen: " + lblPatientInfo.Text);

                planRequests = BuildPlanRequests(currentPatient);
                FillGrid();

                if (planRequests.Count == 0)
                {
                    Log("Keine Plaene oder Summenplaene gefunden (Kurse mit Id 'PD' werden ausgeblendet).");
                    btnStart.Enabled = false;
                }
                else
                {
                    Log(string.Format("{0} Plan/Plaene gefunden. Optionen pruefen, dann 'Bericht erstellen'.", planRequests.Count));
                    btnStart.Enabled = true;
                }
            }
            catch (Exception e)
            {
                Log("Fehler beim Laden: " + e.Message);
                Forms.MessageBox.Show(this, "Fehler beim Laden des Patienten:\n" + e.Message, "Eclipse PlanReport", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Error);
            }
            finally
            {
                UseWaitCursor = false;
                btnLoad.Enabled = true;
            }
        }

        private List<PlanRequest> BuildPlanRequests(Patient patient)
        {
            var requests = new List<PlanRequest>();

            foreach (Course course in patient.Courses.Where(c => !c.Id.Equals("PD", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (PlanSetup planSetup in course.PlanSetups)
                {
                    var request = new PlanRequest
                    {
                        PatientId = patient.Id,
                        CourseId = course.Id,
                        PlanType = "PlanSetup",
                        PlanId = planSetup.Id,
                        PrescriptionInfo = BuildPrescriptionInfo(planSetup),
                        StatusInfo = ReflectionUtils.GetStringProperty(planSetup, "ApprovalStatus"),
                        AvailableSliceTargetIds = GetAvailableSliceTargetIds(planSetup.StructureSet),
                        AvailableDisplayStructureIds = GetAvailableDisplayStructureIds(planSetup.StructureSet),
                        AvailableDvhStructureIds = GetAvailableDvhStructureIds(planSetup.StructureSet)
                    };
                    try
                    {
                        if (planSetup.TotalDose.Dose > 0)
                            request.TotalDoseGy = planSetup.TotalDose.Dose;
                    }
                    catch
                    {
                    }
                    request.SliceTargetId = SelectDefaultSliceTargetId(request, planSetup.TargetVolumeID);
                    request.TemplateId = ReportEngine.InferTemplateIdForPlan(templates, request.PlanId, false);
                    request.SelectedDisplayStructureIds = BuildRecommendedDisplayStructureIds(request, templates);
                    request.SelectedDvhStructureIds = DvhRecommendation.BuildRecommendedDvhStructureIds(request, templates);
                    requests.Add(request);
                }

                foreach (PlanSum planSum in course.PlanSums)
                {
                    var request = new PlanRequest
                    {
                        PatientId = patient.Id,
                        CourseId = course.Id,
                        PlanType = "PlanSum",
                        PlanId = string.IsNullOrEmpty(planSum.Id) ? planSum.Name : planSum.Id,
                        PrescriptionInfo = BuildPlanSumInfo(planSum),
                        StatusInfo = "",
                        AvailableSliceTargetIds = GetAvailableSliceTargetIds(planSum.StructureSet),
                        AvailableDisplayStructureIds = GetAvailableDisplayStructureIds(planSum.StructureSet),
                        AvailableDvhStructureIds = GetAvailableDvhStructureIds(planSum.StructureSet)
                    };
                    request.SliceTargetId = SelectDefaultSliceTargetId(request, null);
                    request.TemplateId = ReportEngine.InferTemplateIdForPlan(templates, request.PlanId, true);
                    request.SelectedDisplayStructureIds = BuildRecommendedDisplayStructureIds(request, templates);
                    request.SelectedDvhStructureIds = DvhRecommendation.BuildRecommendedDvhStructureIds(request, templates);
                    requests.Add(request);
                }
            }

            return requests;
        }

        private static string BuildPrescriptionInfo(PlanSetup planSetup)
        {
            try
            {
                string dose = planSetup.TotalDose.Dose > 0
                    ? planSetup.TotalDose.Dose.ToString("F2", CultureInfo.InvariantCulture) + " " + planSetup.TotalDose.UnitAsString
                    : "";
                string fractions = ReflectionUtils.GetStringProperty(planSetup, "NumberOfFractions");
                if (!string.IsNullOrEmpty(dose) && !string.IsNullOrEmpty(fractions))
                    return dose + " / " + fractions + " Fx";
                return ReflectionUtils.FirstNonEmpty(dose, fractions);
            }
            catch
            {
                return "";
            }
        }

        private static string BuildPlanSumInfo(PlanSum planSum)
        {
            int count = ReflectionUtils.GetEnumerableProperty(planSum, "PlanSetups").Count();
            return count > 0 ? string.Format("Summe aus {0} Plaenen", count) : "Summe";
        }

        private static List<string> GetAvailableDvhStructureIds(StructureSet structureSet)
        {
            if (structureSet == null)
                return new List<string>();

            return structureSet.Structures
                .Where(s => !s.IsEmpty)
                .Select(s => s.Id)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<string> GetAvailableDisplayStructureIds(StructureSet structureSet)
        {
            return GetAvailableDvhStructureIds(structureSet);
        }

        private static List<string> GetAvailableSliceTargetIds(StructureSet structureSet)
        {
            if (structureSet == null)
                return new List<string>();

            var ptv = structureSet.Structures
                .Where(s => !s.IsEmpty && s.Id.ToUpperInvariant().StartsWith("PTV"))
                .Select(s => s.Id)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase);

            var other = structureSet.Structures
                .Where(s => !s.IsEmpty &&
                            !s.Id.ToUpperInvariant().StartsWith("PTV") &&
                            (s.Id.ToUpperInvariant().StartsWith("CTV") || s.Id.ToUpperInvariant().Contains("BOOST")))
                .Select(s => s.Id)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase);

            return ptv.Concat(other).ToList();
        }

        private static string SelectDefaultSliceTargetId(PlanRequest request, string targetVolumeId)
        {
            if (!string.IsNullOrEmpty(targetVolumeId))
            {
                string match = request.AvailableSliceTargetIds
                    .FirstOrDefault(id => id.Equals(targetVolumeId, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                    return match;
            }
            return request.AvailableSliceTargetIds.FirstOrDefault() ?? "";
        }

        private static List<string> BuildRecommendedDisplayStructureIds(PlanRequest request, List<ReportTemplate> templates)
        {
            if (request == null || request.AvailableDisplayStructureIds == null)
                return new List<string>();

            var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(request.SliceTargetId))
                selected.Add(request.SliceTargetId);

            ReportTemplate template = templates.FirstOrDefault(t =>
                t.Id.Equals(request.TemplateId ?? "", StringComparison.OrdinalIgnoreCase) ||
                t.DisplayName.Equals(request.TemplateId ?? "", StringComparison.OrdinalIgnoreCase));
            if (template != null)
            {
                foreach (string id in request.AvailableDisplayStructureIds)
                {
                    if (RenderUtils.MatchesAnyPattern(id, template.TargetPatterns) || LooksLikeDisplayTargetStructure(id))
                        selected.Add(id);
                }
            }
            else
            {
                foreach (string id in request.AvailableDisplayStructureIds.Where(LooksLikeDisplayTargetStructure))
                    selected.Add(id);
            }

            return request.AvailableDisplayStructureIds
                .Where(id => selected.Contains(id))
                .ToList();
        }

        private static bool LooksLikeDisplayTargetStructure(string id)
        {
            string value = (id ?? "").ToUpperInvariant();
            return value.Contains("PTV") ||
                   value.Contains("CTV") ||
                   value.Contains("GTV") ||
                   value.Contains("ITV") ||
                   value.Contains("BOOST");
        }

        // ---------- Grid ----------

        private void FillGrid()
        {
            grid.Rows.Clear();

            foreach (PlanRequest plan in planRequests)
            {
                int rowIndex = grid.Rows.Add(
                    plan.Selected,
                    plan.CourseId,
                    plan.IsPlanSum ? "Summe" : "Plan",
                    plan.PlanId,
                    plan.PrescriptionInfo,
                    plan.StatusInfo,
                    plan.SliceTargetId,
                    GetTemplateDisplayName(plan.TemplateId),
                    GetIsodoseButtonText(plan),
                    plan.ExportDvh,
                    plan.ExportSlices);

                var row = grid.Rows[rowIndex];
                row.Tag = plan;

                var sliceCell = (Forms.DataGridViewComboBoxCell)row.Cells["SliceTargetId"];
                sliceCell.DataSource = plan.AvailableSliceTargetIds.ToList();

                var templateCell = (Forms.DataGridViewComboBoxCell)row.Cells["TemplateId"];
                templateCell.DataSource = GetTemplateChoicesFor(plan);
            }

            if (grid.Rows.Count > 0)
                grid.Rows[0].Selected = true;
            RefreshStructurePanels();
        }

        /// <summary>Fuer Summenplaene stehen nur absolute Templates zur Auswahl.</summary>
        private List<string> GetTemplateChoicesFor(PlanRequest plan)
        {
            return templates
                .Where(t => !plan.IsPlanSum || !t.IsRelative)
                .Select(t => t.DisplayName)
                .ToList();
        }

        private string GetTemplateDisplayName(string templateId)
        {
            ReportTemplate template = templates.FirstOrDefault(t => t.Id.Equals(templateId, StringComparison.OrdinalIgnoreCase));
            return template != null ? template.DisplayName : (templates.FirstOrDefault() != null ? templates.First().DisplayName : "");
        }

        private string GetTemplateIdByDisplayName(string displayName)
        {
            ReportTemplate template = templates.FirstOrDefault(t => t.DisplayName.Equals(displayName, StringComparison.OrdinalIgnoreCase));
            return template != null ? template.Id : (templates.FirstOrDefault() != null ? templates.First().Id : "");
        }

        private Forms.DataGridViewRow GetCurrentPlanRow()
        {
            if (grid.SelectedRows.Count > 0)
                return grid.SelectedRows[0];
            return grid.CurrentRow;
        }

        private ReportTemplate GetSelectedTemplateForCurrentRow(out PlanRequest plan)
        {
            plan = null;
            Forms.DataGridViewRow row = GetCurrentPlanRow();
            if (row == null)
                return null;

            plan = row.Tag as PlanRequest;
            if (plan == null)
                return null;

            string displayName = Convert.ToString(row.Cells["TemplateId"].Value);
            string templateId = plan.TemplateId ?? "";
            ReportTemplate template = templates.FirstOrDefault(t => t.Id.Equals(templateId, StringComparison.OrdinalIgnoreCase));
            if (template == null && !string.IsNullOrEmpty(displayName))
                template = templates.FirstOrDefault(t => t.DisplayName.Equals(displayName, StringComparison.OrdinalIgnoreCase));
            return template;
        }

        private void RefreshTemplateCells()
        {
            foreach (Forms.DataGridViewRow gridRow in grid.Rows)
            {
                var rowPlan = gridRow.Tag as PlanRequest;
                if (rowPlan == null)
                    continue;

                List<string> choices = GetTemplateChoicesFor(rowPlan);
                string displayName = GetTemplateDisplayName(rowPlan.TemplateId);
                if (choices.Count > 0 && !choices.Contains(displayName, StringComparer.OrdinalIgnoreCase))
                {
                    displayName = choices[0];
                    rowPlan.TemplateId = GetTemplateIdByDisplayName(displayName);
                }
                if (choices.Count == 0)
                    displayName = "";

                var templateCell = (Forms.DataGridViewComboBoxCell)gridRow.Cells["TemplateId"];
                templateCell.DataSource = null;
                templateCell.DataSource = choices;
                templateCell.Value = displayName;
                gridRow.Cells["EditIsodoses"].Value = GetIsodoseButtonText(rowPlan);
            }
        }

        private void MoveSelectedTemplate(int direction)
        {
            PlanRequest plan;
            ReportTemplate template = GetSelectedTemplateForCurrentRow(out plan);
            if (template == null)
            {
                Forms.MessageBox.Show(this, "Bitte zuerst eine Planzeile mit Template auswaehlen.", "Eclipse PlanReport", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Information);
                return;
            }

            int index = templates.FindIndex(t => t.Id.Equals(template.Id, StringComparison.OrdinalIgnoreCase));
            int newIndex = index + direction;
            if (index < 0 || newIndex < 0 || newIndex >= templates.Count)
                return;

            List<ReportTemplate> updated = templates.ToList();
            ReportTemplate moved = updated[index];
            updated[index] = updated[newIndex];
            updated[newIndex] = moved;

            if (!TemplateRepository.SaveAll(updated, Log))
                return;

            templates = updated;
            RefreshTemplateCells();
            Log(string.Format("Template '{0}' verschoben.", template.DisplayName));
        }

        private void DeleteSelectedTemplate()
        {
            PlanRequest plan;
            ReportTemplate template = GetSelectedTemplateForCurrentRow(out plan);
            if (template == null)
            {
                Forms.MessageBox.Show(this, "Bitte zuerst eine Planzeile mit Template auswaehlen.", "Eclipse PlanReport", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Information);
                return;
            }

            if (templates.Count <= 1)
            {
                Forms.MessageBox.Show(this, "Das letzte Template kann nicht geloescht werden.", "Eclipse PlanReport", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Warning);
                return;
            }

            if (!template.IsRelative && templates.Count(t => !t.IsRelative) <= 1)
            {
                Forms.MessageBox.Show(this, "Das letzte absolute Template kann nicht geloescht werden, weil Summenplaene absolute Isodosen benoetigen.", "Eclipse PlanReport", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Warning);
                return;
            }

            Forms.DialogResult result = Forms.MessageBox.Show(this,
                string.Format("Template '{0}' wirklich loeschen?", template.DisplayName),
                "Eclipse PlanReport",
                Forms.MessageBoxButtons.YesNo,
                Forms.MessageBoxIcon.Question,
                Forms.MessageBoxDefaultButton.Button2);
            if (result != Forms.DialogResult.Yes)
                return;

            List<ReportTemplate> updated = templates
                .Where(t => !t.Id.Equals(template.Id, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (!TemplateRepository.SaveAll(updated, Log))
                return;

            templates = updated;
            foreach (PlanRequest request in planRequests)
            {
                if (!string.Equals(request.TemplateId, template.Id, StringComparison.OrdinalIgnoreCase))
                    continue;

                ReportTemplate replacement = templates.FirstOrDefault(t => !request.IsPlanSum || !t.IsRelative)
                    ?? templates.FirstOrDefault();
                request.TemplateId = replacement != null ? replacement.Id : "";
                request.CustomIsodoses = null;
                if (!request.DisplaySelectionCustomized)
                    request.SelectedDisplayStructureIds = BuildRecommendedDisplayStructureIds(request, templates);
                if (!request.DvhSelectionCustomized)
                    request.SelectedDvhStructureIds = DvhRecommendation.BuildRecommendedDvhStructureIds(request, templates);
            }

            RefreshTemplateCells();
            RefreshStructurePanels();
            Log(string.Format("Template '{0}' geloescht.", template.DisplayName));
        }

        private void Grid_CellValueChanged(object sender, Forms.DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0)
                return;

            var row = grid.Rows[e.RowIndex];
            var plan = row.Tag as PlanRequest;
            if (plan == null)
                return;

            string columnName = grid.Columns[e.ColumnIndex].Name;
            switch (columnName)
            {
                case "Selected":
                    plan.Selected = Convert.ToBoolean(row.Cells["Selected"].Value ?? false);
                    break;
                case "ExportDvh":
                    plan.ExportDvh = Convert.ToBoolean(row.Cells["ExportDvh"].Value ?? false);
                    break;
                case "ExportSlices":
                    plan.ExportSlices = Convert.ToBoolean(row.Cells["ExportSlices"].Value ?? false);
                    break;
                case "SliceTargetId":
                    plan.SliceTargetId = Convert.ToString(row.Cells["SliceTargetId"].Value) ?? "";
                    if (!plan.DisplaySelectionCustomized)
                    {
                        plan.SelectedDisplayStructureIds = BuildRecommendedDisplayStructureIds(plan, templates);
                        if (plan == currentDisplayPlan)
                            RefreshStructurePanels();
                    }
                    break;
                case "TemplateId":
                    plan.TemplateId = GetTemplateIdByDisplayName(Convert.ToString(row.Cells["TemplateId"].Value));
                    if (plan.CustomIsodoses != null)
                    {
                        plan.CustomIsodoses = null;
                        row.Cells["EditIsodoses"].Value = GetIsodoseButtonText(plan);
                        Log(string.Format("{0}: Template gewechselt - angepasste Isodosen verworfen.", plan.PlanId));
                    }
                    if (!plan.DisplaySelectionCustomized)
                    {
                        plan.SelectedDisplayStructureIds = BuildRecommendedDisplayStructureIds(plan, templates);
                        if (plan == currentDisplayPlan)
                            RefreshStructurePanels();
                    }
                    if (!plan.DvhSelectionCustomized)
                    {
                        plan.SelectedDvhStructureIds = DvhRecommendation.BuildRecommendedDvhStructureIds(plan, templates);
                        if (plan == currentDvhPlan)
                            RefreshStructurePanels();
                    }
                    break;
            }
        }

        // ---------- Isodosen-Editor ----------

        private static string GetIsodoseButtonText(PlanRequest plan)
        {
            return plan.CustomIsodoses != null ? "Angepasst *" : "Anpassen...";
        }

        private void Grid_CellContentClick(object sender, Forms.DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || grid.Columns[e.ColumnIndex].Name != "EditIsodoses")
                return;

            var row = grid.Rows[e.RowIndex];
            var plan = row.Tag as PlanRequest;
            if (plan == null)
                return;

            ReportTemplate template = templates.FirstOrDefault(t => t.Id.Equals(plan.TemplateId, StringComparison.OrdinalIgnoreCase))
                ?? templates.FirstOrDefault();
            var templateLevels = template != null
                ? template.Isodoses.Select(i => i.Clone()).ToList()
                : new List<IsodoseLevel>();
            var initialLevels = plan.CustomIsodoses != null
                ? plan.CustomIsodoses.Select(i => i.Clone()).ToList()
                : templateLevels.Select(i => i.Clone()).ToList();

            using (var editor = new IsodoseEditorForm(plan.PlanId, initialLevels, templateLevels, plan.TotalDoseGy))
            {
                if (editor.ShowDialog(this) != Forms.DialogResult.OK)
                    return;

                if (!string.IsNullOrEmpty(editor.SaveAsTemplateName))
                {
                    SaveIsodosesAsTemplate(plan, template, editor.SaveAsTemplateName, editor.ResultLevels, row);
                    return;
                }

                plan.CustomIsodoses = editor.ResultLevels;
                row.Cells["EditIsodoses"].Value = GetIsodoseButtonText(plan);
                Log(string.Format("{0}: {1} angepasste Isodose(n) uebernommen.", plan.PlanId, editor.ResultLevels.Count));
            }
        }

        /// <summary>
        /// Speichert die bearbeiteten Isodosen als neues Template in report_templates.xml,
        /// laedt die Templateliste neu und waehlt das neue Template fuer den Plan aus.
        /// </summary>
        private void SaveIsodosesAsTemplate(PlanRequest plan, ReportTemplate baseTemplate, string name, List<IsodoseLevel> levels, Forms.DataGridViewRow row)
        {
            var newTemplate = new ReportTemplate
            {
                Id = name,
                DisplayName = name,
                SliceStepMm = baseTemplate != null ? baseTemplate.SliceStepMm : 10.0,
                TargetPatterns = baseTemplate != null ? baseTemplate.TargetPatterns.ToList() : new List<string> { "PTV*" },
                DvhPatterns = baseTemplate != null ? baseTemplate.DvhPatterns.ToList() : new List<string>(),
                Isodoses = levels.Select(l => l.Clone()).ToList()
            };

            if (!TemplateRepository.Save(newTemplate, Log))
                return;

            templates.RemoveAll(t => t.Id.Equals(newTemplate.Id, StringComparison.OrdinalIgnoreCase));
            templates.Add(newTemplate);

            RefreshTemplateCells();

            if (!plan.IsPlanSum || !newTemplate.IsRelative)
            {
                plan.TemplateId = newTemplate.Id;
                plan.CustomIsodoses = null;
                row.Cells["TemplateId"].Value = newTemplate.DisplayName;
                row.Cells["EditIsodoses"].Value = GetIsodoseButtonText(plan);
                Log(string.Format("{0}: neues Template '{1}' ausgewaehlt.", plan.PlanId, newTemplate.DisplayName));
            }
        }

        // ---------- Struktur-Panels ----------

        private void RefreshStructurePanels()
        {
            PlanRequest plan = null;
            if (grid.SelectedRows.Count > 0)
                plan = grid.SelectedRows[0].Tag as PlanRequest;
            else if (grid.CurrentRow != null)
                plan = grid.CurrentRow.Tag as PlanRequest;

            currentDisplayPlan = plan;
            currentDvhPlan = plan;
            if (plan == null)
            {
                ClearStructurePanels();
                return;
            }

            lblDisplayTitle.Text = string.Format("Anzeige-Strukturen: {0} ({1})", plan.PlanId, plan.CourseId);
            lblDvhTitle.Text = string.Format("DVH-Strukturen: {0} ({1})", plan.PlanId, plan.CourseId);

            displayListLoading = true;
            clbDisplay.BeginUpdate();
            clbDisplay.Items.Clear();
            foreach (string structureId in plan.AvailableDisplayStructureIds)
            {
                bool isChecked = plan.SelectedDisplayStructureIds.Contains(structureId, StringComparer.OrdinalIgnoreCase);
                clbDisplay.Items.Add(structureId, isChecked);
            }
            clbDisplay.EndUpdate();
            displayListLoading = false;

            dvhListLoading = true;
            clbDvh.BeginUpdate();
            clbDvh.Items.Clear();
            foreach (string structureId in plan.AvailableDvhStructureIds)
            {
                bool isChecked = plan.SelectedDvhStructureIds.Contains(structureId, StringComparer.OrdinalIgnoreCase);
                clbDvh.Items.Add(structureId, isChecked);
            }
            clbDvh.EndUpdate();
            dvhListLoading = false;
        }

        private void ClearStructurePanels()
        {
            currentDisplayPlan = null;
            currentDvhPlan = null;
            lblDisplayTitle.Text = "Anzeige-Strukturen";
            lblDvhTitle.Text = "DVH-Strukturen";
            displayListLoading = true;
            clbDisplay.Items.Clear();
            displayListLoading = false;
            dvhListLoading = true;
            clbDvh.Items.Clear();
            dvhListLoading = false;
        }

        private void ClbDisplay_ItemCheck(object sender, Forms.ItemCheckEventArgs e)
        {
            if (displayListLoading || currentDisplayPlan == null)
                return;

            string structureId = clbDisplay.Items[e.Index].ToString();
            currentDisplayPlan.DisplaySelectionCustomized = true;

            if (e.NewValue == Forms.CheckState.Checked)
            {
                if (!currentDisplayPlan.SelectedDisplayStructureIds.Contains(structureId, StringComparer.OrdinalIgnoreCase))
                    currentDisplayPlan.SelectedDisplayStructureIds.Add(structureId);
            }
            else
            {
                currentDisplayPlan.SelectedDisplayStructureIds.RemoveAll(id => id.Equals(structureId, StringComparison.OrdinalIgnoreCase));
            }
        }

        private void ClbDvh_ItemCheck(object sender, Forms.ItemCheckEventArgs e)
        {
            if (dvhListLoading || currentDvhPlan == null)
                return;

            string structureId = clbDvh.Items[e.Index].ToString();
            currentDvhPlan.DvhSelectionCustomized = true;

            if (e.NewValue == Forms.CheckState.Checked)
            {
                if (!currentDvhPlan.SelectedDvhStructureIds.Contains(structureId, StringComparer.OrdinalIgnoreCase))
                    currentDvhPlan.SelectedDvhStructureIds.Add(structureId);
            }
            else
            {
                currentDvhPlan.SelectedDvhStructureIds.RemoveAll(id => id.Equals(structureId, StringComparison.OrdinalIgnoreCase));
            }
        }

        private void ApplyRecommendedDisplay()
        {
            if (currentDisplayPlan == null)
                return;
            currentDisplayPlan.SelectedDisplayStructureIds = BuildRecommendedDisplayStructureIds(currentDisplayPlan, templates);
            currentDisplayPlan.DisplaySelectionCustomized = false;
            RefreshStructurePanels();
        }

        private void ApplyRecommendedDvh()
        {
            if (currentDvhPlan == null)
                return;
            currentDvhPlan.SelectedDvhStructureIds = DvhRecommendation.BuildRecommendedDvhStructureIds(currentDvhPlan, templates);
            currentDvhPlan.DvhSelectionCustomized = false;
            RefreshStructurePanels();
        }

        private void SetAllDisplayChecks(bool value)
        {
            if (currentDisplayPlan == null)
                return;
            currentDisplayPlan.DisplaySelectionCustomized = true;
            currentDisplayPlan.SelectedDisplayStructureIds = value
                ? new List<string>(currentDisplayPlan.AvailableDisplayStructureIds)
                : new List<string>();
            RefreshStructurePanels();
        }

        private void SetAllDvhChecks(bool value)
        {
            if (currentDvhPlan == null)
                return;
            currentDvhPlan.DvhSelectionCustomized = true;
            currentDvhPlan.SelectedDvhStructureIds = value
                ? new List<string>(currentDvhPlan.AvailableDvhStructureIds)
                : new List<string>();
            RefreshStructurePanels();
        }

        // ---------- Bericht erstellen ----------

        private void RunReport()
        {
            if (currentPatient == null)
            {
                Forms.MessageBox.Show(this, "Bitte zuerst einen Patienten laden.", "Eclipse PlanReport", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Warning);
                return;
            }

            grid.EndEdit();

            string outputDir = txtOutputDir.Text.Trim();
            if (string.IsNullOrEmpty(outputDir))
            {
                Forms.MessageBox.Show(this, "Bitte einen Ausgabeordner angeben.", "Eclipse PlanReport", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Warning);
                return;
            }

            if (!planRequests.Any(p => p.Selected))
            {
                Forms.MessageBox.Show(this, "Bitte mindestens einen Plan zum Drucken markieren.", "Eclipse PlanReport", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Warning);
                return;
            }

            try
            {
                Directory.CreateDirectory(outputDir);
            }
            catch (Exception e)
            {
                Forms.MessageBox.Show(this, "Ausgabeordner kann nicht erstellt werden:\n" + e.Message, "Eclipse PlanReport", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Error);
                return;
            }

            RenderUtils.FontScale = (double)numFontScale.Value;
            Theme.Dark = chkDarkMode.Checked;
            SaveSettings();

            SetUiBusy(true);
            progressBar.Value = 0;
            btnOpenPdf.Enabled = false;
            btnOpenFolder.Enabled = false;
            lastPdfPath = null;

            try
            {
                var engine = new ReportEngine(Log, fraction =>
                {
                    int value = (int)Math.Round(RenderUtils.Clamp(fraction, 0, 1) * 1000);
                    progressBar.Value = Math.Min(progressBar.Maximum, value);
                    Forms.Application.DoEvents();
                });

                string pdfPath = engine.Generate(currentPatient, planRequests, templates, outputDir);
                if (pdfPath != null && File.Exists(pdfPath))
                {
                    lastPdfPath = pdfPath;
                    btnOpenPdf.Enabled = true;
                    btnOpenFolder.Enabled = true;
                    Log("Fertig.");
                }
                else
                {
                    Log("Es wurde kein PDF erstellt - Details siehe Protokoll.");
                }
            }
            catch (Exception e)
            {
                Log("Fehler bei der Berichtserstellung: " + e.Message);
                Forms.MessageBox.Show(this, "Fehler bei der Berichtserstellung:\n" + e.Message, "Eclipse PlanReport", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Error);
            }
            finally
            {
                SetUiBusy(false);
            }
        }

        private void SetUiBusy(bool busy)
        {
            UseWaitCursor = busy;
            txtPatientId.Enabled = !busy;
            btnLoad.Enabled = !busy;
            txtOutputDir.Enabled = !busy;
            btnBrowse.Enabled = !busy;
            numFontScale.Enabled = !busy;
            chkDarkMode.Enabled = !busy;
            grid.Enabled = !busy;
            clbDisplay.Enabled = !busy;
            btnDisplayRecommended.Enabled = !busy;
            btnDisplayAll.Enabled = !busy;
            btnDisplayNone.Enabled = !busy;
            clbDvh.Enabled = !busy;
            btnDvhRecommended.Enabled = !busy;
            btnDvhAll.Enabled = !busy;
            btnDvhNone.Enabled = !busy;
            btnStart.Enabled = !busy && planRequests.Count > 0;
        }

        // ---------- Hilfsfunktionen ----------

        private void BrowseOutputDir()
        {
            using (var dialog = new Forms.FolderBrowserDialog())
            {
                dialog.SelectedPath = Directory.Exists(txtOutputDir.Text) ? txtOutputDir.Text : @"C:\EclipsePlanReport";
                if (dialog.ShowDialog(this) == Forms.DialogResult.OK)
                    txtOutputDir.Text = dialog.SelectedPath;
            }
        }

        private void OpenPath(string path)
        {
            if (string.IsNullOrEmpty(path) || (!File.Exists(path) && !Directory.Exists(path)))
                return;
            try
            {
                System.Diagnostics.Process.Start(path);
            }
            catch (Exception e)
            {
                Log("Konnte nicht geoeffnet werden: " + e.Message);
            }
        }

        private void Log(string message)
        {
            if (txtLog == null)
                return;
            txtLog.AppendText(string.Format("[{0:HH:mm:ss}] {1}{2}", DateTime.Now, message, Environment.NewLine));
            Forms.Application.DoEvents();
        }
    }
}
