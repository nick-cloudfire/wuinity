"""A minimal GeoTIFF writer in the standard library: one band, uncompressed strips, a projected CRS given by its
EPSG code. Enough for the synthetic inputs (DEM, fuel, masks), so the generator runs wherever Python 3 does -
including a Windows machine whose GDAL is QGIS's command-line tools without Python bindings.

The sample type is chosen on purpose: ELMFIRE reads its fuel raster as 16-bit integers, and a fuel model raster
typed Int32 is exactly what the verification's D1 check feeds it.
"""

import struct

_TYPES = {
    # name: (struct code, bytes per sample, TIFF SampleFormat: 1 uint, 2 int, 3 float)
    "uint8": ("B", 1, 1),
    "int16": ("h", 2, 2),
    "int32": ("i", 4, 2),
    "float32": ("f", 4, 3),
}


def write_geotiff(path, rows, x_west, y_north, cell_size, epsg, dtype="float32", nodata=None):
    """Writes `rows` (a list of rows, north row first, each a list of numbers) as a GeoTIFF whose upper-left
    corner is (x_west, y_north) in EPSG:`epsg`, with square cells of `cell_size`."""
    code, size, sample_format = _TYPES[dtype]
    nrows = len(rows)
    ncols = len(rows[0])
    row_bytes = ncols * size
    rows_per_strip = max(1, min(nrows, 65536 // max(row_bytes, 1)))
    strips = []
    for start in range(0, nrows, rows_per_strip):
        chunk = bytearray()
        for r in rows[start:start + rows_per_strip]:
            if len(r) != ncols:
                raise ValueError("ragged raster")
            if dtype == "float32":
                chunk += struct.pack("<%d%s" % (ncols, code), *[float(v) for v in r])
            else:
                chunk += struct.pack("<%d%s" % (ncols, code), *[int(round(v)) for v in r])
        strips.append(bytes(chunk))

    geokeys = [1, 1, 0, 4,          # GeoKeyDirectory header: version 1.1.0, 4 keys
               1024, 0, 1, 1,       # GTModelTypeGeoKey = projected
               1025, 0, 1, 1,       # GTRasterTypeGeoKey = PixelIsArea
               3072, 0, 1, int(epsg),  # ProjectedCSTypeGeoKey
               3076, 0, 1, 9001]    # ProjLinearUnitsGeoKey = metre

    entries = []  # (tag, type, count, value-bytes)

    def short(tag, values):
        entries.append((tag, 3, len(values), struct.pack("<%dH" % len(values), *values)))

    def long_(tag, values):
        entries.append((tag, 4, len(values), struct.pack("<%dI" % len(values), *values)))

    def double(tag, values):
        entries.append((tag, 12, len(values), struct.pack("<%dd" % len(values), *values)))

    def ascii(tag, text):
        data = text.encode("ascii") + b"\0"
        entries.append((tag, 2, len(data), data))

    short(256, [ncols]) if ncols < 65536 else long_(256, [ncols])
    short(257, [nrows]) if nrows < 65536 else long_(257, [nrows])
    short(258, [size * 8])               # BitsPerSample
    short(259, [1])                      # Compression: none
    short(262, [1])                      # Photometric: min-is-black
    long_(273, [0] * len(strips))        # StripOffsets, patched below
    short(277, [1])                      # SamplesPerPixel
    long_(278, [rows_per_strip])         # RowsPerStrip
    long_(279, [len(s) for s in strips])  # StripByteCounts
    short(284, [1])                      # PlanarConfiguration: contiguous
    short(339, [sample_format])          # SampleFormat
    double(33550, [float(cell_size), float(cell_size), 0.0])                    # ModelPixelScale
    double(33922, [0.0, 0.0, 0.0, float(x_west), float(y_north), 0.0])        # ModelTiepoint
    short(34735, geokeys)                # GeoKeyDirectory
    if nodata is not None:
        ascii(42113, repr(float(nodata)) if dtype == "float32" else str(int(nodata)))  # GDAL_NODATA
    entries.sort(key=lambda e: e[0])

    # Layout: header (8) | IFD | out-of-line values | strips.
    ifd_offset = 8
    ifd_size = 2 + 12 * len(entries) + 4
    extra_offset = ifd_offset + ifd_size
    extra = bytearray()
    placed = []
    for tag, typ, count, data in entries:
        if len(data) <= 4:
            placed.append((tag, typ, count, data.ljust(4, b"\0"), None))
        else:
            if (extra_offset + len(extra)) % 2:
                extra += b"\0"
            placed.append((tag, typ, count, None, extra_offset + len(extra)))
            extra += data
    strip_start = extra_offset + len(extra)
    offsets = []
    pos = strip_start
    for s in strips:
        offsets.append(pos)
        pos += len(s)

    # Patch the strip offsets (tag 273) now that they are known.
    out_entries = []
    for tag, typ, count, inline, offset in placed:
        if tag == 273:
            data = struct.pack("<%dI" % count, *offsets)
            if len(data) <= 4:
                inline, offset = data.ljust(4, b"\0"), None
            else:
                extra[offset - extra_offset:offset - extra_offset + len(data)] = data
        out_entries.append((tag, typ, count, inline, offset))

    with open(path, "wb") as f:
        f.write(b"II*\0" + struct.pack("<I", ifd_offset))
        f.write(struct.pack("<H", len(out_entries)))
        for tag, typ, count, inline, offset in out_entries:
            f.write(struct.pack("<HHI", tag, typ, count))
            f.write(inline if inline is not None else struct.pack("<I", offset))
        f.write(struct.pack("<I", 0))
        f.write(bytes(extra))
        for s in strips:
            f.write(s)


def sample_type(path):
    """The sample type of a (classic, little- or big-endian) TIFF's first image - "int16", "int32", "float32", ... -
    read from its BitsPerSample and SampleFormat tags; None for a BigTIFF or a file that is not a TIFF."""
    with open(path, "rb") as f:
        head = f.read(8)
        if head[:4] == b"II*\0":
            e = "<"
        elif head[:4] == b"MM\0*":
            e = ">"
        else:
            return None
        f.seek(struct.unpack(e + "I", head[4:8])[0])
        count = struct.unpack(e + "H", f.read(2))[0]
        bits, fmt = None, 1  # SampleFormat defaults to unsigned integer
        for _ in range(count):
            tag, typ, n, value = struct.unpack(e + "HHI4s", f.read(12))
            v = struct.unpack(e + "H", value[:2])[0] if typ == 3 else struct.unpack(e + "I", value)[0]
            if tag == 258:
                bits = v
            elif tag == 339:
                fmt = v
    if bits is None:
        return None
    return {1: "uint", 2: "int", 3: "float"}.get(fmt, "?") + str(bits)
