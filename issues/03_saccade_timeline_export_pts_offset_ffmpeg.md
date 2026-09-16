# Issue #03 : Gigue de synchronisation PTS au démarrage FFmpeg (-ss avec filtre fps)

- **Priorité :** P1 (Important - Irrégularités temporelles sur les premières secondes d'export)
- **Composant :** `RhythmoSync.Media`
- **Fichier concerné :**
  - [`VideoExporter.cs`](file:///c:/Users/gener/Downloads/rhythmosync-mvp%20-%20FullRecr%C3%A9er/RhythmoSync.Desktop/src/RhythmoSync.Media/VideoExporter.cs#L234-L241)

---

## 📌 Description du problème

Lorsqu'on lance la lecture d'une vidéo exportée, la timeline/bande rythmo semble hésiter ou saccader durant les premières secondes de lecture (« quand on lance la vidéo »), puis se stabilise par la suite.

## 🔍 Cause racine

Dans [`VideoExporter.cs`](file:///c:/Users/gener/Downloads/rhythmosync-mvp%20-%20FullRecr%C3%A9er/RhythmoSync.Desktop/src/RhythmoSync.Media/VideoExporter.cs#L234) :
```csharp
AddArgs(decoderPsi,
    "-hide_banner", "-loglevel", "error",
    "-hwaccel", "auto",
    "-ss", s.StartTime.ToString("0.######", inv),
    "-i", s.VideoPath,
    "-t", rangeDuration.ToString("0.######", inv),
    "-vf", string.Format(inv,
        "crop={0}:{1}:0:{2},scale={3}:{4}:force_original_aspect_ratio=decrease,pad={3}:{4}:(ow-iw)/2:(oh-ih)/2,fps={5}",
        s.VideoWidth, cropH, s.CropTop, s.ExportWidth, s.VideoRenderHeight, s.Fps),
    "-f", "rawvideo", "-pix_fmt", "bgra", "-an",
    "pipe:1");
```

1. **PTS initial non nul des conteneurs MP4 :**
   De nombreux fichiers MP4 modernes contiennent des délais d'édition (edit lists) ou un décalage dû aux B-frames (par exemple premier PTS vidéo à `0.080 s`).
2. **Réaction du filtre `fps` de FFmpeg :**
   Lorsque le filtre `fps` reçoit des paquets dont le premier timestamp PTS ne commence pas strictement à 0 :
   - Soit il insère des images dupliquées au tout début pour combler le « vide » temporel supposé.
   - Soit il retarde la livraison de la première image.
3. **Divergence avec le compteur d'images C# :**
   Dans le compositeur C#, le temps de la bande rythmo est calculé strictement par :
   `var time = s.StartTime + frameCount / s.Fps;`
   Si le décodeur a décalé ou dupliqué des trames au début du flux vidéo, le synchronisme entre l'image vidéo et la bande rythmo est faussé sur les premières images, créant une impression de saccade ou de déphasage au démarrage de la vidéo.

## 🛠️ Solution proposée

1. Réinitialiser explicitement les timestamps PTS au début du filtre vidéo dans le décodeur FFmpeg en ajoutant le filtre `setpts=PTS-STARTPTS` :
   ```csharp
   "-vf", string.Format(inv,
       "setpts=PTS-STARTPTS,crop={0}:{1}:0:{2},scale={3}:{4}:force_original_aspect_ratio=decrease,pad={3}:{4}:(ow-iw)/2:(oh-ih)/2,fps={5}",
       s.VideoWidth, cropH, s.CropTop, s.ExportWidth, s.VideoRenderHeight, s.Fps)
   ```
2. Cela force le premier paquet décodé à avoir exactement un PTS de 0.0, évitant toute duplication ou décalage de trames par le filtre `fps`.

## ✅ Critères d'acceptation

- Le premier frame décodé correspond rigoureusement à $t=0.0$.
- Aucune trame dupliquée ou délai artificiel au lancement de la vidéo exportée.
