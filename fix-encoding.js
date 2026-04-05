const fs = require('fs');
const path = require('path');

const replacements = {
    'Ã©': 'é',
    'Ã¨': 'è',
    'Ãª': 'ê',
    'Ã«': 'ë',
    'Ã ': 'à',
    'Ã¢': 'â',
    'Ã§': 'ç',
    'Ã®': 'î',
    'Ã¯': 'ï',
    'Ã´': 'ô',
    'Ã¶': 'ö',
    'Ã¹': 'ù',
    'Ã»': 'û',
    'Ã¼': 'ü',
    'Ã‰': 'É',
    'Ãˆ': 'È',
    'ÃŠ': 'Ê',
    'Ã€': 'À',
    'Ã‚': 'Â',
    'Ã‡': 'Ç',
    'ÃŽ': 'Î',
    'Ã”': 'Ô',
    'Ã›': 'Û',
    'Å“': 'œ',
    'Ã¦': 'æ',
    'â€™': '’',
    'â€œ': '“',
    'â€ ': '”',
    'â€°': '‰',
    'â€”': '—',
    'â€“': '–',
    'â€¦': '…',
    'Â°': '°',
    'Â«': '«',
    'Â»': '»'
};

function fixMojibake(file) {
    const ext = path.extname(file);
    if (!['.ts', '.tsx', '.md'].includes(ext)) return;

    let content = fs.readFileSync(file, 'utf8');
    let hasChanges = false;

    // Check if there's any broken character
    if (Object.keys(replacements).some(k => content.includes(k))) {
        for (const [bad, good] of Object.entries(replacements)) {
            if (content.includes(bad)) {
                content = content.split(bad).join(good);
                hasChanges = true;
            }
        }
    }

    if (hasChanges) {
        fs.writeFileSync(file, content, 'utf8');
        console.log('Fixed:', file);
    }
}

function walk(dir) {
    const files = fs.readdirSync(dir);
    for (const f of files) {
        if (['node_modules', '.git', 'dist', 'src-tauri'].includes(f)) continue;
        const p = path.join(dir, f);
        if (fs.statSync(p).isDirectory()) {
            walk(p);
        } else {
            fixMojibake(p);
        }
    }
}

walk('.');
