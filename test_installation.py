#!/usr/bin/env python3
"""
Test script to verify Xbox 360 Memory Carver installation.
Run this to check if all components are working correctly.
"""

import sys
import os


def test_imports():
    """Test if all required modules can be imported."""
    print("Testing imports...")
    try:
        from src import file_signatures, parsers, utils, carver

        print("✓ All core modules imported successfully")
        return True
    except ImportError as e:
        print(f"✗ Import error: {e}")
        return False


def test_dependencies():
    """Test if optional dependencies are available."""
    print("\nTesting dependencies...")

    # Test tqdm
    try:
        import tqdm

        print("✓ tqdm is installed (progress bars enabled)")
    except ImportError:
        print("⚠ tqdm not installed (progress bars disabled)")
        print("  Install with: pip install tqdm")

    return True


def test_signatures():
    """Test file signature definitions."""
    print("\nTesting file signatures...")
    try:
        from src.file_signatures import FILE_SIGNATURES

        required_keys = ["magic", "extension", "description", "min_size", "max_size"]
        all_valid = True

        for sig_name, sig_info in FILE_SIGNATURES.items():
            for key in required_keys:
                if key not in sig_info:
                    print(f"✗ Signature '{sig_name}' missing key: {key}")
                    all_valid = False

        if all_valid:
            print(f"✓ All {len(FILE_SIGNATURES)} file signatures valid")
            return True
        return False
    except Exception as e:
        print(f"✗ Error testing signatures: {e}")
        return False


def test_parsers():
    """Test parser initialization."""
    print("\nTesting parsers...")
    try:
        from src.parsers import DDSParser, XMAParser, NIFParser, ScriptParser

        # Test parser instantiation
        dds = DDSParser()
        xma = XMAParser()
        nif = NIFParser()
        script = ScriptParser()

        print("✓ All parsers initialized successfully")
        return True
    except Exception as e:
        print(f"✗ Error testing parsers: {e}")
        return False


def test_carver():
    """Test carver initialization."""
    print("\nTesting carver...")
    try:
        from src.carver import MemoryCarver

        # Try to create carver instance
        carver = MemoryCarver(output_dir="./test_output")

        print("✓ MemoryCarver initialized successfully")
        return True
    except Exception as e:
        print(f"✗ Error testing carver: {e}")
        return False


def test_directory_structure():
    """Test if directory structure is correct."""
    print("\nTesting directory structure...")

    required_dirs = ["src", "docs", "output"]
    required_files = ["main.py", "README.md", "requirements.txt", "LICENSE"]

    all_present = True

    for dirname in required_dirs:
        if not os.path.isdir(dirname):
            print(f"✗ Missing directory: {dirname}")
            all_present = False

    for filename in required_files:
        if not os.path.isfile(filename):
            print(f"✗ Missing file: {filename}")
            all_present = False

    if all_present:
        print("✓ All required files and directories present")

    return all_present


def main():
    """Run all tests."""
    print("=" * 60)
    print("Xbox 360 Memory Carver - Installation Test")
    print("=" * 60)

    tests = [
        ("Directory Structure", test_directory_structure),
        ("Module Imports", test_imports),
        ("Dependencies", test_dependencies),
        ("File Signatures", test_signatures),
        ("Parsers", test_parsers),
        ("Carver Engine", test_carver),
    ]

    results = []
    for test_name, test_func in tests:
        try:
            result = test_func()
            results.append((test_name, result))
        except Exception as e:
            print(f"✗ {test_name} failed with exception: {e}")
            results.append((test_name, False))

    # Print summary
    print("\n" + "=" * 60)
    print("Test Summary")
    print("=" * 60)

    passed = sum(1 for _, result in results if result)
    total = len(results)

    for test_name, result in results:
        status = "✓ PASS" if result else "✗ FAIL"
        print(f"{status:8s} {test_name}")

    print("=" * 60)
    print(f"Results: {passed}/{total} tests passed")

    if passed == total:
        print("\n✓ All tests passed! Installation is working correctly.")
        print("\nYou can now run the carver with:")
        print("  python main.py --help")
        return 0
    else:
        print("\n✗ Some tests failed. Please check the errors above.")
        print("\nTry:")
        print("  1. Install dependencies: pip install -r requirements.txt")
        print("  2. Ensure you're in the correct directory")
        print("  3. Check that all files were extracted correctly")
        return 1


if __name__ == "__main__":
    sys.exit(main())
