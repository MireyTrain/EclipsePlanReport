# Eclipse PlanReport

Eclipse PlanReport ist eine eigenstaendige WinForms-/ESAPI-Anwendung fuer automatisierte Planberichte aus Varian Eclipse. Nach Eingabe einer Patienten-ID werden Plaene und Summenplaene geladen, konfiguriert und als A4-Querformat-PDF ausgegeben. Excel- oder CSV-Importe sind fuer den Planreport nicht noetig.

## Bedienung

1. Den kompletten Ordner `EclipsePlanReport\debug` auf den Eclipse-/ESAPI-Rechner kopieren.
2. `EclipsePlanReport.exe` starten.
3. Patient-ID eingeben und **Patient laden**.
4. Pro Plan/Summenplan festlegen:
   - ob der Plan gedruckt wird
   - Isodosen-Template
   - Zielstruktur fuer den Schichtdruck
   - DVH ja/nein
   - Schichten ja/nein
   - OARs/Strukturen fuer das DVH
   - optional planindividuelle Isodosen
5. **Bericht erstellen** starten.

Courses mit der ID `PD` werden nicht angezeigt.

## Berichtsinhalt

Pro ausgewaehltem Plan erzeugt die Anwendung, soweit verfuegbar:

1. eine nachgebaute Eclipse-Planberichtseite mit ESAPI-Daten,
2. eine 2x2-Planungsansicht mit transversal/sagittal/frontal und BEV/DRR-Panel,
3. ein DVH mit Dosisstatistik und Planverordnung,
4. transversale CT-Schichten ueber den Bereich der gewaelten Zielstruktur.

Summenplaene werden unterstuetzt. Relative Isodosen-Templates werden fuer Summenplaene nicht verwendet, da dort keine eindeutige 100%-Referenz angenommen wird.

## Bekannte Hinweise

- Im Isodosen-Editor werden relativ eingegebene Werte mit der aktuellen Plangesamtdosis in absolute Gy-Werte umgerechnet und beim Speichern als Template zusammen mit dem Prozentwert abgelegt. Beim Rendern hat der absolute Gy-Wert Vorrang. Solche gespeicherten Templates sind dadurch faktisch plan- bzw. dosisbezogen und skalieren nicht automatisch mit einer anderen Verordnung.
## Orientierung

Die Bilddarstellung wird zentral ueber `Image.XDirection`, `Image.YDirection`, `Image.ZDirection`, `Image.Origin` und `Image.UserOrigin` abgeleitet. Fuer transversale Schichten gilt die klinische Druckkonvention: die Tischseite liegt unten bei 6 Uhr.

Aktuelle Tischseiten-Regel:

| Lagerung | unten im Transversalbild |
| --- | --- |
| HFS / FFS | P |
| HFP / FFP | A |
| HFDL / FFDL | L |
| HFDR / FFDR | R |

Die Randlabels werden aus derselben Transformation abgeleitet wie das Bild selbst. Bei nicht sicher erkannter Orientierung wird im Log gewarnt.

## Ausgabe

Im Ausgabeordner wird pro Lauf ein Unterordner erzeugt:

`Planreport_<PatientID>_<Zeitstempel>`

Darin liegen:

- das zusammengefuehrte PDF `PlanReport_<PatientID>_<Zeitstempel>.pdf`.

PNG-Zwischenseiten werden standardmaessig nicht mehr gespeichert. Die Seiten
werden fuer das finale Vector-PDF im Speicher gehalten.

Voreingestellter Ausgabeordner ist `C:\EclipsePlanReport`; die Einstellung wird neben der EXE in `planreport_settings.txt` gespeichert.

## Build

```powershell
dotnet build EclipsePlanReport\EclipsePlanReport.csproj -p:Configuration=Debug -p:Platform=x64
```

Voraussetzungen:

- .NET Framework 4.8 Targeting Pack
- ESAPI-/VMS-DLLs in `Libs`

Die ESAPI-/VMS-DLLs werden nicht mitversioniert. Fuer einen lokalen Build muessen sie aus der jeweiligen Eclipse-/ESAPI-Installation in `Libs` bereitgestellt werden.

Zur Laufzeit muessen im EXE-Ordner liegen:

- `EclipsePlanReport.exe`
- `EclipsePlanReport.exe.config`
- `VMS.TPS.Common.Model.API.dll`
- `VMS.TPS.Common.Model.Types.dll`
- `report_templates.xml`

## Projektstruktur

| Pfad | Inhalt |
| --- | --- |
| `EclipsePlanReport` | C#-Projekt |
| `EclipsePlanReport\report_templates.xml` | Isodosen- und DVH-Templates |
| `EclipsePlanReport\debug` | Build-Ausgabe |
| `Libs` | ESAPI-Referenzen fuer den Build |
| `diagnostics` | lokale Diagnosebilder aus frueheren Testlaeufen |

## Wichtige Quelldateien

| Datei | Aufgabe |
| --- | --- |
| `Program.cs` | Einstiegspunkt |
| `MainForm.cs` | GUI, Patient laden, Plan- und DVH-Auswahl |
| `ReportEngine.cs` | Ablaufsteuerung und PDF-Zusammenstellung |
| `PlanPageRenderer.cs` | Planberichtseite |
| `EclipseViewRenderer.cs` | 2x2-Planungsansicht und BEV/DRR |
| `SliceRenderer.cs` | transversaler Schichtdruck |
| `DvhRenderer.cs` | DVH, Statistik, OAR-Empfehlung |
| `RenderUtils.cs` | Zeichen-, Dosis- und Orientierungshelfer |
| `ReflectionUtils.cs` | robuste ESAPI-Zugriffe per Reflection |
| `PdfWriter.cs` | minimaler PDF-Writer ohne externe PDF-Bibliothek |
