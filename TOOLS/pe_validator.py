#!/usr/bin/env python3
"""
PE32+ AMD64 Structural Binary Validator (SPEC/76, DEF-CERT-001).

Validates Windows PE executables structurally:
- DOS header: length >= 64, MZ magic, valid e_lfanew inside boundaries
- PE signature: 'PE\\0\\0'
- COFF header: Machine == 0x8664 (AMD64), NumberOfSections > 0, SizeOfOptionalHeader >= 112
- Optional Header: Magic == 0x020B (PE32+ 64-bit), ImageBase > 0, SectionAlignment > 0,
  FileAlignment > 0, SizeOfImage > 0
"""
import os
import struct
import sys

MACHINE_AMD64 = 0x8664
MAGIC_PE32_PLUS = 0x020B
MAGIC_PE32 = 0x010B


def validate_pe_bytes(data: bytes) -> tuple[bool, str, dict]:
    """
    Validates PE binary data.
    Returns (is_valid, message, metadata_dict).
    """
    if not data or len(data) < 64:
        return False, "File too small for DOS header (minimum 64 bytes required).", {}

    if data[:2] != b"MZ":
        return False, f"Invalid DOS header: expected b'MZ', got {data[:2]!r}", {}

    e_lfanew = int.from_bytes(data[0x3C:0x40], "little", signed=True)
    if e_lfanew < 64 or e_lfanew > len(data) - 4:
        return False, f"Invalid e_lfanew offset: {e_lfanew} is outside file boundaries (size={len(data)}).", {}

    if data[e_lfanew:e_lfanew + 4] != b"PE\x00\x00":
        return False, f"Invalid PE signature at offset 0x{e_lfanew:X}: expected b'PE\\x00\\x00'.", {}

    coff_offset = e_lfanew + 4
    if coff_offset + 20 > len(data):
        return False, "Truncated COFF header.", {}

    machine, num_sections, _, _, _, size_opt_header, _ = struct.unpack_from("<HHIIIHH", data, coff_offset)

    if machine != MACHINE_AMD64:
        return False, f"Invalid machine architecture: 0x{machine:04X} (expected 0x8664 AMD64).", {"machine": machine}

    if num_sections == 0:
        return False, "Invalid COFF header: NumberOfSections must be greater than 0.", {"machine": machine, "num_sections": num_sections}

    if size_opt_header < 112:
        return False, f"Invalid Optional Header size: {size_opt_header} bytes (expected >= 112 bytes).", {"machine": machine, "num_sections": num_sections}

    opt_offset = coff_offset + 20
    if opt_offset + 112 > len(data) or opt_offset + size_opt_header > len(data):
        return False, "Truncated Optional Header.", {"machine": machine, "num_sections": num_sections}

    magic = int.from_bytes(data[opt_offset:opt_offset + 2], "little")
    if magic != MAGIC_PE32_PLUS:
        return False, f"Invalid Optional Header Magic: 0x{magic:04X} (expected 0x020B PE32+; standard PE32 is rejected).", {"machine": machine, "magic": magic}

    image_base = int.from_bytes(data[opt_offset + 24:opt_offset + 32], "little")
    section_alignment = int.from_bytes(data[opt_offset + 32:opt_offset + 36], "little")
    file_alignment = int.from_bytes(data[opt_offset + 36:opt_offset + 40], "little")
    size_of_image = int.from_bytes(data[opt_offset + 56:opt_offset + 60], "little")

    if image_base == 0:
        return False, "Invalid ImageBase: must be greater than 0.", {"machine": machine, "magic": magic}

    if section_alignment == 0 or file_alignment == 0:
        return False, "Invalid SectionAlignment or FileAlignment: must be greater than 0.", {"machine": machine, "magic": magic}

    if size_of_image == 0:
        return False, "Invalid SizeOfImage: must be greater than 0.", {"machine": machine, "magic": magic}

    metadata = {
        "machine": hex(machine),
        "magic": hex(magic),
        "num_sections": num_sections,
        "image_base": hex(image_base),
        "section_alignment": section_alignment,
        "file_alignment": file_alignment,
        "size_of_image": size_of_image,
    }
    return True, "Valid Windows PE32+ AMD64 executable.", metadata


def validate_pe_file(file_path: str) -> tuple[bool, str, dict]:
    """Validates a PE file from disk."""
    if not os.path.exists(file_path):
        return False, f"File does not exist: {file_path}", {}
    with open(file_path, "rb") as f:
        data = f.read()
    return validate_pe_bytes(data)


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print("Usage: python pe_validator.py <path-to-pe-file>")
        sys.exit(1)
    target = sys.argv[1]
    ok, msg, meta = validate_pe_file(target)
    print(f"PE Validation: {'PASS' if ok else 'FAIL'} -- {msg}")
    if meta:
        for k, v in meta.items():
            print(f"  {k}: {v}")
    sys.exit(0 if ok else 1)
