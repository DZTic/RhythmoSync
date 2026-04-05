use serde::{Deserialize, Serialize};
use std::io::{BufWriter, Read, Write};
use std::path::PathBuf;
use std::process::{Command, Stdio};
use tauri::{AppHandle, Emitter, Manager};

#[cfg(target_os = "windows")]
use std::os::windows::process::CommandExt;

// --- Types ---

#[derive(Debug, Deserialize)]
pub struct ExportRequest {
    pub video_path: String,
    pub output_path: String,
    pub fps: f64,
    pub bitrate: u64,            // in bps (e.g. 8_000_000)
    pub band_strip_path: String, // path to pre-rendered band strip PNG
    pub video_width: u32,        // native video width

    pub crop_top: u32,
    pub crop_bottom: u32,
    pub export_width: u32,        // always 1920
    pub export_height: u32,       // exactly the target height (e.g. 1080)
    pub video_render_height: u32, // scaled video height (after crop+scale)
    pub band_render_height: u32,  // band height in export

    pub band_strip_height: u32, // height of the band strip image
    pub pps: f64,               // pixels per second
    pub sync_offset: f64,       // sync offset in seconds
    pub duration: f64,
    pub sync_line_x: u32,
    pub title: String,
    pub comment: String,
    pub description: String,
}

#[derive(Debug, Serialize, Clone)]
pub struct ExportProgress {
    pub percent: u32,
    pub fps_effective: f64,
    pub estimated_remaining: String,
}

// --- FFmpeg binary management ---

fn get_ffmpeg_dir(app: &AppHandle) -> PathBuf {
    let data_dir = app
        .path()
        .app_data_dir()
        .expect("Failed to get app data dir");
    data_dir.join("ffmpeg")
}

fn get_ffmpeg_path(app: &AppHandle) -> PathBuf {
    get_ffmpeg_dir(app).join("ffmpeg.exe")
}

#[tauri::command]
pub async fn check_ffmpeg(app: AppHandle) -> Result<bool, String> {
    let ffmpeg_path = get_ffmpeg_path(&app);
    Ok(ffmpeg_path.exists())
}

#[tauri::command]
pub async fn download_ffmpeg(app: AppHandle) -> Result<String, String> {
    let ffmpeg_dir = get_ffmpeg_dir(&app);
    let ffmpeg_path = get_ffmpeg_path(&app);

    if ffmpeg_path.exists() {
        return Ok(ffmpeg_path.to_string_lossy().to_string());
    }

    std::fs::create_dir_all(&ffmpeg_dir).map_err(|e| format!("Failed to create dir: {}", e))?;

    // Download FFmpeg essentials build from gyan.dev (trusted, well-known source)
    let url = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";

    app.emit("ffmpeg-download-progress", "Téléchargement de FFmpeg...")
        .ok();

    let response = reqwest::get(url)
        .await
        .map_err(|e| format!("Download failed: {}", e))?;

    if !response.status().is_success() {
        return Err(format!(
            "Download failed with status: {}",
            response.status()
        ));
    }

    let bytes = response
        .bytes()
        .await
        .map_err(|e| format!("Failed to read response: {}", e))?;

    app.emit("ffmpeg-download-progress", "Extraction de FFmpeg...")
        .ok();

    // Extract ffmpeg.exe from the zip
    let reader = std::io::Cursor::new(&bytes);
    let mut archive =
        zip::ZipArchive::new(reader).map_err(|e| format!("Failed to open zip: {}", e))?;

    let mut found = false;
    for i in 0..archive.len() {
        let mut file = archive
            .by_index(i)
            .map_err(|e| format!("Failed to read zip entry: {}", e))?;

        let name = file.name().to_string();
        if name.ends_with("bin/ffmpeg.exe") || name.ends_with("bin\\ffmpeg.exe") {
            let mut outfile = std::fs::File::create(&ffmpeg_path)
                .map_err(|e| format!("Failed to create ffmpeg.exe: {}", e))?;
            std::io::copy(&mut file, &mut outfile)
                .map_err(|e| format!("Failed to write ffmpeg.exe: {}", e))?;
            found = true;
            break;
        }
    }

    if !found {
        return Err("ffmpeg.exe not found in the downloaded archive".to_string());
    }

    app.emit("ffmpeg-download-progress", "FFmpeg prêt !").ok();
    Ok(ffmpeg_path.to_string_lossy().to_string())
}

// --- Video Format Detection (ffprobe) ---

#[derive(Debug, Serialize, Clone)]
pub struct VideoInfo {
    pub codec_name: String, // e.g. "h264", "hevc", "vp9", "av1", "mpeg4"
    pub container: String,  // e.g. "mp4", "mkv", "avi", "mov"
    pub width: u32,
    pub height: u32,
    pub duration: f64,     // seconds
    pub needs_proxy: bool, // true if the WebView cannot play this natively
    pub reason: String,    // human-readable reason why proxy is needed
}

/// Formats / codecs nativement lus par WebView2 (Chromium) sur Windows.
/// On accepte ces combinaisons sans proxy obligatoire.
fn is_natively_supported(container: &str, codec: &str) -> (bool, &'static str) {
    let container_ok = matches!(container, "mp4" | "webm" | "ogg");
    let codec_ok = matches!(codec, "h264" | "vp8" | "vp9" | "theora" | "av1");

    if !container_ok {
        return (
            false,
            "Conteneur non supporté nativement (MKV, AVI, MOV) — proxy requis",
        );
    }
    if !codec_ok {
        return (
            false,
            "Codec vidéo non supporté nativement (HEVC, MPEG-4, etc.) — proxy requis",
        );
    }
    (true, "")
}

#[tauri::command]
pub async fn get_video_info(app: AppHandle, video_path: String) -> Result<VideoInfo, String> {
    let ffmpeg_dir = get_ffmpeg_dir(&app);
    let ffprobe_path = ffmpeg_dir.join("ffprobe.exe");

    // Fallback: utiliser ffmpeg lui-même si ffprobe n'est pas disponible
    let probe_bin = if ffprobe_path.exists() {
        ffprobe_path.to_string_lossy().to_string()
    } else {
        let ffmpeg_path = get_ffmpeg_path(&app);
        if !ffmpeg_path.exists() {
            // FFmpeg absent: on ne peut pas sonder, retourner une info basique
            let ext = std::path::Path::new(&video_path)
                .extension()
                .unwrap_or_default()
                .to_string_lossy()
                .to_lowercase();
            let container = ext.clone();
            let (supported, reason) = is_natively_supported(&container, "h264");
            return Ok(VideoInfo {
                codec_name: "unknown".to_string(),
                container: container.to_string(),
                width: 0,
                height: 0,
                duration: 0.0,
                needs_proxy: !supported,
                reason: if supported {
                    "".to_string()
                } else {
                    reason.to_string()
                },
            });
        }
        ffmpeg_path.to_string_lossy().to_string()
    };

    // Interroger ffprobe/ffmpeg pour récupérer les infos de la piste vidéo
    let output = Command::new(&probe_bin)
        .args(&[
            "-v",
            "quiet",
            "-print_format",
            "json",
            "-show_streams",
            "-show_format",
            "-select_streams",
            "v:0",
            &video_path,
        ])
        .stdout(Stdio::piped())
        .stderr(Stdio::null())
        .creation_flags(0x08000000)
        .output()
        .map_err(|e| format!("Impossible de lancer ffprobe: {}", e))?;

    let json_str = String::from_utf8_lossy(&output.stdout);

    // Parse JSON basique sans dépendance serde_json complexe
    let codec_name = extract_json_str(&json_str, "codec_name").unwrap_or("unknown".to_string());
    let width = extract_json_u32(&json_str, "width").unwrap_or(0);
    let height = extract_json_u32(&json_str, "height").unwrap_or(0);
    let duration_str = extract_json_str(&json_str, "duration").unwrap_or("0".to_string());
    let duration: f64 = duration_str.parse().unwrap_or(0.0);

    // Déterminer le conteneur depuis l'extension du fichier
    let ext = std::path::Path::new(&video_path)
        .extension()
        .unwrap_or_default()
        .to_string_lossy()
        .to_lowercase();
    let container = ext.to_string();

    let (supported, reason) = is_natively_supported(&container, &codec_name);

    Ok(VideoInfo {
        codec_name,
        container,
        width,
        height,
        duration,
        needs_proxy: !supported,
        reason: if supported {
            "".to_string()
        } else {
            reason.to_string()
        },
    })
}

/// Extrait une valeur string depuis un JSON plat  
fn extract_json_str(json: &str, key: &str) -> Option<String> {
    let pattern = format!("\"{}\":", key);
    let start = json.find(&pattern)? + pattern.len();
    let rest = json[start..].trim_start();
    if rest.starts_with('"') {
        let inner = &rest[1..];
        let end = inner.find('"')?;
        Some(inner[..end].to_string())
    } else {
        // Valeur numérique retournée comme string
        let end = rest.find(|c: char| c == ',' || c == '}' || c == '\n')?;
        Some(rest[..end].trim().to_string())
    }
}

/// Extrait une valeur u32 depuis un JSON plat
fn extract_json_u32(json: &str, key: &str) -> Option<u32> {
    extract_json_str(json, key)?.parse().ok()
}

// --- GPU Encoder Detection ---

#[derive(Debug, Serialize, Clone)]
pub struct GpuEncoderInfo {
    pub encoder: String, // e.g. "h264_nvenc", "h264_amf", "libx264"
    pub label: String,   // e.g. "NVIDIA NVENC (GPU)", "CPU (libx264)"
    pub is_gpu: bool,
}

/// Probe FFmpeg to find the best available H.264 encoder.
/// Priority: NVENC (NVIDIA) > AMF (AMD) > VideoToolbox (macOS) > libx264 (CPU fallback)
fn detect_best_encoder(ffmpeg_path: &str) -> GpuEncoderInfo {
    let gpu_encoders = vec![
        ("h264_nvenc", "NVIDIA NVENC (GPU)"),
        ("h264_amf", "AMD AMF (GPU)"),
        ("h264_videotoolbox", "Apple VideoToolbox (GPU)"),
    ];

    for (encoder, label) in &gpu_encoders {
        // Try to initialize the encoder with a null input to verify it actually works
        let result = Command::new(ffmpeg_path)
            .args(&[
                "-hide_banner",
                "-loglevel",
                "error",
                "-f",
                "lavfi",
                "-i",
                "nullsrc=s=256x256:d=0.1",
                "-c:v",
                encoder,
                "-f",
                "null",
                "-",
            ])
            .stdout(Stdio::null())
            .stderr(Stdio::piped())
            .creation_flags(0x08000000)
            .output();

        if let Ok(output) = result {
            if output.status.success() {
                return GpuEncoderInfo {
                    encoder: encoder.to_string(),
                    label: label.to_string(),
                    is_gpu: true,
                };
            }
        }
    }

    // Fallback: CPU
    GpuEncoderInfo {
        encoder: "libx264".to_string(),
        label: "CPU (libx264)".to_string(),
        is_gpu: false,
    }
}

#[tauri::command]
pub async fn detect_gpu_encoder(app: AppHandle) -> Result<GpuEncoderInfo, String> {
    let ffmpeg_path = get_ffmpeg_path(&app);
    if !ffmpeg_path.exists() {
        return Ok(GpuEncoderInfo {
            encoder: "libx264".to_string(),
            label: "CPU (libx264)".to_string(),
            is_gpu: false,
        });
    }
    let ffmpeg = ffmpeg_path.to_string_lossy().to_string();
    Ok(detect_best_encoder(&ffmpeg))
}

// --- Export Pipeline ---

#[tauri::command]
pub async fn export_video_native(app: AppHandle, request: ExportRequest) -> Result<String, String> {
    let ffmpeg_path = get_ffmpeg_path(&app);
    if !ffmpeg_path.exists() {
        return Err("FFmpeg not found. Please download it first.".to_string());
    }

    // Ensure even dimensions
    let export_width = if request.export_width % 2 == 0 {
        request.export_width
    } else {
        request.export_width - 1
    };
    let export_height = if request.export_height % 2 == 0 {
        request.export_height
    } else {
        request.export_height - 1
    };
    let total_frames = (request.duration * request.fps).ceil() as u32;

    // Load the pre-rendered band strip image
    let band_strip = image::open(&request.band_strip_path)
        .map_err(|e| format!("Failed to load band strip: {}", e))?
        .to_rgba8();

    let ffmpeg = ffmpeg_path.to_string_lossy().to_string();

    // --- DETECT BEST ENCODER (GPU or CPU) ---
    let encoder_info = detect_best_encoder(&ffmpeg);
    app.emit("export-encoder-info", &encoder_info).ok();

    // GPU encoders need different preset names and some need extra options
    let (codec, preset, extra_args): (&str, &str, Vec<String>) = match encoder_info.encoder.as_str()
    {
        "h264_nvenc" => (
            "h264_nvenc",
            "p4", // NVENC presets: p1 (fastest) to p7 (slowest/best quality)
            vec![
                "-rc".to_string(),
                "vbr".to_string(),
                "-rc-lookahead".to_string(),
                "32".to_string(),
            ],
        ),
        "h264_amf" => (
            "h264_amf",
            "balanced",
            vec!["-quality".to_string(), "balanced".to_string()],
        ),
        "h264_videotoolbox" => (
            "h264_videotoolbox",
            "", // VideoToolbox doesn't use -preset
            vec!["-allow_sw".to_string(), "1".to_string()],
        ),
        _ => ("libx264", "medium", vec![]),
    };

    // --- DECODER PROCESS ---
    // FFmpeg decodes the video and outputs raw RGBA frames to stdout
    let crop_h = request.crop_bottom - request.crop_top;
    let decoder_args = vec![
        "-hide_banner".to_string(),
        "-loglevel".to_string(),
        "error".to_string(),
        "-hwaccel".to_string(),
        "auto".to_string(),
        "-i".to_string(),
        request.video_path.clone(),
        "-vf".to_string(),
        format!(
            "crop={}:{}:0:{},scale={}:{}:force_original_aspect_ratio=decrease,pad={}:{}:(ow-iw)/2:(oh-ih)/2,fps={}",
            request.video_width,
            crop_h,
            request.crop_top,
            export_width,
            request.video_render_height,
            export_width,
            request.video_render_height,
            request.fps
        ),
        "-f".to_string(),
        "rawvideo".to_string(),
        "-pix_fmt".to_string(),
        "rgba".to_string(),
        "-an".to_string(), // no audio
        "pipe:1".to_string(),
    ];

    let mut decoder = Command::new(&ffmpeg)
        .args(&decoder_args)
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .creation_flags(0x08000000) // CREATE_NO_WINDOW on Windows
        .spawn()
        .map_err(|e| format!("Failed to start decoder: {}", e))?;

    // --- ENCODER PROCESS ---
    // FFmpeg reads raw RGBA frames from stdin and encodes to MP4
    let mut encoder_args = vec![
        "-hide_banner".to_string(),
        "-loglevel".to_string(),
        "error".to_string(),
        "-f".to_string(),
        "rawvideo".to_string(),
        "-pix_fmt".to_string(),
        "rgba".to_string(),
        "-s".to_string(),
        format!("{}x{}", export_width, export_height),
        "-r".to_string(),
        format!("{}", request.fps),
        "-i".to_string(),
        "pipe:0".to_string(),
        "-c:v".to_string(),
        codec.to_string(),
    ];

    // Add preset if applicable (VideoToolbox doesn't use -preset)
    if !preset.is_empty() {
        encoder_args.push("-preset".to_string());
        encoder_args.push(preset.to_string());
    }

    // Add bitrate
    encoder_args.push("-b:v".to_string());
    encoder_args.push(format!("{}", request.bitrate));

    // Add GPU-specific extra args
    encoder_args.extend(extra_args);

    // Common output args
    encoder_args.extend(vec![
        "-pix_fmt".to_string(),
        "yuv420p".to_string(),
        "-movflags".to_string(),
        "+faststart".to_string(),
        // Metadata
        "-metadata".to_string(),
        format!("title={}", request.title),
        "-metadata".to_string(),
        format!("comment={}", request.comment),
        "-metadata".to_string(),
        format!("description={}", request.description),
        "-metadata".to_string(),
        "encoder=RhythmoSync Studio".to_string(),
        "-y".to_string(), // overwrite output
        request.output_path.clone(),
    ]);

    let mut encoder = Command::new(&ffmpeg)
        .args(&encoder_args)
        .stdin(Stdio::piped())
        .stderr(Stdio::piped())
        .creation_flags(0x08000000) // CREATE_NO_WINDOW
        .spawn()
        .map_err(|e| format!("Failed to start encoder: {}", e))?;

    let decoder_stdout = decoder
        .stdout
        .take()
        .ok_or("Failed to capture decoder stdout")?;
    let encoder_stdin = encoder
        .stdin
        .take()
        .ok_or("Failed to capture encoder stdin")?;

    // --- FRAME PROCESSING LOOP ---
    let frame_size = (export_width * request.video_render_height * 4) as usize; // RGBA
    let out_frame_size = (export_width * export_height * 4) as usize;
    let mut video_buf = vec![0u8; frame_size];
    let mut reader = std::io::BufReader::new(decoder_stdout);
    let mut writer = BufWriter::new(encoder_stdin);

    let start_time = std::time::Instant::now();
    let mut frame_count: u32 = 0;
    let mut last_progress_update = std::time::Instant::now();

    loop {
        // Read one decoded video frame (RGBA)
        let read_result: Result<(), std::io::Error> = reader.read_exact(&mut video_buf);
        match read_result {
            Ok(()) => {}
            Err(ref e) if e.kind() == std::io::ErrorKind::UnexpectedEof => break,
            Err(e) => {
                return Err(format!(
                    "Decoder read error at frame {}: {}",
                    frame_count, e
                ))
            }
        }

        // Build composite frame: video on top, band strip section below
        let mut out_frame = vec![0u8; out_frame_size];

        // Copy video pixels (top portion)
        out_frame[..frame_size].copy_from_slice(&video_buf);

        // Calculate band strip X offset for this frame's timestamp
        let time = frame_count as f64 / request.fps;
        let strip_x_offset = ((time + request.sync_offset) * request.pps) as i32;

        // Copy the appropriate horizontal section of the band strip
        let band_y_start = request.video_render_height;
        for row in 0..request.band_render_height {
            // Source row in the band strip (scale from render height to strip height)
            let src_row = (row as f64 / request.band_render_height as f64
                * request.band_strip_height as f64) as u32;
            if src_row >= band_strip.height() {
                break;
            }

            for col in 0..export_width {
                // Source column in the band strip
                let src_col = strip_x_offset + col as i32;

                let out_idx = ((band_y_start + row) * export_width + col) as usize * 4;
                if out_idx + 3 >= out_frame.len() {
                    continue;
                }

                // Draw Sync Line (Red, 2px wide)
                if col >= request.sync_line_x && col < request.sync_line_x + 2 {
                    out_frame[out_idx] = 0xFF; // R
                    out_frame[out_idx + 1] = 0x00; // G
                    out_frame[out_idx + 2] = 0x00; // B
                    out_frame[out_idx + 3] = 0xFF; // A
                    continue;
                }

                if src_col >= 0 && (src_col as u32) < band_strip.width() {
                    let pixel = band_strip.get_pixel(src_col as u32, src_row);
                    out_frame[out_idx] = pixel[0]; // R
                    out_frame[out_idx + 1] = pixel[1]; // G
                    out_frame[out_idx + 2] = pixel[2]; // B
                    out_frame[out_idx + 3] = pixel[3]; // A
                } else {
                    // Outside strip bounds: dark background
                    out_frame[out_idx] = 0x11;
                    out_frame[out_idx + 1] = 0x11;
                    out_frame[out_idx + 2] = 0x11;
                    out_frame[out_idx + 3] = 0xFF;
                }
            }
        }

        // Write composite frame to encoder
        writer
            .write_all(&out_frame)
            .map_err(|e| format!("Encoder write error at frame {}: {}", frame_count, e))?;

        frame_count += 1;

        // Progress update (every 500ms)
        if last_progress_update.elapsed().as_millis() > 500 {
            let elapsed = start_time.elapsed().as_secs_f64();
            let fps_eff = frame_count as f64 / elapsed;
            let remaining = if fps_eff > 0.0 {
                let rem_frames = total_frames.saturating_sub(frame_count);
                let rem_secs = rem_frames as f64 / fps_eff;
                let m = (rem_secs / 60.0) as u32;
                let s = (rem_secs % 60.0) as u32;
                format!("{}m {}s", m, s)
            } else {
                "Calcul...".to_string()
            };

            let percent = ((frame_count as f64 / total_frames as f64) * 100.0).min(100.0) as u32;
            app.emit(
                "export-progress",
                ExportProgress {
                    percent,
                    fps_effective: fps_eff,
                    estimated_remaining: remaining,
                },
            )
            .ok();

            last_progress_update = std::time::Instant::now();
        }
    }

    // Flush and close encoder input
    writer
        .flush()
        .map_err(|e| format!("Failed to flush encoder: {}", e))?;
    drop(writer); // Close stdin to signal EOF

    // Capture stderr before waiting (take ownership)
    let mut decoder_stderr_pipe = decoder.stderr.take();
    let mut encoder_stderr_pipe = encoder.stderr.take();

    // Wait for both processes to finish
    let decoder_status = decoder
        .wait()
        .map_err(|e| format!("Decoder process error: {}", e))?;
    let encoder_status = encoder
        .wait()
        .map_err(|e| format!("Encoder process error: {}", e))?;

    if !decoder_status.success() {
        let mut stderr = String::new();
        if let Some(ref mut err) = decoder_stderr_pipe {
            err.read_to_string(&mut stderr).ok();
        }
        return Err(format!(
            "Decoder failed (exit {}): {}",
            decoder_status, stderr
        ));
    }

    if !encoder_status.success() {
        let mut stderr = String::new();
        if let Some(ref mut err) = encoder_stderr_pipe {
            err.read_to_string(&mut stderr).ok();
        }
        return Err(format!(
            "Encoder failed (exit {}): {}",
            encoder_status, stderr
        ));
    }

    let total_time = start_time.elapsed().as_secs_f64();
    let final_fps = frame_count as f64 / total_time;

    // Final progress
    app.emit(
        "export-progress",
        ExportProgress {
            percent: 100,
            fps_effective: final_fps,
            estimated_remaining: "Terminé !".to_string(),
        },
    )
    .ok();

    // Clean up temp band strip file
    std::fs::remove_file(&request.band_strip_path).ok();

    Ok(format!(
        "Export terminé: {} frames en {:.1}s ({:.1} fps effectifs)",
        frame_count, total_time, final_fps
    ))
}

// ─────────────────────────────────────────────────────────────────────────────
// Génération du Proxy Vidéo
// Encode une copie légère de la vidéo source en H.264 720p "All-Intra"
// (chaque image est un keyframe indépendant → seeking instantané).
// ─────────────────────────────────────────────────────────────────────────────

#[derive(Debug, Serialize, Clone)]
pub struct ProxyProgress {
    pub percent: u32,
    pub message: String,
}

#[tauri::command]
pub async fn generate_proxy_video(app: AppHandle, video_path: String) -> Result<String, String> {
    let ffmpeg_path = get_ffmpeg_path(&app);
    if !ffmpeg_path.exists() {
        return Err(
            "FFmpeg introuvable. Veuillez l'installer via Fichier > Télécharger FFmpeg."
                .to_string(),
        );
    }

    // Proxy stored in app_data_dir/proxies to prevent accidental deletion
    let data_dir = app
        .path()
        .app_data_dir()
        .unwrap_or_else(|_| std::path::PathBuf::from("."));
    let proxies_dir = data_dir.join("proxies");
    std::fs::create_dir_all(&proxies_dir).ok();

    let source_path = std::path::Path::new(&video_path);
    let stem = source_path
        .file_stem()
        .unwrap_or_default()
        .to_string_lossy();

    // Hash the video path to avoid collisions for files with the same name
    use std::collections::hash_map::DefaultHasher;
    use std::hash::{Hash, Hasher};
    let mut hasher = DefaultHasher::new();
    video_path.hash(&mut hasher);
    let path_hash = hasher.finish();

    let proxy_filename = format!("{}_{:x}_proxy.mp4", stem, path_hash);
    let proxy_path = proxies_dir.join(&proxy_filename);

    // If proxy already exists, skip re-encoding
    if proxy_path.exists() {
        app.emit(
            "proxy-progress",
            ProxyProgress {
                percent: 100,
                message: "Proxy déjà disponible.".to_string(),
            },
        )
        .ok();
        return Ok(proxy_path.to_string_lossy().to_string());
    }

    app.emit(
        "proxy-progress",
        ProxyProgress {
            percent: 0,
            message: "Démarrage de l'encodage proxy…".to_string(),
        },
    )
    .ok();

    // All-Intra H.264 @ 720p, CRF 28 (très rapide à encoder et seeker)
    // -g 1 = GOP size 1 → every frame is a keyframe (All-Intra)
    let args = vec![
        "-hide_banner".to_string(),
        "-loglevel".to_string(),
        "error".to_string(),
        "-hwaccel".to_string(),
        "auto".to_string(),
        "-i".to_string(),
        video_path.clone(),
        "-vf".to_string(),
        "scale=-2:720".to_string(), // 720p, keep AR
        "-c:v".to_string(),
        "libx264".to_string(),
        "-preset".to_string(),
        "ultrafast".to_string(), // fast encode
        "-crf".to_string(),
        "28".to_string(),
        "-g".to_string(),
        "1".to_string(), // All-Intra (keyframe every frame)
        "-keyint_min".to_string(),
        "1".to_string(),
        "-sc_threshold".to_string(),
        "0".to_string(),
        "-c:a".to_string(),
        "aac".to_string(),
        "-b:a".to_string(),
        "128k".to_string(),
        "-movflags".to_string(),
        "+faststart".to_string(),
        "-y".to_string(),
        proxy_path.to_string_lossy().to_string(),
    ];

    let mut child = std::process::Command::new(&ffmpeg_path)
        .args(&args)
        .stdout(std::process::Stdio::null())
        .stderr(std::process::Stdio::piped())
        .creation_flags(0x08000000) // CREATE_NO_WINDOW on Windows
        .spawn()
        .map_err(|e| format!("Impossible de lancer FFmpeg: {}", e))?;

    // Read stderr to parse duration / time progress
    let stderr = child.stderr.take().ok_or("Cannot capture stderr")?;
    let reader = std::io::BufReader::new(stderr);

    use std::io::BufRead;
    let mut duration_secs: f64 = 0.0;

    for line in reader.lines() {
        let line = match line {
            Ok(l) => l,
            Err(_) => break,
        };

        // Parse total duration (appears once near the start)
        if duration_secs == 0.0 {
            if let Some(pos) = line.find("Duration:") {
                let after = &line[pos + 9..];
                let parts: Vec<&str> = after.trim().split(':').collect();
                if parts.len() >= 3 {
                    let h: f64 = parts[0].parse().unwrap_or(0.0);
                    let m: f64 = parts[1].parse().unwrap_or(0.0);
                    let s: f64 = parts[2]
                        .split(',')
                        .next()
                        .unwrap_or("0")
                        .parse()
                        .unwrap_or(0.0);
                    duration_secs = h * 3600.0 + m * 60.0 + s;
                }
            }
        }

        // Parse encoding progress (time=HH:MM:SS.xx)
        if let Some(pos) = line.find("time=") {
            let after = &line[pos + 5..];
            let parts: Vec<&str> = after.trim().split(':').collect();
            if parts.len() >= 3 && duration_secs > 0.0 {
                let h: f64 = parts[0].parse().unwrap_or(0.0);
                let m: f64 = parts[1].parse().unwrap_or(0.0);
                let s: f64 = parts[2]
                    .split_whitespace()
                    .next()
                    .unwrap_or("0")
                    .parse()
                    .unwrap_or(0.0);
                let encoded_secs = h * 3600.0 + m * 60.0 + s;
                let percent = ((encoded_secs / duration_secs) * 100.0).min(99.0) as u32;
                app.emit(
                    "proxy-progress",
                    ProxyProgress {
                        percent,
                        message: format!("Encodage proxy… {:.0}%", percent),
                    },
                )
                .ok();
            }
        }
    }

    let status = child
        .wait()
        .map_err(|e| format!("FFmpeg wait error: {}", e))?;

    if !status.success() {
        // Clean up partial file
        std::fs::remove_file(&proxy_path).ok();
        return Err("L'encodage du proxy a échoué.".to_string());
    }

    app.emit(
        "proxy-progress",
        ProxyProgress {
            percent: 100,
            message: "Proxy prêt !".to_string(),
        },
    )
    .ok();

    Ok(proxy_path.to_string_lossy().to_string())
}

#[tauri::command]
pub async fn delete_proxy_video(app: AppHandle, video_path: String) -> Result<String, String> {
    let data_dir = app.path().app_data_dir().map_err(|e| e.to_string())?;
    let proxies_dir = data_dir.join("proxies");

    let source_path = std::path::Path::new(&video_path);
    let stem = source_path
        .file_stem()
        .unwrap_or_default()
        .to_string_lossy();

    // Reproduce the same hash logic
    use std::collections::hash_map::DefaultHasher;
    use std::hash::{Hash, Hasher};
    let mut hasher = DefaultHasher::new();
    video_path.hash(&mut hasher);
    let path_hash = hasher.finish();

    let proxy_filename = format!("{}_{:x}_proxy.mp4", stem, path_hash);
    let proxy_path = proxies_dir.join(&proxy_filename);

    if proxy_path.exists() {
        std::fs::remove_file(&proxy_path)
            .map_err(|e| format!("Impossible de supprimer le proxy: {}", e))?;
        Ok("Proxy supprimé avec succès.".to_string())
    } else {
        Ok("Aucun proxy trouvé à supprimer.".to_string())
    }
}
