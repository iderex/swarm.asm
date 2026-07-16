namespace Swarm.Tests;

/// <summary>
/// A minimal PE64 reader — just enough to enumerate the import directory and
/// read the headers the conformance tests assert on. Deliberately dependency
/// free: the point of these tests is to inspect the shipped bytes with our own
/// eyes, not to trust a library's view of them.
/// </summary>
public sealed class PeImage
{
    private readonly byte[] _bytes;
    private readonly uint _importRva;
    private readonly uint _importSize;
    private readonly (uint Rva, uint Size, uint RawPtr, uint RawSize)[] _sections;

    public IReadOnlyList<string> ImportedDlls { get; }

    /// <summary>File size in bytes — the size-budget metric.</summary>
    public int FileSize => _bytes.Length;

    private PeImage(byte[] bytes)
    {
        _bytes = bytes;

        if (bytes.Length < 0x40 || bytes[0] != (byte)'M' || bytes[1] != (byte)'Z')
        {
            throw new InvalidDataException("not an MZ image");
        }

        int peOff = BitConverter.ToInt32(bytes, 0x3C);
        if (peOff < 0 || peOff > bytes.Length - 24)
        {
            throw new InvalidDataException($"PE header offset 0x{peOff:X} out of range");
        }
        if (bytes[peOff] != 'P' || bytes[peOff + 1] != 'E' || bytes[peOff + 2] != 0 || bytes[peOff + 3] != 0)
        {
            throw new InvalidDataException("PE signature not found");
        }

        int coff = peOff + 4;
        int numSections = BitConverter.ToUInt16(bytes, coff + 2);
        int optSize = BitConverter.ToUInt16(bytes, coff + 16);
        int opt = coff + 20;

        ushort magic = BitConverter.ToUInt16(bytes, opt);
        if (magic != 0x20B)
        {
            throw new InvalidDataException($"not a PE32+ (optional-header magic 0x{magic:X})");
        }

        // The import table is data directory entry 1; PE32+ places the count
        // at optional-header offset 108 and the directory array at 112. A
        // valid image may carry fewer than 2 directories — then it has no
        // import table at all.
        uint numDirs = BitConverter.ToUInt32(bytes, opt + 108);
        int dirs = opt + 112;
        _importRva = numDirs >= 2 ? BitConverter.ToUInt32(bytes, dirs + 1 * 8) : 0;
        _importSize = numDirs >= 2 ? BitConverter.ToUInt32(bytes, dirs + 1 * 8 + 4) : 0;

        int sectionTable = opt + optSize;
        _sections = new (uint, uint, uint, uint)[numSections];
        for (int i = 0; i < numSections; i++)
        {
            int s = sectionTable + i * 40;
            uint vsize = BitConverter.ToUInt32(bytes, s + 8);
            uint vaddr = BitConverter.ToUInt32(bytes, s + 12);
            uint rawSize = BitConverter.ToUInt32(bytes, s + 16);
            uint rawPtr = BitConverter.ToUInt32(bytes, s + 20);
            _sections[i] = (vaddr, vsize, rawPtr, rawSize);
        }

        ImportedDlls = ReadImportedDlls();
    }

    public static PeImage Load(string path) => new(File.ReadAllBytes(path));

    private List<string> ReadImportedDlls()
    {
        var names = new List<string>();
        if (_importRva == 0 || _importSize == 0)
        {
            return names;
        }

        // Import Directory Table: 20-byte descriptors terminated by an
        // all-zero entry; the Name field (offset 12) is an RVA to the DLL name.
        // The walk is bounded by the file so a missing terminator fails loudly
        // instead of running off the end.
        for (int off = (int)RvaToOffset(_importRva); ; off += 20)
        {
            if (off < 0 || off + 20 > _bytes.Length)
            {
                throw new InvalidDataException("import directory runs past the end of the file");
            }
            uint nameRva = BitConverter.ToUInt32(_bytes, off + 12);
            uint originalFirstThunk = BitConverter.ToUInt32(_bytes, off + 0);
            uint firstThunk = BitConverter.ToUInt32(_bytes, off + 16);
            if (nameRva == 0 && originalFirstThunk == 0 && firstThunk == 0)
            {
                break;
            }
            names.Add(ReadCString(RvaToOffset(nameRva)));
        }
        return names;
    }

    private uint RvaToOffset(uint rva)
    {
        // Only the raw-data window maps to file bytes: an RVA in a
        // virtual-only tail (VirtualSize > SizeOfRawData) has no file
        // representation and must fail here, not silently read the next
        // section's bytes.
        foreach (var (vaddr, _, rawPtr, rawSize) in _sections)
        {
            if (rva >= vaddr && rva < vaddr + rawSize)
            {
                return rawPtr + (rva - vaddr);
            }
        }
        throw new InvalidDataException($"RVA 0x{rva:X} maps to no raw data in any section");
    }

    private string ReadCString(uint offset)
    {
        int end = (int)offset;
        while (end < _bytes.Length && _bytes[end] != 0)
        {
            end++;
        }
        return System.Text.Encoding.ASCII.GetString(_bytes, (int)offset, end - (int)offset);
    }
}
