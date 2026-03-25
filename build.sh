#!/bin/bash
set -e

PLUGIN_DIR="Jellyfin.Plugin.SshTerminal"
OUTPUT_DIR="dist"

echo "Building plugin..."
dotnet publish "$PLUGIN_DIR" -c Release -o "$OUTPUT_DIR/publish"

echo "Creating plugin zip..."
mkdir -p "$OUTPUT_DIR"
cd "$OUTPUT_DIR/publish"
zip -r "../jellyfin-plugin-ssh-terminal_1.0.0.0.zip" *.dll
cd ../..

echo "Computing checksum..."
MD5=$(md5sum "$OUTPUT_DIR/jellyfin-plugin-ssh-terminal_1.0.0.0.zip" | awk '{print $1}')
echo "MD5: $MD5"

echo ""
echo "Build complete: $OUTPUT_DIR/jellyfin-plugin-ssh-terminal_1.0.0.0.zip"
echo "Update manifest.json checksum to: $MD5"
