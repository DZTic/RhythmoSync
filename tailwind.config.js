/** @type {import('tailwindcss').Config} */
module.exports = {
    content: [
        "./index.html",
        "./components/**/*.{js,ts,jsx,tsx}",
        "./*.{js,ts,jsx,tsx}",
    ],
    theme: {
        extend: {
            // ── Palette personnalisée RhythmoSync ─────────────────────
            colors: {
                rs: {
                    base: '#080c14',
                    surface: '#0e1420',
                    elevated: '#161d2e',
                    muted: '#1e2842',
                    border: '#1e2d47',
                    accent: '#6366f1',
                    'accent-light': '#818cf8',
                    danger: '#ef4444',
                },
            },
            // ── Typographie ─────────────────────────────────────────────
            fontFamily: {
                sans: ['Inter', 'system-ui', '-apple-system', 'sans-serif'],
                mono: ['Roboto Mono', 'JetBrains Mono', 'ui-monospace', 'monospace'],
            },
            // ── Animations personnalisées ──────────────────────────────
            keyframes: {
                'fade-in-down': {
                    '0%': { opacity: '0', transform: 'translateY(-6px) scale(0.98)' },
                    '100%': { opacity: '1', transform: 'translateY(0) scale(1)' },
                },
                'slide-up': {
                    '0%': { opacity: '0', transform: 'translateY(8px)' },
                    '100%': { opacity: '1', transform: 'translateY(0)' },
                },
                'glow-pulse': {
                    '0%, 100%': { boxShadow: '0 0 0 0 rgba(99,102,241,0)' },
                    '50%': { boxShadow: '0 0 14px 4px rgba(99,102,241,0.3)' },
                },
            },
            animation: {
                'fade-in-down': 'fade-in-down 180ms cubic-bezier(0.4,0,0.2,1) both',
                'slide-up': 'slide-up 200ms cubic-bezier(0.4,0,0.2,1) both',
                'glow-pulse': 'glow-pulse 2.5s ease-in-out infinite',
            },
            // ── Ombres custom ─────────────────────────────────────────
            boxShadow: {
                'accent-sm': '0 0 10px rgba(99,102,241,0.25)',
                'accent-md': '0 0 20px rgba(99,102,241,0.35)',
            },
        },
    },
    plugins: [],
};
