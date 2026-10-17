"""Generate the sample GIFs shipped in k580rgb/media_samples (keyboard-sized: 180x52).

Usage: python make_samples.py <out_dir>
"""
import math
import os
import random
import sys

from PIL import Image, ImageDraw

W, H = 180, 52


def save(frames, path, ms):
    frames[0].save(path, save_all=True, append_images=frames[1:], duration=ms, loop=0, optimize=False)


def pacman(n=72):
    frames = []
    for f in range(n):
        im = Image.new('RGB', (W, H), (0, 0, 20))
        d = ImageDraw.Draw(im)
        x = -20 + (W + 60) * f / n
        for dx in range(10, W, 16):                 # dots not eaten yet
            if dx > x + 8:
                d.ellipse((dx - 3, 23, dx + 3, 29), fill=(255, 200, 150))
        mouth = 35 * abs(math.sin(f * 0.6))
        d.pieslice((x - 16, 10, x + 16, 42), mouth, 360 - mouth, fill=(255, 230, 0))
        gx = x - 46                                 # ghost
        d.chord((gx - 15, 10, gx + 15, 40), 180, 360, fill=(255, 40, 40))
        d.rectangle((gx - 15, 25, gx + 15, 40), fill=(255, 40, 40))
        for ex in (-6, 6):
            d.ellipse((gx + ex - 4, 17, gx + ex + 4, 25), fill=(255, 255, 255))
            d.ellipse((gx + ex - 1, 19, gx + ex + 3, 23), fill=(0, 60, 255))
        frames.append(im)
    return frames


def fireworks(n=90):
    rnd = random.Random(5)
    bursts = [(rnd.randrange(20, W - 20), rnd.randrange(10, 35), rnd.randrange(0, n),
               tuple(rnd.choice([255, 120, 0]) for _ in range(3)) or (255, 255, 255)) for _ in range(9)]
    bursts = [(x, y, t, c if sum(c) > 200 else (255, 220, 120)) for x, y, t, c in bursts]
    frames = []
    for f in range(n):
        im = Image.new('RGB', (W, H), (0, 0, 8))
        d = ImageDraw.Draw(im)
        for x, y, t0, c in bursts:
            age = (f - t0) % n
            if age > 30:
                continue
            fade = 1 - age / 30
            col = tuple(int(v * fade) for v in c)
            r = age * 1.6
            for k in range(14):
                a = k / 14 * 2 * math.pi
                px, py = x + r * math.cos(a), y + r * math.sin(a) * 0.8 + age * age * 0.02
                d.ellipse((px - 3, py - 3, px + 3, py + 3), fill=col)
        frames.append(im)
    return frames


def bouncing(n=120):
    balls = [(20, 10, 1.9, 1.3, (255, 0, 120)), (90, 30, -2.3, 0.9, (0, 220, 255)),
             (150, 20, 1.5, -1.6, (255, 200, 0)), (60, 40, -1.2, -1.1, (120, 255, 0))]
    frames = []
    for f in range(n):
        im = Image.new('RGB', (W, H), (0, 0, 0))
        d = ImageDraw.Draw(im)
        for x, y, vx, vy, c in balls:
            px = abs((x + vx * f * 2) % (2 * (W - 16)) - (W - 16)) + 8   # triangle wave = bounce
            py = abs((y + vy * f * 2) % (2 * (H - 16)) - (H - 16)) + 8
            d.ellipse((px - 8, py - 8, px + 8, py + 8), fill=c)
        frames.append(im)
    return frames


if __name__ == '__main__':
    out = sys.argv[1]
    os.makedirs(out, exist_ok=True)
    save(pacman(), os.path.join(out, 'Pac-Man chase.gif'), 50)
    save(fireworks(), os.path.join(out, 'Fireworks.gif'), 40)
    save(bouncing(), os.path.join(out, 'Bouncing balls.gif'), 33)
    print(sorted(os.listdir(out)))
