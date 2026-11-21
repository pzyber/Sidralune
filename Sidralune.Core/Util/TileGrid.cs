using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Sidralune.Core.Util
{
    /// <summary>
    /// A large grid of 16-bit values (heights on the Earth or the Moon) kept on disk in square tiles, of which only the
    /// ones that matter are stored ("SLTG1"): the Earth's elevation without the open sea, the Moon's heights only in the
    /// band that can be on its limb. Values are read on demand, a tile at a time, through a short-lived file handle, so
    /// the file is never locked (Settings → Data can update or remove it while Sidralune runs) and memory stays small.
    /// Rows run from north to south, columns from west to east; node (r, c) is at latitude North − r·Cell and longitude
    /// West + c·Cell, and holds Value·Scale.
    /// </summary>
    public sealed class TileGrid
    {
        private const string Magic = "SLTG1";
        public int Cols, Rows, Tile, TilesX, TilesY;
        public double West, North, Cell, Scale;
        /// <summary>The longitudes go all the way round (columns wrap), as for the Moon's 0°–360° grids.</summary>
        public bool Wraps;
        private long[] _offsets; // per tile, −1 when not stored (reads as 0)
        private long _dataStart;
        private readonly string _path;
        private readonly Dictionary<int, short[]> _cache = new Dictionary<int, short[]>();
        private readonly object _sync = new object();
        private FileStream _open;
        private const int CacheTiles = 4096; // 32 MB at most

        private TileGrid(string path) { _path = path; }

        /// <summary>Reads the header and the tile index of a file written by <see cref="Writer"/>.</summary>
        public static TileGrid Open(string path)
        {
            var g = new TileGrid(path);
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var r = new BinaryReader(f))
            {
                if (Encoding.ASCII.GetString(r.ReadBytes(Magic.Length)) != Magic) throw new InvalidDataException(path + " is not a Sidralune tile grid");
                g.Cols = r.ReadInt32(); g.Rows = r.ReadInt32(); g.Tile = r.ReadInt32();
                g.West = r.ReadDouble(); g.North = r.ReadDouble(); g.Cell = r.ReadDouble(); g.Scale = r.ReadDouble();
                g.Wraps = r.ReadBoolean();
                g.TilesX = (g.Cols + g.Tile - 1) / g.Tile; g.TilesY = (g.Rows + g.Tile - 1) / g.Tile;
                var slots = new int[g.TilesX * g.TilesY];
                for (int i = 0; i < slots.Length; i++) slots[i] = r.ReadInt32();
                g._dataStart = f.Position;
                long tileBytes = (long)g.Tile * g.Tile * 2;
                g._offsets = new long[slots.Length];
                for (int i = 0; i < slots.Length; i++) g._offsets[i] = slots[i] < 0 ? -1 : g._dataStart + slots[i] * tileBytes;
            }
            return g;
        }

        /// <summary>How many tiles are stored, of all there are.</summary>
        public int StoredTiles { get { int n = 0; foreach (var o in _offsets) if (o >= 0) n++; return n; } }
        public int AllTiles => _offsets.Length;

        /// <summary>
        /// Runs a batch of reads with the file open once (a limb profile reads thousands of values); the file is closed
        /// again afterwards, so it is not kept locked.
        /// </summary>
        public T Batch<T>(Func<T> f)
        {
            lock (_sync)
            {
                _batchDepth++;
                try { return f(); }
                finally
                {
                    if (--_batchDepth == 0 && _open != null) { _open.Dispose(); _open = null; }
                }
            }
        }

        /// <summary>The value at node (r, c), scaled; 0 where no tile is stored, NaN outside the grid.</summary>
        public double Node(int r, int c)
        {
            if (r < 0 || r >= Rows) return double.NaN;
            if (Wraps) { c %= Cols; if (c < 0) c += Cols; }
            else if (c < 0 || c >= Cols) return double.NaN;
            int tr = r / Tile, tc = c / Tile, key = tr * TilesX + tc;
            lock (_sync)
            {
                if (_offsets[key] < 0) return 0;
                short[] t;
                if (!_cache.TryGetValue(key, out t))
                {
                    if (_cache.Count >= CacheTiles) _cache.Clear();
                    t = ReadTile(key);
                    _cache[key] = t;
                }
                return t[(r - tr * Tile) * Tile + (c - tc * Tile)] * Scale;
            }
        }

        // within a Batch the first tile read opens the file and leaves it open for the rest of the batch
        private int _batchDepth;

        private short[] ReadTile(int key)
        {
            if (_open == null) _open = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            try
            {
                int n = Tile * Tile;
                var bytes = new byte[n * 2];
                _open.Position = _offsets[key];
                int got = 0;
                while (got < bytes.Length) { int k = _open.Read(bytes, got, bytes.Length - got); if (k <= 0) throw new EndOfStreamException(_path); got += k; }
                var t = new short[n];
                Buffer.BlockCopy(bytes, 0, t, 0, bytes.Length);
                return t;
            }
            finally
            {
                if (_batchDepth == 0) { _open.Dispose(); _open = null; }
            }
        }

        /// <summary>Bilinear between the nodes round a place; NaN outside the grid.</summary>
        public double At(double lat, double lon)
        {
            double fr = (North - lat) / Cell, fc = (lon - West) / Cell;
            if (Wraps) { double w = Cols; fc %= w; if (fc < 0) fc += w; }
            if (fr < -0.5 - 1e-9 || fr > Rows - 0.5 + 1e-9) return double.NaN; // pixel-centred grids reach half a cell further (the poles)
            fr = Math.Max(0, Math.Min(Rows - 1, fr));
            if (!Wraps) { if (fc < -1e-9 || fc > Cols - 1 + 1e-9) return double.NaN; fc = Math.Max(0, Math.Min(Cols - 1, fc)); }
            int r0 = (int)Math.Floor(fr), c0 = (int)Math.Floor(fc);
            int r1 = Math.Min(Rows - 1, r0 + 1), c1 = Wraps ? c0 + 1 : Math.Min(Cols - 1, c0 + 1);
            double dr = fr - r0, dc = fc - c0;
            return (Node(r0, c0) * (1 - dc) + Node(r0, c1) * dc) * (1 - dr) + (Node(r1, c0) * (1 - dc) + Node(r1, c1) * dc) * dr;
        }

        /// <summary>
        /// Writes a grid a row at a time (north to south), keeping the tiles that <c>keep</c> asks for: it is given the
        /// tile's row and column and its values, row by row.
        /// </summary>
        public sealed class Writer : IDisposable
        {
            private readonly FileStream _f;
            private readonly BinaryWriter _w;
            private readonly int _cols, _rows, _tile, _tilesX, _tilesY;
            private readonly Func<int, int, short[], bool> _keep;
            private readonly short[][] _band; // the rows of the current band of tiles
            private int _row, _stored;
            private readonly int[] _slots;
            private readonly long _indexAt;

            public Writer(string path, int cols, int rows, double west, double north, double cell, double scale, bool wraps, Func<int, int, short[], bool> keep, int tile = 64)
            {
                _cols = cols; _rows = rows; _tile = tile; _keep = keep;
                _tilesX = (cols + tile - 1) / tile; _tilesY = (rows + tile - 1) / tile;
                _slots = new int[_tilesX * _tilesY];
                for (int i = 0; i < _slots.Length; i++) _slots[i] = -1;
                _band = new short[tile][];
                _f = new FileStream(path, FileMode.Create, FileAccess.ReadWrite);
                _w = new BinaryWriter(_f);
                _w.Write(Encoding.ASCII.GetBytes(Magic));
                _w.Write(cols); _w.Write(rows); _w.Write(tile);
                _w.Write(west); _w.Write(north); _w.Write(cell); _w.Write(scale); _w.Write(wraps);
                _indexAt = _f.Position;
                for (int i = 0; i < _slots.Length; i++) _w.Write(-1); // filled in at the end
            }

            /// <summary>The next row, west to east (cols values).</summary>
            public void AddRow(short[] row)
            {
                if (row.Length != _cols) throw new ArgumentException("A row of " + row.Length + " values, not " + _cols);
                _band[_row % _tile] = (short[])row.Clone();
                _row++;
                if (_row % _tile == 0 || _row == _rows) FlushBand();
            }

            private void FlushBand()
            {
                int tr = (_row - 1) / _tile, rowsHere = _row - tr * _tile;
                var t = new short[_tile * _tile];
                var bytes = new byte[t.Length * 2];
                for (int tc = 0; tc < _tilesX; tc++)
                {
                    Array.Clear(t, 0, t.Length);
                    for (int i = 0; i < rowsHere; i++)
                    {
                        int c0 = tc * _tile, n = Math.Min(_tile, _cols - c0);
                        Array.Copy(_band[i], c0, t, i * _tile, n);
                    }
                    if (!_keep(tr, tc, t)) continue;
                    Buffer.BlockCopy(t, 0, bytes, 0, bytes.Length);
                    _w.Write(bytes);
                    _slots[tr * _tilesX + tc] = _stored++;
                }
                for (int i = 0; i < _tile; i++) _band[i] = null;
            }

            public void Dispose()
            {
                if (_row != _rows) { _w.Dispose(); throw new InvalidDataException("A grid of " + _rows + " rows given " + _row); }
                _f.Position = _indexAt;
                foreach (int s in _slots) _w.Write(s);
                _w.Dispose();
            }
        }
    }
}
