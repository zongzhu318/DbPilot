#!/usr/bin/env python3
"""Build a DMG in isolated scratch directories; retry only transient busy errors."""
import argparse
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import time


def create_dmg(app, output, attempts=3):
    app, output = Path(app).resolve(), Path(output).resolve()
    if not app.is_dir():
        raise FileNotFoundError(app)
    output.parent.mkdir(parents=True, exist_ok=True)
    for attempt in range(1, attempts + 1):
        with tempfile.TemporaryDirectory(prefix='dbpilot-dmg-', dir=os.environ.get('RUNNER_TEMP')) as temp:
            scratch = Path(temp)
            source = scratch / 'source'
            source.mkdir()
            subprocess.run(['ditto', str(app), str(source / 'DbPilot.app')], check=True)
            (source / 'Applications').symlink_to('/Applications')
            image = scratch / 'DbPilot.dmg'
            # Explicit HFS+ avoids dependence on the runner's default APFS image layout.
            result = subprocess.run(['hdiutil', 'create', '-volname', 'DbPilot', '-fs', 'HFS+',
                                     '-srcfolder', str(source), '-format', 'UDZO', str(image)],
                                    capture_output=True, text=True)
            if result.returncode == 0:
                subprocess.run(['hdiutil', 'verify', str(image)], check=True)
                # Publish only a verified image. Never replace the destination with a partial DMG.
                with tempfile.NamedTemporaryFile(dir=output.parent, suffix='.dmg', delete=False) as handle:
                    pending = Path(handle.name)
                try:
                    shutil.copyfile(image, pending)
                    os.replace(pending, output)
                finally:
                    pending.unlink(missing_ok=True)
                print(f'Created: {output}', flush=True)
                return
            error = (result.stdout or '') + (result.stderr or '')
            if 'resource busy' not in error.lower() or attempt == attempts:
                raise RuntimeError(f'DMG creation failed (attempt {attempt}/{attempts}):\n{error}')
            print(f'hdiutil resource busy; retrying with a fresh directory ({attempt}/{attempts}).', flush=True)
        time.sleep(attempt * 5)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--app', required=True)
    parser.add_argument('--output', required=True)
    args = parser.parse_args()
    create_dmg(args.app, args.output)
