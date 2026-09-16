# Issue #09 : Sur-échantillonnage audio inutile (48 kHz) lors de la génération de la waveform

- **Priorité :** P2 (Optimisation de bande passante et CPU au premier chargement)
- **Composant :** `RhythmoSync.Media`
- **Fichier concerné :**
  - [`WaveformGenerator.cs`](file:///c:/Users/gener/Downloads/rhythmosync-mvp%20-%20FullRecr%C3%A9er/RhythmoSync.Desktop/src/RhythmoSync.Media/WaveformGenerator.cs#L22-L55)

---

## 📌 Description du problème

Lors du premier import d'une vidéo (quand la forme d'onde n'est pas encore en cache), la génération de la forme d'onde prend un temps inutilement long et consomme une bande passante I/O importante.

## 🔍 Cause racine

Dans [`WaveformGenerator.cs`](file:///c:/Users/gener/Downloads/rhythmosync-mvp%20-%20FullRecr%C3%A9er/RhythmoSync.Desktop/src/RhythmoSync.Media/WaveformGenerator.cs#L22) :
```csharp
var (duration, sampleRate) = await ProbeAsync(ffmpegPath, mediaPath, ct);
...
psi.ArgumentList.Add("-ac"); psi.ArgumentList.Add("1"); // mono
psi.ArgumentList.Add("-ar"); psi.ArgumentList.Add(sampleRate.ToString(CultureInfo.InvariantCulture));
psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("s16le"); // PCM brut 16 bits LE
```

1. **Volume gigantesque de données PCM non nécessaire :**
   `WaveformGenerator` demande à FFmpeg de décoder le flux audio à sa fréquence d'échantillonnage native (le plus souvent 48 000 Hz ou 44 100 Hz).
   - Pour un film de 2 heures (7 200 secondes) à 48 kHz mono 16-bit :
     $$7200 \times 48000 \times 2 \text{ octets} \approx 691\,200\,000 \text{ octets (soit 691 Mo !)}$$
2. **Traitement lourd en C# :**
   Ces 691 Mo de données transitent par le flux standard `stdout`, puis C# analyse et traite individuellement **345 millions d'échantillons audio** dans une boucle `while` avec des appels de méthode `Accumulate(sample)`.
3. **Sur-dimensionnement total :**
   Le nombre final de points affichés dans l'interface (`numSamples`) est plafonné à 65 536 points. Dériver 65 536 buckets à partir de 345 millions d'échantillons est un gaspillage massif de ressources.

## 🛠️ Solution proposée

1. **Sous-échantillonnage dès le décodage FFmpeg :**
   Remplacer la fréquence native par une fréquence fixe compacte, par exemple **8 000 Hz** (ou 4 000 Hz) :
   ```csharp
   const int targetSampleRate = 8000;
   psi.ArgumentList.Add("-ar"); psi.ArgumentList.Add(targetSampleRate.ToString(CultureInfo.InvariantCulture));
   ```
2. **Impact chiffré :**
   - Volume de données PCM réduit de 691 Mo à **115 Mo** (division par 6).
   - Le nombre d'échantillons à traiter en C# passe de 345 millions à 57 millions.
   - Les pics d'amplitude audio (voix humaine, musique, bruitages) demeurent rigoureusement identiques à l'affichage visuel, car une fréquence de 8 kHz capture fidèlement toute l'enveloppe sonore nécessaire à une bande rythmo.
   - Temps d'extraction FFmpeg divisé par 3 à 5.

## ✅ Critères d'acceptation

- Temps de génération de la forme d'onde divisé au moins par 3 sur les fichiers de plus de 30 minutes.
- Parfaite fidélité visuelle de l'enveloppe audio dans `WaveformControl`.
