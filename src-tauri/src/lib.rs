pub mod diarization;
pub mod export;
pub mod import;
pub mod waveform;

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_shell::init())
        .plugin(tauri_plugin_fs::init())
        .plugin(tauri_plugin_dialog::init())
        .plugin(tauri_plugin_log::Builder::new().build())
        .invoke_handler(tauri::generate_handler![
            waveform::generate_waveform,
            import::import_subtitles,
            export::check_ffmpeg,
            export::download_ffmpeg,
            export::get_video_info,
            export::detect_gpu_encoder,
            export::export_video_native,
            export::generate_proxy_video,
            export::delete_proxy_video,
            diarization::check_whisper,
            diarization::download_whisper,
            diarization::delete_whisper_model,
            diarization::run_whisper_transcription,
        ])
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
