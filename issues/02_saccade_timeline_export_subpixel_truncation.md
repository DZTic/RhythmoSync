# Issue #02 : Troncature entière (int) de stripX et absence d'anti-aliasing subpixel à l'export

- **Priorité :** P0 (Critique - Cause directe de saccades spatiales / judder de pixel)
- **Composant :** `RhythmoSync.Media`
- **Fichier concerné :**
  - [`VideoExporter.cs`](file:///c:/Users/gener/Downloads/rhythmosync-mvp%20-%20FullRecr%C3%A9er/RhythmoSync.Desktop/src/RhythmoSync.Media/VideoExporter.cs#L429-L432)

---

## 📌 Description du problème

Lors de la lecture de la vidéo exportée, les lettres et blocs de la bande rythmo avancent par à-coups ou micro-sauts de pixels plutôt qu'avec une translation continue et soyeuse.

## 🔍 Cause racine

Dans [`VideoExporter.cs`](file:///c:/Users/gener/Downloads/rhythmosync-mvp%20-%20FullRecr%C3%A9er/RhythmoSync.Desktop/src/RhythmoSync.Media/VideoExporter.cs#L430) :
```csharp
var time = s.StartTime + frameCount / s.Fps;
var stripX = (int)((time + s.SyncOffsetEffective) * s.Pps);
ComposeBandRows(outFrame, s, band, Tile, stripX, darkRow);
```

1. **Troncature brute vers zéro :**
   En C#, `(int)` tronque vers zéro (`truncate`), et non vers le bas (`floor`).
   - Pour toute valeur dans l'intervalle `]-1.0, 1.0[`, `(int)` produit `0`.
   - Au tout début de la timeline (autour de $t=0$), le défilement reste **complètement immobilisé pendant presque 2 pixels entiers**, puis saute brusquement à 1 px, provoquant un arrêt suivi d'un décrochage perceptible dès le lancement de la vidéo.

2. **Pas d'avancement fractionnaire (Cadence 4px / 5px) :**
   Le déplacement théorique par image vaut :
   $$\Delta x = \frac{Pps}{Fps}$$
   Par exemple, pour $Pps = 135$ et $Fps = 25$, $\Delta x = 5.4 \text{ px/image}$.
   La troncature `(int)` projette les coordonnées sur des pixels entiers sans interpolation :
   - Frame 0 : $0.0 \rightarrow 0 \text{ px}$ (saut: 0)
   - Frame 1 : $5.4 \rightarrow 5 \text{ px}$ (saut: +5)
   - Frame 2 : $10.8 \rightarrow 10 \text{ px}$ (saut: +5)
   - Frame 3 : $16.2 \rightarrow 16 \text{ px}$ (saut: +6)
   - Frame 4 : $21.6 \rightarrow 21 \text{ px}$ (saut: +5)
   - Frame 5 : $27.0 \rightarrow 27 \text{ px}$ (saut: +6)
   Cette alternance permanente $+5, +5, +6, +5, +6$ est directement perçue par l'œil comme une vibration/saccade continue sur les contours tranchés des polices Consolas.

## 🛠️ Solution proposée

1. **Suppression du palier à zéro :**
   Remplacer `(int)` par `(int)Math.Floor(...)` ou `(int)Math.Round(...)` pour éliminer le plateau artificiel autour de zéro :
   ```csharp
   var stripX = (int)Math.Floor((time + s.SyncOffsetEffective) * s.Pps);
   ```

2. **Harmonisation Pps / Fps ou Subpixel Sampling :**
   - Option A (Recommandée) : Dans `ExportLayout.Compute`, arrondir ou contraindre légèrement `ExportPps` de façon à ce que `ExportPps / Fps` soit soit un nombre entier (ex: 4.0 ou 5.0 ou 6.0 px/frame), soit un ratio à modulation imperceptible.
   - Option B : Implémenter une interpolation horizontale subpixel (filtrage bilinéaire pondéré par la fraction résiduelle $f = x - \lfloor x \rfloor$) lors du mélange dans `ComposeBandRows`.

## ✅ Critères d'acceptation

- Disparition de la zone morte à $t=0$.
- Déplacement spatial linéaire régulier de la bande rythmo d'une trame à l'autre.
