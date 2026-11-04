"""Pack grow_coat_fur.py outputs into one .furasset (format: FurRenderer/FurAsset.cs).

  python pack_furasset.py DIR OUT.furasset      # DIR holds coat.furstrands, coat.furskin, coat-fur.json
"""
import json, struct, sys
import numpy as np

SEED, PPS = 20240611, 4


def pack(src, out):
    with open(f"{src}/coat.furstrands", "rb") as f:
        magic, ver, n, ppc = struct.unpack("<4sIII", f.read(16))
        pts = np.frombuffer(f.read(), "<f4").reshape(n, ppc, 4)
    assert magic == b"FURS" and ver == 1 and ppc == PPS, (magic, ver, ppc)
    with open(f"{src}/coat.furskin", "rb") as f:
        assert struct.unpack("<I", f.read(4))[0] == n
        raw = np.frombuffer(f.read(), np.dtype([("i", "<u2", 4), ("w", "<f4", 4)]))
    assert len(raw) == n
    names = json.load(open(f"{src}/coat-fur.json", encoding="utf-8"))["boneNames"]
    idx, w = raw["i"].astype(np.int64), raw["w"].astype(np.float32)
    used = np.unique(idx[w > 0])  # unused slots are (index 0, weight 0) and must not pull bone 0 in
    remap = np.zeros(int(idx.max()) + 1, np.uint8); remap[used] = np.arange(len(used))
    idx = np.where(w > 0, remap[idx], 0).astype(np.uint8)
    bones = [names[i].encode("utf-8") for i in used]
    assert len(bones) < 256 and all(len(b) < 256 for b in bones)
    assert np.isfinite(pts).all() and np.allclose(w.sum(axis=1), 1, atol=1e-3) and (idx < max(len(bones), 1)).all()

    order = np.random.default_rng(SEED).permutation(n)
    pts, idx, w = pts[order], idx[order], w[order]
    head = b"FURA" + struct.pack("<IIII", 1, n, PPS, len(bones)) + b"".join(bytes([len(b)]) + b for b in bones)
    head += b"\0" * (-len(head) % 4)
    skin = np.empty(n, np.dtype([("i", "u1", 4), ("w", "<f4", 4)])); skin["i"] = idx; skin["w"] = w
    with open(out, "wb") as f:
        f.write(head); f.write(np.ascontiguousarray(pts).tobytes()); f.write(skin.tobytes())
    return n, len(bones), len(head)


def check(path, n, nb, head_len):
    """Re-parse the way FurAsset.Load does."""
    b = open(path, "rb").read()
    assert b[:4] == b"FURA" and struct.unpack_from("<IIII", b, 4) == (1, n, PPS, nb)
    off = 20
    for _ in range(nb):
        off += 1 + b[off]
    off = (off + 3) & ~3
    assert off == head_len and len(b) == off + n * PPS * 16 + n * 20, "size mismatch"
    skin = np.frombuffer(b, np.dtype([("i", "u1", 4), ("w", "<f4", 4)]), n, off + n * PPS * 16)
    assert (skin["i"] < nb).all() and np.allclose(skin["w"].sum(axis=1), 1, atol=1e-3)
    assert np.isfinite(np.frombuffer(b, "<f4", n * PPS * 4, off)).all()


if __name__ == "__main__":
    n, nb, hl = pack(sys.argv[1], sys.argv[2])
    check(sys.argv[2], n, nb, hl)
    print(f"{sys.argv[2]}: strands={n} bones={nb}")
