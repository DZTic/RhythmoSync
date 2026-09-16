# Issue #11 : Absence de parallélisme (pipeline séquentiel) entre décodage, composition et encodage

- **Priorité :** P1 (Vitesse d'export vidéo / Débit d'encodage)
- **Composant :** `RhythmoSync.Media`
- **Fichier concerné :**
  - [`VideoExporter.cs`](file:///c:/Users/gener/Downloads/rhythmosync-mvp%20-%20FullRecr%C3%A9er/RhythmoSync.Desktop/src/RhythmoSync.Media/VideoExporter.cs#L414-L437)

---

## 📌 Description du problème

La vitesse globale d'exportation vidéo plafonne nettement sous le potentiel réel du matériel : le GPU et les cœurs CPU restent sous-utilisés (souvent entre 30 % et 50 % de leur capacité maximale).

## 🔍 Cause racine

Dans [`VideoExporter.cs`](file:///c:/Users/gener/Downloads/rhythmosync-mvp%20-%20FullRecr%C3%A9er/RhythmoSync.Desktop/src/RhythmoSync.Media/VideoExporter.cs#L414-L436) :
```csharp
while (true)
{
    // 1. Lire une frame vidéo décodée complète (attente du décodeur FFmpeg)
    while (read < videoFrameSize) { ... }

    // 2. Composition synchrone en C# (copies de mémoire et dessin)
    ComposeBandRows(outFrame, s, band, Tile, stripX, darkRow);
    DrawSyncLine(outFrame, s);

    // 3. Écrire la frame dans l'encodeur FFmpeg (attente de l'encodeur)
    await output.WriteAsync(outFrame.AsMemory(0, outFrameSize), ct);
}
```

1. **Pipeline strictement séquentiel (Stop-and-Wait) :**
   - Tant que le code C# compose la bande, le décodeur et l'encodeur FFmpeg ont de fortes chances d'attendre la libération de leurs pipes.
   - Tant que l'encodeur traite la frame, le décodeur et le compositeur C# sont bloqués par `await output.WriteAsync(...)`.
2. **Absence de chevauchement asynchrone :**
   Il n'y a aucun tampon d'échange (double ou triple buffering). Les trois étapes (décodage, composition CPU, encodage GPU/CPU) s'exécutent en série trame par trame au lieu de tourner en parallèle sur des threads séparés.

## 🛠️ Solution proposée

1. **Modèle Producteur-Consommateur (Pipelining) :**
   Mettre en place deux canaux asynchrones bornés (`System.Threading.Channels.Channel<byte[]>`) avec une capacité de 2 à 3 trames :
   - **Thread 1 (Décodage) :** Lit les trames brutes depuis le processus décodeur FFmpeg et les pousse dans `decodedChannel`.
   - **Thread 2 (Composition) :** Dépile une trame décodée, compose la bande rythmo C#, et pousse la trame terminée dans `encodedChannel`.
   - **Thread 3 (Encodage) :** Dépile la trame composée et l'envoie en continu sur le stdin de l'encodeur FFmpeg.
2. **Pool de tampons (`ArrayPool<byte>` ou buffers recyclés) :**
   Réutiliser un jeu fixe de 4 à 6 tampons de taille `outFrameSize` pour éviter toute allocation en cours d'export.
3. **Gain attendu :**
   Augmentation de 40 % à 80 % du framerate effectif d'exportation (fps effectifs) sur les machines multi-cœurs.

## ✅ Critères d'acceptation

- L'exportation sature convenablement l'encodeur matériel (NVENC / QuickSync / AMF) ou libx264.
- Hausse mesurable du débit d'encodage (FPS effectifs).
