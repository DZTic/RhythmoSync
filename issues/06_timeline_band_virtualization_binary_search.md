# Issue #06 : Parcours linéaire O(N) à 60 FPS dans RhythmoBandControl.VirtualizePass

- **Priorité :** P1 (Scalabilité / Performance sur les projets de longs métrages)
- **Composant :** `RhythmoSync.App`
- **Fichier concerné :**
  - [`RhythmoBandControl.cs`](file:///c:/Users/gener/Downloads/rhythmosync-mvp%20-%20FullRecr%C3%A9er/RhythmoSync.Desktop/src/RhythmoSync.App/Controls/RhythmoBandControl.cs#L168-L212)

---

## 📌 Description du problème

Sur les projets contenant un volume important de dialogues (long métrage ou épisode de série de 50 à 120 minutes comportant 1500 à 4000 répliques), le défilement de la bande rythmo perd de sa fluidité à 60 FPS et consomme une part excessive du CPU sur le thread principal UI.

## 🔍 Cause racine

Dans [`RhythmoBandControl.cs`](file:///c:/Users/gener/Downloads/rhythmosync-mvp%20-%20FullRecr%C3%A9er/RhythmoSync.Desktop/src/RhythmoSync.App/Controls/RhythmoBandControl.cs#L175-L180) :
```csharp
private void VirtualizePass(double pps)
{
    var t0 = (0 - _scroll.X) / pps - VirtualizeMarginSeconds;
    var t1 = (ActualWidth - _scroll.X) / pps + VirtualizeMarginSeconds;

    var seen = _visibleIds;
    seen.Clear();
    var dialogues = _state.Dialogues;
    for (var i = 0; i < dialogues.Count; i++)
    {
        var block = dialogues[i];
        if (block.EndTime < t0 || block.StartTime > t1) continue;
        seen.Add(block.Id);
        ...
    }
    ...
}
```

1. **Parcours séquentiel $O(N)$ systématique :**
   La méthode `VirtualizePass` est déclenchée **60 fois par seconde** (à chaque frame d'affichage via `UpdateTime`).
   Elle boucle sur la totalité des dialogues de la liste `_state.Dialogues`, même pour ceux qui se situent à 1 heure de l'instant affiché.
2. **Volumétrie :**
   Sur un projet avec 3 000 blocs :
   $$3000 \times 60 = 180\,000 \text{ itérations / seconde}$$
   exécutées sans relâche sur le thread UI pour simplement tester `block.EndTime < t0`.

## 🛠️ Solution proposée

1. **Exploitation du tri chronologique :**
   Les dialogues d'un projet sont naturellement ou très facilement maintenus triés par `StartTime`.
2. **Recherche dichotomique ($O(\log N)$) :**
   - Trouver par recherche dichotomique l'index du premier bloc dont `EndTime >= t0`.
   - Itérer ensuite de façon contiguë jusqu'à rencontrer un bloc dont `StartTime > t1`.
   - Dès que cette condition est remplie, interrompre la boucle (`break`).
3. **Gain :**
   Au lieu de tester 3 000 blocs à chaque frame, l'algorithme n'examine plus que les 5 à 15 blocs réellement visibles dans la fenêtre temporelle actuelle, divisant le coût CPU de la passe de virtualisation par un facteur supérieur à 100.

## ✅ Critères d'acceptation

- L'affichage et le défilement restent calés à 60 FPS constants même avec plus de 5 000 blocs dans le projet.
- Temps d'exécution de `VirtualizePass` inférieur à 0.05 ms par frame.
