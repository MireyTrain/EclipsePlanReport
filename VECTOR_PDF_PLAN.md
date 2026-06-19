# Vector PDF Prototype

Dieser Arbeitsordner ist ein isolierter Prototyp fuer eine Hybrid-/Vektor-PDF-Ausgabe.
Der funktionierende Raster-PlanReport bleibt im Ordner `EclipsePlanReport-Project`
unveraendert.

## Ziel

- Text, Tabellen, Linien, DVH-Kurven, Konturen, Isodosen und einfache Formen als
  PDF-Vektorpfade ausgeben.
- CT-, DRR- und Screenshot-Inhalte bleiben Rasterbilder, aber werden als separate
  Image-XObjects in das PDF eingebettet.
- Wenn eine Seite nicht als WPF-Drawing verfuegbar ist oder der Vektorpfad fehlschlaegt,
  faellt der Writer automatisch auf den bisherigen Raster-PDF-Weg zurueck.

## Gewaehlter Ansatz

Die bestehenden Renderer zeichnen weiterhin in WPF-`DrawingVisual`s. Beim bisherigen
PNG-Speichern registriert `RenderUtils.SaveVisualAsPng` nun zusaetzlich eine Kopie des
`Drawing` im `VectorPdfPageStore`.

`PdfWriter.CreatePdfFromImages` prueft anschliessend:

1. Sind alle Seiten im `VectorPdfPageStore` vorhanden?
2. Wenn ja: PDF direkt aus den WPF-Drawings erzeugen.
3. Wenn nein oder bei Fehler: bisherigen PNG/JPEG-Fallback verwenden.

Damit muessen die Renderer zunaechst nicht komplett neu geschrieben werden.

## Unterstuetzte WPF-Drawing-Typen

- `DrawingGroup`: Transformationen und Clip-Geometrien
- `GeometryDrawing`: Flaechen und Linien als PDF-Pfade
- `GlyphRunDrawing`: Schrift als scharfe Vektor-Glyphenpfade
- `ImageDrawing`: Rasterbilder als JPEG-XObject

## Bewusste Grenzen des ersten Prototyps

- Schrift wird als Glyphenpfad geschrieben, nicht als editierbarer PDF-Text.
- Transparenzen/Opacity werden noch nicht als PDF-ExtGState abgebildet.
- Gradients, komplexe Brushes und Effekte fallen aktuell nicht vektoriell aus.
- Rasterbilder werden weiterhin JPEG-komprimiert.
- Die Vektorqualitaet muss mit echten Eclipse-Testreports validiert werden,
  besonders bei Clips, gedrehten Texten, DVH-Dash-Linien und CT/Overlay-Ausrichtung.

## Testmatrix

1. Planberichtseite: Tabellenlinien, Kopf/Fusszeile, Signaturfelder.
2. DVH: Achsen, Tickmarks, Kurven, gedrehte y-Achsenbeschriftung.
3. Dosisstatistik: Tabellenbreite, Zeilenlinien, lange Strukturnamen.
4. 2x2-Ansicht: CT/DRR-Raster korrekt positioniert, Overlays scharf, Clips korrekt.
5. Schichtdruck: CT-Raster, Isodosen/Konturen, Orientierungslabels, Lineal.
6. Orientierungstest HFS/HFP/FFDL/FFDR: gleiche Bildlage wie im Raster-Branch.

## Naechste Schritte nach dem ersten Test

- Falls Transparenzen fehlen: PDF-ExtGState fuer Brush-/Pen-Alpha ergaenzen.
- Falls Textdateien zu gross werden: optional echte PDF-Fonts statt Glyphenpfade.
- Falls CT/DRR-Kompression stoert: PNG/Flate fuer medizinische Rasterbilder pruefen.
- Wenn stabil: Renderer koennen spaeter direkt Seitenobjekte statt PNG-Pfade liefern.
