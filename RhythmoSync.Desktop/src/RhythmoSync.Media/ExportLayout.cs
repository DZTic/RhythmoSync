namespace RhythmoSync.Media;

/// <summary>
/// Calcul du layout d'export (port de la logique d'App.tsx) : le conteneur final est
/// toujours 1920×1080 pour garder le texte net (le choix 480p/720p ne joue que sur le
/// bitrate). La vidéo est mise à l'échelle en largeur ; la bande rythmo remplit tout
/// l'espace restant en bas, sans jamais réduire la vidéo sous 40 % de la hauteur.
/// </summary>
public sealed record ExportLayout
{
    public required int ExportWidth { get; init; }
    public required int ExportHeight { get; init; }
    public required int VideoRenderHeight { get; init; }
    public required int BandRenderHeight { get; init; }
    /// <summary>Échelle appliquée à la bande (laneScale) : hauteurs, polices, zoom.</summary>
    public required double LaneScale { get; init; }
    /// <summary>Pixels par seconde dans l'export (zoom UI × laneScale).</summary>
    public required double ExportPps { get; init; }
    /// <summary>Position X de la ligne de synchro dans l'export.</summary>
    public required int SyncLineX { get; init; }

    public static ExportLayout Compute(
        int nativeWidth, int croppedNativeHeight,
        double bandStripHeight, double bandScale,
        double zoomLevel, double syncLineX,
        int exportWidth = 1920, int exportHeight = 1080)
    {
        // Dimensions paires exigées par l'encodeur
        exportWidth -= exportWidth % 2;
        exportHeight -= exportHeight % 2;

        // 1. Hauteur idéale de la vidéo (mise à l'échelle sans déformation)
        var videoScaleFactor = (double)exportWidth / nativeWidth;
        var idealVideoHeight = (int)Math.Round(croppedNativeHeight * videoScaleFactor);

        // 2. La vidéo garde au minimum 40 % de la hauteur de l'export
        var minVideoHeight = (int)Math.Round(exportHeight * 0.4);

        // 3. Hauteur demandée pour la bande (échelle utilisateur, relative à 1920)
        var userLaneScale = exportWidth / 1920.0 * bandScale;
        var idealBandHeight = (int)Math.Round(bandStripHeight * userLaneScale);

        int videoRenderHeight, bandRenderHeight;
        if (idealVideoHeight + idealBandHeight <= exportHeight)
        {
            // Espace libre en bas : la bande s'étend pour le remplir entièrement
            videoRenderHeight = idealVideoHeight;
            bandRenderHeight = exportHeight - idealVideoHeight;
        }
        else
        {
            bandRenderHeight = Math.Min(idealBandHeight, exportHeight - minVideoHeight);
            videoRenderHeight = exportHeight - bandRenderHeight;
        }

        var laneScale = bandRenderHeight / bandStripHeight;
        return new ExportLayout
        {
            ExportWidth = exportWidth,
            ExportHeight = exportHeight,
            VideoRenderHeight = videoRenderHeight,
            BandRenderHeight = bandRenderHeight,
            LaneScale = laneScale,
            ExportPps = zoomLevel * laneScale,
            SyncLineX = (int)Math.Round(syncLineX * laneScale),
        };
    }
}
