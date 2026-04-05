use serde::{Deserialize, Serialize};
use std::sync::atomic::{AtomicUsize, Ordering};
use std::time::{SystemTime, UNIX_EPOCH};

static COUNTER: AtomicUsize = AtomicUsize::new(0);

fn generate_id() -> String {
    let start = SystemTime::now();
    let since_the_epoch = start.duration_since(UNIX_EPOCH).unwrap_or_default();
    let count = COUNTER.fetch_add(1, Ordering::SeqCst);
    format!("{:x}-{:x}", since_the_epoch.as_nanos(), count)
}

#[derive(Debug, Serialize, Deserialize)]
pub struct DialogueBlock {
    pub id: String,
    pub text: String,
    #[serde(rename = "startTime")]
    pub start_time: f64,
    pub duration: f64,
    #[serde(rename = "characterName")]
    pub character_name: String,
    pub color: String,
    pub lane: u32,
}

#[tauri::command]
pub fn import_subtitles(path: String) -> Result<Vec<DialogueBlock>, String> {
    let content = std::fs::read_to_string(&path)
        .map_err(|e| format!("Impossible de lire le fichier: {}", e))?;

    let path_lower = path.to_lowercase();
    if path_lower.ends_with(".srt") {
        Ok(parse_srt(&content))
    } else if path_lower.ends_with(".vtt") {
        Ok(parse_vtt(&content))
    } else {
        Err("Format non supporté. Veuillez utiliser .srt ou .vtt".to_string())
    }
}

fn parse_srt(content: &str) -> Vec<DialogueBlock> {
    let mut blocks = Vec::new();
    let content_normalized = content.replace("\r\n", "\n");
    let chunks: Vec<&str> = content_normalized.split("\n\n").collect();

    for chunk in chunks {
        let lines: Vec<&str> = chunk.split('\n').collect();
        if lines.len() < 3 {
            continue;
        }

        let mut time_line_idx = 1;
        if !lines[1].contains("-->") {
            time_line_idx = 0;
            if !lines[0].contains("-->") {
                continue;
            }
        }

        let time_line = lines[time_line_idx];
        let parts: Vec<&str> = time_line.split("-->").collect();
        if parts.len() < 2 {
            continue;
        }

        let start_time = parse_srt_time(parts[0]);
        let end_time = parse_srt_time(parts[1]);

        // lines.slice(timeLineIndex + 1).join('\n');
        let text = lines[time_line_idx + 1..].join("\n").trim().to_string();

        blocks.push(DialogueBlock {
            id: generate_id(),
            text,
            start_time,
            duration: (end_time - start_time).max(0.1),
            character_name: "Import".to_string(),
            color: "#8b5cf6".to_string(),
            lane: 0,
        });
    }

    blocks
}

fn parse_srt_time(time_str: &str) -> f64 {
    // 00:00:00,000
    let parts: Vec<&str> = time_str.trim().split(':').collect();
    if parts.len() == 3 {
        let sec_ms: Vec<&str> = parts[2].split(',').collect();
        if sec_ms.len() == 2 {
            let h: f64 = parts[0].trim().parse().unwrap_or(0.0);
            let m: f64 = parts[1].trim().parse().unwrap_or(0.0);
            let s: f64 = sec_ms[0].trim().parse().unwrap_or(0.0);
            let ms: f64 = sec_ms[1].trim().parse().unwrap_or(0.0);
            return h * 3600.0 + m * 60.0 + s + ms / 1000.0;
        }
    }
    0.0
}

fn parse_vtt(content: &str) -> Vec<DialogueBlock> {
    let mut blocks = Vec::new();
    let content_normalized = content.replace("\r\n", "\n");
    let lines: Vec<&str> = content_normalized.split('\n').collect();

    let mut i = 0;
    if !lines.is_empty() && lines[0].starts_with("WEBVTT") {
        i += 1;
    }

    while i < lines.len() {
        let line = lines[i].trim();
        if line.is_empty() {
            i += 1;
            continue;
        }

        if line.contains("-->") {
            let parts: Vec<&str> = line.split("-->").collect();
            if parts.len() >= 2 {
                let start_time = parse_vtt_time(parts[0]);
                // Remove potential alignment tags like "line:10% align:center"
                let end_str = parts[1].split_whitespace().next().unwrap_or("");
                let end_time = parse_vtt_time(end_str);

                i += 1;
                let mut text = String::new();
                while i < lines.len() && !lines[i].trim().is_empty() {
                    text.push_str(lines[i]);
                    text.push('\n');
                    i += 1;
                }

                blocks.push(DialogueBlock {
                    id: generate_id(),
                    text: text.trim().to_string(),
                    start_time,
                    duration: (end_time - start_time).max(0.1),
                    character_name: "Import".to_string(),
                    color: "#10b981".to_string(),
                    lane: 0,
                });
            } else {
                i += 1;
            }
        } else {
            i += 1;
        }
    }

    blocks
}

fn parse_vtt_time(time_str: &str) -> f64 {
    // HH:MM:SS.mmm or MM:SS.mmm
    let parts: Vec<&str> = time_str.trim().split(':').collect();
    if parts.len() == 3 {
        let h: f64 = parts[0].trim().parse().unwrap_or(0.0);
        let m: f64 = parts[1].trim().parse().unwrap_or(0.0);
        let s: f64 = parts[2].trim().parse().unwrap_or(0.0);
        h * 3600.0 + m * 60.0 + s
    } else if parts.len() == 2 {
        let m: f64 = parts[0].trim().parse().unwrap_or(0.0);
        let s: f64 = parts[1].trim().parse().unwrap_or(0.0);
        m * 60.0 + s
    } else {
        0.0
    }
}
