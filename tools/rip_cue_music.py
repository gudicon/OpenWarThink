"""Cut the music (Red Book audio tracks) out of a BIN/CUE image of your own game disc and save it as NN.ogg.

usage: python tools/rip_cue_music.py <image.cue> <output folder>
needs: pip install numpy soundfile

Track N spans from its INDEX 01 to the next track's INDEX 01 (the pregap is not stored in the BIN): 2352-byte
sectors of 16-bit little-endian stereo PCM at 44100 Hz. Data tracks are skipped. See tools/README.md for where
OpenWarThink looks for the files.
"""
import os
import re
import sys

if len(sys.argv) != 3:
    sys.exit(__doc__)
try:
    import numpy as np
    import soundfile as sf
except ImportError:
    sys.exit("This script needs numpy and soundfile: pip install numpy soundfile")
cue, out = sys.argv[1], sys.argv[2]
binf = None
tracks = []  # [number, type, start sector]
cur = None
for line in open(cue, encoding="latin-1"):
    m = re.match(r'\s*FILE "(.+)" BINARY', line, re.I)
    if m:
        binf = os.path.join(os.path.dirname(cue), m.group(1))
    m = re.match(r"\s*TRACK (\d+) (\S+)", line, re.I)
    if m:
        cur = [int(m.group(1)), m.group(2).upper(), None]
        tracks.append(cur)
    m = re.match(r"\s*INDEX 01 (\d+):(\d+):(\d+)", line, re.I)
    if m and cur is not None:
        mm, ss, ff = map(int, m.groups())
        cur[2] = (mm * 60 + ss) * 75 + ff

if binf is None:
    sys.exit("No BINARY file in " + cue)
if not os.path.exists(binf):  # the cue may name the bin in a different case
    folder = os.path.dirname(cue) or "."
    cands = [f for f in os.listdir(folder) if f.lower() == os.path.basename(binf).lower()]
    if not cands:
        sys.exit("BIN file not found: " + binf)
    binf = os.path.join(folder, cands[0])
total = os.path.getsize(binf) // 2352
os.makedirs(out, exist_ok=True)
with open(binf, "rb") as f:
    for i, (num, kind, start) in enumerate(tracks):
        if kind != "AUDIO":
            continue
        end = tracks[i + 1][2] if i + 1 < len(tracks) else total
        f.seek(start * 2352)
        pcm = np.frombuffer(f.read((end - start) * 2352), dtype="<i2").reshape(-1, 2)
        path = os.path.join(out, "%02d.ogg" % num)
        # libsndfile's Vorbis encoder overflows the stack on one large write, so feed it in blocks.
        with sf.SoundFile(path, "w", 44100, 2, format="OGG", subtype="VORBIS", compression_level=0.25) as o:
            for k in range(0, len(pcm), 8192):
                o.write(pcm[k:k + 8192])
        print("%02d  sectors %6d-%6d  %6.1f s  -> %d bytes" % (num, start, end, len(pcm) / 44100, os.path.getsize(path)))
