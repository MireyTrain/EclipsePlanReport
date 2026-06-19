using System;
using Forms = System.Windows.Forms;

namespace EclipsePlanReport
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Forms.Application.EnableVisualStyles();
            Forms.Application.SetCompatibleTextRenderingDefault(false);

            try
            {
                Forms.Application.Run(new MainForm());
            }
            catch (Exception e)
            {
                Forms.MessageBox.Show(
                    "Unerwarteter Fehler:\n" + e,
                    "Eclipse PlanReport",
                    Forms.MessageBoxButtons.OK,
                    Forms.MessageBoxIcon.Error);
            }
        }
    }
}
