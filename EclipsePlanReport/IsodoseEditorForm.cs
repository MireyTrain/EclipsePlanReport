using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace EclipsePlanReport
{
    /// <summary>
    /// Editor fuer planindividuelle Isodosen (an Eclipse angelehnt):
    /// Tabelle mit 2D-Haekchen, relativer Dosis [%], absoluter Dosis [Gy],
    /// Farbe und Linienbreite. Relative und absolute Werte sind ueber die
    /// verordnete Gesamtdosis gekoppelt (sofern bekannt).
    /// </summary>
    internal class IsodoseEditorForm : Forms.Form
    {
        private readonly double totalDoseGy;
        private Forms.DataGridView grid;
        private Forms.Button btnAddRow;
        private Forms.Button btnRemoveRow;
        private Forms.Button btnReset;
        private Forms.Button btnSaveTemplate;
        private Forms.Button btnOk;
        private Forms.Button btnCancel;
        private Forms.Label lblInfo;
        private readonly List<IsodoseLevel> templateLevels;
        private bool syncing;

        /// <summary>Ergebnis nach OK: die bearbeiteten Isodosen (nur Zeilen mit 2D-Haekchen).</summary>
        public List<IsodoseLevel> ResultLevels { get; private set; }

        /// <summary>Gesetzt, wenn der Nutzer die Isodosen zusaetzlich als Template speichern moechte.</summary>
        public string SaveAsTemplateName { get; private set; }

        public IsodoseEditorForm(string planLabel, List<IsodoseLevel> initialLevels, List<IsodoseLevel> templateLevels, double totalDoseGy)
        {
            this.totalDoseGy = totalDoseGy;
            this.templateLevels = templateLevels ?? new List<IsodoseLevel>();
            BuildLayout(planLabel);
            FillGrid(initialLevels ?? this.templateLevels);
        }

        private void BuildLayout(string planLabel)
        {
            Text = "Isodosen anpassen - " + planLabel;
            StartPosition = Forms.FormStartPosition.CenterParent;
            ClientSize = new Drawing.Size(760, 420);
            MinimumSize = new Drawing.Size(700, 340);
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;

            lblInfo = new Forms.Label
            {
                Left = 12,
                Top = 10,
                Width = ClientSize.Width - 24,
                Height = 30,
                Anchor = Forms.AnchorStyles.Top | Forms.AnchorStyles.Left | Forms.AnchorStyles.Right,
                Text = totalDoseGy > 0
                    ? string.Format(CultureInfo.InvariantCulture, "Verordnete Gesamtdosis: {0:F3} Gy - relative und absolute Werte werden automatisch umgerechnet.", totalDoseGy)
                    : "Keine Gesamtdosis bekannt (z. B. Summenplan) - bitte absolute Werte in Gy verwenden.",
                ForeColor = Drawing.Color.DimGray
            };

            grid = new Forms.DataGridView
            {
                Left = 12,
                Top = 44,
                Width = ClientSize.Width - 24,
                Height = ClientSize.Height - 44 - 56,
                Anchor = Forms.AnchorStyles.Top | Forms.AnchorStyles.Bottom | Forms.AnchorStyles.Left | Forms.AnchorStyles.Right,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AutoSizeColumnsMode = Forms.DataGridViewAutoSizeColumnsMode.Fill,
                SelectionMode = Forms.DataGridViewSelectionMode.CellSelect,
                MultiSelect = false,
                RowHeadersVisible = false
            };
            grid.Columns.Add(new Forms.DataGridViewCheckBoxColumn { Name = "Show2D", HeaderText = "2D", FillWeight = 10 });
            grid.Columns.Add(new Forms.DataGridViewTextBoxColumn { Name = "RelPct", HeaderText = "Relative Dosis [%]", FillWeight = 26 });
            grid.Columns.Add(new Forms.DataGridViewTextBoxColumn { Name = "AbsGy", HeaderText = "Absolute Dosis [Gy]", FillWeight = 26 });
            grid.Columns.Add(new Forms.DataGridViewTextBoxColumn { Name = "Color", HeaderText = "Farbe", FillWeight = 24, ReadOnly = true });
            grid.Columns.Add(new Forms.DataGridViewTextBoxColumn { Name = "Width", HeaderText = "Breite", FillWeight = 14 });

            grid.CellEndEdit += Grid_CellEndEdit;
            grid.CellClick += Grid_CellClick;
            grid.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (grid.IsCurrentCellDirty && grid.CurrentCell is Forms.DataGridViewCheckBoxCell)
                    grid.CommitEdit(Forms.DataGridViewDataErrorContexts.Commit);
            };
            grid.DataError += (s, e) => { e.ThrowException = false; };

            btnAddRow = new Forms.Button
            {
                Text = "Zeile hinzufuegen",
                Left = 12,
                Top = ClientSize.Height - 44,
                Width = 130,
                Anchor = Forms.AnchorStyles.Bottom | Forms.AnchorStyles.Left
            };
            btnRemoveRow = new Forms.Button
            {
                Text = "Zeile loeschen",
                Left = 150,
                Top = ClientSize.Height - 44,
                Width = 110,
                Anchor = Forms.AnchorStyles.Bottom | Forms.AnchorStyles.Left
            };
            btnReset = new Forms.Button
            {
                Text = "Template",
                Left = 268,
                Top = ClientSize.Height - 44,
                Width = 90,
                Anchor = Forms.AnchorStyles.Bottom | Forms.AnchorStyles.Left
            };
            btnSaveTemplate = new Forms.Button
            {
                Text = "Als Template speichern...",
                Left = 366,
                Top = ClientSize.Height - 44,
                Width = 160,
                Anchor = Forms.AnchorStyles.Bottom | Forms.AnchorStyles.Left
            };
            btnOk = new Forms.Button
            {
                Text = "OK",
                Left = ClientSize.Width - 12 - 80 - 88,
                Top = ClientSize.Height - 44,
                Width = 80,
                Anchor = Forms.AnchorStyles.Bottom | Forms.AnchorStyles.Right
            };
            btnCancel = new Forms.Button
            {
                Text = "Abbrechen",
                Left = ClientSize.Width - 12 - 80,
                Top = ClientSize.Height - 44,
                Width = 80,
                Anchor = Forms.AnchorStyles.Bottom | Forms.AnchorStyles.Right,
                DialogResult = Forms.DialogResult.Cancel
            };

            btnAddRow.Click += (s, e) => AddRow(null);
            btnRemoveRow.Click += (s, e) => RemoveCurrentRow();
            btnReset.Click += (s, e) => FillGrid(templateLevels);
            btnSaveTemplate.Click += BtnSaveTemplate_Click;
            btnOk.Click += BtnOk_Click;

            AcceptButton = btnOk;
            CancelButton = btnCancel;

            Controls.AddRange(new Forms.Control[] { lblInfo, grid, btnAddRow, btnRemoveRow, btnReset, btnSaveTemplate, btnOk, btnCancel });
        }

        // ---------- Grid fuellen ----------

        private void FillGrid(IEnumerable<IsodoseLevel> levels)
        {
            grid.Rows.Clear();
            foreach (IsodoseLevel level in levels)
                AddRow(level);
        }

        private void AddRow(IsodoseLevel level)
        {
            double relPct = 0, absGy = 0, thickness = 1;
            var color = System.Windows.Media.Color.FromRgb(255, 0, 0);

            if (level != null)
            {
                relPct = level.RelativeDosePercent;
                absGy = level.DoseGy;
                thickness = level.Thickness > 0 ? level.Thickness : 1;
                color = level.Color;

                // fehlende Seite aus der Gesamtdosis ergaenzen
                if (totalDoseGy > 0)
                {
                    if (absGy <= 0 && relPct > 0)
                        absGy = totalDoseGy * relPct / 100.0;
                    if (relPct <= 0 && absGy > 0)
                        relPct = absGy / totalDoseGy * 100.0;
                }
            }

            int rowIndex = grid.Rows.Add(
                true,
                relPct > 0 ? relPct.ToString("F1", CultureInfo.InvariantCulture) : "",
                absGy > 0 ? absGy.ToString("F3", CultureInfo.InvariantCulture) : "",
                "",
                thickness.ToString("F0", CultureInfo.InvariantCulture));

            SetRowColor(grid.Rows[rowIndex], ToDrawingColor(color));
        }

        private static Drawing.Color ToDrawingColor(System.Windows.Media.Color c)
        {
            return Drawing.Color.FromArgb(c.R, c.G, c.B);
        }

        private static System.Windows.Media.Color ToMediaColor(Drawing.Color c)
        {
            return System.Windows.Media.Color.FromRgb(c.R, c.G, c.B);
        }

        private void SetRowColor(Forms.DataGridViewRow row, Drawing.Color color)
        {
            var cell = row.Cells["Color"];
            cell.Style.BackColor = color;
            cell.Style.SelectionBackColor = color;
            // Schriftfarbe nach Helligkeit, Farbname als Text
            double luminance = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255.0;
            var fore = luminance > 0.6 ? Drawing.Color.Black : Drawing.Color.White;
            cell.Style.ForeColor = fore;
            cell.Style.SelectionForeColor = fore;
            cell.Value = ColorDisplayName(color);
            cell.Tag = color;
        }

        private static string ColorDisplayName(Drawing.Color color)
        {
            var known = Enum.GetValues(typeof(Drawing.KnownColor))
                .Cast<Drawing.KnownColor>()
                .Select(Drawing.Color.FromKnownColor)
                .FirstOrDefault(k => !k.IsSystemColor && k.R == color.R && k.G == color.G && k.B == color.B);
            return known.IsEmpty
                ? string.Format("{0},{1},{2}", color.R, color.G, color.B)
                : known.Name;
        }

        // ---------- Bearbeitung ----------

        private void Grid_CellClick(object sender, Forms.DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || grid.Columns[e.ColumnIndex].Name != "Color")
                return;

            var cell = grid.Rows[e.RowIndex].Cells["Color"];
            using (var dialog = new Forms.ColorDialog())
            {
                dialog.FullOpen = true;
                if (cell.Tag is Drawing.Color)
                    dialog.Color = (Drawing.Color)cell.Tag;
                if (dialog.ShowDialog(this) == Forms.DialogResult.OK)
                    SetRowColor(grid.Rows[e.RowIndex], dialog.Color);
            }
        }

        /// <summary>Gegenseite (%/Gy) nach Eingabe automatisch nachfuehren.</summary>
        private void Grid_CellEndEdit(object sender, Forms.DataGridViewCellEventArgs e)
        {
            if (syncing || e.RowIndex < 0 || totalDoseGy <= 0)
                return;

            var row = grid.Rows[e.RowIndex];
            string columnName = grid.Columns[e.ColumnIndex].Name;

            syncing = true;
            try
            {
                if (columnName == "RelPct")
                {
                    double pct;
                    if (TryParseDouble(Convert.ToString(row.Cells["RelPct"].Value), out pct) && pct > 0)
                        row.Cells["AbsGy"].Value = (totalDoseGy * pct / 100.0).ToString("F3", CultureInfo.InvariantCulture);
                }
                else if (columnName == "AbsGy")
                {
                    double gy;
                    if (TryParseDouble(Convert.ToString(row.Cells["AbsGy"].Value), out gy) && gy > 0)
                        row.Cells["RelPct"].Value = (gy / totalDoseGy * 100.0).ToString("F1", CultureInfo.InvariantCulture);
                }
            }
            finally
            {
                syncing = false;
            }
        }

        private static bool TryParseDouble(string text, out double value)
        {
            text = (text ?? "").Trim().Replace(',', '.');
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !double.IsNaN(value);
        }

        private void RemoveCurrentRow()
        {
            if (grid.CurrentRow != null)
                grid.Rows.Remove(grid.CurrentRow);
        }

        // ---------- OK / Validierung ----------

        private void BtnOk_Click(object sender, EventArgs e)
        {
            var levels = CollectLevels();
            if (levels == null)
                return;

            ResultLevels = levels;
            DialogResult = Forms.DialogResult.OK;
            Close();
        }

        /// <summary>Isodosen uebernehmen und zusaetzlich als wiederverwendbares Template speichern.</summary>
        private void BtnSaveTemplate_Click(object sender, EventArgs e)
        {
            var levels = CollectLevels();
            if (levels == null)
                return;

            string name = PromptTemplateName();
            if (string.IsNullOrWhiteSpace(name))
                return;

            ResultLevels = levels;
            SaveAsTemplateName = name.Trim();
            DialogResult = Forms.DialogResult.OK;
            Close();
        }

        private string PromptTemplateName()
        {
            using (var dialog = new Forms.Form())
            {
                dialog.Text = "Template-Name";
                dialog.StartPosition = Forms.FormStartPosition.CenterParent;
                dialog.ClientSize = new Drawing.Size(360, 110);
                dialog.FormBorderStyle = Forms.FormBorderStyle.FixedDialog;
                dialog.MaximizeBox = false;
                dialog.MinimizeBox = false;
                dialog.ShowInTaskbar = false;

                var label = new Forms.Label { Text = "Name des neuen Templates:", Left = 12, Top = 12, Width = 330 };
                var textBox = new Forms.TextBox { Left = 12, Top = 36, Width = 336 };
                var ok = new Forms.Button { Text = "OK", Left = 180, Top = 72, Width = 80, DialogResult = Forms.DialogResult.OK };
                var cancel = new Forms.Button { Text = "Abbrechen", Left = 268, Top = 72, Width = 80, DialogResult = Forms.DialogResult.Cancel };
                dialog.Controls.AddRange(new Forms.Control[] { label, textBox, ok, cancel });
                dialog.AcceptButton = ok;
                dialog.CancelButton = cancel;

                return dialog.ShowDialog(this) == Forms.DialogResult.OK ? textBox.Text : null;
            }
        }

        /// <summary>Liest und validiert die Tabelle; null bei leerem Ergebnis (mit Hinweis).</summary>
        private List<IsodoseLevel> CollectLevels()
        {
            grid.EndEdit();

            var levels = new List<IsodoseLevel>();
            foreach (Forms.DataGridViewRow row in grid.Rows)
            {
                if (!Convert.ToBoolean(row.Cells["Show2D"].Value ?? false))
                    continue;

                double relPct, absGy, thickness;
                TryParseDouble(Convert.ToString(row.Cells["RelPct"].Value), out relPct);
                TryParseDouble(Convert.ToString(row.Cells["AbsGy"].Value), out absGy);
                if (!TryParseDouble(Convert.ToString(row.Cells["Width"].Value), out thickness) || thickness <= 0)
                    thickness = 1;

                if (relPct <= 0 && absGy <= 0)
                    continue; // leere Zeile

                var colorTag = row.Cells["Color"].Tag;
                var color = colorTag is Drawing.Color ? (Drawing.Color)colorTag : Drawing.Color.Red;

                levels.Add(new IsodoseLevel
                {
                    DoseGy = absGy > 0 ? absGy : 0,
                    RelativeDosePercent = relPct > 0 ? relPct : 0,
                    Color = ToMediaColor(color),
                    Thickness = thickness
                });
            }

            if (!levels.Any())
            {
                Forms.MessageBox.Show(this,
                    "Bitte mindestens eine Isodose mit Wert und 2D-Haekchen angeben\noder mit 'Abbrechen' schliessen.",
                    "Isodosen anpassen", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Warning);
                return null;
            }

            return levels.OrderByDescending(l => l.DoseGy > 0 ? l.DoseGy : l.RelativeDosePercent).ToList();
        }
    }
}
