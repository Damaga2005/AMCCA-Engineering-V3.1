using System;
using System.IO;

namespace AMCCA.Core.Packaging;

public record PeValidationResult(
    bool IsValid,
    string FailureReason = "",
    ushort Machine = 0,
    ushort Magic = 0,
    ushort NumberOfSections = 0,
    ulong ImageBase = 0,
    uint SectionAlignment = 0,
    uint SizeOfImage = 0);

public static class PeBinaryValidator
{
    public const ushort MachineAmd64 = 0x8664;
    public const ushort MagicPe32Plus = 0x020B;
    public const ushort MagicPe32 = 0x010B;

    public static PeValidationResult Validate(byte[] bytes)
    {
        if (bytes == null || bytes.Length < 64)
        {
            return new PeValidationResult(false, "File too small for DOS header (minimum 64 bytes required).");
        }

        // DOS Header: MZ
        if (bytes[0] != 0x4D || bytes[1] != 0x5A)
        {
            return new PeValidationResult(false, "Invalid DOS header: missing 'MZ' signature.");
        }

        // e_lfanew at 0x3C
        int e_lfanew = BitConverter.ToInt32(bytes, 0x3C);
        if (e_lfanew < 64 || e_lfanew > bytes.Length - 4)
        {
            return new PeValidationResult(false, $"Invalid e_lfanew offset: {e_lfanew} is outside file boundaries.");
        }

        // PE Signature: PE\0\0
        if (bytes[e_lfanew] != 0x50 || bytes[e_lfanew + 1] != 0x45 ||
            bytes[e_lfanew + 2] != 0x00 || bytes[e_lfanew + 3] != 0x00)
        {
            return new PeValidationResult(false, "Invalid PE signature: missing 'PE\\0\\0'.");
        }

        int coffOffset = e_lfanew + 4;
        if (coffOffset + 20 > bytes.Length)
        {
            return new PeValidationResult(false, "Truncated COFF header.");
        }

        ushort machine = BitConverter.ToUInt16(bytes, coffOffset);
        ushort numberOfSections = BitConverter.ToUInt16(bytes, coffOffset + 2);
        ushort sizeOfOptionalHeader = BitConverter.ToUInt16(bytes, coffOffset + 16);

        if (machine != MachineAmd64)
        {
            return new PeValidationResult(false, $"Invalid machine architecture: 0x{machine:X4} (expected 0x8664 AMD64).", Machine: machine);
        }

        if (numberOfSections == 0)
        {
            return new PeValidationResult(false, "Invalid COFF header: NumberOfSections must be greater than 0.", Machine: machine, NumberOfSections: numberOfSections);
        }

        if (sizeOfOptionalHeader < 112)
        {
            return new PeValidationResult(false, $"Invalid Optional Header size: {sizeOfOptionalHeader} bytes (expected >= 112 bytes).", Machine: machine, NumberOfSections: numberOfSections);
        }

        int optOffset = coffOffset + 20;
        if (optOffset + 112 > bytes.Length || optOffset + sizeOfOptionalHeader > bytes.Length)
        {
            return new PeValidationResult(false, "Truncated Optional Header.", Machine: machine, NumberOfSections: numberOfSections);
        }

        ushort magic = BitConverter.ToUInt16(bytes, optOffset);
        if (magic != MagicPe32Plus)
        {
            return new PeValidationResult(false, $"Invalid Optional Header Magic: 0x{magic:X4} (expected 0x020B PE32+; PE32 standard is rejected).", Machine: machine, Magic: magic, NumberOfSections: numberOfSections);
        }

        // In PE32+:
        // optOffset + 24: ImageBase (8 bytes, uint64)
        // optOffset + 32: SectionAlignment (4 bytes, uint32)
        // optOffset + 36: FileAlignment (4 bytes, uint32)
        // optOffset + 56: SizeOfImage (4 bytes, uint32)
        ulong imageBase = BitConverter.ToUInt64(bytes, optOffset + 24);
        uint sectionAlignment = BitConverter.ToUInt32(bytes, optOffset + 32);
        uint fileAlignment = BitConverter.ToUInt32(bytes, optOffset + 36);
        uint sizeOfImage = BitConverter.ToUInt32(bytes, optOffset + 56);

        if (imageBase == 0)
        {
            return new PeValidationResult(false, "Invalid ImageBase: must be greater than 0.", Machine: machine, Magic: magic, NumberOfSections: numberOfSections);
        }

        if (sectionAlignment == 0 || fileAlignment == 0)
        {
            return new PeValidationResult(false, "Invalid SectionAlignment or FileAlignment: must be greater than 0.", Machine: machine, Magic: magic, NumberOfSections: numberOfSections, ImageBase: imageBase);
        }

        if (sizeOfImage == 0)
        {
            return new PeValidationResult(false, "Invalid SizeOfImage: must be greater than 0.", Machine: machine, Magic: magic, NumberOfSections: numberOfSections, ImageBase: imageBase, SectionAlignment: sectionAlignment);
        }

        return new PeValidationResult(
            true,
            "Valid Windows PE32+ AMD64 executable.",
            Machine: machine,
            Magic: magic,
            NumberOfSections: numberOfSections,
            ImageBase: imageBase,
            SectionAlignment: sectionAlignment,
            SizeOfImage: sizeOfImage);
    }

    public static PeValidationResult ValidateFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return new PeValidationResult(false, $"File does not exist: {filePath}");
        }
        byte[] bytes = File.ReadAllBytes(filePath);
        return Validate(bytes);
    }
}
