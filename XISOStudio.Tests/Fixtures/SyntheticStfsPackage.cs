using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace XISOStudio.Tests.Fixtures;

/// <summary>
/// Builds small, deterministic single-file XBLA/XBLIG STFS packages for tests
/// and the fuzzer. The fixture is not a real package and carries no game data:
/// it writes the XContent header, level-0 hash tables (and a level-1 table for
/// packages over 170 blocks) with real SHA-1 hashes, the STFS file table and
/// the file data. Packages are limited to 170 logical data blocks so the
/// level-2 table is not needed.
/// </summary>
internal static class SyntheticStfsPackage
{
    private const int BlockSize = 0x1000;
    private const int HeaderSize = 0xB000;
    private const int DirectoryEntrySize = 0x40;
    private const int MaxDataBlocks = 170 * 170;
    private const uint ContentTypeXbla = 0x000D0000;
    private const uint ContentTypeXblig = 0x00000002;
    private const uint EndOfChain = 0xFFFFFF;

    private const ushort FatDate = 0x5D62; // 2026-11-02
    private const ushort FatTime = 0x645C; // 12:34:56

    /// <summary>
    /// Creates a synthetic STFS package at <paramref name="packagePath"/>.
    /// </summary>
    /// <param name="packagePath">Destination package path (overwritten).</param>
    /// <param name="files">Files to include; parent directories are created automatically.</param>
    /// <param name="readOnlyFormat">Use one hash table per level (read-only format) when <c>true</c>.</param>
    /// <param name="rootActiveIndex">Use the secondary top-level hash table (writable format only).</param>
    /// <param name="xblig">Write the XBLIG content type instead of XBLA.</param>
    /// <param name="titleName">Title name stored in the XContent header.</param>
    /// <param name="extraDirectories">Additional empty directories to create.</param>
    /// <param name="nonContiguousPath">A file path whose blocks are separated by an unused block.</param>
    /// <exception cref="ArgumentException">A path is empty or unsafe.</exception>
    /// <exception cref="NotSupportedException">The fixture would need more than 170 data blocks.</exception>
    public static void Create(
        string packagePath,
        IReadOnlyList<SyntheticStfsFile> files,
        bool readOnlyFormat = true,
        bool rootActiveIndex = false,
        bool xblig = false,
        string titleName = "Synthetic STFS",
        IReadOnlyList<string>? extraDirectories = null,
        string? nonContiguousPath = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(packagePath);
        ArgumentNullException.ThrowIfNull(files);

        byte[] package = Build(files, readOnlyFormat, rootActiveIndex, xblig, titleName,
            extraDirectories, nonContiguousPath);
        File.WriteAllBytes(packagePath, package);
    }

    /// <summary>
    /// Builds a synthetic STFS package and returns its bytes.
    /// </summary>
    /// <param name="files">Files to include; parent directories are created automatically.</param>
    /// <param name="readOnlyFormat">Use one hash table per level (read-only format) when <c>true</c>.</param>
    /// <param name="rootActiveIndex">Use the secondary top-level hash table (writable format only).</param>
    /// <param name="xblig">Write the XBLIG content type instead of XBLA.</param>
    /// <param name="titleName">Title name stored in the XContent header.</param>
    /// <param name="extraDirectories">Additional empty directories to create.</param>
    /// <param name="nonContiguousPath">A file path whose blocks are separated by an unused block.</param>
    /// <exception cref="ArgumentException">A path is empty or unsafe.</exception>
    /// <exception cref="NotSupportedException">The fixture would need more than 170 data blocks.</exception>
    private static byte[] Build(
        IReadOnlyList<SyntheticStfsFile> files,
        bool readOnlyFormat = true,
        bool rootActiveIndex = false,
        bool xblig = false,
        string titleName = "Synthetic STFS",
        IReadOnlyList<string>? extraDirectories = null,
        string? nonContiguousPath = null)
    {
        ArgumentNullException.ThrowIfNull(files);

        List<Node> nodes = BuildNodes(files, extraDirectories);
        int fileTableBlocks = Math.Max(1, (nodes.Count * DirectoryEntrySize + BlockSize - 1) / BlockSize);
        if (fileTableBlocks > MaxDataBlocks)
        {
            throw new NotSupportedException("The synthetic STFS fixture has too many file table entries.");
        }

        string? nonContiguous = NormalizePath(nonContiguousPath);
        uint nextBlock = (uint)fileTableBlocks;
        foreach (Node node in nodes)
        {
            if (node.IsDirectory || node.Data.Length == 0)
            {
                node.StartBlock = 0;
                node.BlockCount = 0;
                node.IsContiguous = true;
                continue;
            }

            int blockCount = (node.Data.Length + BlockSize - 1) / BlockSize;
            node.BlockCount = (uint)blockCount;
            node.IsContiguous = !string.Equals(node.Path, nonContiguous, StringComparison.OrdinalIgnoreCase) ||
                                blockCount < 2;
            node.Blocks = new uint[blockCount];
            for (int index = 0; index < blockCount; index++)
            {
                node.Blocks[index] = nextBlock++;
                if (!node.IsContiguous && index + 1 < blockCount)
                {
                    nextBlock++; // Leave one unused logical block between the file's blocks.
                }
            }

            node.StartBlock = node.Blocks[0];
        }

        uint totalBlocks = nextBlock;
        if (totalBlocks > MaxDataBlocks)
        {
            throw new NotSupportedException(
                $"The synthetic STFS fixture needs {totalBlocks} data blocks; the limit is {MaxDataBlocks}.");
        }

        int blocksPerHashTable = readOnlyFormat ? 1 : 2;
        int totalPhysicalBlocks = checked((int)BlockToBacking(totalBlocks - 1, blocksPerHashTable) + 1);
        int totalSize = checked(HeaderSize + (totalPhysicalBlocks * BlockSize));
        byte[] package = new byte[totalSize];

        WriteHeader(package, xblig, titleName, readOnlyFormat, rootActiveIndex,
            fileTableBlocks, totalBlocks);

        WriteFileTable(package, nodes, fileTableBlocks, blocksPerHashTable);

        foreach (Node node in nodes)
        {
            if (node.IsDirectory || node.Data.Length == 0)
            {
                continue;
            }

            for (int index = 0; index < node.Blocks.Length; index++)
            {
                int offset = HeaderSize +
                             ((int)BlockToBacking(node.Blocks[index], blocksPerHashTable) * BlockSize);
                int length = Math.Min(BlockSize, node.Data.Length - (index * BlockSize));
                Array.Copy(node.Data, index * BlockSize, package, offset, length);
            }
        }

        WriteHashTables(package, nodes, fileTableBlocks, totalBlocks, blocksPerHashTable,
            readOnlyFormat, rootActiveIndex);

        return package;
    }

    private static void WriteHeader(byte[] package, bool xblig, string titleName, bool readOnlyFormat,
        bool rootActiveIndex, int fileTableBlocks, uint totalBlocks)
    {
        BinaryPrimitives.WriteUInt32BigEndian(package.AsSpan(0), 0x4C495645); // "LIVE"
        BinaryPrimitives.WriteUInt32BigEndian(package.AsSpan(0x340), HeaderSize);
        BinaryPrimitives.WriteUInt32BigEndian(
            package.AsSpan(0x344), xblig ? ContentTypeXblig : ContentTypeXbla);
        BinaryPrimitives.WriteUInt32BigEndian(package.AsSpan(0x360), 0x584109B7);
        package[0x366] = 0;
        package[0x367] = 1;

        byte[] title = Encoding.BigEndianUnicode.GetBytes(titleName);
        title.AsSpan(0, Math.Min(title.Length, 0x80)).CopyTo(package.AsSpan(0x411));

        package[0x379] = 0x24;
        package[0x37A] = 0;
        package[0x37B] = (byte)((readOnlyFormat ? 0x01 : 0) | (rootActiveIndex ? 0x02 : 0));
        BinaryPrimitives.WriteUInt16LittleEndian(package.AsSpan(0x37C), (ushort)fileTableBlocks);
        WriteUInt24LittleEndian(package, 0x37E, 0);
        BinaryPrimitives.WriteUInt32BigEndian(package.AsSpan(0x395), totalBlocks);
        BinaryPrimitives.WriteUInt32BigEndian(package.AsSpan(0x399), 0);
        BinaryPrimitives.WriteUInt32BigEndian(package.AsSpan(0x39D), 1);
        BinaryPrimitives.WriteUInt32BigEndian(package.AsSpan(0x3A9), 0);
    }

    private static void WriteHashTables(byte[] package, List<Node> nodes, int fileTableBlocks,
        uint totalBlocks, int blocksPerHashTable, bool readOnlyFormat, bool rootActiveIndex)
    {
        Dictionary<uint, uint> nextByBlock = new();
        for (int block = 0; block < fileTableBlocks; block++)
        {
            nextByBlock[(uint)block] = block + 1 < fileTableBlocks
                ? (uint)(block + 1)
                : EndOfChain;
        }

        foreach (Node node in nodes)
        {
            if (node.IsDirectory || node.BlockCount == 0)
            {
                continue;
            }

            for (int index = 0; index < node.Blocks.Length; index++)
            {
                nextByBlock[node.Blocks[index]] = index + 1 < node.Blocks.Length
                    ? node.Blocks[index + 1]
                    : EndOfChain;
            }
        }

        bool topLevelZero = totalBlocks <= 170;
        uint groupCount = (totalBlocks + 169) / 170;

        // Level 0: next-block info plus the SHA-1 of each logical data block.
        for (uint group = 0; group < groupCount; group++)
        {
            uint firstBlock = group * 170;
            uint lastBlock = Math.Min(totalBlocks - 1, firstBlock + 169);
            int tableBlock = (int)HashTableBlockNumber(firstBlock, 0, blocksPerHashTable);
            if (topLevelZero && !readOnlyFormat && rootActiveIndex)
            {
                tableBlock++; // The top-level (level-0) table uses its secondary block.
            }

            int tableOffset = HeaderSize + (tableBlock * BlockSize);
            for (uint block = firstBlock; block <= lastBlock; block++)
            {
                int entryOffset = tableOffset + ((int)(block % 170) * 0x18);
                if (nextByBlock.TryGetValue(block, out uint next))
                {
                    WriteHashEntry(package, tableOffset, (int)(block % 170), next);
                }

                int dataOffset = HeaderSize +
                                 ((int)BlockToBacking(block, blocksPerHashTable) * BlockSize);
                SHA1.HashData(
                    package.AsSpan(dataOffset, BlockSize),
                    package.AsSpan(entryOffset, 20));
            }
        }

        // Level 1: hash every level-0 table into its parent. The generator is
        // capped at 170*170 logical blocks, so level 2 is never needed.
        if (!topLevelZero)
        {
            int level1Block = (int)HashTableBlockNumber(0, 1, blocksPerHashTable);
            if (!readOnlyFormat && rootActiveIndex)
            {
                level1Block++; // The top-level (level-1) table uses its secondary block.
            }

            int tableOffset = HeaderSize + (level1Block * BlockSize);
            for (uint group = 0; group < groupCount; group++)
            {
                uint firstBlock = group * 170;
                int childBlock = (int)HashTableBlockNumber(firstBlock, 0, blocksPerHashTable);
                int childOffset = HeaderSize + (childBlock * BlockSize);
                int entryOffset = tableOffset + ((int)group * 0x18);

                // Active-index bit 0 selects the primary level-0 table.
                BinaryPrimitives.WriteUInt32BigEndian(
                    package.AsSpan(entryOffset + 0x14), 0);
                SHA1.HashData(
                    package.AsSpan(childOffset, BlockSize),
                    package.AsSpan(entryOffset, 20));
            }
        }

        // Top hash: SHA-1 of the active top-level hash table.
        int topBlock = topLevelZero
            ? (int)HashTableBlockNumber(0, 0, blocksPerHashTable)
            : (int)HashTableBlockNumber(0, 1, blocksPerHashTable);
        if (!readOnlyFormat && rootActiveIndex)
        {
            topBlock++;
        }

        SHA1.HashData(
            package.AsSpan(HeaderSize + (topBlock * BlockSize), BlockSize),
            package.AsSpan(0x381, 20));

        // Header hash: SHA-1 of the XContent body from 0x344 to the first block.
        SHA1.HashData(
            package.AsSpan(0x344, HeaderSize - 0x344),
            package.AsSpan(0x32C, 20));
    }

    private static void WriteHashEntry(byte[] package, int tableOffset, int index, uint next)
    {
        int offset = tableOffset + (index * 0x18) + 0x14;
        BinaryPrimitives.WriteUInt32BigEndian(package.AsSpan(offset), 0x80000000u | (next & EndOfChain));
    }

    private static void WriteFileTable(byte[] package, List<Node> nodes, int fileTableBlocks,
        int blocksPerHashTable)
    {
        byte[] table = new byte[fileTableBlocks * BlockSize];
        for (int index = 0; index < nodes.Count; index++)
        {
            Node node = nodes[index];
            int offset = index * DirectoryEntrySize;
            byte[] name = Encoding.ASCII.GetBytes(node.Name);
            name.AsSpan(0, Math.Min(name.Length, 40)).CopyTo(table.AsSpan(offset));
            int nameLength = Math.Min(name.Length, 40);
            table[offset + 0x28] = (byte)(nameLength |
                                         (node.IsContiguous ? 0x40 : 0) |
                                         (node.IsDirectory ? 0x80 : 0));
            WriteUInt24LittleEndian(table, offset + 0x29, node.BlockCount);
            WriteUInt24LittleEndian(table, offset + 0x2C, node.BlockCount);
            WriteUInt24LittleEndian(table, offset + 0x2F, node.StartBlock);
            BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(offset + 0x32), node.ParentIndex);
            BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(offset + 0x34), (uint)node.Data.Length);
            BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(offset + 0x38), FatDate);
            BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(offset + 0x3A), FatTime);
            BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(offset + 0x3C), FatDate);
            BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(offset + 0x3E), FatTime);
        }

        for (int block = 0; block < fileTableBlocks; block++)
        {
            int backingBlock = (int)BlockToBacking((uint)block, blocksPerHashTable);
            Array.Copy(table, block * BlockSize, package,
                HeaderSize + (backingBlock * BlockSize), BlockSize);
        }
    }

    private static uint BlockToBacking(uint block, int blocksPerHashTable)
    {
        ulong baseSize = 170;
        ulong mapped = block;
        for (int level = 0; level < 3; level++)
        {
            mapped += ((block + baseSize) / baseSize) * (ulong)blocksPerHashTable;
            if (block < baseSize)
            {
                break;
            }

            baseSize *= 170;
        }

        return checked((uint)mapped);
    }

    private static uint HashTableBlockNumber(uint block, int level, int blocksPerHashTable)
    {
        uint blockStep0 = (uint)(170 + blocksPerHashTable);
        uint blockStep1 = (uint)(28900 + (171 * blocksPerHashTable));
        if (level == 0)
        {
            if (block < 170)
            {
                return 0;
            }

            uint number = ((block / 170) * blockStep0) +
                          (((block / 28900) + 1) * (uint)blocksPerHashTable);
            if (block >= 28900)
            {
                number += (uint)blocksPerHashTable;
            }

            return number;
        }

        if (level == 1)
        {
            return block < 28900
                ? blockStep0
                : ((block / 28900) * blockStep1) + (uint)blocksPerHashTable;
        }

        return blockStep1;
    }

    private static List<Node> BuildNodes(IReadOnlyList<SyntheticStfsFile> files,
        IReadOnlyList<string>? extraDirectories)
    {
        Dictionary<string, Node> byPath = new(StringComparer.OrdinalIgnoreCase);
        foreach (SyntheticStfsFile file in files)
        {
            ArgumentNullException.ThrowIfNull(file);
            string path = NormalizePath(file.Path) ??
                          throw new ArgumentException("STFS fixture paths cannot be empty.", nameof(files));
            AddParents(byPath, path);
            if (!byPath.TryAdd(path, new Node(path, isDirectory: false, file.Data)))
            {
                throw new ArgumentException($"Duplicate STFS fixture path: '{path}'.", nameof(files));
            }
        }

        if (extraDirectories != null)
        {
            foreach (string directory in extraDirectories)
            {
                string path = NormalizePath(directory) ??
                              throw new ArgumentException("STFS fixture directories cannot be empty.");
                AddParents(byPath, path);
                byPath.TryAdd(path, new Node(path, isDirectory: true, []));
            }
        }

        List<Node> nodes = byPath.Values
            .OrderBy(static node => Depth(node.Path))
            .ThenBy(static node => node.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (int index = 0; index < nodes.Count; index++)
        {
            Node node = nodes[index];
            node.Index = index;
            node.ParentIndex = 0xFFFF;
        }

        foreach (Node node in nodes)
        {
            int slash = node.Path.LastIndexOf('/');
            if (slash < 0)
            {
                continue;
            }

            string parentPath = node.Path[..slash];
            if (byPath.TryGetValue(parentPath, out Node? parent))
            {
                node.ParentIndex = (ushort)parent.Index;
            }
        }

        return nodes;
    }

    private static void AddParents(Dictionary<string, Node> byPath, string path)
    {
        int slash = path.IndexOf('/');
        while (slash >= 0)
        {
            string parent = path[..slash];
            byPath.TryAdd(parent, new Node(parent, isDirectory: true, []));
            slash = path.IndexOf('/', slash + 1);
        }
    }

    private static int Depth(string path)
    {
        int depth = 0;
        foreach (char c in path)
        {
            if (c == '/')
            {
                depth++;
            }
        }

        return depth;
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string normalized = path.Replace('\\', '/').Trim('/');
        if (normalized.Length == 0)
        {
            return null;
        }

        foreach (string component in normalized.Split('/'))
        {
            if (component.Length is 0 or > 40 ||
                component is "." or ".." ||
                component.IndexOfAny([':', '*', '?', '"', '<', '>', '|', '\0']) >= 0 ||
                component.Any(char.IsControl))
            {
                throw new ArgumentException($"Unsafe STFS fixture path: '{path}'.", nameof(path));
            }
        }

        return normalized;
    }

    private static void WriteUInt24LittleEndian(Span<byte> buffer, int offset, uint value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
    }

    private sealed class Node
    {
        internal Node(string path, bool isDirectory, byte[] data)
        {
            Path = path;
            int slash = path.LastIndexOf('/');
            Name = slash < 0 ? path : path[(slash + 1)..];
            IsDirectory = isDirectory;
            Data = data;
        }

        internal string Path { get; }

        internal string Name { get; }

        internal bool IsDirectory { get; }

        internal byte[] Data { get; }

        internal int Index { get; set; }

        internal ushort ParentIndex { get; set; }

        internal uint StartBlock { get; set; }

        internal uint BlockCount { get; set; }

        internal bool IsContiguous { get; set; }

        internal uint[] Blocks { get; set; } = [];
    }
}
