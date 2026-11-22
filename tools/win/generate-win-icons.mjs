#!/usr/bin/env node
import { execFile } from 'node:child_process';
import { mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { promisify } from 'node:util';
import { fileURLToPath, pathToFileURL } from 'node:url';

const execFileAsync = promisify(execFile);
const root = fileURLToPath(new URL('../../', import.meta.url));

/**
 * Packs an array of `{ size: number, png: Buffer }` into a standard Windows `.ico` container.
 */
export function buildIcoFromPngFrames(frames) {
  const header = Buffer.alloc(6);
  header.writeUInt16LE(0, 0); // Reserved
  header.writeUInt16LE(1, 2); // Type: 1 = ICO
  header.writeUInt16LE(frames.length, 4);

  const dirSize = 16 * frames.length;
  let offset = 6 + dirSize;
  const dirEntries = [];

  for (const frame of frames) {
    const entry = Buffer.alloc(16);
    entry.writeUInt8(frame.size >= 256 ? 0 : frame.size, 0);
    entry.writeUInt8(frame.size >= 256 ? 0 : frame.size, 1);
    entry.writeUInt8(0, 2); // Color count
    entry.writeUInt8(0, 3); // Reserved
    entry.writeUInt16LE(1, 4); // Planes
    entry.writeUInt16LE(32, 6); // BitCount
    entry.writeUInt32LE(frame.png.length, 8);
    entry.writeUInt32LE(offset, 12);
    dirEntries.push(entry);
    offset += frame.png.length;
  }

  return Buffer.concat([header, ...dirEntries, ...frames.map(f => f.png)]);
}

async function renderResizedPngsWindows(sourcePng, outDir, sizes, tintColorHex = null) {
  const psScript = `
Add-Type -AssemblyName System.Drawing
$src = [System.Drawing.Image]::FromFile($env:GB_ICON_SRC)
$sizes = $env:GB_ICON_SIZES.Split(',') | ForEach-Object { [int]$_ }
foreach ($s in $sizes) {
  $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
  $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
  $g.DrawImage($src, 0, 0, $s, $s)
  if ($env:GB_ICON_TINT) {
    $dotSize = [Math]::Max(4, [int]($s * 0.28))
    $color = [System.Drawing.ColorTranslator]::FromHtml($env:GB_ICON_TINT)
    $brush = New-Object System.Drawing.SolidBrush($color)
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, [Math]::Max(1, [int]($s * 0.06)))
    $x = $s - $dotSize - 1
    $y = $s - $dotSize - 1
    $g.FillEllipse($brush, $x, $y, $dotSize, $dotSize)
    $g.DrawEllipse($pen, $x, $y, $dotSize, $dotSize)
    $brush.Dispose()
    $pen.Dispose()
  }
  $g.Dispose()
  $outPath = Join-Path $env:GB_ICON_OUT "$s.png"
  $bmp.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()
}
$src.Dispose()
`;
  await execFileAsync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', psScript], {
    env: {
      ...process.env,
      GB_ICON_SRC: sourcePng,
      GB_ICON_OUT: outDir,
      GB_ICON_SIZES: sizes.join(','),
      GB_ICON_TINT: tintColorHex ?? '',
    },
    windowsHide: true,
  });

  const frames = [];
  for (const size of sizes) {
    const png = await readFile(join(outDir, `${size}.png`));
    frames.push({ size, png });
  }
  return frames;
}

export async function generateWindowsIcons() {
  if (process.platform !== 'win32') {
    throw new Error('generate-win-icons.mjs must be run on Windows');
  }

  const masterPng = join(root, 'assets', 'master-1024.png');
  const trayBasePng = join(root, 'assets', 'icons', 'menu-connected.png');
  const appIconOut = join(root, 'assets', 'AppIcon.ico');
  const iconsDir = join(root, 'assets', 'icons');
  await mkdir(iconsDir, { recursive: true });

  const stage = await mkdtemp(join(tmpdir(), 'gb-icons-'));
  try {
    const appDir = join(stage, 'app');
    await mkdir(appDir);
    const appFrames = await renderResizedPngsWindows(masterPng, appDir, [16, 24, 32, 48, 64, 128, 256]);
    await writeFile(appIconOut, buildIcoFromPngFrames(appFrames));

    const traySpecs = [
      { name: 'tray-connected.ico', tint: '#10B981' },
      { name: 'tray-waiting.ico', tint: '#6E6E73' },
      { name: 'tray-warning.ico', tint: '#F59E0B' },
    ];
    for (const spec of traySpecs) {
      const subDir = join(stage, spec.name);
      await mkdir(subDir);
      const frames = await renderResizedPngsWindows(trayBasePng, subDir, [16, 24, 32, 48], spec.tint);
      await writeFile(join(iconsDir, spec.name), buildIcoFromPngFrames(frames));
    }
  } finally {
    await rm(stage, { recursive: true, force: true });
  }

  console.log('Generated assets/AppIcon.ico and tray-{connected,waiting,warning}.ico');
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  generateWindowsIcons().catch(err => {
    console.error(`[generate-win-icons] ${err.message}`);
    process.exitCode = 1;
  });
}
