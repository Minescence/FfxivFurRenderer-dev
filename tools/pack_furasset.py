"""Pack grow_coat_fur.py outputs into one .furasset (format: FurRenderer/FurAsset.cs).

  python pack_furasset.py DIR OUT.furasset      # DIR holds coat.furstrands, coat.furskin, coat.furmesh, coat-fur.json
"""
import json, struct, sys
import numpy as np

SEED, PPS, INF = 20240611, 4, 8  # INF: bone influences per strand / undercoat vertex
SRC_SKIN = np.dtype([("i", "<u2", INF), ("w", "<f4", INF)])
SKIN = np.dtype([("i", "u1", INF), ("w", "<f4", INF)])


def pack(src, out):
    with open(f"{src}/coat.furstrands", "rb") as f:
        magic, ver, n, ppc = struct.unpack("<4sIII", f.read(16))
        pts = np.frombuffer(f.read(), "<f4").reshape(n, ppc, 4)
    assert magic == b"FURS" and ver == 1 and ppc == PPS, (magic, ver, ppc)
    with open(f"{src}/coat.furskin", "rb") as f:
        assert struct.unpack("<I", f.read(4))[0] == n
        raw = np.frombuffer(f.read(), SRC_SKIN)
    assert len(raw) == n
    with open(f"{src}/coat.furmesh", "rb") as f:
        nv, nt = struct.unpack("<II", f.read(8))
        verts = np.frombuffer(f.read(nv * 24), "<f4").reshape(nv, 6)
        mraw = np.frombuffer(f.read(nv * SRC_SKIN.itemsize), SRC_SKIN)
        tris = np.frombuffer(f.read(nt * 12), "<u4")
        assert len(tris) == nt * 3 and not f.read(), "furmesh size"
    names = json.load(open(f"{src}/coat-fur.json", encoding="utf-8"))["boneNames"]
    idx, w = raw["i"].astype(np.int64), raw["w"].astype(np.float32)
    midx, mw = mraw["i"].astype(np.int64), mraw["w"].astype(np.float32)
    mw = mw / mw.sum(axis=1, keepdims=True)  # mdl weights are bytes / 255
    used = np.unique(np.concatenate([idx[w > 0], midx[mw > 0]]))  # unused slots are (index 0, weight 0) and must not pull bone 0 in
    remap = np.zeros(int(max(idx.max(), midx.max())) + 1, np.uint8); remap[used] = np.arange(len(used))
    idx = np.where(w > 0, remap[idx], 0).astype(np.uint8)
    midx = np.where(mw > 0, remap[midx], 0).astype(np.uint8)
    bones = [names[i].encode("utf-8") for i in used]
    assert len(bones) < 256 and all(len(b) < 256 for b in bones)
    assert np.isfinite(pts).all() and np.allclose(w.sum(axis=1), 1, atol=1e-3) and (idx < max(len(bones), 1)).all()
    assert np.isfinite(verts).all() and np.allclose(mw.sum(axis=1), 1, atol=1e-3) and (tris < nv).all()

    order = np.random.default_rng(SEED).permutation(n)
    pts, idx, w = pts[order], idx[order], w[order]
    head = b"FURA" + struct.pack("<IIII", 3, n, PPS, len(bones)) + b"".join(bytes([len(b)]) + b for b in bones)
    head += b"\0" * (-len(head) % 4)
    skin = np.empty(n, SKIN); skin["i"] = idx; skin["w"] = w
    mskin = np.empty(nv, SKIN); mskin["i"] = midx; mskin["w"] = mw
    with open(out, "wb") as f:
        f.write(head); f.write(np.ascontiguousarray(pts).tobytes()); f.write(skin.tobytes())
        f.write(struct.pack("<II", nv, len(tris))); f.write(verts.tobytes()); f.write(mskin.tobytes()); f.write(tris.tobytes())
    return n, len(bones), len(head), nv, len(tris)


def check(path, n, nb, head_len, nv, ni):
    """Re-parse the way FurAsset.Load does."""
    b = open(path, "rb").read()
    assert b[:4] == b"FURA" and struct.unpack_from("<IIII", b, 4) == (3, n, PPS, nb)
    off = 20
    for _ in range(nb):
        off += 1 + b[off]
    off = (off + 3) & ~3
    assert off == head_len
    skin = np.frombuffer(b, SKIN, n, off + n * PPS * 16)
    assert (skin["i"] < nb).all() and np.allclose(skin["w"].sum(axis=1), 1, atol=1e-3)
    assert np.isfinite(np.frombuffer(b, "<f4", n * PPS * 4, off)).all()
    off += n * PPS * 16 + n * SKIN.itemsize
    assert struct.unpack_from("<II", b, off) == (nv, ni)
    off += 8
    mskin = np.frombuffer(b, SKIN, nv, off + nv * 24)
    assert (mskin["i"] < nb).all() and (np.frombuffer(b, "<u4", ni, off + nv * (24 + SKIN.itemsize)) < nv).all()
    assert len(b) == off + nv * (24 + SKIN.itemsize) + ni * 4, "size mismatch"


if __name__ == "__main__":
    r = pack(sys.argv[1], sys.argv[2])
    check(sys.argv[2], *r)
    print(f"{sys.argv[2]}: strands={r[0]} bones={r[1]} undercoat vertices={r[3]} triangles={r[4] // 3}")
