# Issue #05 : Absence de cache mémoire (LRU) des tuiles de waveform lors du scrubbing

- **Priorité :** P1 (Fluidité du scrubbing sur la forme d'onde)
- **Composant :** `RhythmoSync.App`
- **Fichier concerné :**
  - [`WaveformControl.cs`](file:///c:/Users/gener/Downloads/rhythmosync-mvp%20-%20FullRecr%C3%A9er/RhythmoSync.Desktop/src/RhythmoSync.App/Controls/WaveformControl.cs#L92-L111) & [`WaveformControl.cs`](file:///c:/Users/gener/Downloads/rhythmosync-mvp%20-%20FullRecr%C3%A9er/RhythmoSync.Desktop/src/RhythmoSync.App/Controls/WaveformControl.cs#L136-L163)

---

## 📌 Description du problème

Lorsqu'on effectue un scrubbing rapide à la souris (glisser-déplacer d'avant en arrière) sur la timeline audio, des micro-ralentissements et des à-coups apparaissent, particulièrement lorsque le curseur oscille autour de la limite d'une tranche de 30 secondes.

## 🔍 Cause racine

Dans [`WaveformControl.cs`](file:///c:/Users/gener/Downloads/rhythmosync-mvp%20-%20FullRecr%C3%A9er/RhythmoSync.Desktop/src/RhythmoSync.App/Controls/WaveformControl.cs#L97) :
```csharp
var stale = _tiles.Keys.Where(i => i < firstTile || i > lastTile).ToList();
foreach (var i in stale)
{
    _movingRoot.Children.Remove(_tiles[i]);
    _tiles.Remove(i);
}

for (var i = firstTile; i <= lastTile; i++)
{
    if (_tiles.ContainsKey(i)) continue;
    var tile = RenderTile(i, pps);
    _tiles[i] = tile;
    _movingRoot.Children.Add(tile);
}
```

1. **Destruction immédiate des tuiles :**
   Le dictionnaire `_tiles` ne conserve que les tuiles strictement visibles dans la fenêtre actuelle ($\pm 1$ tuile de marge). Dès qu'une tuile sort de cette marge, elle est immédiatement détruite et retirée de la mémoire.
2. **Coût élevé de `RenderTile` :**
   Dans `RenderTile` :
   - Un objet vectoriel complexe `StreamGeometry` est instancié.
   - Une boucle itère sur toute la largeur de la tuile en pixels (par exemple 3000 à 6000 pixels à fort zoom), appelant `geo.BeginFigure` et `geo.LineTo` pour chaque colonne de pixel.
   - La géométrie est compilée et figée (`geometry.Freeze()`).
   - L'ensemble est dessiné dans un `DrawingVisual`.
3. **Va-et-vient lors du scrubbing :**
   Si l'utilisateur déplace la tête de lecture de gauche à droite sur une même zone, les mêmes tuiles sont détruites puis recalculées plusieurs fois par seconde, saturant le thread UI de WPF et provoquant des pics d'allocation sur le garbage collector.

## 🛠️ Solution proposée

1. Implémenter un cache mémoire LRU (Least Recently Used) pour les tuiles de waveform :
   - Conserver un pool de 15 à 30 tuiles récemment affichées au niveau de zoom courant.
   - Quand une tuile sort de la zone visible, la retirer de `_movingRoot.Children`, mais la conserver dans le cache LRU.
   - Si la tête de lecture revient sur cette tranche temporelle, réinsérer le `DrawingVisual` existant sans ré-exécuter `RenderTile`.
   - Vider le cache uniquement lors d'un changement de zoom (`_state.ViewChanged`) ou de changement de média.

## ✅ Critères d'acceptation

- Scrubbing fluide et instantané d'avant en arrière sans régénération de géométrie pour les zones récemment survolées.
- Réduction significative de l'activité du GC lors de la navigation interactive.
