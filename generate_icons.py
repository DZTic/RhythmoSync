
import os
from PIL import Image, ImageDraw, ImageFont, ImageFilter
import math

def create_base_icon(size=1024):
    # Create main image
    img = Image.new('RGBA', (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)
    
    # 1. Background: Rounded Rectangle with Gradient
    # Gradient simulation: Draw many rectangles
    rect_padding = size * 0.05
    rect_radius = size * 0.2
    
    # Gradient colors: Deep Purple (#2d1b4e) to Blue/Cyan (#00d2ff)
    c1 = (45, 27, 78)   # Top-Left
    c2 = (0, 210, 255)  # Bottom-Right
    
    # Draw gradient background (simple linear interpolation)
    # Since we can't easily do complex gradients in PIL, let's do a solid vibrant background
    # or simulate it with concentric circles or linear steps.
    # Let's do a simple radial gradient for depth.
    
    center_x, center_y = size // 2, size // 2
    max_dist = math.sqrt(center_x**2 + center_y**2)
    
    for y in range(size):
        for x in range(size):
            # Check rounded rect mask later, for now fill whole canvas then mask?
            # It's faster to just draw a solidbg.
            pass

    # Easier approach: Solid Dark Blue Background w/ Glow
    bg_color = (13, 17, 23, 255) # Dark GitHub-like
    # Actually, user wanted vibrant.
    # Let's do a linear gradient from top-left to bottom-right
    # create a gradient image
    gradient = Image.new('RGBA', (size, size), color=0)
    g_draw = ImageDraw.Draw(gradient)
    
    for i in range(size):
        r = int(c1[0] + (c2[0] - c1[0]) * i / size)
        g = int(c1[1] + (c2[1] - c1[1]) * i / size)
        b = int(c1[2] + (c2[2] - c1[2]) * i / size)
        g_draw.line([(0, i), (size, i)], fill=(r, g, b, 255))
        
    # Rotate 45 deg to make diagonal? No, vertical is fine for simplicity.
    
    # Apply Rounded Mask
    mask = Image.new('L', (size, size), 0)
    m_draw = ImageDraw.Draw(mask)
    m_draw.rounded_rectangle([(rect_padding, rect_padding), (size - rect_padding, size - rect_padding)], radius=rect_radius, fill=255)
    
    # Composite Gradient into Shape
    final_bg = Image.new('RGBA', (size, size), (0,0,0,0))
    final_bg.paste(gradient, mask=mask)
    
    img = final_bg
    draw = ImageDraw.Draw(img)
    
    # 2. Add Stylized Waveform (White/Cyan)
    # Sine wave
    wave_color = (255, 255, 255, 220)
    line_width = int(size * 0.08)
    
    points = []
    amplitude = size * 0.25
    frequency = 2 # cycles
    
    start_x = size * 0.2
    end_x = size * 0.8
    y_offset = size * 0.5
    
    for x in range(int(start_x), int(end_x)):
        # Normalize x from 0 to 1
        norm_x = (x - start_x) / (end_x - start_x)
        # Apply sine
        y = y_offset + amplitude * math.sin(norm_x * 2 * math.pi * frequency)
        points.append((x, y))
        
    draw.line(points, fill=wave_color, width=line_width, joint='curve')
    
    # 3. Add Film Strip Perforations (Top and Bottom or Sides? Maybe inside the wave?)
    # Let's just do a "Play" triangle overlay to signify video/media
    
    play_color = (0, 210, 255, 180) # Cyan transparent
    play_padding = size * 0.35
    
    # Triangle points
    p1 = (size * 0.4, size * 0.35)
    p2 = (size * 0.4, size * 0.65)
    p3 = (size * 0.7, size * 0.5)
    
    # Draw Play Button with glow?
    # Solid
    draw.polygon([p1, p2, p3], fill=play_color)
    
    # Add a border?
    # draw.rounded_rectangle([(rect_padding, rect_padding), (size - rect_padding, size - rect_padding)], radius=rect_radius, outline=(255,255,255,100), width=int(size*0.02))

    return img

def main():
    base_dir = r"c:\Users\gener\Downloads\rhythmosync-mvp - FullRecréer\src-tauri\icons"
    if not os.path.exists(base_dir):
        print(f"Directory not found: {base_dir}")
        return

    print("Generating base icon...")
    icon = create_base_icon(1024)
    
    # Save standard png
    icon_path = os.path.join(base_dir, "icon.png")
    # Resize to 512x512
    try:
        resample_filter = Image.Resampling.LANCZOS
    except AttributeError:
        resample_filter = Image.LANCZOS

    icon_512 = icon.resize((512, 512), resample_filter)
    icon_512.save(icon_path)
    print(f"Saved {icon_path}")
    
    # List of sizes to generate
    sizes = {
        "32x32.png": 32,
        "128x128.png": 128,
        "128x128@2x.png": 256,
        "Square30x30Logo.png": 30,
        "Square44x44Logo.png": 44,
        "Square71x71Logo.png": 71,
        "Square89x89Logo.png": 89,
        "Square107x107Logo.png": 107,
        "Square142x142Logo.png": 142,
        "Square150x150Logo.png": 150,
        "Square284x284Logo.png": 284,
        "Square310x310Logo.png": 310,
        "StoreLogo.png": 50, # Arbitrary small size often used
    }
    
    for name, size in sizes.items():
        path = os.path.join(base_dir, name)
        resized = icon.resize((size, size), resample_filter)
        resized.save(path)
        print(f"Saved {name}")
        
    # Generate .ico (Windows)
    # ICO can contain multiple sizes. Standard sets: 256, 128, 64, 48, 32, 16
    ico_path = os.path.join(base_dir, "icon.ico")
    icon_512.save(ico_path, format='ICO', sizes=[(256,256), (128,128), (64,64), (48,48), (32,32), (16,16)])
    print(f"Saved {ico_path}")
    
    # Generate .icns (Mac) - Optional/Harder without tools
    # Pillow can save ICNS on some systems if configured, or just save png as fallback
    try:
        icns_path = os.path.join(base_dir, "icon.icns")
        icon_512.save(icns_path, format='ICNS')
        print(f"Saved {icns_path}")
    except Exception as e:
        print(f"Could not save ICNS: {e} (Expected if not on Mac or missing lib)")

if __name__ == "__main__":
    main()
