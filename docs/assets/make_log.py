"""Makes log-round.webp (the hero) and log-round-mark.webp (the section marker) for the Stability Fork site: the end
grain of a fresh-cut log, seen from the front. Procedural
(numpy and Pillow, fixed seed); no source images and no generative model. Run from this folder: python make_log.py"""
import numpy as np
from PIL import Image, ImageFilter

def draw(name, W, H, cx, cy, rx, ry, ring_a, ring_b, bark, n_rays, blur, quality):
    rng = np.random.default_rng(1114)

    y, x = np.mgrid[0:H, 0:W].astype(float)
    dx, dy = (x - cx) / rx, (y - cy) / ry
    r = np.sqrt(dx * dx + dy * dy)             # 0 at the pith, 1 at the bark line
    ang = np.arctan2(dy, dx)

    # growth rings: wobble with the angle so they're never perfect circles, closer together toward the bark
    wobble = sum(rng.uniform(.002, .007) * np.sin(k * ang + rng.uniform(0, 6.3)) for k in range(2, 7)) + .012 * np.sin(ang + .8)
    rr = r + wobble
    phase = ring_a * rr + ring_b * rr ** 2
    rings = (np.sin(2 * np.pi * phase / 6.0) * .5 + .5) ** 6
    late = (np.sin(2 * np.pi * phase / 6.0 + .6) * .5 + .5) ** 16

    light = np.array([240, 212, 156], float)
    mid = np.array([218, 176, 108], float)
    dark = np.array([170, 118, 60], float)
    t = (rr.clip(0, 1) ** 1.5)[..., None]
    wood = light * (1 - t) + mid * t
    wood = wood - rings[..., None] * (wood - dark) * .42 - late[..., None] * 22

    # medullary rays and a couple of drying checks running out from the pith
    rays = np.zeros((H, W))
    for a0 in rng.uniform(-np.pi, np.pi, n_rays):
        rays += np.exp(-((np.angle(np.exp(1j * (ang - a0)))) / .004) ** 2) * (r < .95)
    wood -= (rays.clip(0, 1) * 18)[..., None]
    for a0, ln in ((-.9, .55), (2.2, .38)):
        crack = np.exp(-((np.angle(np.exp(1j * (ang - a0)))) / .008) ** 2) * (r < ln)
        wood -= (crack * 90)[..., None]

    # a fine grain of pores and saw marks, then the bark ring with its rough outer edge
    wood += rng.normal(0, 4, (H, W))[..., None]
    saw = np.sin((x * .9 + y * .35) / 3.2) * 3
    wood += saw[..., None]
    edge = 1 + sum(rng.uniform(.002, .006) * np.sin(k * ang + rng.uniform(0, 6.3)) for k in range(5, 30, 4))
    bark_in, bark_out = bark, 1.06 * edge
    bark_col = np.array([74, 51, 34], float) + rng.normal(0, 10, (H, W))[..., None] * np.array([1, .8, .6])
    cambium = np.exp(-((r - bark_in) / .01) ** 2)[..., None]
    img = np.where((r > bark_in)[..., None], bark_col, wood)
    img = img * (1 - cambium * .35)
    # the whole face a touch lighter at the top left, as if lit from there
    img *= (1.06 - .12 * ((dx + dy) * .5 + .5).clip(0, 1))[..., None]

    alpha = np.clip((bark_out - r) * 60, 0, 1) * 255
    out = np.dstack([img.clip(0, 255), alpha]).astype(np.uint8)
    Image.fromarray(out, "RGBA").filter(ImageFilter.GaussianBlur(blur)).save(name, quality=quality, method=6)
    print("made", name)

# the hero: drawn at twice the size the page shows it; a slightly flattened ellipse, as a log seen a little from above
draw("log-round.webp", 1040, 760, 520, 400, 352, 268, 118, 40, .955, 70, .35, 84)
# the section marker, 44x38 on the page (drawn at 2x): a few wide rings and a thick bark rim, so it still reads small
draw("log-round-mark.webp", 88, 76, 44, 39, 36.5, 30.5, 30, 6, .86, 10, 0, 92)
