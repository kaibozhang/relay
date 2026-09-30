from PIL import Image, ImageDraw
from pathlib import Path
root=Path(__file__).resolve().parent / "Relay.Desktop" / "Assets"
root.mkdir(exist_ok=True)
im=Image.new("RGBA", (256,256),(0,0,0,0))
d=ImageDraw.Draw(im)
d.rounded_rectangle((5,5,251,251),radius=56,fill="#1c4133")
d.rounded_rectangle((69,48,183,205),radius=12,fill="#f4f3ec")
d.polygon([(155,48),(183,76),(155,76)],fill="#b8c9af")
d.line([(92,89),(143,89)],fill="#a8b79c",width=9)
d.line([(92,113),(154,113)],fill="#a8b79c",width=9)
d.line([(46,141),(108,141),(108,164),(150,164)],fill="#c28b58",width=14,joint="curve")
d.polygon([(143,144),(168,164),(143,184)],fill="#c28b58")
im.save(root / "relay.ico",sizes=[(16,16),(24,24),(32,32),(48,48),(64,64),(128,128),(256,256)])
im.save(root / "relay.png")
print(root / "relay.ico")
