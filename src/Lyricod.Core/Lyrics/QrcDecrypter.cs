// Ported from Lyricify Lyrics Helper (Decrypter/Qrc/DESHelper.cs, Decrypter.cs)
// https://github.com/WXRIW/Lyricify-Lyrics-Helper
//
// Copyright 2023 WXRIW
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//
// Changes: restructured to match Lyricod's macOS port (Sources/Lyricod/Lyrics/QRCDecrypter.swift).

using System.IO.Compression;
using System.Text;

namespace Lyricod.Core;

/// <summary>
/// Decrypts QQ Music's QRC lyrics: hex text → QQ's modified triple-DES → zlib.
/// QQ's DES differs from the standard (two altered S-box entries, its own bit order),
/// so the platform's DES can't do this.
/// </summary>
public static class QrcDecrypter
{
    static readonly byte[] Key = Encoding.ASCII.GetBytes("!@#)(*$%123ZXC!@!@#)(NHL");
    // Built on first use: static fields initialise in order, and the tables come later in the file.
    static byte[][][]? _schedule;
    static byte[][][] Schedule => _schedule ??= BuildSchedule();

    static byte[][][] BuildSchedule()
    {
        var s = new byte[3][][];
        for (int i = 0; i < 3; i++) { s[i] = new byte[16][]; for (int j = 0; j < 16; j++) s[i][j] = new byte[6]; }
        // Triple-DES decrypt key setup: D(k3) · E(k2) · D(k1).
        KeySchedule(Key, s[2], decrypt: true);
        KeySchedule(Key[8..], s[1], decrypt: false);
        KeySchedule(Key[16..], s[0], decrypt: true);
        return s;
    }

    public static string? Decrypt(string hex)
    {
        if (hex.Length > LyricData.MaxBytes * 2) return null;
        if (HexBytes(hex) is not { Length: > 0 } bytes || bytes.Length % 8 != 0) return null;
        var block = new byte[8];
        for (int i = 0; i < bytes.Length; i += 8)
        {
            Array.Copy(bytes, i, block, 0, 8);
            foreach (var k in Schedule) Crypt(block, k);
            Array.Copy(block, 0, bytes, i, 8);
        }
        return Inflate(bytes);
    }

    internal static string? Inflate(byte[] bytes)
    {
        // A zlib stream: skip its 2-byte header and inflate the raw deflate data.
        if (bytes.Length <= 2) return null;
        try
        {
            using var input = new MemoryStream(bytes, 2, bytes.Length - 2);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            var raw = LyricData.Read(deflate);
            if (raw.Length == 0) return null;
            int skip = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF ? 3 : 0;
            return new UTF8Encoding(false, true).GetString(raw, skip, raw.Length - skip);
        }
        catch (Exception e) when (e is InvalidDataException or DecoderFallbackException) { return null; }
    }

    static byte[]? HexBytes(string hex)
    {
        if (hex.Length % 2 != 0) return null;
        try { return Convert.FromHexString(hex); } catch (FormatException) { return null; }
    }

    // ---- DES ----

    static uint BitNum(byte[] a, int b, int c) => (uint)((a[b / 32 * 4 + 3 - b % 32 / 8] >> (7 - b % 8)) & 0x01) << c;
    static byte BitNumIntR(uint a, int b, int c) => (byte)(((a >> (31 - b)) & 1) << c);
    static uint BitNumIntL(uint a, int b, int c) => ((a << b) & 0x8000_0000) >> c;
    static int SBoxBit(int a) => (a & 0x20) | ((a & 0x1f) >> 1) | ((a & 0x01) << 4);

    static readonly uint[] SBox1 = [
        14, 4, 13, 1, 2, 15, 11, 8, 3, 10, 6, 12, 5, 9, 0, 7,
        0, 15, 7, 4, 14, 2, 13, 1, 10, 6, 12, 11, 9, 5, 3, 8,
        4, 1, 14, 8, 13, 6, 2, 11, 15, 12, 9, 7, 3, 10, 5, 0,
        15, 12, 8, 2, 4, 9, 1, 7, 5, 11, 3, 14, 10, 0, 6, 13];
    static readonly uint[] SBox2 = [
        15, 1, 8, 14, 6, 11, 3, 4, 9, 7, 2, 13, 12, 0, 5, 10,
        3, 13, 4, 7, 15, 2, 8, 15, 12, 0, 1, 10, 6, 9, 11, 5,
        0, 14, 7, 11, 10, 4, 13, 1, 5, 8, 12, 6, 9, 3, 2, 15,
        13, 8, 10, 1, 3, 15, 4, 2, 11, 6, 7, 12, 0, 5, 14, 9];
    static readonly uint[] SBox3 = [
        10, 0, 9, 14, 6, 3, 15, 5, 1, 13, 12, 7, 11, 4, 2, 8,
        13, 7, 0, 9, 3, 4, 6, 10, 2, 8, 5, 14, 12, 11, 15, 1,
        13, 6, 4, 9, 8, 15, 3, 0, 11, 1, 2, 12, 5, 10, 14, 7,
        1, 10, 13, 0, 6, 9, 8, 7, 4, 15, 14, 3, 11, 5, 2, 12];
    static readonly uint[] SBox4 = [
        7, 13, 14, 3, 0, 6, 9, 10, 1, 2, 8, 5, 11, 12, 4, 15,
        13, 8, 11, 5, 6, 15, 0, 3, 4, 7, 2, 12, 1, 10, 14, 9,
        10, 6, 9, 0, 12, 11, 7, 13, 15, 1, 3, 14, 5, 2, 8, 4,
        3, 15, 0, 6, 10, 10, 13, 8, 9, 4, 5, 11, 12, 7, 2, 14];
    static readonly uint[] SBox5 = [
        2, 12, 4, 1, 7, 10, 11, 6, 8, 5, 3, 15, 13, 0, 14, 9,
        14, 11, 2, 12, 4, 7, 13, 1, 5, 0, 15, 10, 3, 9, 8, 6,
        4, 2, 1, 11, 10, 13, 7, 8, 15, 9, 12, 5, 6, 3, 0, 14,
        11, 8, 12, 7, 1, 14, 2, 13, 6, 15, 0, 9, 10, 4, 5, 3];
    static readonly uint[] SBox6 = [
        12, 1, 10, 15, 9, 2, 6, 8, 0, 13, 3, 4, 14, 7, 5, 11,
        10, 15, 4, 2, 7, 12, 9, 5, 6, 1, 13, 14, 0, 11, 3, 8,
        9, 14, 15, 5, 2, 8, 12, 3, 7, 0, 4, 10, 1, 13, 11, 6,
        4, 3, 2, 12, 9, 5, 15, 10, 11, 14, 1, 7, 6, 0, 8, 13];
    static readonly uint[] SBox7 = [
        4, 11, 2, 14, 15, 0, 8, 13, 3, 12, 9, 7, 5, 10, 6, 1,
        13, 0, 11, 7, 4, 9, 1, 10, 14, 3, 5, 12, 2, 15, 8, 6,
        1, 4, 11, 13, 12, 3, 7, 14, 10, 15, 6, 8, 0, 5, 9, 2,
        6, 11, 13, 8, 1, 4, 10, 7, 9, 5, 0, 15, 14, 2, 3, 12];
    static readonly uint[] SBox8 = [
        13, 2, 8, 4, 6, 15, 11, 1, 10, 9, 3, 14, 5, 0, 12, 7,
        1, 15, 13, 8, 10, 3, 7, 4, 12, 5, 6, 11, 0, 14, 9, 2,
        7, 11, 4, 1, 9, 12, 14, 2, 0, 6, 10, 13, 15, 3, 5, 8,
        2, 1, 14, 7, 4, 10, 8, 13, 15, 12, 9, 0, 3, 5, 6, 11];

    static readonly int[] RndShift = [1, 1, 2, 2, 2, 2, 2, 2, 1, 2, 2, 2, 2, 2, 2, 1];
    static readonly int[] PermC = [56, 48, 40, 32, 24, 16, 8, 0, 57, 49, 41, 33, 25, 17,
                                   9, 1, 58, 50, 42, 34, 26, 18, 10, 2, 59, 51, 43, 35];
    static readonly int[] PermD = [62, 54, 46, 38, 30, 22, 14, 6, 61, 53, 45, 37, 29, 21,
                                   13, 5, 60, 52, 44, 36, 28, 20, 12, 4, 27, 19, 11, 3];
    static readonly int[] Compression = [13, 16, 10, 23, 0, 4, 2, 27, 14, 5, 20, 9,
                                         22, 18, 11, 3, 25, 7, 15, 6, 26, 19, 12, 1,
                                         40, 51, 30, 36, 46, 54, 29, 39, 50, 44, 32, 47,
                                         43, 48, 38, 55, 33, 52, 45, 41, 49, 35, 28, 31];

    static void KeySchedule(byte[] key, byte[][] schedule, bool decrypt)
    {
        uint c = 0, d = 0;
        for (int i = 0; i < 28; i++)
        {
            c |= BitNum(key, PermC[i], 31 - i);
            d |= BitNum(key, PermD[i], 31 - i);
        }
        for (int i = 0; i < 16; i++)
        {
            int s = RndShift[i];
            c = ((c << s) | (c >> (28 - s))) & 0xffff_fff0;
            d = ((d << s) | (d >> (28 - s))) & 0xffff_fff0;
            int gen = decrypt ? 15 - i : i;
            var k = schedule[gen];
            Array.Clear(k);
            for (int j = 0; j < 24; j++) k[j / 8] |= BitNumIntR(c, Compression[j], 7 - j % 8);
            for (int j = 24; j < 48; j++) k[j / 8] |= BitNumIntR(d, Compression[j] - 27, 7 - j % 8);
        }
    }

    static readonly int[] IpOrder = [57, 49, 41, 33, 25, 17, 9, 1, 59, 51, 43, 35, 27, 19, 11, 3,
                                     61, 53, 45, 37, 29, 21, 13, 5, 63, 55, 47, 39, 31, 23, 15, 7];

    static (uint, uint) Ip(byte[] input)
    {
        uint s0 = 0, s1 = 0;
        for (int i = 0; i < IpOrder.Length; i++)
        {
            int b = IpOrder[i];
            s0 |= BitNum(input, b, 31 - i);
            s1 |= BitNum(input, b - 1, 31 - i); // state[1] uses the even bit before each
        }
        return (s0, s1);
    }

    static readonly int[] InvIpTargets = [3, 2, 1, 0, 7, 6, 5, 4];

    static void InvIp(uint s0, uint s1, byte[] output)
    {
        // Output byte k (in order 3,2,1,0,7,6,5,4) takes bits b, b+8, b+16, b+24 of
        // state[1] and state[0], interleaved, for b = 7…0.
        for (int n = 0; n < InvIpTargets.Length; n++)
        {
            int b = 7 - n;
            output[InvIpTargets[n]] = (byte)(BitNumIntR(s1, b, 7) | BitNumIntR(s0, b, 6) | BitNumIntR(s1, b + 8, 5) |
                BitNumIntR(s0, b + 8, 4) | BitNumIntR(s1, b + 16, 3) | BitNumIntR(s0, b + 16, 2) |
                BitNumIntR(s1, b + 24, 1) | BitNumIntR(s0, b + 24, 0));
        }
    }

    static readonly int[] PPerm = [15, 6, 19, 20, 28, 11, 27, 16, 0, 14, 22, 25, 4, 17, 30, 9,
                                   1, 7, 23, 13, 31, 26, 2, 8, 18, 12, 29, 5, 21, 10, 3, 24];

    static uint F(uint state, byte[] key)
    {
        uint t1 = BitNumIntL(state, 31, 0) | ((state & 0xf000_0000) >> 1) | BitNumIntL(state, 4, 5) |
            BitNumIntL(state, 3, 6) | ((state & 0x0f00_0000) >> 3) | BitNumIntL(state, 8, 11) |
            BitNumIntL(state, 7, 12) | ((state & 0x00f0_0000) >> 5) | BitNumIntL(state, 12, 17) |
            BitNumIntL(state, 11, 18) | ((state & 0x000f_0000) >> 7) | BitNumIntL(state, 16, 23);
        uint t2 = BitNumIntL(state, 15, 0) | ((state & 0x0000_f000) << 15) | BitNumIntL(state, 20, 5) |
            BitNumIntL(state, 19, 6) | ((state & 0x0000_0f00) << 13) | BitNumIntL(state, 24, 11) |
            BitNumIntL(state, 23, 12) | ((state & 0x0000_00f0) << 11) | BitNumIntL(state, 28, 17) |
            BitNumIntL(state, 27, 18) | ((state & 0x0000_000f) << 9) | BitNumIntL(state, 0, 23);
        int l0 = (int)((t1 >> 24) & 0xff) ^ key[0], l1 = (int)((t1 >> 16) & 0xff) ^ key[1], l2 = (int)((t1 >> 8) & 0xff) ^ key[2];
        int l3 = (int)((t2 >> 24) & 0xff) ^ key[3], l4 = (int)((t2 >> 16) & 0xff) ^ key[4], l5 = (int)((t2 >> 8) & 0xff) ^ key[5];
        uint s = SBox1[SBoxBit(l0 >> 2)] << 28;
        s |= SBox2[SBoxBit(((l0 & 0x03) << 4) | (l1 >> 4))] << 24;
        s |= SBox3[SBoxBit(((l1 & 0x0f) << 2) | (l2 >> 6))] << 20;
        s |= SBox4[SBoxBit(l2 & 0x3f)] << 16;
        s |= SBox5[SBoxBit(l3 >> 2)] << 12;
        s |= SBox6[SBoxBit(((l3 & 0x03) << 4) | (l4 >> 4))] << 8;
        s |= SBox7[SBoxBit(((l4 & 0x0f) << 2) | (l5 >> 6))] << 4;
        s |= SBox8[SBoxBit(l5 & 0x3f)];
        uint p = 0;
        for (int i = 0; i < PPerm.Length; i++) p |= BitNumIntL(s, PPerm[i], i);
        return p;
    }

    static void Crypt(byte[] block, byte[][] key)
    {
        var (s0, s1) = Ip(block);
        for (int idx = 0; idx < 15; idx++)
        {
            uint t = s1;
            s1 = F(s1, key[idx]) ^ s0;
            s0 = t;
        }
        s0 = F(s1, key[15]) ^ s0;
        InvIp(s0, s1, block);
    }
}
