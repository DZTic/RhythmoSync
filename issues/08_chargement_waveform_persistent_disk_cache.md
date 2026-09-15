# Issue #08 : Absence de cache disque persistant pour la forme d'onde (WaveformData)

- **Priorité :** P1 (Temps de chargement et réouverture de projet)
- **Composant :** `RhythmoSync.Media` / `RhythmoSync.App`
- **Fichiers concernés :**
  - [`WaveformGenerator.cs`](file:///c:/Users/gener/Downloads/rhythmosync-mvp%20-%20FullRecr%C3%A9er/RhythmoSync.Desktop/src/RhythmoSync.Media/WaveformGenerator.cs#L17)
  - [`MainWindow.xaml.cs`](file:///c:/Users/gener/Downloads/rhythmosync-mvp%20-%20FullRecr%C3%A9er/RhythmoSync.Desktop/src/RhythmoSync.App/MainWindow.xaml.cs#L914-L952)

---

## 📌 Description du problème

À chaque ouverture d'un projet existant ou ré-import d'une vidéo déjà manipulée auparavant, l'utilisateur doit attendre plusieurs secondes (voire dizaines de secondes sur un film de 2h) que FFmpeg décode l'audio et ré-extraie l'ensemble des pics de la forme d'onde.

## 🔍 Cause racine

Dans [`MainWindow.xaml.cs`](file:///c:/Users/gener/Downloads/rhythmosync-mvp%20-%20FullRecr%C3%A9er/RhythmoSync.Desktop/src/RhythmoSync.App/MainWindow.xaml.cs#L940) :
```csharp
var data = await Task.Run(() => WaveformGenerator.GenerateAsync(_ffmpegPath, videoPath, numSamples, cts.Token), cts.Token);
```
Alors que [`ProxyGenerator.cs`](file:///c:/Users/gener/Downloads/rhythmosync-mvp%20-%20FullRecr%C3%A9er/RhythmoSync.Desktop/src/RhythmoSync.Media/ProxyGenerator.cs#L29-L43) dispose d'un système robuste de cache disque persistant basé sur le hash SHA256 (chemin + taille + date de modification) dans `%APPDATA%\RhythmoSync Studio\proxies`, `WaveformGenerator` ne dispose **d'aucun système de persistance sur disque**.

Les pics audio min/max normalisés calculés sont uniquement conservés dans une variable en mémoire vive (`_data` dans `WaveformControl`), et sont totalement perdus à la fermeture de l'application.

## 🛠️ Solution proposée

1. **Création d'un dossier de cache waveform :**
   `%APPDATA%\RhythmoSync Studio\waveforms`
2. **Identification unique du fichier média :**
   Calculer une clé de hachage SHA256 basée sur :
   `$"{videoPath}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{numSamples}"`
3. **Format de cache binaire ultra-rapide (`.wavecache`) :**
   Écrire un format binaire simple :
   - Magic Header (4 octets : `WAVE`)
   - Durée (`double`, 8 octets)
   - SampleRate (`int32`, 4 octets)
   - Nombre de pics (`int32`, 4 octets)
   - Données brutes `float[]` des pics (écrites directement via `BinaryWriter.Write` ou `Span<byte>`).
4. **Chargement instantané :**
   Si le fichier cache existe, le charger via un simple `FileStream` : temps de chargement < 5 ms au lieu de 5 000 ms avec FFmpeg !

## ✅ Critères d'acceptation

- Le rechargement d'une vidéo ou la réouverture d'un projet affiche la forme d'onde immédiatement (< 50 ms).
- Si le fichier vidéo d'origine est modifié ou remplacé, le cache est automatiquement invalidé et régénéré.
- Option de purge du cache accessible depuis l'interface ou liée au nettoyage du cache proxy.
