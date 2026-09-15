# Issue #07 : Optimisation du magnétisme SnapEngine en O(N) sur chaque mouvement souris

- **Priorité :** P2 (Confort utilisateur & fluidité d'édition)
- **Composant :** `RhythmoSync.Core`
- **Fichier concerné :**
  - [`SnapEngine.cs`](file:///c:/Users/gener/Downloads/rhythmosync-mvp%20-%20FullRecr%C3%A9er/RhythmoSync.Desktop/src/RhythmoSync.Core/SnapEngine.cs#L22-L65) & [`SnapEngine.cs`](file:///c:/Users/gener/Downloads/rhythmosync-mvp%20-%20FullRecr%C3%A9er/RhythmoSync.Desktop/src/RhythmoSync.Core/SnapEngine.cs#L71-L98)

---

## 📌 Description du problème

Lors du déplacement ou du redimensionnement d'un bloc de texte à la souris dans la bande rythmo, le curseur peut sembler accuser un léger temps de réponse ou saccader sur les projets volumineux.

## 🔍 Cause racine

Dans [`SnapEngine.cs`](file:///c:/Users/gener/Downloads/rhythmosync-mvp%20-%20FullRecr%C3%A9er/RhythmoSync.Desktop/src/RhythmoSync.Core/SnapEngine.cs#L55) :
```csharp
public static SnapResult? SnapMove(
    double rawStartTime, double duration, string blockId,
    IReadOnlyList<DialogueBlock> all, double targetSyncTime, double zoom)
{
    var threshold = ThresholdPx / zoom;
    ...
    foreach (var other in all)
    {
        if (other.Id == blockId) continue;
        TryStart(other.StartTime, SnapTargetKind.BlockEdge);
        TryStart(other.EndTime, SnapTargetKind.BlockEdge);
        TryEnd(other.StartTime, SnapTargetKind.BlockEdge);
        TryEnd(other.EndTime, SnapTargetKind.BlockEdge);
    }
    return best;
}
```

1. **Appel à haute fréquence :**
   Lorsqu'un utilisateur fait glisser un bloc à la souris, l'événement `MouseMove` est levé à une fréquence élevée (souvent 125 Hz à 1000 Hz selon la souris).
2. **Parcours global $O(N)$ inutile :**
   `SnapMove` et `SnapEdge` comparent le bloc avec **l'intégralité** des blocs du projet (`foreach (var other in all)`).
3. **Inutilité physique des calculs :**
   Le seuil d'accroche magnétique `threshold` ne vaut que 15 pixels divisés par le zoom (soit environ 0.1 à 0.15 seconde). Tester les blocs situés 30 minutes avant ou après est totalement inutile et consomme des cycles processeurs en pure perte pendant le geste de glissement.

## 🛠️ Solution proposée

1. **Filtrage temporel des candidats au magnétisme :**
   Ne passer à `SnapMove` / `SnapEdge` que les blocs dont la position temporelle chevauche la fenêtre d'intérêt :
   $$[\text{rawStartTime} - \text{threshold},\; \text{rawStartTime} + \text{duration} + \text{threshold}]$$
2. En profitant du tri chronologique de la liste, extraire ces candidats en $O(\log N + K)$ où $K \le 5$, rendant le coût du magnétisme quasi-instantané (< 1 microseconde) à chaque paquet de souris reçu.

## ✅ Critères d'acceptation

- Déplacement et redimensionnement de blocs ultra-réactifs même sur les projets comptant plus de 5 000 dialogues.
- Temps d'exécution de `SnapEngine` quasi-nul au profiler (< 0.01 ms par événement).
