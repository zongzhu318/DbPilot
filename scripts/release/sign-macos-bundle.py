#!/usr/bin/env python3
"""Prepare the code-only MacOS directory and sign nested code before the app."""
import argparse
from pathlib import Path
import shutil
import subprocess


def sign_bundle(app, identity, entitlements):
    app = Path(app).resolve()
    executable = app / 'Contents/MacOS/DbStudio'
    # codesign treats files in MacOS as code. Old publish outputs can retain PDBs
    # even when the current publish disables symbol generation.
    for symbols in (app / 'Contents/MacOS').rglob('*.pdb'):
        target = app / 'Contents/Resources/DebugSymbols' / symbols.relative_to(app / 'Contents/MacOS')
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.move(str(symbols), str(target))
    paths = []
    for path in app.rglob('*'):
        if path.is_symlink() or path == executable:
            continue
        if path.is_file():
            kind = subprocess.run(['file', '-b', str(path)], capture_output=True, text=True, check=True).stdout
            if 'Mach-O' in kind:
                paths.append(path)
        elif path.is_dir() and path.suffix in ('.app', '.xpc', '.framework'):
            paths.append(path)
    # Signing the main executable directly makes codesign validate the enclosing
    # app too early. Signing the app last signs its executable and seals resources.
    for path in sorted(set(paths), key=lambda p: (-len(p.parts), str(p))) + [app]:
        args = ['codesign', '--force', '--sign', identity]
        if identity != '-':
            args += ['--timestamp', '--options', 'runtime']
        if path == app:
            args += ['--entitlements', str(entitlements)]
        subprocess.run(args + [str(path)], check=True)
    subprocess.run(['codesign', '--verify', '--deep', '--strict', str(app)], check=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--app', required=True)
    parser.add_argument('--identity', default='-')
    parser.add_argument('--entitlements', required=True)
    args = parser.parse_args()
    sign_bundle(args.app, args.identity, args.entitlements)
