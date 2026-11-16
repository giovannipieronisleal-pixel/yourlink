using System.Text;

namespace ClothingStore.Shared.Helpers;

/// <summary>
/// Gerador puro em C# (sem dependências pagas/externas) de QR Code compatível com ISO/IEC 18004
/// no modo Byte com correção de erro Reed-Solomon (níveis M e L), legível por câmeras Android e iOS.
/// </summary>
public static class QrCodeGenerator
{
    private readonly record struct QrSpec(
        int Version,
        bool UseLevelM,
        int EccPerBlock,
        int Group1Blocks,
        int Group1DataCodewords,
        int Group2Blocks,
        int Group2DataCodewords)
    {
        public int TotalDataCodewords => Group1Blocks * Group1DataCodewords + Group2Blocks * Group2DataCodewords;
        public int MatrixSize => 17 + Version * 4;
    }

    // Especificações ISO/IEC 18004 (versões 1..10). Priorizamos Level M (15% ECC) e recorremos a Level L (7%) para URLs extensas.
    private static readonly QrSpec[] Specs =
    [
        new(1, true, 10, 1, 16, 0, 0),
        new(2, true, 16, 1, 28, 0, 0),
        new(3, true, 26, 1, 44, 0, 0),
        new(4, true, 18, 2, 32, 0, 0),
        new(5, true, 24, 2, 43, 0, 0),
        new(6, true, 16, 4, 27, 0, 0),
        new(7, true, 18, 4, 31, 0, 0),
        new(8, true, 22, 2, 38, 2, 39),
        new(9, true, 22, 3, 36, 2, 37),
        new(9, false, 30, 2, 116, 0, 0),
        new(10, false, 18, 2, 68, 2, 69)
    ];

    private static readonly int[][] AlignmentCenters =
    [
        [],                 // V1
        [6, 18],            // V2
        [6, 22],            // V3
        [6, 26],            // V4
        [6, 30],            // V5
        [6, 34],            // V6
        [6, 22, 38],        // V7
        [6, 24, 42],        // V8
        [6, 26, 46],        // V9
        [6, 28, 50]         // V10
    ];

    private static readonly byte[] GfExp = new byte[512];
    private static readonly byte[] GfLog = new byte[256];

    static QrCodeGenerator()
    {
        var val = 1;
        for (var i = 0; i < 255; i++)
        {
            GfExp[i] = (byte)val;
            GfExp[i + 255] = (byte)val;
            GfLog[val] = (byte)i;
            val <<= 1;
            if ((val & 0x100) != 0)
                val ^= 0x11D;
        }
        GfExp[510] = GfExp[0];
        GfExp[511] = GfExp[1];
    }

    public static bool[,] EncodeMatrix(string? text)
    {
        var payload = Encoding.UTF8.GetBytes(string.IsNullOrWhiteSpace(text) ? "https://youlink-loja.onrender.com" : text.Trim());
        var spec = SelectSpec(payload.Length);
        if (payload.Length > MaxPayloadBytes(spec))
        {
            payload = payload[..MaxPayloadBytes(spec)];
        }

        var dataCodewords = EncodeDataCodewords(payload, spec);
        var allCodewords = BuildInterleavedCodewords(dataCodewords, spec);

        var n = spec.MatrixSize;
        var modules = new bool[n, n];
        var reserved = new bool[n, n];

        PlaceFunctionPatterns(modules, reserved, spec);
        PlaceDataBits(modules, reserved, allCodewords, n);

        var bestMask = 0;
        var bestPenalty = int.MaxValue;
        bool[,]? bestMatrix = null;

        for (var mask = 0; mask < 8; mask++)
        {
            var candidate = (bool[,])modules.Clone();
            ApplyMask(candidate, reserved, mask, n);
            PlaceFormatAndVersionInfo(candidate, spec, mask, n);
            var penalty = EvaluatePenalty(candidate, n);
            if (penalty < bestPenalty)
            {
                bestPenalty = penalty;
                bestMask = mask;
                bestMatrix = candidate;
            }
        }

        return bestMatrix!;
    }

    public static string GenerateSvgMarkup(string? content, int quietZoneModules = 4)
    {
        var matrix = EncodeMatrix(content);
        var size = matrix.GetLength(0);
        var total = size + quietZoneModules * 2;

        var path = new StringBuilder();
        for (var r = 0; r < size; r++)
        {
            var c = 0;
            while (c < size)
            {
                if (!matrix[r, c])
                {
                    c++;
                    continue;
                }

                var start = c;
                while (c < size && matrix[r, c])
                    c++;

                var width = c - start;
                path.Append($"M{start + quietZoneModules},{r + quietZoneModules}h{width}v1h-{width}z");
            }
        }

        return $"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 {total} {total}' width='180' height='180' shape-rendering='crispEdges'>" +
               $"<rect width='{total}' height='{total}' fill='#FFFFFF'/>" +
               $"<path d='{path}' fill='#18181B'/>" +
               "</svg>";
    }

    public static string GenerateSvgDataUri(string? content)
    {
        var svg = GenerateSvgMarkup(content);
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));
        return $"data:image/svg+xml;base64,{base64}";
    }

    private static int MaxPayloadBytes(QrSpec spec)
    {
        var countBits = spec.Version <= 9 ? 8 : 16;
        return (spec.TotalDataCodewords * 8 - 4 - countBits) / 8;
    }

    private static QrSpec SelectSpec(int byteLength)
    {
        foreach (var spec in Specs)
        {
            if (byteLength <= MaxPayloadBytes(spec))
                return spec;
        }

        return Specs[^1];
    }

    private static byte[] EncodeDataCodewords(byte[] payload, QrSpec spec)
    {
        var totalBits = spec.TotalDataCodewords * 8;
        var bits = new List<int>(totalBits);

        static void AppendBits(List<int> target, int value, int bitCount)
        {
            for (var i = bitCount - 1; i >= 0; i--)
                target.Add((value >> i) & 1);
        }

        // Mode indicator: Byte = 0100
        AppendBits(bits, 0b0100, 4);
        AppendBits(bits, payload.Length, spec.Version <= 9 ? 8 : 16);

        foreach (var b in payload)
            AppendBits(bits, b, 8);

        var remaining = totalBits - bits.Count;
        var terminator = Math.Min(4, Math.Max(0, remaining));
        for (var i = 0; i < terminator; i++)
            bits.Add(0);

        while (bits.Count % 8 != 0)
            bits.Add(0);

        var padBytes = new byte[] { 0xEC, 0x11 };
        var padIdx = 0;
        while (bits.Count < totalBits)
        {
            AppendBits(bits, padBytes[padIdx % 2], 8);
            padIdx++;
        }

        var result = new byte[spec.TotalDataCodewords];
        for (var i = 0; i < result.Length; i++)
        {
            var val = 0;
            for (var b = 0; b < 8; b++)
                val = (val << 1) | bits[i * 8 + b];
            result[i] = (byte)val;
        }

        return result;
    }

    private static byte[] BuildInterleavedCodewords(byte[] dataCodewords, QrSpec spec)
    {
        var totalBlocks = spec.Group1Blocks + spec.Group2Blocks;
        var dataBlocks = new byte[totalBlocks][];
        var eccBlocks = new byte[totalBlocks][];
        var generator = BuildGeneratorPolynomial(spec.EccPerBlock);

        var offset = 0;
        for (var i = 0; i < totalBlocks; i++)
        {
            var blockLen = i < spec.Group1Blocks ? spec.Group1DataCodewords : spec.Group2DataCodewords;
            var block = new byte[blockLen];
            Array.Copy(dataCodewords, offset, block, 0, blockLen);
            offset += blockLen;

            dataBlocks[i] = block;
            eccBlocks[i] = ComputeReedSolomonEcc(block, generator, spec.EccPerBlock);
        }

        var maxDataLen = Math.Max(spec.Group1DataCodewords, spec.Group2DataCodewords);
        var output = new List<byte>(spec.TotalDataCodewords + totalBlocks * spec.EccPerBlock);

        for (var col = 0; col < maxDataLen; col++)
        {
            for (var b = 0; b < totalBlocks; b++)
            {
                if (col < dataBlocks[b].Length)
                    output.Add(dataBlocks[b][col]);
            }
        }

        for (var col = 0; col < spec.EccPerBlock; col++)
        {
            for (var b = 0; b < totalBlocks; b++)
                output.Add(eccBlocks[b][col]);
        }

        return output.ToArray();
    }

    private static byte[] BuildGeneratorPolynomial(int degree)
    {
        var poly = new byte[] { 1 };
        for (var i = 0; i < degree; i++)
        {
            var factor = new byte[] { 1, GfExp[i] };
            poly = MultiplyPolynomials(poly, factor);
        }
        return poly;
    }

    private static byte[] MultiplyPolynomials(byte[] a, byte[] b)
    {
        var res = new byte[a.Length + b.Length - 1];
        for (var i = 0; i < a.Length; i++)
        {
            for (var j = 0; j < b.Length; j++)
            {
                res[i + j] ^= GfMultiply(a[i], b[j]);
            }
        }
        return res;
    }

    private static byte GfMultiply(byte a, byte b) =>
        (a == 0 || b == 0) ? (byte)0 : GfExp[GfLog[a] + GfLog[b]];

    private static byte[] ComputeReedSolomonEcc(byte[] data, byte[] generator, int eccLen)
    {
        var buffer = new byte[data.Length + eccLen];
        Array.Copy(data, buffer, data.Length);

        for (var i = 0; i < data.Length; i++)
        {
            var factor = buffer[i];
            if (factor == 0)
                continue;

            for (var j = 0; j < generator.Length; j++)
                buffer[i + j] ^= GfMultiply(generator[j], factor);
        }

        var ecc = new byte[eccLen];
        Array.Copy(buffer, data.Length, ecc, 0, eccLen);
        return ecc;
    }

    private static void PlaceFunctionPatterns(bool[,] m, bool[,] res, QrSpec spec)
    {
        var n = spec.MatrixSize;
        PlaceFinderPattern(m, res, 0, 0, n);
        PlaceFinderPattern(m, res, 0, n - 7, n);
        PlaceFinderPattern(m, res, n - 7, 0, n);

        // Alignment patterns (V2+)
        var centers = AlignmentCenters[spec.Version - 1];
        for (var i = 0; i < centers.Length; i++)
        {
            for (var j = 0; j < centers.Length; j++)
            {
                var r = centers[i];
                var c = centers[j];
                if (res[r, c])
                    continue;

                PlaceAlignmentPattern(m, res, r, c);
            }
        }

        // Timing patterns
        for (var i = 8; i < n - 8; i++)
        {
            var isDark = i % 2 == 0;
            if (!res[6, i])
            {
                m[6, i] = isDark;
                res[6, i] = true;
            }
            if (!res[i, 6])
            {
                m[i, 6] = isDark;
                res[i, 6] = true;
            }
        }

        // Dark module
        m[4 * spec.Version + 9, 8] = true;
        res[4 * spec.Version + 9, 8] = true;

        // Reserve format info zones
        for (var i = 0; i < 9; i++)
        {
            if (i != 6)
            {
                res[8, i] = true;
                res[i, 8] = true;
            }
        }
        for (var i = 0; i < 8; i++)
        {
            res[8, n - 1 - i] = true;
            if (i < 7)
                res[n - 1 - i, 8] = true;
        }

        // Reserve version info zones (V7+)
        if (spec.Version >= 7)
        {
            for (var i = 0; i < 6; i++)
            {
                for (var j = 0; j < 3; j++)
                {
                    res[i, n - 11 + j] = true;
                    res[n - 11 + j, i] = true;
                }
            }
        }
    }

    private static void PlaceFinderPattern(bool[,] m, bool[,] res, int top, int left, int n)
    {
        for (var dr = -1; dr <= 7; dr++)
        {
            for (var dc = -1; dc <= 7; dc++)
            {
                var r = top + dr;
                var c = left + dc;
                if (r < 0 || r >= n || c < 0 || c >= n)
                    continue;

                var isSeparator = dr == -1 || dr == 7 || dc == -1 || dc == 7;
                var isBorder = !isSeparator && (dr == 0 || dr == 6 || dc == 0 || dc == 6);
                var isCenter = !isSeparator && dr >= 2 && dr <= 4 && dc >= 2 && dc <= 4;

                m[r, c] = isBorder || isCenter;
                res[r, c] = true;
            }
        }
    }

    private static void PlaceAlignmentPattern(bool[,] m, bool[,] res, int centerR, int centerC)
    {
        for (var dr = -2; dr <= 2; dr++)
        {
            for (var dc = -2; dc <= 2; dc++)
            {
                var dist = Math.Max(Math.Abs(dr), Math.Abs(dc));
                m[centerR + dr, centerC + dc] = dist == 0 || dist == 2;
                res[centerR + dr, centerC + dc] = true;
            }
        }
    }

    private static void PlaceDataBits(bool[,] m, bool[,] res, byte[] codewords, int n)
    {
        var totalBits = codewords.Length * 8;
        var bitIdx = 0;
        var upward = true;

        for (var rightCol = n - 1; rightCol >= 1; rightCol -= 2)
        {
            if (rightCol == 6)
                rightCol = 5;

            for (var step = 0; step < n; step++)
            {
                var row = upward ? (n - 1 - step) : step;
                for (var offset = 0; offset < 2; offset++)
                {
                    var col = rightCol - offset;
                    if (res[row, col])
                        continue;

                    var bit = false;
                    if (bitIdx < totalBits)
                    {
                        var byteVal = codewords[bitIdx >> 3];
                        bit = ((byteVal >> (7 - (bitIdx & 7))) & 1) == 1;
                        bitIdx++;
                    }
                    m[row, col] = bit;
                }
            }

            upward = !upward;
        }
    }

    private static void ApplyMask(bool[,] m, bool[,] res, int maskPattern, int n)
    {
        for (var r = 0; r < n; r++)
        {
            for (var c = 0; c < n; c++)
            {
                if (res[r, c])
                    continue;

                var invert = maskPattern switch
                {
                    0 => (r + c) % 2 == 0,
                    1 => r % 2 == 0,
                    2 => c % 3 == 0,
                    3 => (r + c) % 3 == 0,
                    4 => ((r / 2) + (c / 3)) % 2 == 0,
                    5 => ((r * c) % 2) + ((r * c) % 3) == 0,
                    6 => (((r * c) % 2) + ((r * c) % 3)) % 2 == 0,
                    _ => (((r + c) % 2) + ((r * c) % 3)) % 2 == 0
                };

                if (invert)
                    m[r, c] = !m[r, c];
            }
        }
    }

    private static void PlaceFormatAndVersionInfo(bool[,] m, QrSpec spec, int maskPattern, int n)
    {
        // Format indicator: Level L = 01, Level M = 00
        var eccBits = spec.UseLevelM ? 0b00 : 0b01;
        var formatData = (eccBits << 3) | maskPattern;
        var rem = formatData << 10;
        for (var i = 14; i >= 10; i--)
        {
            if (((rem >> i) & 1) != 0)
                rem ^= 0x537 << (i - 10);
        }
        var formatBits = ((formatData << 10) | rem) ^ 0x5412;

        // k = 0 corresponde ao bit 14 (MSB) e k = 14 ao bit 0 (LSB), conforme ISO/IEC 18004 Seção 7.9.
        for (var k = 0; k < 15; k++)
        {
            var bit = ((formatBits >> (14 - k)) & 1) == 1;

            // Cópia superior-esquerda
            if (k < 6)
                m[8, k] = bit;
            else if (k == 6)
                m[8, 7] = bit;
            else if (k == 7)
                m[8, 8] = bit;
            else if (k == 8)
                m[7, 8] = bit;
            else
                m[14 - k, 8] = bit;

            // Cópia inferior-esquerda (bits 14..8) + superior-direita (bits 7..0)
            if (k < 7)
                m[n - 1 - k, 8] = bit;
            else
                m[8, n - 15 + k] = bit;
        }

        if (spec.Version >= 7)
        {
            var vRem = spec.Version << 12;
            for (var i = 17; i >= 12; i--)
            {
                if (((vRem >> i) & 1) != 0)
                    vRem ^= 0x1F25 << (i - 12);
            }
            var versionBits = (spec.Version << 12) | vRem;
            for (var i = 0; i < 18; i++)
            {
                var bit = ((versionBits >> i) & 1) == 1;
                var a = i / 3;
                var b = i % 3;
                m[a, n - 11 + b] = bit;
                m[n - 11 + b, a] = bit;
            }
        }
    }

    private static int EvaluatePenalty(bool[,] m, int n)
    {
        var penalty = 0;

        // N1: 5+ consecutive same color modules in row/col
        for (var i = 0; i < n; i++)
        {
            var runRow = 1;
            var runCol = 1;
            for (var j = 1; j < n; j++)
            {
                if (m[i, j] == m[i, j - 1])
                    runRow++;
                else
                {
                    if (runRow >= 5) penalty += 3 + (runRow - 5);
                    runRow = 1;
                }

                if (m[j, i] == m[j - 1, i])
                    runCol++;
                else
                {
                    if (runCol >= 5) penalty += 3 + (runCol - 5);
                    runCol = 1;
                }
            }
            if (runRow >= 5) penalty += 3 + (runRow - 5);
            if (runCol >= 5) penalty += 3 + (runCol - 5);
        }

        // N2: 2x2 blocks
        for (var r = 0; r < n - 1; r++)
        {
            for (var c = 0; c < n - 1; c++)
            {
                var v = m[r, c];
                if (v == m[r, c + 1] && v == m[r + 1, c] && v == m[r + 1, c + 1])
                    penalty += 3;
            }
        }

        // N4: dark ratio
        var darkCount = 0;
        for (var r = 0; r < n; r++)
            for (var c = 0; c < n; c++)
                if (m[r, c]) darkCount++;

        var percent = (darkCount * 100.0) / (n * n);
        var prev5 = (int)(Math.Floor(percent / 5.0) * 5);
        var next5 = prev5 + 5;
        penalty += Math.Min(Math.Abs(prev5 - 50) / 5, Math.Abs(next5 - 50) / 5) * 10;

        return penalty;
    }
}
