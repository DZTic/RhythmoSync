# Issue #01 : Saccade de la bande rythmo à l'export — Désynchronisation de cadence & FPS source ignoré

- **Priorité :** P0 (Critique - Impact direct sur la qualité du livrable vidéo)
- **Composants :** `RhythmoSync.App` / `RhythmoSync.Media`
- **Fichiers concernés :**
  - [`MainWindow.xaml.cs`](file:///c:/Users/gener/Downloads/rhythmosync-mvp%20-%20FullRecr%C3%A9er/RhythmoSync.Desktop/src/RhythmoSync.App/MainWindow.xaml.cs#L1318-L1325)
  - [`ExportDialog.xaml.cs`](file:///c:/Users/gener/Downloads/rhythmosync-mvp%20-%20FullRecr%C3%A9er/RhythmoSync.Desktop/src/RhythmoSync.App/Export/ExportDialog.xaml.cs#L206)
  - [`VideoExporter.cs`](file:///c:/Users/gener/Downloads/rhythmosync-mvp%20-%20FullRecr%C3%A9er/RhythmoSync.Desktop/src/RhythmoSync.Media/VideoExporter.cs#L238-L252)

---

## 📌 Description du problème

Lors de l'exportation d'une vidéo avec la bande rythmo incrustée, la vidéo source joue de manière fluide en haut, mais la bande rythmo qui défile en bas subit des micro-saccades régulières (effet de judder / télécinéma).

## 🔍 Cause racine

1. **Le FPS natif de la vidéo n'est jamais affecté au projet :**
   Dans [`MainWindow.LoadVideo`](file:///c:/Users/gener/Downloads/rhythmosync-mvp%20-%20FullRecr%C3%A9er/RhythmoSync.Desktop/src/RhythmoSync.App/MainWindow.xaml.cs#L1318), `VideoProber.ProbeAsync` est appelé et renvoie correctement le FPS du fichier (ex. 23.976, 24, 29.97, 30 ou 60 FPS).
   Cependant, `MainWindow` ne met jamais à jour `_state.Fps`. La valeur reste figée sur la valeur par défaut `_state.Fps = 25`.

2. **Rééchantillonnage destructeur lors de l'exportation :**
   Dans [`ExportDialog.xaml.cs`](file:///c:/Users/gener/Downloads/rhythmosync-mvp%20-%20FullRecr%C3%A9er/RhythmoSync.Desktop/src/RhythmoSync.App/Export/ExportDialog.xaml.cs#L206), `Fps = _state.Fps` (donc 25).
   Dans [`VideoExporter.cs`](file:///c:/Users/gener/Downloads/rhythmosync-mvp%20-%20FullRecr%C3%A9er/RhythmoSync.Desktop/src/RhythmoSync.Media/VideoExporter.cs#L239), le décodeur FFmpeg applique le filtre `-vf ...,fps=25`.
   - Si la vidéo source est à 24 FPS, FFmpeg duplique 1 image par seconde.
   - Si la vidéo source est à 30 FPS, FFmpeg jette 5 images par seconde.
   - Mais surtout, la bande rythmo avance linéairement selon `frameCount / 25`.

3. **Incompatibilité avec le taux de rafraîchissement des moniteurs (60 Hz / 120 Hz) :**
   Sur un écran standard à 60 Hz, une vidéo à 25 FPS subit un judder de cadence (pulldown 2:3 ou 2.4:1). L'œil humain tolère cela sur les scènes vidéo naturelles grâce au flou de mouvement de la caméra, mais sur du **texte graphique contrasté défilant à vitesse constante**, chaque hésitation de trame est immédiatement visible sous forme de saccade.

## 🛠️ Solution proposée

1. **Synchronisation automatique du FPS à l'import :**
   Dans `MainWindow.LoadVideo`, après réception de `_videoInfo` :
   ```csharp
   if (_videoInfo is { Fps: > 0 })
   {
       _state.Fps = _videoInfo.Fps;
       SyncFpsCombo();
   }
   ```
2. **Options de framerate dans le dialogue d'exportation :**
   Ajouter dans `ExportDialog` la possibilité de choisir le framerate d'exportation :
   - `FPS Source (détecté)` (recommandé par défaut)
   - `25 FPS (PAL standard)`
   - `50 FPS / 60 FPS (Haute Fluidité Bande Rythmo)`
3. À 50 ou 60 FPS, le défilement de la bande rythmo devient parfaitement fluide sur tous les écrans d'ordinateur contemporains.

## ✅ Critères d'acceptation

- L'import d'une vidéo à 24, 29.97, 30 ou 60 FPS met à jour `_state.Fps` et la liste déroulante FPS de l'interface.
- L'export vidéo respecte le FPS source ou le FPS haute fluidité choisi.
- Le défilement de la bande rythmo dans la vidéo exportée ne présente plus de judder de pulldown.
