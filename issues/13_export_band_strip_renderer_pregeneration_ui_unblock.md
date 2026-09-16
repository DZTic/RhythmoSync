# Issue #13 : Appels bloquants Dispatcher.Invoke lors du rendu des tuiles dans BandStripRenderer

- **Priorité :** P2 (Réactivité de l'interface graphique utilisateur pendant l'export)
- **Composant :** `RhythmoSync.App`
- **Fichier concerné :**
  - [`BandStripRenderer.cs`](file:///c:/Users/gener/Downloads/rhythmosync-mvp%20-%20FullRecr%C3%A9er/RhythmoSync.Desktop/src/RhythmoSync.App/Export/BandStripRenderer.cs#L44-L52)

---

## 📌 Description du problème

Pendant le déroulement de l'exportation vidéo en arrière-plan, la fenêtre de l'application subit des micro-gels ponctuels : la barre de progression se fige brièvement, le bouton « Annuler » peut mettre du temps à réagir, et le compteur de FPS effectifs a des à-coups.

## 🔍 Cause racine

Dans [`BandStripRenderer.cs`](file:///c:/Users/gener/Downloads/rhythmosync-mvp%20-%20FullRecr%C3%A9er/RhythmoSync.Desktop/src/RhythmoSync.App/Export/BandStripRenderer.cs#L48-L51) :
```csharp
public byte[] GetTile(int tileIndex)
{
    // Le rendu WPF doit se faire sur le thread UI ; l'export tourne en arrière-plan.
    var app = Application.Current;
    if (app is null || app.Dispatcher.CheckAccess())
        return RenderTile(tileIndex);
    return app.Dispatcher.Invoke(() => RenderTile(tileIndex));
}
```

1. **Rendu sur le thread UI via Dispatcher :**
   WPF impose que les objets visuels (`DrawingVisual`, `RenderTargetBitmap`, `FormattedText`) soient créés et rendus sur un thread STA (ici, le thread UI principal de l'application).
2. **Appel bloquant synchrone :**
   Le thread d'arrière-plan de l'exportation fait un appel `Dispatcher.Invoke` bloquant chaque fois qu'une nouvelle tuile (largeur 4 096 px) doit être calculée.
3. **Conséquence :**
   Pendant la génération de la tuile (création des polices, tracé des rectangles, rasterisation du `RenderTargetBitmap`), le thread UI est monopolisé. Les événements souris, les rafraîchissements de la barre de progression WPF et le traitement des messages Windows sont suspendus.

## 🛠️ Solution proposée

1. **Pré-génération avant encodage vidéo :**
   Dans `ExportDialog.OnStartExport`, avant de lancer `VideoExporter.ExportAsync` :
   - Le nombre total de tuiles pour la plage demandée est faible (ex: pour 10 minutes à 100 pps, seulement 15 tuiles de 4096 px).
   - Pré-rendre ces tuiles en amont avec une barre de progression dédiée (« Préparation de la bande rythmo… »), ce qui ne prend que quelques dizaines de millisecondes au total.
2. **Alternative (Thread STA dédié) :**
   Instancier un thread STA dédié hors UI pour le rendu `RenderTargetBitmap` si l'on souhaite un rendu paresseux sans jamais solliciter le `Dispatcher` de l'interface principale.

## ✅ Critères d'acceptation

- Le thread UI principal de l'application reste totalement réactif tout au long de l'encodage vidéo.
- Les boutons « Annuler », les animations et la barre de progression restent parfaitement fluides sans aucun micro-gel.
