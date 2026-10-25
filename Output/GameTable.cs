using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;

namespace UniversalTrackIR.Output;

public sealed class GameEntry
{
    public required int Id { get; init; }
    public required string Name { get; init; }

    /// <summary>8-byte key some TrackIR games use to decode head data. 0 = game doesn't use one.</summary>
    public required ulong Key { get; init; }
}

/// <summary>
/// The TrackIR game list from opentrack ("facetracknoir supported games.csv"): game ID to name and key.
/// Embedded in the exe.
/// </summary>
public static class GameTable
{
    private static readonly Lazy<Dictionary<int, GameEntry>> Games = new(Load);

    public static int Count => Games.Value.Count;

    public static GameEntry? Find(int id) => id != 0 && Games.Value.TryGetValue(id, out var g) ? g : null;

    private static Dictionary<int, GameEntry> Load()
    {
        var map = new Dictionary<int, GameEntry>();
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Native.games.csv");
            if (stream == null) return map;
            using var reader = new StreamReader(stream);

            reader.ReadLine(); // header
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                // No;Game Name;Game protocol;Supported since;Verified;By;INTERNATIONAL_ID;FTN_ID
                var cols = line.Split(';');
                if (cols.Length != 8) continue;
                if (!int.TryParse(cols[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)) continue;

                map[id] = new GameEntry { Id = id, Name = cols[1], Key = cols[3] == "V160" ? 0 : DecodeKey(cols[7]) };
            }
        }
        catch
        {
            // No list means no game names and no keys. Unencrypted games still work.
        }
        return map;
    }

    /// <summary>
    /// FTN_ID is 11 hex bytes: three filler bytes around the 8 key bytes, shuffled.
    /// Same unpacking as opentrack's csv.cpp.
    /// </summary>
    private static ulong DecodeKey(string hex)
    {
        if (hex.Length != 22) return 0;

        var b = new byte[11];
        for (int i = 0; i < 11; i++)
        {
            if (!byte.TryParse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b[i])) return 0;
        }

        // b: filler, filler, t3, t2, t1, t0, t7, t6, t5, t4, filler
        Span<byte> table = stackalloc byte[8];
        table[3] = b[2]; table[2] = b[3]; table[1] = b[4]; table[0] = b[5];
        table[7] = b[6]; table[6] = b[7]; table[5] = b[8]; table[4] = b[9];
        return BitConverter.ToUInt64(table);
    }
}
