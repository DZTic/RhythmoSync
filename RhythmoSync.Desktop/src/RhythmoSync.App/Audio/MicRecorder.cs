using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace RhythmoSync.App.Audio;

/// <summary>Périphérique d'entrée audio (micro) listé pour le sélecteur.</summary>
public readonly record struct MicDevice(string Id, string Name);

/// <summary>
/// Capture micro vers un fichier WAV (16-bit PCM) via WASAPI (NAudio).
///
/// Spécificité doublage : la capture démarre pendant le pré-roll mais ne conserve
/// RIEN tant que <see cref="BeginKeeping"/> n'a pas été appelé. La boucle de rendu
/// déclenche <see cref="BeginKeeping"/> au moment exact où l'horloge franchit le
/// début du bloc — la prise est donc calée pile sur le bloc, sans le décompte.
///
/// Le flux WASAPI tourne sur un thread de fond : Start/Stop sont appelés depuis le
/// thread UI, et <see cref="Finalized"/> est ré-émis sur ce même thread (contexte de
/// synchronisation capturé par NAudio), où l'on peut sans risque toucher l'état.
/// </summary>
public sealed class MicRecorder : IDisposable
{
    private WasapiCapture? _capture;
    private WaveFileWriter? _writer;
    private string? _path;
    private bool _sourceIsFloat;
    private long _keptBytes;
    private volatile bool _keeping;
    // Sérialise l'écriture (thread de capture WASAPI) et la fermeture (thread UI) du
    // writer : un dernier buffer en retard pouvait sinon écrire dans un writer déjà libéré.
    private readonly object _writerLock = new();

    /// <summary>Enregistrement terminé et fichier finalisé : (chemin, vrai si du son a été gardé).</summary>
    public event Action<string?, bool>? Finalized;

    public bool IsRecording => _capture is not null;
    public bool IsKeeping => _keeping;

    /// <summary>Liste les micros actifs (noms complets via WASAPI).</summary>
    public static IReadOnlyList<MicDevice> ListDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
        var result = new List<MicDevice>();
        foreach (var d in devices)
        {
            result.Add(new MicDevice(d.ID, d.FriendlyName));
            d.Dispose();
        }
        return result;
    }

    /// <summary>
    /// Ouvre le micro <paramref name="deviceId"/> (ou le périphérique par défaut si null/
    /// introuvable) et commence la capture, sans encore rien conserver. Peut lever une
    /// exception si le périphérique est inutilisable — l'appelant l'intercepte.
    /// </summary>
    public void Start(string? deviceId, string outputPath)
    {
        if (_capture is not null) throw new InvalidOperationException("Enregistrement déjà en cours.");

        using var enumerator = new MMDeviceEnumerator();
        var device = ResolveDevice(enumerator, deviceId);

        _capture = new WasapiCapture(device);
        var src = _capture.WaveFormat;
        _sourceIsFloat = src.Encoding == NAudio.Wave.WaveFormatEncoding.IeeeFloat;

        // Sortie : 16-bit PCM (lisible partout, fichiers compacts). Si la source n'est
        // pas en float, on conserve son format tel quel (déjà PCM).
        var outFormat = _sourceIsFloat
            ? new WaveFormat(src.SampleRate, 16, src.Channels)
            : src;

        _path = outputPath;
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        _writer = new WaveFileWriter(outputPath, outFormat);
        _keptBytes = 0;
        _keeping = false;

        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
        _capture.StartRecording();
    }

    /// <summary>À partir de maintenant, l'audio capté est écrit dans le WAV. Idempotent.</summary>
    public void BeginKeeping() => _keeping = true;

    /// <summary>Arrête la capture ; <see cref="Finalized"/> sera émis une fois le fichier clos.</summary>
    public void Stop() => _capture?.StopRecording();

    private static MMDevice ResolveDevice(MMDeviceEnumerator enumerator, string? deviceId)
    {
        if (!string.IsNullOrEmpty(deviceId))
        {
            try { return enumerator.GetDevice(deviceId); }
            catch { /* périphérique débranché : on retombe sur le défaut */ }
        }
        return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!_keeping) return;
        lock (_writerLock)
        {
            if (_writer is null) return;

            if (_sourceIsFloat)
            {
                // 32-bit float → 16-bit PCM, échantillon par échantillon.
                var sampleCount = e.BytesRecorded / 4;
                var pcm = new byte[sampleCount * 2];
                for (var i = 0; i < sampleCount; i++)
                {
                    var f = BitConverter.ToSingle(e.Buffer, i * 4);
                    var s = (short)Math.Clamp((int)(f * 32767f), short.MinValue, short.MaxValue);
                    pcm[i * 2] = (byte)s;
                    pcm[i * 2 + 1] = (byte)(s >> 8);
                }
                _writer.Write(pcm, 0, pcm.Length);
                _keptBytes += pcm.Length;
            }
            else
            {
                _writer.Write(e.Buffer, 0, e.BytesRecorded);
                _keptBytes += e.BytesRecorded;
            }
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        var path = _path;
        var saved = _keptBytes > 0;

        lock (_writerLock) { _writer?.Dispose(); _writer = null; }
        if (_capture is not null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            _capture.Dispose();
            _capture = null;
        }
        _keeping = false;

        // Aucun son conservé (arrêt avant le début du bloc) : on retire le WAV vide.
        if (!saved && path is not null)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* sans gravité */ }
        }

        _path = null;
        Finalized?.Invoke(path, saved);
    }

    public void Dispose()
    {
        try { _capture?.StopRecording(); } catch { /* ignore */ }
        lock (_writerLock) { _writer?.Dispose(); _writer = null; }
        _capture?.Dispose();
        _capture = null;
    }
}
