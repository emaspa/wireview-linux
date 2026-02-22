using System;
using System.IO;
using System.IO.Compression;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: BundleExtractor <input.exe> <output-dir>");
    return 1;
}

string inputPath = args[0];
string outputDir = args[1];

Directory.CreateDirectory(outputDir);

using var stream = File.OpenRead(inputPath);
using var reader = new BinaryReader(stream);

// Find the bundle header signature by scanning the file
long bundleHeaderOffset = FindBundleHeaderOffset(stream);
if (bundleHeaderOffset < 0)
{
    Console.Error.WriteLine("Could not find bundle header. File may not be a .NET single-file bundle.");
    return 1;
}

Console.WriteLine($"Bundle header at offset: {bundleHeaderOffset:N0} (0x{bundleHeaderOffset:x})");
stream.Position = bundleHeaderOffset;

// Read bundle header
uint majorVersion = reader.ReadUInt32();
uint minorVersion = reader.ReadUInt32();
int fileCount = reader.ReadInt32();
string bundleId = reader.ReadString();

// v2+ has deps.json and runtimeconfig.json offsets
long depsJsonOffset = 0, depsJsonSize = 0;
long runtimeConfigOffset = 0, runtimeConfigSize = 0;
ulong flags = 0;

if (majorVersion >= 2)
{
    depsJsonOffset = reader.ReadInt64();
    depsJsonSize = reader.ReadInt64();
    runtimeConfigOffset = reader.ReadInt64();
    runtimeConfigSize = reader.ReadInt64();
    flags = reader.ReadUInt64();
}

Console.WriteLine($"Bundle version: {majorVersion}.{minorVersion}");
Console.WriteLine($"Bundle ID: {bundleId}");
Console.WriteLine($"File count: {fileCount}");
Console.WriteLine($"Flags: 0x{flags:X}");
Console.WriteLine();

int extracted = 0;
for (int i = 0; i < fileCount; i++)
{
    long offset = reader.ReadInt64();
    long size = reader.ReadInt64();

    // v6+ has compressed size
    long compressedSize = 0;
    if (majorVersion >= 6)
    {
        compressedSize = reader.ReadInt64();
    }

    byte fileType = reader.ReadByte();
    string relativePath = reader.ReadString();

    string outPath = Path.Combine(outputDir, relativePath);
    string? dir = Path.GetDirectoryName(outPath);
    if (dir != null) Directory.CreateDirectory(dir);

    long savedPos = stream.Position;
    stream.Position = offset;

    long readSize = (majorVersion >= 6 && compressedSize > 0) ? compressedSize : size;
    byte[] data = reader.ReadBytes((int)readSize);

    if (majorVersion >= 6 && compressedSize > 0 && compressedSize != size)
    {
        // Decompress using deflate
        using var compressedStream = new MemoryStream(data);
        using var deflateStream = new DeflateStream(compressedStream, CompressionMode.Decompress);
        using var outStream = File.Create(outPath);
        deflateStream.CopyTo(outStream);
    }
    else
    {
        File.WriteAllBytes(outPath, data);
    }

    string typeStr = fileType switch
    {
        0 => "Unknown",
        1 => "Assembly",
        2 => "NativeBinary",
        3 => "DepsJson",
        4 => "RuntimeConfigJson",
        5 => "Symbols",
        _ => $"Type{fileType}"
    };

    Console.WriteLine($"  [{typeStr}] {relativePath} ({size:N0} bytes)");
    extracted++;

    stream.Position = savedPos;
}

Console.WriteLine($"\nExtracted {extracted} files to {outputDir}");
return 0;

static long FindBundleHeaderOffset(Stream stream)
{
    // Search for the .NET single-file bundle signature anywhere in the file
    // Layout: [header offset (8 bytes)] [signature (8 bytes)]
    // Signature bytes: 0x8b 0x12 0x02 0xb9 0x6a 0x61 0x20 0x38
    byte[] signature = { 0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38 };

    // Read the entire file to search for the signature
    stream.Position = 0;
    byte[] fileData = new byte[stream.Length];
    stream.Read(fileData, 0, fileData.Length);

    // Search for the signature
    for (long i = 8; i < fileData.Length - 7; i++)
    {
        bool match = true;
        for (int j = 0; j < 8; j++)
        {
            if (fileData[i + j] != signature[j])
            {
                match = false;
                break;
            }
        }
        if (match)
        {
            // Read the 8 bytes before the signature as the header offset
            long headerOffset = BitConverter.ToInt64(fileData, (int)(i - 8));
            Console.WriteLine($"Found bundle signature at offset {i:N0} (0x{i:x})");
            Console.WriteLine($"Header offset value: {headerOffset:N0} (0x{headerOffset:x})");

            // Validate: header offset should be within the file
            if (headerOffset > 0 && headerOffset < stream.Length)
            {
                return headerOffset;
            }
            else
            {
                Console.WriteLine($"  Invalid header offset, continuing search...");
            }
        }
    }

    return -1;
}
