# Issue #04 : Gel et saut de la timeline au démarrage de la lecture dans l'application (_playKickTicks)

- **Priorité :** P1 (Expérience utilisateur / Fluidité playback dans l'application)
- **Composant :** `RhythmoSync.App`
- **Fichier concerné :**
  - [`MainWindow.xaml.cs`](file:///c:/Users/gener/Downloads/rhythmosync-mvp%20-%20FullRecr%C3%A9er/RhythmoSync.Desktop/src/RhythmoSync.App/MainWindow.xaml.cs#L321-L338) & [`MainWindow.xaml.cs`](file:///c:/Users/gener/Downloads/rhythmosync-mvp%20-%20FullRecr%C3%A9er/RhythmoSync.Desktop/src/RhythmoSync.App/MainWindow.xaml.cs#L460-L477)

---

## 📌 Description du problème

Lorsqu'on appuie sur le bouton « ▶ Lecture » (ou la touche Espace) dans l'application pour lancer la lecture, la vidéo démarre immédiatement, mais la bande rythmo reste figée sur place pendant environ 150 à 300 ms, puis « saute » brutalement en avant pour rattraper la vidéo.

## 🔍 Cause racine

Dans [`MainWindow.xaml.cs`](file:///c:/Users/gener/Downloads/rhythmosync-mvp%20-%20FullRecr%C3%A9er/RhythmoSync.Desktop/src/RhythmoSync.App/MainWindow.xaml.cs#L474) :
Lors du clic sur Lecture :
```csharp
_playKickPos = Media.Position.TotalSeconds;
_playKickTicks = Stopwatch.GetTimestamp();
```
Et dans la méthode `GetClockTime()` :
```csharp
var mediaPos = Media.Position.TotalSeconds;
var now = Stopwatch.GetTimestamp();
if (mediaPos != _lastMediaPos)
{
    _lastMediaPos = mediaPos;
    _anchorTicks = now;
}
var time = mediaPos;
// On n'extrapole PAS tant que la lecture n'a pas confirmé qu'elle avance
// (_playKickTicks != 0 = on attend le premier palier après Play)...
if (_isPlaying && _playKickTicks == 0)
{
    var extrapolated = (now - _anchorTicks) / (double)Stopwatch.Frequency * _playbackRate;
    time += Math.Min(extrapolated, MaxExtrapolationSeconds);
}
```

1. **Latence de mise à jour de MediaElement :**
   Le décodeur WPF `MediaElement` (DirectShow / Media Foundation) met entre 150 ms et 300 ms pour actualiser sa propriété `Position`.
2. **Blocage volontaire de l'extrapolation :**
   Tant que `Media.Position` n'a pas dépassé `_playKickPos + 0.001`, `_playKickTicks` reste non nul.
   Pendant ce temps, la condition `_playKickTicks == 0` est fausse : **toute extrapolation à 60 FPS est interdite**.
3. **Résultat :**
   La timeline affiche une position statique pendant 10 à 20 frames de rendu WPF, puis dès que `_playKickTicks` repasse à zéro, elle encaisse un saut discontinu de 0.2 à 0.3 seconde en une seule frame.

## 🛠️ Solution proposée

1. Plutôt que de désactiver complètement l'extrapolation :
   - Initialiser l'ancre `_anchorTicks = Stopwatch.GetTimestamp()` dès l'appel à `TogglePlay()`.
   - Autoriser l'extrapolation continue dès le clic de lecture, plafonnée à une valeur raisonnable (ex: 200 ms).
   - Si un stall réel est détecté par `CheckPlaybackStall()`, opérer un réalignement amorti (lerp temporel) sur 2 ou 3 frames au lieu d'un bond brutal.

## ✅ Critères d'acceptation

- Au clic sur « Lecture », la bande rythmo se met en mouvement de façon instantanée et continue à 60 FPS.
- Aucun gel initial ni saut discontinu perceptible au démarrage de la lecture.
