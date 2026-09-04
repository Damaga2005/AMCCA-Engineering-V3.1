#!/usr/bin/env python3
"""
Adversarial Release Certification Mutation Suite (DEF-CERT-008, SPEC/76, Sections 11.3 & 17).

Demonstrates that the Release Gate strictly rejects any invalid, corrupted,
inconsistent, or failing build artifact/metadata, and accepts only fully verified releases.
"""
import hashlib
import os
import shutil
import struct
import sys
import tempfile
import unittest
import zipfile

# Ensure TOOLS dir is in sys.path
SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
if SCRIPT_DIR not in sys.path:
    sys.path.insert(0, SCRIPT_DIR)

from release_gate import verify_release_invariants


def create_minimal_pe32plus(
    machine: int = 0x8664,
    magic: int = 0x020B,
    num_sections: int = 3,
    image_base: int = 0x00400000,
    section_alignment: int = 4096,
    file_alignment: int = 512,
    size_of_image: int = 65536,
) -> bytes:
    """Generates a structurally valid minimal PE32+ AMD64 executable binary."""
    buf = bytearray(512)
    buf[0] = 0x4D
    buf[1] = 0x5A  # MZ
    e_lfanew = 128
    buf[0x3C:0x40] = struct.pack("<I", e_lfanew)

    buf[e_lfanew:e_lfanew + 4] = b"PE\x00\x00"
    coff = e_lfanew + 4
    buf[coff:coff + 20] = struct.pack("<HHIIIHH", machine, num_sections, 0, 0, 0, 240, 0)

    opt = coff + 20
    buf[opt:opt + 2] = struct.pack("<H", magic)
    buf[opt + 24:opt + 32] = struct.pack("<Q", image_base)
    buf[opt + 32:opt + 36] = struct.pack("<I", section_alignment)
    buf[opt + 36:opt + 40] = struct.pack("<I", file_alignment)
    buf[opt + 56:opt + 60] = struct.pack("<I", size_of_image)

    return bytes(buf)


def create_minimal_msi() -> bytes:
    """Generates a dummy MSI file starting with standard OLE compound file header."""
    header = b"\xD0\xCF\x11\xE0\xA1\xB1\x1A\xE1" + (b"\x00" * 504)
    return header


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest().lower()


class ReleaseCertificationMutationTests(unittest.TestCase):
    def setUp(self):
        self.test_dir = tempfile.mkdtemp(prefix="amcca_cert_test_")
        self.mock_sha = "a" * 40

        # Create valid components
        self.exe_bytes = create_minimal_pe32plus()
        self.msi_bytes = create_minimal_msi()

        self.exe_path = os.path.join(self.test_dir, "AMCCA-Setup.exe")
        with open(self.exe_path, "wb") as f:
            f.write(self.exe_bytes)

        self.msi_path = os.path.join(self.test_dir, "AMCCA-Setup.msi")
        with open(self.msi_path, "wb") as f:
            f.write(self.msi_bytes)

        self.zip_path = os.path.join(self.test_dir, "AMCCA-Desktop-win-x64.zip")
        with zipfile.ZipFile(self.zip_path, "w") as zf:
            zf.writestr("app.dll", b"binary content")

        with open(self.zip_path, "rb") as f:
            self.zip_bytes = f.read()

        self.exe_hash = sha256_bytes(self.exe_bytes)
        self.msi_hash = sha256_bytes(self.msi_bytes)
        self.zip_hash = sha256_bytes(self.zip_bytes)

        self.total_tests = 513
        self.write_trx(total=513, passed=513, failed=0, not_executed=0)
        self.write_sums()
        self.write_metadata()

    def tearDown(self):
        shutil.rmtree(self.test_dir, ignore_errors=True)

    def write_trx(self, total: int = 513, passed: int = 513, failed: int = 0, not_executed: int = 0):
        trx_content = f"""<?xml version="1.0" encoding="utf-8"?>
<TestRun id="1" name="ReleaseTests" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <ResultSummary outcome="Completed">
    <Counters total="{total}" executed="{passed + failed}" passed="{passed}" failed="{failed}" error="0" timeout="0" aborted="0" inconclusive="0" passedButRunAborted="0" notRunnable="0" notExecuted="{not_executed}" disconnected="0" warning="0" completed="0" inProgress="0" pending="0" />
  </ResultSummary>
</TestRun>"""
        trx_path = os.path.join(self.test_dir, "release-tests.trx")
        with open(trx_path, "w", encoding="utf-8") as f:
            f.write(trx_content)

    def write_sums(self, custom_entries: list[str] | None = None):
        sums_path = os.path.join(self.test_dir, "SHA256SUMS.txt")
        if custom_entries is None:
            entries = [
                f"{self.exe_hash}  AMCCA-Setup.exe",
                f"{self.msi_hash}  AMCCA-Setup.msi",
                f"{self.zip_hash}  AMCCA-Desktop-win-x64.zip",
            ]
        else:
            entries = custom_entries
        with open(sums_path, "w", encoding="utf-8") as f:
            f.write("\n".join(entries) + "\n")

    def write_metadata(
        self,
        commit_sha: str | None = None,
        working_tree: str = "CLEAN",
        warnings: int = 0,
        errors: int = 0,
        total_tests: int = 513,
        exe_hash: str | None = None,
        msi_hash: str | None = None,
        zip_hash: str | None = None,
    ):
        sha = commit_sha or self.mock_sha
        ehash = exe_hash or self.exe_hash
        mhash = msi_hash or self.msi_hash
        zhash = zip_hash or self.zip_hash

        content = f"""# AMCCA Engineering V3.1 -- Deterministic Release Certification Metadata

- Git Commit SHA: {sha}
- Working Tree: {working_tree}
- Build Configuration: Release
- Target Runtime: win-x64
- Operating System: Windows
- Total Tests Executed: {total_tests}
- Total Tests Passed: {total_tests}
- Total Tests Failed: 0
- Total Tests Skipped: 0
- Compiler Warnings: {warnings}
- Compiler Errors: {errors}
- Release Verification Status: VERIFIED

## Cryptographic Artifact Hashes (SHA-256)

| Artifact Name | Format | SHA-256 Checksum |
|---|---|---|
| AMCCA-Setup.exe | PE32+ Bootstrapper (WiX Burn) | {ehash} |
| AMCCA-Setup.msi | Windows Installer Package (MSI) | {mhash} |
| AMCCA-Desktop-win-x64.zip | Standalone Publish Package | {zhash} |

## Validation Results
- Schemas and Invariants: 57/57 PASS
- Conformance and Conditionals: 65/65 PASS
- Automated Tests: {total_tests}/{total_tests} PASS (0 failed, 0 skipped)
- PE Header Verification: Structural PE32+ AMD64 confirmed, distinct from MSI
"""
        meta_path = os.path.join(self.test_dir, "RELEASE_METADATA.md")
        with open(meta_path, "w", encoding="utf-8") as f:
            f.write(content)

    def test_00_baseline_passes(self):
        """Baseline valid bundle must produce strict PASS."""
        ok, failures = verify_release_invariants(
            release_dir=self.test_dir,
            expected_commit_sha=self.mock_sha,
            check_git=False,
            check_tools=False,
        )
        self.assertTrue(ok, f"Baseline should PASS but got: {failures}")
        self.assertEqual(len(failures), 0)

    def test_01_mutation_failed_tests_in_trx(self):
        """Mutation 1 / Caso A: Tests failed > 0 must cause FAIL."""
        self.write_trx(total=513, passed=512, failed=1, not_executed=0)
        ok, failures = verify_release_invariants(
            release_dir=self.test_dir,
            expected_commit_sha=self.mock_sha,
            check_git=False,
            check_tools=False,
        )
        self.assertFalse(ok)
        self.assertTrue(any("tests failed > 0" in f for f in failures), f"Expected 'tests failed > 0' in {failures}")

    def test_02_mutation_skipped_tests_in_trx(self):
        """Mutation 2 / Caso B: Skipped tests > 0 must cause FAIL."""
        self.write_trx(total=513, passed=512, failed=0, not_executed=1)
        ok, failures = verify_release_invariants(
            release_dir=self.test_dir,
            expected_commit_sha=self.mock_sha,
            check_git=False,
            check_tools=False,
        )
        self.assertFalse(ok)
        self.assertTrue(any("tests skipped > 0" in f for f in failures), f"Expected 'tests skipped > 0' in {failures}")

    def test_03_mutation_compiler_warnings(self):
        """Mutation 3 / Caso C: Compiler warnings > 0 must cause FAIL."""
        ok, failures = verify_release_invariants(
            release_dir=self.test_dir,
            expected_commit_sha=self.mock_sha,
            build_warnings=1,
            check_git=False,
            check_tools=False,
        )
        self.assertFalse(ok)
        self.assertTrue(any("warnings > 0" in f for f in failures), f"Expected 'warnings > 0' in {failures}")

    def test_04_mutation_msi_hash_mismatch(self):
        """Mutation 4 / Caso G: SHA of MSI altered in manifest must cause FAIL."""
        corrupted_hash = "0" * 64
        self.write_sums([
            f"{self.exe_hash}  AMCCA-Setup.exe",
            f"{corrupted_hash}  AMCCA-Setup.msi",
            f"{self.zip_hash}  AMCCA-Desktop-win-x64.zip",
        ])
        ok, failures = verify_release_invariants(
            release_dir=self.test_dir,
            expected_commit_sha=self.mock_sha,
            check_git=False,
            check_tools=False,
        )
        self.assertFalse(ok)
        self.assertTrue(any("hash mismatch" in f for f in failures), f"Expected 'hash mismatch' in {failures}")

    def test_05_mutation_pe_byte_corruption(self):
        """Mutation 5 / Caso F: 1 byte altered in EXE invalidating PE structure must cause FAIL."""
        corrupted_exe = bytearray(self.exe_bytes)
        # Corrupt PE magic signature from 'P' to 'X'
        corrupted_exe[128] = ord('X')
        with open(self.exe_path, "wb") as f:
            f.write(corrupted_exe)

        # Update sums with the new hash so sums match, but PE validator catches the corruption
        new_hash = sha256_bytes(corrupted_exe)
        self.write_sums([
            f"{new_hash}  AMCCA-Setup.exe",
            f"{self.msi_hash}  AMCCA-Setup.msi",
            f"{self.zip_hash}  AMCCA-Desktop-win-x64.zip",
        ])
        self.write_metadata(exe_hash=new_hash)

        ok, failures = verify_release_invariants(
            release_dir=self.test_dir,
            expected_commit_sha=self.mock_sha,
            check_git=False,
            check_tools=False,
        )
        self.assertFalse(ok)
        self.assertTrue(any("invalid PE" in f for f in failures), f"Expected PE failure in {failures}")

    def test_06_mutation_metadata_contradiction(self):
        """Mutation 6 / Caso I: Metadata contradicting real test evidence must cause FAIL."""
        # Metadata claims 600 tests, but TRX has 513
        self.write_metadata(total_tests=600)
        ok, failures = verify_release_invariants(
            release_dir=self.test_dir,
            expected_commit_sha=self.mock_sha,
            check_git=False,
            check_tools=False,
        )
        self.assertFalse(ok)
        self.assertTrue(any("metadata contradicts real evidence" in f for f in failures), f"Expected metadata failure in {failures}")

    def test_07_mutation_commit_sha_mismatch(self):
        """Mutation 7 / Caso E: Expected commit SHA differs from metadata commit SHA."""
        different_sha = "b" * 40
        ok, failures = verify_release_invariants(
            release_dir=self.test_dir,
            expected_commit_sha=different_sha,
            check_git=False,
            check_tools=False,
        )
        self.assertFalse(ok)
        self.assertTrue(any("metadata contradicts real evidence: commit SHA mismatch" in f for f in failures))

    def test_08_mutation_missing_mandatory_artifact(self):
        """Caso H: Missing mandatory artifact (e.g. AMCCA-Setup.exe deleted) must cause FAIL."""
        os.remove(self.exe_path)
        ok, failures = verify_release_invariants(
            release_dir=self.test_dir,
            expected_commit_sha=self.mock_sha,
            check_git=False,
            check_tools=False,
        )
        self.assertFalse(ok)
        self.assertTrue(any("missing artifact" in f for f in failures), f"Expected 'missing artifact' in {failures}")

    def test_09_mutation_sha256sums_self_reference(self):
        """Self-reference in SHA256SUMS.txt must be rejected."""
        self.write_sums([
            f"{self.exe_hash}  AMCCA-Setup.exe",
            f"{self.msi_hash}  AMCCA-Setup.msi",
            f"{self.zip_hash}  AMCCA-Desktop-win-x64.zip",
            f"{'c'*64}  SHA256SUMS.txt",
        ])
        ok, failures = verify_release_invariants(
            release_dir=self.test_dir,
            expected_commit_sha=self.mock_sha,
            check_git=False,
            check_tools=False,
        )
        self.assertFalse(ok)
        self.assertTrue(any("self-reference is forbidden" in f for f in failures))

    def test_10_mutation_exe_msi_identical_hash(self):
        """MSI and EXE identical hash must be strictly rejected (DEF-CERT-001)."""
        # Overwrite EXE with MSI content
        with open(self.exe_path, "wb") as f:
            f.write(self.msi_bytes)
        self.write_sums([
            f"{self.msi_hash}  AMCCA-Setup.exe",
            f"{self.msi_hash}  AMCCA-Setup.msi",
            f"{self.zip_hash}  AMCCA-Desktop-win-x64.zip",
        ])
        self.write_metadata(exe_hash=self.msi_hash)

        ok, failures = verify_release_invariants(
            release_dir=self.test_dir,
            expected_commit_sha=self.mock_sha,
            check_git=False,
            check_tools=False,
        )
        self.assertFalse(ok)
        self.assertTrue(any("MSI == EXE" in f or "invalid PE" in f for f in failures))


if __name__ == "__main__":
    print("=" * 72)
    print("ADVERSARIAL RELEASE CERTIFICATION MUTATION SUITE (DEF-CERT-008)")
    print("=" * 72)
    suite = unittest.TestLoader().loadTestsFromTestCase(ReleaseCertificationMutationTests)
    runner = unittest.TextTestRunner(verbosity=2)
    result = runner.run(suite)
    sys.exit(0 if result.wasSuccessful() else 1)

