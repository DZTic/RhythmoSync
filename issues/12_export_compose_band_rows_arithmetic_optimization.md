# Issue #12 : Répétition arithmétique et appels redondants dans ComposeBandRows

- **Priorité :** P2 (Performance CPU de la composition de pixels)
- **Composant :** `RhythmoSync.Media`
- **Fichier concerné :**
  - [`VideoExporter.cs`](file:///c:/Users/gener/Downloads/rhythmosync-mvp%20-%20FullRecr%C3%A9er/RhythmoSync.Desktop/src/RhythmoSync.Media/VideoExporter.cs#L479-L518)

---

## 📌 Description du problème

Pendant l'exportation vidéo, une proportion significative du temps CPU est consommée dans la boucle interne de `ComposeBandRows`, limitant la vitesse à laquelle les trames peuvent être transmises à l'encodeur FFmpeg.

## 🔍 Cause racine

Dans [`VideoExporter.cs`](file:///c:/Users/gener/Downloads/rhythmosync-mvp%20-%20FullRecr%C3%A9er/RhythmoSync.Desktop/src/RhythmoSync.Media/VideoExporter.cs#L487-L517) :
```csharp
for (var row = 0; row < s.BandRenderHeight; row++)
{
    var destBase = (bandTop + row) * width * 4;
    var col = 0;
    while (col < width)
    {
        var srcX = stripX + col;
        if (srcX < 0) { ... }
        else if (srcX >= band.TotalWidthPx) { ... }
        else
        {
            var tileIndex = srcX / tileW;
            var inTileX = srcX % tileW;
            var run = Math.Min(width - col, Math.Min(tileW - inTileX, band.TotalWidthPx - srcX));
            var tilePixels = tile(tileIndex);
            Buffer.BlockCopy(tilePixels, (row * tileW + inTileX) * 4, outFrame, destBase + col * 4, run * 4);
            col += run;
        }
    }
}
```

1. **Calculs répétés sur chaque ligne verticale :**
   Pour une bande rythmo de hauteur 400 pixels (`BandRenderHeight = 400`) :
   La boucle `while (col < width)` est exécutée **400 fois par frame**, recalculant à chaque ligne :
   - `srcX / tileW` (division entière)
   - `srcX % tileW` (modulo)
   - `Math.Min(...)`
   - L'appel au délégué `tile(tileIndex)` et la recherche dans le dictionnaire du cache de tuiles !
2. **Invariance de la segmentation horizontale :**
   Pour une frame donnée, la position horizontale `stripX` est **strictement constante** pour toutes les lignes verticales (`row = 0` à `row = BandRenderHeight - 1`). La liste des segments (tuiles impliquées, colonnes de départ et longueurs `run`) est identique sur les 400 lignes !

## 🛠️ Solution proposée

1. **Pré-calcul des tranches horizontales une seule fois par frame :**
   Avant la boucle `for (var row = 0; row < s.BandRenderHeight; row++)`, construire une structure de tranches (souvent 1 ou 2 segments seulement) :
   ```csharp
   struct BandSpan { public byte[] SourcePixels; public int SrcX; public int DestX; public int Length; }
   ```
2. La boucle sur les lignes se résume alors à une simple séquence directe de `Buffer.BlockCopy` sans aucune division, modulo ou invocation de délégué :
   ```csharp
   for (var row = 0; row < s.BandRenderHeight; row++)
   {
       var destRowBase = (bandTop + row) * width * 4;
       foreach (var span in spans)
       {
           Buffer.BlockCopy(span.SourcePixels, (row * tileW + span.SrcX) * 4,
                            outFrame, destRowBase + span.DestX * 4, span.Length * 4);
       }
   }
   ```

## ✅ Critères d'acceptation

- Temps d'exécution de `ComposeBandRows` réduit de plus de 60 % par trame.
- Aucun impact visuel : résultat de composition strictement identique au pixel près.
