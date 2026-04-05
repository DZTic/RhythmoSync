use serde::Serialize;
use std::io::Read;
use std::path::PathBuf;
use std::process::{Command, Stdio};
use tauri::{AppHandle, Manager};

#[cfg(target_os = "windows")]
use std::os::windows::process::CommandExt;

/// Returns the path to the bundled FFmpeg binary.
fn get_ffmpeg_path(app: &AppHandle) -> PathBuf {
    let data_dir = app
        .path()
        .app_data_dir()
        .expect("Failed to get app data dir");
    data_dir.join("ffmpeg").join("ffmpeg.exe")
}

/// The data structure returned to the frontend.
/// Contains the RMS peaks (one per "bucket") normalized to [-1.0, 1.0].
/// `peaks` is a Vec<f32> containing interleaved [min, max] values per channel,
/// which is the standard format expected by waveform renderers.
#[derive(Debug, Serialize)]
pub struct WaveformData {
    pub peaks: Vec<f32>,
    pub duration: f64,
    pub sample_rate: u32,
    pub num_channels: u8,
}

/// Tauri command: generates waveform peak data from a video/audio file using FFmpeg.
///
/// # Arguments
/// * `video_path` - Absolute path to the source video or audio file.
/// * `num_samples` - Number of [min, max] "buckets" to generate. Typically ~2000-4000
///   for the full timeline width at default zoom.
///
/// # Returns
/// A `WaveformData` struct with the peaks, duration, sample rate, and channel count.
///
/// # Strategy
/// We use FFmpeg to:
/// 1. Decode the audio stream into raw PCM (signed 16-bit, little-endian, mono).
/// 2. Read the raw bytes into Rust.
/// 3. Bucket the samples into `num_samples` groups, computing the RMS min/max
///    for each bucket to produce an accurate visual representation.
///
/// This offloads the expensive audio decoding from the browser's WebAudio API
/// (which blocks the main thread and loads the entire file into WASM memory)
/// to a fast, native Rust + FFmpeg pipeline running in a separate OS process.
#[tauri::command]
pub async fn generate_waveform(
    app: AppHandle,
    video_path: String,
    num_samples: u32,
) -> Result<WaveformData, String> {
    let ffmpeg_path = get_ffmpeg_path(&app);
    if !ffmpeg_path.exists() {
        return Err("FFmpeg non trouvé. Veuillez le télécharger d'abord.".to_string());
    }

    // --- Étape 1: Obtenir la durée et le sample rate via ffprobe-style (ffmpeg -i) ---
    // We need the sample rate & duration to correctly calculate bucket sizes.
    // We get this from a quick ffmpeg stderr probe (no output file).
    let info = get_audio_info(&ffmpeg_path, &video_path)?;

    let num_samples = num_samples.max(128).min(65536); // Clamp to reasonable limits

    // --- Étape 2: Décoder l'audio en PCM brut via FFmpeg (mono, s16le) ---
    // Output format: signed 16-bit little-endian, 1 channel, full sample rate.
    // Piping to stdout avoids any temporary file on disk.
    let args = [
        "-hide_banner",
        "-loglevel",
        "error",
        "-i",
        &video_path,
        "-ac",
        "1", // Force mono (mix down all channels) — simplifies bucketing
        "-ar",
        &info.sample_rate.to_string(), // Keep original sample rate
        "-f",
        "s16le",  // Raw signed 16-bit little-endian PCM
        "-vn",    // No video
        "pipe:1", // Output to stdout
    ];

    let mut child = Command::new(&ffmpeg_path)
        .args(&args)
        .stdout(Stdio::piped())
        .stderr(Stdio::null())
        .creation_flags(0x08000000) // CREATE_NO_WINDOW on Windows
        .spawn()
        .map_err(|e| format!("Impossible de démarrer FFmpeg: {}", e))?;

    let mut stdout = child
        .stdout
        .take()
        .ok_or("Impossible de capturer la sortie de FFmpeg")?;

    // Read all PCM bytes
    let mut raw_bytes: Vec<u8> =
        Vec::with_capacity((info.sample_rate as usize) * (info.duration as usize + 1) * 2);
    stdout
        .read_to_end(&mut raw_bytes)
        .map_err(|e| format!("Erreur de lecture PCM: {}", e))?;

    child
        .wait()
        .map_err(|e| format!("Erreur d'attente FFmpeg: {}", e))?;

    if raw_bytes.len() < 2 {
        return Err("Aucune donnée audio trouvée dans le fichier.".to_string());
    }

    // --- Étape 3: Convertir les bytes bruts en échantillons i16 ---
    // s16le = 2 bytes per sample, little-endian
    let total_samples = raw_bytes.len() / 2;
    let samples: Vec<i16> = raw_bytes
        .chunks_exact(2)
        .map(|chunk| i16::from_le_bytes([chunk[0], chunk[1]]))
        .collect();

    // --- Étape 4: Calculer les pics min/max par bucket ---
    // Produce `num_samples` buckets. Each bucket contains [min, max] normalized to [-1.0, 1.0].
    // This is the standard "dual-line" waveform format used by audio editors.
    let bucket_size = (total_samples as f64 / num_samples as f64).max(1.0);
    let mut peaks: Vec<f32> = Vec::with_capacity(num_samples as usize * 2);

    for i in 0..num_samples as usize {
        let start = (i as f64 * bucket_size) as usize;
        let end = ((i + 1) as f64 * bucket_size) as usize;
        let end = end.min(total_samples);

        if start >= end {
            peaks.push(0.0);
            peaks.push(0.0);
            continue;
        }

        let bucket = &samples[start..end];
        let min_sample = bucket.iter().copied().min().unwrap_or(0) as f32 / i16::MAX as f32;
        let max_sample = bucket.iter().copied().max().unwrap_or(0) as f32 / i16::MAX as f32;

        peaks.push(min_sample.clamp(-1.0, 1.0));
        peaks.push(max_sample.clamp(-1.0, 1.0));
    }

    Ok(WaveformData {
        peaks,
        duration: info.duration,
        sample_rate: info.sample_rate,
        num_channels: 1,
    })
}

// ============================================================
// Internal Helper: Parse audio duration & sample rate via FFmpeg
// ============================================================

struct AudioInfo {
    duration: f64,
    sample_rate: u32,
}

fn get_audio_info(ffmpeg_path: &PathBuf, video_path: &str) -> Result<AudioInfo, String> {
    // Use ffmpeg with null output to get stream info from stderr
    let output = Command::new(ffmpeg_path)
        .args(["-hide_banner", "-i", video_path, "-f", "null", "-"])
        .stdout(Stdio::null())
        .stderr(Stdio::piped())
        .creation_flags(0x08000000)
        .output()
        .map_err(|e| format!("Impossible d'analyser le fichier: {}", e))?;

    let stderr = String::from_utf8_lossy(&output.stderr);

    // Parse duration from "Duration: HH:MM:SS.ss"
    let duration = parse_duration_from_ffmpeg_output(&stderr)
        .ok_or_else(|| "Impossible de lire la durée du fichier.".to_string())?;

    // Parse sample rate from "Audio: ..., 44100 Hz"
    let sample_rate = parse_sample_rate_from_ffmpeg_output(&stderr).unwrap_or(44100); // Safe default

    Ok(AudioInfo {
        duration,
        sample_rate,
    })
}

fn parse_duration_from_ffmpeg_output(stderr: &str) -> Option<f64> {
    // Looks for: "Duration: 01:23:45.67,"
    let duration_prefix = "Duration: ";
    let line = stderr.lines().find(|l| l.contains(duration_prefix))?;
    let start = line.find(duration_prefix)? + duration_prefix.len();
    let end = line[start..].find(',')?;
    let time_str = &line[start..start + end]; // "HH:MM:SS.ss"

    let parts: Vec<&str> = time_str.split(':').collect();
    if parts.len() != 3 {
        return None;
    }

    let hours: f64 = parts[0].trim().parse().ok()?;
    let minutes: f64 = parts[1].trim().parse().ok()?;
    let seconds: f64 = parts[2].trim().parse().ok()?;

    Some(hours * 3600.0 + minutes * 60.0 + seconds)
}

fn parse_sample_rate_from_ffmpeg_output(stderr: &str) -> Option<u32> {
    // Looks for: "Audio: aac, 44100 Hz" or similar audio info lines
    let audio_line = stderr
        .lines()
        .find(|l| l.contains("Audio:") && l.contains(" Hz"))?;

    // Find " XXXXX Hz" pattern
    let hz_pos = audio_line.find(" Hz")?;
    // Walk backwards from " Hz" to find the number
    let before_hz = &audio_line[..hz_pos];
    let num_start = before_hz.rfind(' ')?;
    let num_str = &before_hz[num_start + 1..];

    num_str.trim().parse::<u32>().ok()
}
