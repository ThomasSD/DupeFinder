# Duplicate File Linker

A .NET console tool to detect duplicate files and consolidate them using NTFS hardlinks.
It scans a target directory, groups files with identical content, and replaces duplicates with hardlinks pointing to a chosen master file.
This saves disk space while preserving filenames, paths, and timestamps.

Especially useful for AI model folders and toolchains that reuse same large model files under different names or in multiple directories 
(e.g., Stable Diffusion / ComfyUI / Krita / etc. model caches). 

⚠️ **Windows / NTFS only.** Hardlinks cannot span different volumes.

---

## Features

- Scans all files in a directory (recursively).
- Groups duplicates by size + 1 MiB SHA-256 prefix hash.
- Keeps one file as the **master** and replaces others with hardlinks.
- Respects **minimum file size** to skip small files.
- Can restrict scan by **file extension patterns** (wildcards, multiple allowed).
- Supports **dry-run** (no changes) and **report-only** modes.
- Outputs detailed info about each duplicate set.

---

## Usage

```
duplink <ScanDir> -MasterDir:<Path> [options]
```

### Options

| Option                  | Description                                                                 |
|-------------------------|-----------------------------------------------------------------------------|
| `-MasterDir:<Path>`     | Directory where master copies are preferred (must be on same volume).       |
| `-MinMb:<int>`          | Minimum file size to include (default: 100 MB).                             |
| `-ext:<patterns>`       | Restrict by extensions. Supports wildcards and multiple patterns via `;`.   |
|                         | Example: `-ext:*.jpg;*.png;*.gif`                                           |
| `-dryrun`               | Show what would be changed, without modifying files.                        |
| `-report`               | Print a detailed report of detected duplicates, no modifications applied.   |
| `-h` / `--help`         | Show help message.                                                          |

---

## Examples

### 1. Simple scan
Scan all files ≥100 MB under `C:\AI\comfyui` and consolidate into `C:\AI\models`:

```
duplink C:\AI\comfyui -masterdir:C:\AI\models
```

### 2. Dry run
Show what would happen, without making changes:

```
duplink C:\AI\comfyui -masterdir:C:\AI\models -dryrun
```

### 3. Report mode
Only print duplicates and grouping info, don’t touch files:

```
duplink C:\AI\comfyui -masterdir:C:\AI\models -report
```

### 4. Restrict by extensions
Only check `.safesensors` files ≥200 MB:

```
duplink C:\AI\comfyui -masterdir:C:\AI\models -minmb:200 -ext:*.safesensors
```

Check multiple image types:

```
duplink D:\Photos -masterdir:D:\MasterPhotos -ext:*.jpg;*.png;*.gif
```

---

## 🛠️ Installation

You have two options:

### 1. Compile yourself
Requires **.NET 8 SDK**.  
Clone the repo and run:

```
dotnet build
```

The executable will be available in `bin/Debug/net8.0/` 

### 2. Download prebuilt binary
If you prefer not to compile, grab the prebuilt `duplink.exe` from the project’s release page or just dl 
the exe and dll from the source code.  
Just place both files in a folder on your system and run it from the command line.

⚠️ Note: Since this is an unsigned executable, Windows may show a warning when you try to run it. That’s expected for tools distributed outside the Microsoft Universe.

The safest approach is always to compile it yourself from source.
If you’re comfortable, you can use the prebuilt binary directly.


---
## Notes

- Master and scan directories **must be on the same NTFS volume**.
- Hardlinks are **transparent to applications**: file size, timestamps, and paths remain unchanged.
- Once linked, multiple filenames point to the same physical data.
  Deleting one occurrence of a linked file does not delete the content — the data stays accessible via the remaining links.
  The file’s content is only removed once all links to it have been deleted!

---

## License

MIT License. Free to use and modify.


## DISCLAIMER:

This tool modifies files by replacing duplicates with NTFS hardlinks.
- While it has been tested, use it at your own risk.
- Always ensure you have proper backups before running it on important data.
- The authors and contributors take no responsibility for any data loss, corruption, or unintended side effects caused by using this software.
