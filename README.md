[![.NET Core Desktop](https://github.com/Jessomadic/ImagingTool/actions/workflows/dotnet-desktop.yml/badge.svg)](https://github.com/Jessomadic/ImagingTool/actions/workflows/dotnet-desktop.yml) [![.NET Core Desktop](https://github.com/Jessomadic/ImagingTool/actions/workflows/dotnet-desktop.yml/badge.svg)](https://github.com/Jessomadic/ImagingTool/actions/workflows/dotnet-desktop.yml)


ImagingTool - System Imaging & Backup Tool (Prototype)

How to Use:
-----------
1. Run the program with administrative privileges.
2. It will check if Wimlib is available at C:\WimLib and download it if missing.
3. You will be prompted to choose a destination path for the image (e.g., D:\Backup.wim).
4. The program will create a full image of the system boot drive.
5. The image is created in WIM format and is multithreaded using wimlib.

Note:
-----
- BitLocker checks are currently disabled.
- Progress updates display in 1% increments.
- Ensure you are connected to the internet during the first run to allow Wimlib download.
- Wimlib is extracted to: C:\WimLib

This is a prototype and currently does not support scheduled backups or network deployment.
