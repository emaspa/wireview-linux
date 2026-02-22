using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

string inputPath = args[0];
string outputDir = args[1];

Directory.CreateDirectory(outputDir);

byte[] data = File.ReadAllBytes(inputPath);
int pos = 0;

int totalSize = BitConverter.ToInt32(data, pos); pos += 4;
int version = BitConverter.ToInt32(data, pos); pos += 4;
int entryCount = BitConverter.ToInt32(data, pos); pos += 4;

Console.WriteLine($"Total size: {totalSize}, Version: {version}, Entries: {entryCount}");

var entries = new List<(string path, int offset, int size)>();

for (int i = 0; i < entryCount; i++)
{
    int pathLen = 0;
    int shift = 0;
    while (true)
    {
        byte b = data[pos++];
        pathLen |= (b & 0x7F) << shift;
        if ((b & 0x80) == 0) break;
        shift += 7;
    }
    string path = Encoding.UTF8.GetString(data, pos, pathLen);
    pos += pathLen;

    int offset = BitConverter.ToInt32(data, pos); pos += 4;
    int size = BitConverter.ToInt32(data, pos); pos += 4;

    entries.Add((path, offset, size));
    Console.WriteLine($"  {path} (offset={offset}, size={size})");
}

foreach (var entry in entries)
{
    string outPath = Path.Combine(outputDir, entry.path.TrimStart('/'));
    string? dir = Path.GetDirectoryName(outPath);
    if (dir != null) Directory.CreateDirectory(dir);

    byte[] content = new byte[entry.size];
    Array.Copy(data, entry.offset, content, 0, entry.size);
    File.WriteAllBytes(outPath, content);
    Console.WriteLine($"  Extracted: {outPath} ({entry.size} bytes)");
}

Console.WriteLine($"\nExtracted {entries.Count} assets to {outputDir}");
