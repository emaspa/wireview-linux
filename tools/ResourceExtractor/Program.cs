using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

string dllPath = args[0];
string outputDir = args[1];

Directory.CreateDirectory(outputDir);

var asm = Assembly.LoadFrom(dllPath);
Console.WriteLine($"Loaded: {asm.FullName}");

string[] resourceNames = asm.GetManifestResourceNames();
string avaloniaResName = Array.Find(resourceNames, n => n.Contains("AvaloniaResources"))
    ?? throw new Exception("No Avalonia resources found");

Console.WriteLine($"Parsing: {avaloniaResName}");
using var resStream = asm.GetManifestResourceStream(avaloniaResName)
    ?? throw new Exception("Failed to open resource stream");

byte[] data = new byte[resStream.Length];
resStream.ReadExactly(data, 0, data.Length);

int pos = 0;

// The first 4 bytes are the header/index size (including these 4 bytes? or not?)
// headerSize value = 417 (0x1A1), and the PNG data starts at byte 421
// So: dataBase = headerSize + 4 (the 4 bytes for the headerSize field itself)
int headerSize = BitConverter.ToInt32(data, pos); pos += 4;
int version = BitConverter.ToInt32(data, pos); pos += 4;
int entryCount = BitConverter.ToInt32(data, pos); pos += 4;

// The data section starts right after the index
// headerSize includes the version+count+entries but not the headerSize field itself
int dataBase = headerSize + 4; // +4 for the headerSize int32

Console.WriteLine($"Header size: {headerSize}, Version: {version}, Entries: {entryCount}");
Console.WriteLine($"Data section starts at byte: {dataBase}");

// Verify PNG at dataBase
if (data[dataBase] == 0x89 && data[dataBase+1] == 0x50)
    Console.WriteLine($"Confirmed: PNG signature at data base offset {dataBase}");

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
}

foreach (var entry in entries)
{
    string outPath = Path.Combine(outputDir, entry.path.TrimStart('/'));
    string dir = Path.GetDirectoryName(outPath)!;
    Directory.CreateDirectory(dir);

    int realOffset = dataBase + entry.offset;
    if (realOffset + entry.size > data.Length)
    {
        Console.WriteLine($"  SKIP (out of bounds): {entry.path}");
        continue;
    }

    byte[] content = new byte[entry.size];
    Array.Copy(data, realOffset, content, 0, entry.size);
    File.WriteAllBytes(outPath, content);

    string fmt = "unknown";
    if (content.Length >= 4)
    {
        if (content[0] == 0x89 && content[1] == 0x50 && content[2] == 0x4E && content[3] == 0x47) fmt = "PNG";
        else if (content[0] == 0x00 && content[1] == 0x00 && (content[2] == 0x01 || content[2] == 0x02) && content[3] == 0x00) fmt = "ICO";
        else if (content[0] == 0x3C) fmt = "XML";
        else if (content[0] == 0x3A || (content[0] >= 0x30 && content[0] <= 0x39)) fmt = "HEX/text";
    }

    Console.WriteLine($"  {entry.path} ({entry.size} bytes) -> {fmt}");
}

Console.WriteLine($"\nDone. Extracted {entries.Count} assets.");
