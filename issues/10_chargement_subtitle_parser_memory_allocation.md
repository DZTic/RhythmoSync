# Issue #10 : Allocations massives de chaînes dans les parseurs de sous-titres (SRT / VTT)

- **Priorité :** P2 (Pression mémoire GC lors des imports texte volumineux)
- **Composant :** `RhythmoSync.Core`
- **Fichier concerné :**
  - [`SubtitleIo.cs`](file:///c:/Users/gener/Downloads/rhythmosync-mvp%20-%20FullRecr%C3%A9er/RhythmoSync.Desktop/src/RhythmoSync.Core/SubtitleIo.cs#L130-L164) & [`SubtitleIo.cs`](file:///c:/Users/gener/Downloads/rhythmosync-mvp%20-%20FullRecr%C3%A9er/RhythmoSync.Desktop/src/RhythmoSync.Core/SubtitleIo.cs#L166-L212)

---

## 📌 Description du problème

L'importation de fichiers de sous-titres volumineux (.srt ou .vtt) contenant des milliers d'entrées entraîne une création massive d'objets temporaires en mémoire, provoquant des saccades de l'interface graphique dues aux collectes d'ordures (Garbage Collection Gen 1 & Gen 2).

## 🔍 Cause racine

Dans [`SubtitleIo.cs`](file:///c:/Users/gener/Downloads/rhythmosync-mvp%20-%20FullRecr%C3%A9er/RhythmoSync.Desktop/src/RhythmoSync.Core/SubtitleIo.cs#L133) :
```csharp
public static List<DialogueBlock> ParseSrt(string content)
{
    var blocks = new List<DialogueBlock>();
    var chunks = content.Replace("\r\n", "\n").Split("\n\n");
    foreach (var chunk in chunks)
    {
        var lines = chunk.Split('\n');
        ...
        var parts = lines[timeIdx].Split("-->");
        ...
    }
}
```

1. **Allocations en cascade :**
   - `content.Replace("\r\n", "\n")` duplique la chaîne entière en mémoire.
   - `.Split("\n\n")` alloue un tableau géant contenant des milliers de sous-chaînes.
   - À l'intérieur de la boucle, `.Split('\n')` réalloue à nouveau un tableau de chaînes pour chaque bloc.
   - `.Split("-->")` alloue encore d'autres chaînes.
2. **Pression GC :**
   Ces milliers d'objets de petite et moyenne taille saturent rapidement la mémoire éphémère du Garbage Collector .NET et finissent par provoquer des pauses de collecte perceptibles lors de l'import.

## 🛠️ Solution proposée

1. **Parsing en flux sans duplication :**
   Remplacer les `.Split()` par un lecteur de flux `StringReader` ou une itération via `ReadOnlySpan<char>` / `MemoryExtensions.EnumerateLines()`.
2. **Parsing zéro-allocation des timestamps :**
   Parser directement les heures, minutes, secondes et millisecondes depuis un `ReadOnlySpan<char>` sans instancier de sous-chaînes intermédiaires.

## ✅ Critères d'acceptation

- Réduction de plus de 80 % des allocations mémoire lors de l'import de gros fichiers SRT/VTT.
- Temps d'importation divisé par 2.
