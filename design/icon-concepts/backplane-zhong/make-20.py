from pathlib import Path
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent
TEAL = (20, 113, 127, 255)
IVORY = (246, 243, 234, 255)

im = Image.new("RGBA", (20, 20), (0, 0, 0, 0))
d = ImageDraw.Draw(im)
d.rounded_rectangle((0, 0, 19, 19), radius=3, fill=TEAL)
# 中：2 px strokes on a 20 px grid.
d.rectangle((9, 2, 10, 17), fill=IVORY)       # vertical stem
d.rectangle((4, 4, 15, 5), fill=IVORY)        # top bar
d.rectangle((4, 4, 5, 15), fill=IVORY)        # left side
d.rectangle((14, 4, 15, 15), fill=IVORY)      # right side
d.rectangle((4, 14, 15, 15), fill=IVORY)      # bottom bar
im.save(ROOT / "zhong-20-crisp.png")
im.resize((400, 400), Image.Resampling.NEAREST).save(ROOT.parents[1] / "verification" / "icon20-nearest.png")
print("written")
