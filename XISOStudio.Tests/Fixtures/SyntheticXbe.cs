using System.Buffers.Binary;
using System.Text;

namespace XISOStudio.Tests.Fixtures;

/// <summary>
/// Builds deterministic, copyright-free XBE executables with a readable
/// certificate, logo, <c>$$XTIMAGE</c> and <c>$$XSIMAGE</c> sections. Adapted
/// from the MIT-licensed XISOSharp test data generator.
/// </summary>
internal static class SyntheticXbe
{
    /// <summary>Builds an XBEH executable with all three image assets.</summary>
    /// <param name="titleName">Certificate title name.</param>
    /// <param name="titleId">Certificate title id.</param>
    /// <returns>Raw XBE bytes.</returns>
    public static byte[] BuildWithImages(
        string titleName = "Synthetic XBE",
        uint titleId = 0x4D530004)
    {
        const int titleOffset = 0x3000;
        const int saveOffset = 0x10000;
        const int logoOffset = 0x800;
        const int logoSize = 100 * 17 * 4;
        const int titleSize = 54 + (128 * 128 * 3);
        const int saveSize = 54 + (128 * 128 / 16 * 8);

        byte[] data = new byte[saveOffset + saveSize + 0x1000];
        Span<byte> span = data.AsSpan();

        "XBEH"u8.CopyTo(span);
        WriteU32(span, 0x104, 0x00010000);
        WriteU32(span, 0x118, 0x00010400);
        WriteU32(span, 0x11C, 2);
        WriteU32(span, 0x120, 0x00010500);
        WriteU32(span, 0x170, 0x00010000 + logoOffset);
        WriteU32(span, 0x174, logoSize);

        const int cert = 0x400;
        WriteU32(span, cert, 0x1D0);
        WriteU32(span, cert + 0x04, 0x3A2B1C00);
        WriteU32(span, cert + 0x08, titleId);
        Encoding.Unicode.GetBytes(titleName).CopyTo(span[(cert + 0x0C)..]);
        WriteU32(span, cert + 0x9C, 0x08);
        WriteU32(span, cert + 0xA0, 0x07);
        WriteU32(span, cert + 0xA8, 1);
        WriteU32(span, cert + 0xAC, 0x00010000);

        WriteSection(span, 0x500, titleOffset, titleSize, 0x700);
        WriteSection(span, 0x538, saveOffset, saveSize, 0x720);
        "$$XTIMAGE\0"u8.CopyTo(span[0x700..]);
        "$$XSIMAGE\0"u8.CopyTo(span[0x720..]);

        for (int i = 0; i < 100 * 17; i++)
        {
            int p = logoOffset + (i * 4);
            data[p] = (byte)(i & 0xFF);
            data[p + 1] = (byte)((i * 3) & 0xFF);
            data[p + 2] = (byte)((i * 7) & 0xFF);
            data[p + 3] = 0xFF;
        }

        "DDS "u8.CopyTo(span[titleOffset..]);
        WriteU32(span, titleOffset + 0x0C, 128);
        WriteU32(span, titleOffset + 0x10, 128);
        for (int i = 0; i < 128 * 128; i++)
        {
            int p = titleOffset + 54 + (i * 3);
            data[p] = (byte)(i & 0xFF);
            data[p + 1] = (byte)((i * 5) & 0xFF);
            data[p + 2] = (byte)((i * 11) & 0xFF);
        }

        "DDS "u8.CopyTo(span[saveOffset..]);
        WriteU32(span, saveOffset + 0x0C, 128);
        WriteU32(span, saveOffset + 0x10, 128);
        "DXT1"u8.CopyTo(span[(saveOffset + 0x20)..]);
        for (int block = 0; block < 128 * 128 / 16; block++)
        {
            int p = saveOffset + 54 + (block * 8);
            BinaryPrimitives.WriteUInt16LittleEndian(span[p..], 0xF800);
            BinaryPrimitives.WriteUInt16LittleEndian(span[(p + 2)..], 0xF800);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(p + 4)..], 0);
        }

        return data;

        static void WriteSection(Span<byte> target, int offset, int rawOffset, int rawSize, int nameOffset)
        {
            WriteU32(target, offset + 0x0C, (uint)rawOffset);
            WriteU32(target, offset + 0x10, (uint)rawSize);
            WriteU32(target, offset + 0x14, 0x00010000 + (uint)nameOffset);
        }

        static void WriteU32(Span<byte> target, int offset, uint value) =>
            BinaryPrimitives.WriteUInt32LittleEndian(target[offset..], value);
    }
}
